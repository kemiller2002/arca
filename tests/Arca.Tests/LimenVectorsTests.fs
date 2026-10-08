/// Limen's shared store conformance vectors (LCP-075), run against the
/// FakeStore that Arca.Limen's tests use. The vectors are the same file the
/// TypeScript pack passes under node, in Chromium and in WebKit, copied from
/// kemiller2002/limen v0.8.0 (509ef4f) and pinned by digest: if the fake
/// Arca tests against stops answering as the browser pack does, these fail.
/// The runner is Limen's own (conformance/store/fsharp), as xUnit facts.
module Arca.Tests.LimenVectorsTests

open System
open System.IO
open System.Security.Cryptography
open Limen.Contract
open Limen.Store
open Xunit

/// The vectors' SHA-256 at Limen v0.8.0.
[<Literal>]
let PinnedDigest = "d595938ed590bd282d003fc4ecf7e678e2401d1d942ed1f9e0f0686ae4b52418"

let private vectorsPath () =
    match RequirementsTraceability.repositoryRoot (DirectoryInfo AppContext.BaseDirectory) with
    | Some root -> Path.Combine(root, "tests", "Arca.Tests", "limen", "store.vectors.json")
    | None -> failwith "the repository root (Arca.slnx) was not found"

let private document =
    lazy
        (match Json.parse (RawJson(File.ReadAllText(vectorsPath ()))) with
         | Ok json -> json
         | Error error -> failwith error)

let private field (name: string) (json: Json) =
    match json with
    | Json.Object members -> members |> List.tryFind (fun (key, _) -> key = name) |> Option.map snd
    | _ -> None

let private text (json: Json option) =
    match json with
    | Some(Json.String value) -> Some value
    | _ -> None

let private items (json: Json option) =
    match json with
    | Some(Json.Array values) -> values
    | _ -> []

/// {"$any": kind} matches any value of that kind; otherwise JSON equality
/// with exactly the same object keys.
let rec private matches (expected: Json) (actual: Json) =
    match expected, actual with
    | Json.Object [ "$any", Json.String kind ], _ ->
        match kind, actual with
        | "bool", Json.Bool _ -> true
        | "int", Json.Number n -> Math.Floor n = n
        | "string", Json.String _ -> true
        | _ -> false
    | Json.Array e, Json.Array a -> e.Length = a.Length && List.forall2 matches e a
    | Json.Object e, Json.Object a ->
        let keys members = members |> List.map fst |> List.sort
        keys e = keys a && e |> List.forall (fun (name, value) -> a |> List.exists (fun (other, item) -> other = name && matches value item))
    | Json.Number x, Json.Number y -> x = y
    | _ -> expected = actual

let private render (json: Json) =
    let (RawJson t) = Json.render json
    t

let private jsonOf (serialized: string) =
    match Json.parse (RawJson serialized) with
    | Ok json -> json
    | Error _ -> Json.Null

let private requirements =
    [ "holdOpen"; "inject:quota"; "inject:openFails"; "inject:storageCleared"; "inject:missing"; "storage:present"; "storage:absent" ]

let private scripted: FakeStorage =
    { Persist = true
      Persisted = false
      Usage = 4096L
      Quota = 1073741824L }

[<NoEquality; NoComparison>]
type private Harness =
    { Ask: string -> bool -> Limen.Contract.Store.Types.StoreRequest -> Limen.Contract.Store.Types.StoreResult
      TakeFacts: string -> Limen.Contract.Store.Types.StoreFact list
      Inject: string -> FakeFault -> unit
      HoldOpen: string -> string -> unit
      Release: string -> string -> unit }

let private configOf (index: int) (vector: Json) (tab: string) : FakeTabConfig =
    let app =
        field "tabs" vector |> Option.bind (field tab) |> Option.bind (field "app" >> text) |> Option.defaultValue "one"

    let limits =
        match field "registration" vector |> Option.bind (field "limits") with
        | Some l ->
            let number name =
                match field name l with
                | Some(Json.Number n) -> int64 n
                | _ -> 0L

            { MaxValueBytes = number "maxValueBytes"
              MaxTransactionBytes = number "maxTransactionBytes" }
        | None -> FakeTabConfig.defaultLimits

    { Namespace = "v" + string index + "-" + app
      Limits = limits }

