/// The provider conformance suite, run against every provider, and the
/// in-memory provider for consumers (ARCA-TEST-001, ARCA-TEST-004).
module Arca.Tests.ConformanceTests

open System
open Arca
open Arca.GitHub
open Arca.Tests.FakeGitHub
open Xunit

let private ok result =
    match result with
    | Ok value -> value
    | Error error -> failwith $"expected Ok, got {error}"

let private location = DataLocation.create "acme" "data" "main" "apps" |> ok

let private chrona =
    Namespace.ofApplication
        { Application = AppId.create "chrona" |> ok
          Environment = { Kind = EnvironmentKind.Test; Name = "conformance" }
          Location = location }
    |> ok

let private assertConforms (results: ConformanceResult list) =
    Assert.Equal(Conformance.cases.Length, results.Length)

    let problems =
        results
        |> List.choose (fun result ->
            match result.Outcome with
            | ConformanceOutcome.Passed -> None
            | ConformanceOutcome.Failed reason -> Some $"{result.Case} ({result.Requirement}) failed: {reason}"
            | ConformanceOutcome.Unsupported reason -> Some $"{result.Case} ({result.Requirement}) unsupported: {reason}")

    Assert.True(problems.IsEmpty, String.concat "\n" problems)

[<Fact>]
let ``the suite covers every case ARCA-TEST-001 names`` () =
    let names = Conformance.cases |> List.map (fun (name, _, _) -> name) |> Set.ofList

    for required in
        [ "round-trip"
          "conditional write"
          "compare-and-swap"
          "concurrent modification"
          "idempotent repeat"
          "OutcomeUnknown that landed"
          "OutcomeUnknown that was lost"
          "missing object"
          "corrupt object"
          "unsupported capability"
          "pagination"
          "rate limit"
          "oversized object"
          "authentication failure"
          "read-only mode"
          "stale change token"
          "immutable record protected"
          "corrupt record not overwritten"
          "external edit detected" ] do
        Assert.Contains(required, names)

[<Fact>]
let ``the in-memory provider conforms (ARCA-TEST-001)`` () =
    Conformance.run (fun () -> Conformance.inMemory chrona)
    |> Async.RunSynchronously
    |> assertConforms

/// The GitHub adapter over the simulated GitHub, with every fault arranged
/// the way GitHub itself produces it.
let private githubSubject () =
    async {
        let server = Server("acme", "data")
        let revoked = ref false

        let host =
            { Send = fun request -> async { return server.Send request }
              Wait = fun _ -> async { return () }
              Tokens =
                fun () ->
                    async {
                        if revoked.Value then return Error TokenUnavailable.Revoked else return server.ValidToken()
                    } }

        let config = ref { GitHubConfig.create location with ListingLimit = 2 }
        let provider = ref (GitHubStorage.provider host config.Value)

        let rebuild change =
            config.Value <- change config.Value
            provider.Value <- GitHubStorage.provider host config.Value

        let afterRefUpdate sent =
            request HttpMethod.Get "/git/ref/heads/main" sent
            && server.Requests |> List.exists (fun earlier -> earlier.Request.Method = HttpMethod.Patch)

        let unreachableAfterUpdate () =
            for _ in 1..4 do
                server.Inject(afterRefUpdate, FailNetwork)

        return
            { Provider =
                { Capabilities = Provider.capabilities
                  ChangeToken = fun ns -> provider.Value.ChangeToken ns
                  Read = fun ns path -> provider.Value.Read ns path
                  List = fun ns prefix -> provider.Value.List ns prefix
                  Commit = fun operation -> provider.Value.Commit operation
                  Reconcile = fun ns pending -> provider.Value.Reconcile ns pending
                  History = fun ns path -> provider.Value.History ns path }
              Namespace = chrona
              WriteExternally =
                fun relative content ->
                    async {
                        let address = Namespace.resolve chrona relative |> ok
                        server.CommitDirectly("main", [ address.Path, content ], "edited on github.com") |> ignore
                    }
              Arrange =
                fun fault ->
                    async {
                        match fault with
                        | ConformanceFault.OutcomeUnknownLanded ->
                            server.Inject(request HttpMethod.Patch "/git/refs/heads/main", ApplyThenLose UnknownReason.TimeoutAfterDispatch)
                            unreachableAfterUpdate ()
                        | ConformanceFault.OutcomeUnknownLost ->
                            server.Inject(request HttpMethod.Patch "/git/refs/heads/main", Lose UnknownReason.ConnectionLost)
                            unreachableAfterUpdate ()
                        | ConformanceFault.RateLimited ->
                            server.Inject((fun _ -> true), Answer(403, [ "x-ratelimit-remaining", "0"; "x-ratelimit-reset", "1791460000" ], "{}"))
                        | ConformanceFault.CredentialRevoked -> revoked.Value <- true
                        | ConformanceFault.ReadOnly -> server.CanPush <- false
                        | ConformanceFault.ListingLimit entries ->
                            server.ListingLimit <- Some entries
                            rebuild (fun current -> { current with ListingLimit = entries })
                        | ConformanceFault.MaxObjectBytes bytes -> rebuild (fun current -> { current with MaxObjectBytes = bytes })

                        return true
                    } }
    }

