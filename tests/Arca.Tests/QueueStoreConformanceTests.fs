/// The queue-store conformance suite (WI-0019; ARCA-OFF-002; Limen LCP-046,
/// LCP-060, LCP-075), run against every QueueStore Arca ships: the in-memory
/// store and the localStorage store. The IndexedDB adapter runs it in
/// Arca.Limen's tests.
module Arca.Tests.QueueStoreConformanceTests

open System
open System.Collections.Generic
open Arca
open Arca.GitHub
open Xunit
open FsCheck
open FsCheck.FSharp
open FsCheck.Xunit

let private ok result =
    match result with
    | Ok value -> value
    | Error error -> failwith $"expected Ok, got {error}"

let private chrona =
    Namespace.ofApplication
        { Application = AppId.create "chrona" |> ok
          Environment = { Kind = EnvironmentKind.Test; Name = "offline" }
          Location = DataLocation.create "acme" "data" "main" "apps" |> ok }
    |> ok

let private run fresh = QueueStoreConformance.run fresh |> Async.RunSynchronously

let private problems (results: ConformanceResult list) =
    results
    |> List.choose (fun result ->
        match result.Outcome with
        | ConformanceOutcome.Passed -> None
        | ConformanceOutcome.Failed reason -> Some $"{result.Case} ({result.Requirement}) failed: {reason}"
        | ConformanceOutcome.Unsupported reason -> Some $"{result.Case} ({result.Requirement}) unsupported: {reason}")

let private assertConforms (results: ConformanceResult list) =
    Assert.Equal(QueueStoreConformance.cases.Length, results.Length)
    let found = problems results
    Assert.True(found.IsEmpty, String.concat "\n" found)

/// One browser localStorage, shared by every store opened over it.
type private BrowserStorage() =
    let items = Dictionary<string, string>()
    member val Unavailable = false with get, set
    member _.Items = items

    member this.Execute(request: LocalStorageRequest) =
        async {
            match this.Unavailable, request with
            | true, _ -> return LocalStorageOutcome.Failure LocalStorageFailure.Unavailable
            | false, LocalStorageRequest.Get key ->
                match items.TryGetValue key with
                | true, value -> return LocalStorageOutcome.Success(Some value)
                | _ -> return LocalStorageOutcome.Success None
            | false, LocalStorageRequest.Set(key, value) ->
                items[key] <- value
                return LocalStorageOutcome.Success None
            | false, LocalStorageRequest.Remove key ->
                items.Remove key |> ignore
                return LocalStorageOutcome.Success None
        }

let private localStorage (budget: int64) () =
    async {
        let browser = BrowserStorage()

        return
            { Namespace = chrona
              Store = LocalStorageQueue.store browser.Execute budget chrona
              Reopen = fun () -> async { return LocalStorageQueue.store browser.Execute budget chrona }
              Budget = budget
              Arrange =
                fun fault ->
                    async {
                        match fault with
                        | QueueStoreFault.Unavailable -> browser.Unavailable <- true
                        | QueueStoreFault.Stored text -> browser.Items[LocalStorageQueue.key chrona] <- text

                        return true
                    } }
    }

[<Fact>]
let ``the suite covers what WI-0019 names`` () =
    let names = QueueStoreConformance.cases |> List.map (fun (name, _, _) -> name) |> Set.ofList

    for required in
        [ "absent queue loads nothing"
          "round-trip keeps order, sequences and states"
          "over budget is QuotaExceeded, nothing truncated"
          "another namespace's queue is Corrupt"
          "an unreadable queue is Corrupt"
          "unavailable storage is Unavailable" ] do
        Assert.Contains(required, names)