let private storageFor (vector: Json) =
    if items (field "requires" vector) |> List.contains (Json.String "storage:present") then Some scripted else None

/// FakeStore's pure transition, folded in a local cell.
let private pureHarness (index: int) (vector: Json) : Harness =
    let state = ref (FakeStore.create (storageFor vector))
    let registered = Collections.Generic.HashSet<string>()

    let ensure tab =
        if registered.Add tab then
            state.Value <- FakeStore.register tab (configOf index vector tab) state.Value

    { Ask =
        fun tab cancelled request ->
            ensure tab
            let next, result = if cancelled then FakeStore.cancelled state.Value request else FakeStore.stepAs tab state.Value request
            state.Value <- next
            result
      TakeFacts =
        fun tab ->
            ensure tab
            let facts, next = FakeStore.takeFacts tab state.Value
            state.Value <- next
            facts
      Inject =
        fun tab fault ->
            ensure tab
            state.Value <- FakeStore.inject tab fault state.Value
      HoldOpen =
        fun tab database ->
            ensure tab
            state.Value <- FakeStore.holdOpen tab database state.Value
      Release = fun tab database -> state.Value <- FakeStore.release tab database state.Value }

/// The executors: exactly what Arca.Limen's tests use.
let private executorHarness (index: int) (vector: Json) : Harness =
    let origin = FakeStore.origin (storageFor vector)
    let tabs = Collections.Generic.Dictionary<string, FakeExecutor>()

    let tabOf tab =
        match tabs.TryGetValue tab with
        | true, executor -> executor
        | false, _ ->
            let executor = origin tab (configOf index vector tab)
            tabs[tab] <- executor
            executor

    { Ask =
        fun tab cancelled request ->
            if cancelled then
                Limen.Contract.Store.Types.StoreResult.Cancelled
            else
                (tabOf tab).Execute request |> Async.RunSynchronously
      TakeFacts = fun tab -> (tabOf tab).TakeFacts()
      Inject = fun tab fault -> (tabOf tab).Inject fault
      HoldOpen = fun _ _ -> invalidOp "unsupported: holdOpen through an executor"
      Release = fun _ _ -> () }

let private runVector (harness: Harness) (vector: Json) : Result<unit, string> =
    let step (number: int) (s: Json) : Result<unit, string> =
        let tab = text (field "tab" s) |> Option.defaultValue "a"

        match field "request" s, field "facts" s, text (field "inject" s), text (field "holdOpen" s), text (field "release" s) with
        | Some request, _, _, _, _ ->
            match Limen.Contract.Store.Codec.parseStoreRequest (render request) with
            | Error error -> Error("step " + string number + ": the request does not decode at " + error.Path)
            | Ok decoded ->
                let actual =
                    jsonOf (Limen.Contract.Store.Codec.serializeStoreResult (harness.Ask tab (field "cancelled" s = Some(Json.Bool true)) decoded))

                let expected = field "expect" s |> Option.defaultValue Json.Null

                if matches expected actual then
                    Ok()
                else
                    Error("step " + string number + ": expected " + render expected + ", got " + render actual)
        | None, Some(Json.String factsTab), _, _, _ ->
            let actual = Json.Array(harness.TakeFacts factsTab |> List.map (Limen.Contract.Store.Codec.serializeStoreFact >> jsonOf))
            let expected = field "expect" s |> Option.defaultValue (Json.Array [])

            if matches expected actual then
                Ok()
            else
                Error("step " + string number + " (facts of " + factsTab + "): expected " + render expected + ", got " + render actual)
        | None, _, Some injection, _, _ ->
            let fault =
                match injection with
                | "quota" -> Some FakeFault.Quota
                | "openFails" -> Some(FakeFault.OpenFails(text (field "error" s) |> Option.defaultValue "UnknownError"))
                | "missing" -> Some FakeFault.Missing
                | "storageCleared" -> Some FakeFault.StorageCleared
                | _ -> None

            match fault with
            | Some f ->
                harness.Inject tab f
                Ok()
            | None -> Error("step " + string number + ": unknown injection " + injection)
        | None, _, None, Some database, _ ->
            harness.HoldOpen tab database
            Ok()
        | None, _, None, None, Some database ->
            harness.Release tab database
            Ok()
        | _ -> Error("step " + string number + ": unknown step " + render s)

    items (field "steps" vector)
    |> List.mapi (fun i s -> i, s)
    |> List.fold
        (fun acc (i, s) ->
            match acc with
            | Ok() -> step i s
            | failed -> failed)
        (Ok())