[<Fact>]
let ``the GitHub adapter conforms (ARCA-TEST-001)`` () =
    Conformance.run githubSubject |> Async.RunSynchronously |> assertConforms

[<Fact>]
let ``a harness that cannot arrange a fault is reported Unsupported, never Passed`` () =
    let limited () =
        async {
            let! subject = Conformance.inMemory chrona
            return { subject with Arrange = fun _ -> async { return false } }
        }

    let results = Conformance.run limited |> Async.RunSynchronously

    let unsupported =
        results
        |> List.filter (fun result ->
            match result.Outcome with
            | ConformanceOutcome.Unsupported _ -> true
            | _ -> false)
        |> List.map _.Case

    Assert.Contains("rate limit", unsupported)
    Assert.Contains("OutcomeUnknown that landed", unsupported)
    Assert.DoesNotContain("round-trip", unsupported)

[<Fact>]
let ``in-memory revisions are content hashes, so equal content has an equal revision`` () =
    Assert.Equal(InMemory.revision "{}", InMemory.revision "{}")
    Assert.NotEqual(InMemory.revision "{}", InMemory.revision "{ }")

[<Fact>]
let ``the in-memory store lists files and folders under a prefix`` () =
    let store = InMemoryStore()

    let operation =
        Operation.create
            chrona
            { Summary = "seed"
              Actor = { Kind = ActorKind.Service; Id = ActorId.create "test" |> ok }
              ProviderIdentity = None
              ExecutionId = None
              CorrelationId = CorrelationId.create "c" |> ok
              IdempotencyKey = IdempotencyKey.create "seed-0001" |> ok }
            [ Change.Create(RelativePath.parse "records/a.json" |> ok, "{}")
              Change.Create(RelativePath.parse "records/2026/b.json" |> ok, "{}") ]
        |> ok

    store.Provider.Commit operation |> Async.RunSynchronously |> ok |> ignore

    let listing =
        store.Provider.List chrona (RelativePath.parse "records" |> ok) |> Async.RunSynchronously |> ok

    Assert.True(listing.Complete)

    Assert.Equal<(string * bool) list>(
        [ "records/2026", true; "records/a.json", false ],
        listing.Entries |> List.map (fun entry -> RelativePath.render entry.Path, entry.IsFolder) |> List.sort
    )

[<Fact>]
let ``faults and failures carry identifiers and measures only, never tokens or business content (ARCA-TEST-004)`` () =
    let server = Server("acme", "data")
    let secretBody = """{"message":"Internal error","note":"Customer ACME owes 4,200"}"""
    server.Inject((fun _ -> true), Answer(500, [], secretBody))

    let session = Session.create (GitHubConfig.create location)

    let (result, _), _ =
        Conversation.simulate server.Send server.ValidToken [] (GitHubStorage.changeToken chrona session)

    let rendered = sprintf "%A" result
    Assert.DoesNotContain("ACME owes", rendered)
    Assert.DoesNotContain(server.Token, rendered)

    let failure =
        Faults.ofOutcome "acme/data" (HttpOutcome.Response(500, Map.empty, secretBody)) |> Option.get

    let fault =
        Faults.toFault (Aegis.FaultId "F-1", Aegis.CorrelationId "C-1", DateTimeOffset.UnixEpoch) ("chrona", None) "arca.changeToken" Map.empty failure

    let technical = fault.TechnicalDetails |> Option.defaultValue ""
    Assert.DoesNotContain("ACME owes", technical + fault.UserMessage)
    Assert.DoesNotContain(server.Token, technical + fault.UserMessage)