[<Fact>]
let ``the round-trip samples hold every entry state and both policies`` () =
    let samples = QueueStoreConformance.samples chrona
    let states = samples |> List.collect _.Entries |> List.map _.State

    let kinds =
        states
        |> List.map (function
            | EntryState.Pending -> "pending"
            | EntryState.InFlight _ -> "in-flight"
            | EntryState.Synchronized _ -> "synchronized"
            | EntryState.Conflicted _ -> "conflicted"
            | EntryState.OutcomeUnknown _ -> "outcome-unknown"
            | EntryState.Refused _ -> "refused"
            | EntryState.Abandoned _ -> "abandoned")
        |> Set.ofList

    Assert.Equal(7, kinds.Count)
    Assert.Equal(2, samples |> List.map _.Policy |> List.distinct |> List.length)

[<Fact>]
let ``the in-memory store conforms`` () =
    run (fun () -> QueueStoreConformance.inMemory 4096L chrona) |> assertConforms

[<Fact>]
let ``the localStorage store conforms`` () = run (localStorage 4096L) |> assertConforms

[<Fact>]
let ``the localStorage store conforms at its default budget`` () =
    run (localStorage LocalStorageQueue.DefaultBudget) |> assertConforms

[<Property(MaxTest = 60)>]
let ``any queue round-trips through the in-memory store (LCP-060)`` () =
    Prop.forAll (Arb.fromGen OfflineQueueTests.queueGen) (fun queue ->
        let subject = QueueStoreConformance.inMemory LocalStorageQueue.DefaultBudget chrona |> Async.RunSynchronously
        QueueStoreConformance.roundTrip subject queue |> Async.RunSynchronously = ConformanceOutcome.Passed)

[<Property(MaxTest = 60)>]
let ``any queue round-trips through the localStorage store (LCP-060)`` () =
    Prop.forAll (Arb.fromGen OfflineQueueTests.queueGen) (fun queue ->
        let subject = localStorage LocalStorageQueue.DefaultBudget () |> Async.RunSynchronously
        QueueStoreConformance.roundTrip subject queue |> Async.RunSynchronously = ConformanceOutcome.Passed)

[<Fact>]
let ``a fault the harness cannot arrange is Unsupported, never passed`` () =
    let fresh () =
        async {
            let! subject = QueueStoreConformance.inMemory 4096L chrona
            return { subject with Arrange = fun _ -> async { return false } }
        }

    let results = run fresh

    let unsupported =
        results
        |> List.filter (fun result ->
            match result.Outcome with
            | ConformanceOutcome.Unsupported _ -> true
            | _ -> false)
        |> List.map _.Case

    Assert.Equal<string list>(
        [ "another namespace's queue is Corrupt"; "an unreadable queue is Corrupt"; "unavailable storage is Unavailable" ],
        unsupported
    )

/// A store that keeps only the newest entry: the loss the suite must catch.
let private truncating () =
    async {
        let! subject = QueueStoreConformance.inMemory 4096L chrona
        let inner = subject.Store

        let store: QueueStore =
            { Load = inner.Load
              Save = fun queue -> inner.Save { queue with Entries = queue.Entries |> List.truncate 1 } }

        return { subject with Store = store }
    }

/// A store that saves whatever it is given, past the budget.
let private unbounded () =
    async {
        let cell = MemoryQueueStore.cell ()
        let! subject = QueueStoreConformance.inMemory 4096L chrona
        let store = MemoryQueueStore.over cell Int64.MaxValue chrona

        return
            { subject with
                Store = store
                Reopen = fun () -> async { return store } }
    }

[<Fact>]
let ``a store that drops entries fails the suite`` () =
    let failed =
        run truncating
        |> List.filter (fun result ->
            match result.Outcome with
            | ConformanceOutcome.Failed _ -> true
            | _ -> false)
        |> List.map _.Case

    Assert.Contains("round-trip keeps order, sequences and states", failed)
    // Truncation also hides the over-budget entry, so that save "succeeds".
    Assert.Contains("over budget is QuotaExceeded, nothing truncated", failed)

[<Fact>]
let ``a store that ignores its budget fails the suite`` () =
    let results = run unbounded

    let overBudget =
        results |> List.find (fun result -> result.Case = "over budget is QuotaExceeded, nothing truncated")

    match overBudget.Outcome with
    | ConformanceOutcome.Failed _ -> ()
    | other -> failwith $"expected Failed, got {other}"