type private Verdict =
    | Passed
    | Failed of string
    | Unsupported of string

let private runAll (harnessOf: int -> Json -> Harness) (supports: string list) =
    items (field "vectors" document.Value)
    |> List.mapi (fun index vector ->
        let name = text (field "name" vector) |> Option.defaultValue "?"

        let missing =
            items (field "requires" vector)
            |> List.choose (function
                | Json.String r when not (supports |> List.contains r) -> Some r
                | _ -> None)

        if not missing.IsEmpty then
            name, Unsupported(String.Join(", ", missing))
        else
            match (try runVector (harnessOf index vector) vector with error -> Error("threw " + error.Message)) with
            | Ok() -> name, Passed
            | Error message -> name, Failed message)

let private failures verdicts =
    verdicts
    |> List.choose (fun (name, verdict) ->
        match verdict with
        | Failed message -> Some(name + ": " + message)
        | _ -> None)

[<Fact>]
let ``the vectors are Limen 0.8.0's, byte for byte`` () =
    let digest = Convert.ToHexStringLower(SHA256.HashData(File.ReadAllBytes(vectorsPath ())))
    Assert.Equal(PinnedDigest, digest)
    Assert.Equal(32, items (field "vectors" document.Value) |> List.length)

[<Fact>]
let ``FakeStore's pure transition passes every shared vector, none unsupported (LCP-074, LCP-075)`` () =
    let verdicts = runAll pureHarness requirements
    let found = failures verdicts
    Assert.True(found.IsEmpty, String.concat "\n" found)
    Assert.All(verdicts, fun (name, verdict) -> Assert.True((verdict = Passed), $"{name} did not pass"))

[<Fact>]
let ``FakeStore's executors, as Arca's tests use them, fail no shared vector (LCP-075)`` () =
    let verdicts = runAll executorHarness (requirements |> List.filter ((<>) "holdOpen"))
    let found = failures verdicts
    Assert.True(found.IsEmpty, String.concat "\n" found)

    let passed = verdicts |> List.filter (fun (_, verdict) -> verdict = Passed) |> List.length
    let unsupported = verdicts |> List.filter (fun (_, verdict) -> match verdict with Unsupported _ -> true | _ -> false)
    Assert.True(passed > 0)
    // Only holdOpen, which an executor cannot do, is unsupported.
    Assert.All(unsupported, fun (_, verdict) -> Assert.Equal(Unsupported "holdOpen", verdict))

[<Fact>]
let ``the runner reports a wrong expectation as failed`` () =
    match items (field "vectors" document.Value) with
    | (Json.Object members) :: _ ->
        let wrong =
            Json.Object(
                members
                |> List.map (fun (name, value) ->
                    if name <> "steps" then
                        name, value
                    else
                        name,
                        Json.Array(
                            items (Some value)
                            |> List.mapi (fun i step ->
                                match i, step with
                                | 0, Json.Object m -> Json.Object(m |> List.map (fun (k, v) -> if k = "expect" then k, Json.Object [ "kind", Json.String "Blocked" ] else k, v))
                                | _ -> step)
                        ))
            )

        Assert.True(runVector (pureHarness 9999 wrong) wrong |> Result.isError)
    | _ -> failwith "no vectors"
