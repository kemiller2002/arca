/// The GitHub adapter (ARCA-API-001..005, ARCA-OUT-001..002,
/// ARCA-COMMIT-001, ARCA-COMMIT-006, ARCA-CON-001..002 against GitHub).
module Arca.Tests.GitHubStorageTests

open System
open Arca
open Arca.GitHub
open Arca.Tests.FakeGitHub
open Xunit

let private ok result =
    match result with
    | Ok value -> value
    | Error error -> failwith $"expected Ok, got {error}"

let private path text = RelativePath.parse text |> ok

let private location = DataLocation.create "acme" "data" "main" "apps" |> ok

let private chrona =
    Namespace.ofApplication
        { Application = AppId.create "chrona" |> ok
          Environment = { Kind = EnvironmentKind.Production; Name = "production" }
          Location = location }
    |> ok

let private metadata key =
    { Summary = "record time"
      Actor = { Kind = ActorKind.Human; Id = ActorId.create "u-1" |> ok }
      ProviderIdentity = Some "octocat"
      ExecutionId = None
      CorrelationId = CorrelationId.create "req-1" |> ok
      IdempotencyKey = IdempotencyKey.create key |> ok }

let private operation key changes =
    Operation.create chrona (metadata key) changes |> ok

let private session () = Session.create (GitHubConfig.create location)

/// Runs a step against the server; returns the result, the next session and the waits.
let private runOp (server: Server) (current: GitHubSession) (step: Op<'a>) =
    let (result, next), waits =
        Conversation.simulate server.Send server.ValidToken [] (step current)

    result, next, waits

let private posts (server: Server) =
    server.Requests |> List.filter (fun sent -> sent.Request.Method <> HttpMethod.Get)

[<Fact>]
let ``one operation becomes one commit carrying every record and the trailers (ARCA-COMMIT-001)`` () =
    let server = Server("acme", "data")
    let before = server.Head "main"

    let receipt, _, _ =
        runOp server (session ()) (GitHubStorage.commit (operation "op-00000001" [ Change.Create(path "records/a.json", "{\"a\":1}"); Change.Create(path "records/b.json", "{\"b\":2}") ]))

    let receipt = receipt |> ok
    let after = server.Head "main"
    Assert.Equal(ChangeToken after, receipt.ChangeToken)
    Assert.Equal<string list>([ before ], (server.Commit after).Parents)
    Assert.StartsWith("chrona: record time\n\nArca-Format: 1", (server.Commit after).Message)

    let files = server.Files "main"
    Assert.Equal("{\"a\":1}", files["apps/chrona/records/a.json"])
    Assert.Equal("{\"b\":2}", files["apps/chrona/records/b.json"])
    Assert.Equal(Some(Revision(blobSha "{\"a\":1}")), receipt.Revisions["records/a.json"])

[<Fact>]
let ``reads return content and GitHub's own revision; absent objects are Absent (ARCA-API-001)`` () =
    let server = Server("acme", "data")
    server.CommitDirectly("main", [ "apps/chrona/records/a.json", Some "{\"a\":1}" ], "seed") |> ignore

    let found, current, _ = runOp server (session ()) (GitHubStorage.read chrona (path "records/a.json"))

    match found |> ok with
    | ReadOutcome.Found stored ->
        Assert.Equal("{\"a\":1}", stored.Content)
        Assert.Equal(Revision(blobSha "{\"a\":1}"), stored.Revision)
    | ReadOutcome.Absent -> failwith "expected the record"

    let missing, _, _ = runOp server current (GitHubStorage.read chrona (path "records/none.json"))
    Assert.Equal(Ok ReadOutcome.Absent, missing)

[<Fact>]
let ``an oversized object is a typed ObjectTooLarge, never truncated (ARCA-API-004)`` () =
    let server = Server("acme", "data")
    server.CommitDirectly("main", [ "apps/chrona/records/big.json", Some(String('x', 1048577)) ], "big") |> ignore
    let result, _, _ = runOp server (session ()) (GitHubStorage.read chrona (path "records/big.json"))
    Assert.Equal(Error(StorageFailure.ObjectTooLarge("apps/chrona/records/big.json", 1048577L, 1048576L)), result)

[<Fact>]
let ``listings name files and folders; a listing at GitHub's limit is partial (ARCA-API-004)`` () =
    let server = Server("acme", "data")

    server.CommitDirectly(
        "main",
        [ "apps/chrona/records/a.json", Some "{}"
          "apps/chrona/records/2026/b.json", Some "{}"
          "apps/summa/x.json", Some "{}" ],
        "seed"
    )
    |> ignore

    let listing, current, _ = runOp server (session ()) (GitHubStorage.list chrona (path "records"))
    let listing = listing |> ok
    Assert.True(listing.Complete)
    Assert.Equal<string list>([ "records/2026"; "records/a.json" ], listing.Entries |> List.map (_.Path >> RelativePath.render) |> List.sort)
    Assert.Equal<bool list>([ true; false ], listing.Entries |> List.sortBy (_.Path >> RelativePath.render) |> List.map _.IsFolder)

    let many =
        Json.Array [ for i in 1..1000 -> Json.objectOf [ "name", Json.String $"r{i}.json"; "sha", Json.String "s"; "type", Json.String "file"; "size", Json.Number 2m ] ]

    server.Inject(request HttpMethod.Get "/contents/apps/chrona/records?", Answer(200, [], Json.canonicalText many))
    // A fresh session: the first listing is cached immutably by commit.
    ignore current
    let partial, _, _ = runOp server (session ()) (GitHubStorage.list chrona (path "records"))
    let partial = partial |> ok
    Assert.False(partial.Complete)
    Assert.Equal(1000, partial.Entries.Length)

[<Fact>]
let ``a stale expectation is a typed conflict and nothing is committed (ARCA-CON-001, ARCA-CON-002)`` () =
    let server = Server("acme", "data")
    server.CommitDirectly("main", [ "apps/chrona/records/a.json", Some "{\"v\":2}" ], "someone else") |> ignore
    let before = server.Head "main"

    let result, _, _ =
        runOp server (session ()) (GitHubStorage.commit (operation "op-00000002" [ Change.Update(path "records/a.json", "{\"v\":3}", Revision(blobSha "{\"v\":1}")) ]))

    Assert.Equal(
        Error(
            StorageFailure.Conflicted
                [ { Path = path "records/a.json"
                    Expected = Some(Revision(blobSha "{\"v\":1}"))
                    Actual = Some(Revision(blobSha "{\"v\":2}")) } ]
        ),
        result
    )

    Assert.Equal(before, server.Head "main")
    Assert.Empty(posts server)

[<Fact>]
let ``a concurrent commit to other records is built upon, keeping both (fast-forward race)`` () =
    let server = Server("acme", "data")

    server.Inject(
        request HttpMethod.Patch "/git/refs/heads/main",
        Before(fun () -> server.CommitDirectly("main", [ "apps/chrona/records/other.json", Some "{}" ], "concurrent") |> ignore)
    )

    let result, _, _ =
        runOp server (session ()) (GitHubStorage.commit (operation "op-00000003" [ Change.Create(path "records/mine.json", "{\"m\":1}") ]))

    Assert.True(Result.isOk result)
    let files = server.Files "main"
    Assert.True(files.ContainsKey "apps/chrona/records/other.json")
    Assert.True(files.ContainsKey "apps/chrona/records/mine.json")

[<Fact>]
let ``a concurrent commit to the same record turns the race into a conflict, never an overwrite`` () =
    let server = Server("acme", "data")

    server.Inject(
        request HttpMethod.Patch "/git/refs/heads/main",
        Before(fun () -> server.CommitDirectly("main", [ "apps/chrona/records/mine.json", Some "{\"theirs\":1}" ], "concurrent") |> ignore)
    )

    let result, _, _ =
        runOp server (session ()) (GitHubStorage.commit (operation "op-00000004" [ Change.Create(path "records/mine.json", "{\"m\":1}") ]))

    match result with
    | Error(StorageFailure.Conflicted [ conflict ]) ->
        Assert.Equal(None, conflict.Expected)
        Assert.Equal(Some(Revision(blobSha "{\"theirs\":1}")), conflict.Actual)
    | other -> failwith $"expected a conflict, got {other}"

    Assert.Equal("{\"theirs\":1}", (server.Files "main")["apps/chrona/records/mine.json"])

let private commitsWithKey (server: Server) (key: string) =
    let rec walk (id: string) acc =
        let commit = server.Commit id
        let acc = if commit.Message.Contains $"Arca-Idempotency-Key: {key}" then acc + 1 else acc

        match commit.Parents with
        | parent :: _ -> walk parent acc
        | [] -> acc

    walk (server.Head "main") 0

[<Fact>]
let ``an unknown outcome that actually landed is reconciled as landed, never resent (ARCA-OUT-001, ARCA-OUT-002)`` () =
    let server = Server("acme", "data")
    server.Inject(request HttpMethod.Patch "/git/refs/heads/main", ApplyThenLose UnknownReason.TimeoutAfterDispatch)

    let result, _, _ =
        runOp server (session ()) (GitHubStorage.commit (operation "op-00000005" [ Change.Create(path "records/a.json", "{}") ]))

    Assert.Equal(ChangeToken(server.Head "main"), (result |> ok).ChangeToken)
    Assert.Equal(1, commitsWithKey server "op-00000005")

[<Fact>]
let ``an unknown outcome that did not land is retried exactly once more`` () =
    let server = Server("acme", "data")
    server.Inject(request HttpMethod.Patch "/git/refs/heads/main", Lose UnknownReason.ConnectionLost)

    let result, _, _ =
        runOp server (session ()) (GitHubStorage.commit (operation "op-00000006" [ Change.Create(path "records/a.json", "{}") ]))

    Assert.True(Result.isOk result)
    Assert.Equal(1, commitsWithKey server "op-00000006")

[<Fact>]
let ``when GitHub cannot tell, the obligation is returned, and later reconciliation settles it`` () =
    let server = Server("acme", "data")
    server.Inject(request HttpMethod.Patch "/git/refs/heads/main", ApplyThenLose UnknownReason.TimeoutAfterDispatch)

    // After the ref update is sent, GitHub cannot be reached to inspect it.
    let afterUpdate sent =
        request HttpMethod.Get "/git/ref/heads/main" sent
        && server.Requests |> List.exists (fun earlier -> earlier.Request.Method = HttpMethod.Patch)

    for _ in 1..4 do
        server.Inject(afterUpdate, FailNetwork)

    let first = session ()

    let result, next, _ =
        runOp server first (GitHubStorage.commit (operation "op-00000007" [ Change.Create(path "records/a.json", "{}") ]))

    let pending =
        match result with
        | Error(StorageFailure.OutcomeUnknown pending) -> pending
        | other -> failwith $"expected OutcomeUnknown, got {other}"

    Assert.Equal(IdempotencyKey.create "op-00000007" |> ok, pending.IdempotencyKey)

    let settled, _, _ = runOp server next (GitHubStorage.reconcile chrona pending)

    match settled |> ok with
    | ReconcileOutcome.Landed receipt -> Assert.Equal(ChangeToken(server.Head "main"), receipt.ChangeToken)
    | other -> failwith $"expected Landed, got {other}"

[<Fact>]
let ``reconciliation without a candidate searches history for the idempotency key`` () =
    let server = Server("acme", "data")
    let baseCommit = server.Head "main"

    let result, current, _ =
        runOp server (session ()) (GitHubStorage.commit (operation "op-00000008" [ Change.Create(path "records/a.json", "{}") ]))

    Assert.True(Result.isOk result)
    server.CommitDirectly("main", [ "apps/chrona/records/later.json", Some "{}" ], "later") |> ignore

    let pending =
        { IdempotencyKey = IdempotencyKey.create "op-00000008" |> ok
          Base = ChangeToken baseCommit
          Candidate = None
          Revisions = Map.empty }

    match runOp server current (GitHubStorage.reconcile chrona pending) with
    | Ok(ReconcileOutcome.Landed _), _, _ -> ()
    | other -> failwith $"expected Landed, got {other}"

    let unknownKey = { pending with IdempotencyKey = IdempotencyKey.create "op-never-sent" |> ok }

    match runOp server current (GitHubStorage.reconcile chrona unknownKey) with
    | Ok ReconcileOutcome.NotLanded, _, _ -> ()
    | other -> failwith $"expected NotLanded, got {other}"

[<Fact>]
let ``a protected branch refusal at the ref update is typed and clears the snapshot (ARCA-COMMIT-006)`` () =
    let server = Server("acme", "data")
    server.ProtectedRefUpdate <- true

    let result, next, _ =
        runOp server (session ()) (GitHubStorage.commit (operation "op-00000009" [ Change.Create(path "records/a.json", "{}") ]))

    Assert.Equal(Error(StorageFailure.Refused WriteRefusal.BranchProtected), result)
    Assert.Equal(None, next.Snapshot)

[<Fact>]
let ``a write the snapshot says GitHub would refuse sends no mutation (ARCA-COMMIT-006)`` () =
    let server = Server("acme", "data")
    server.Archived <- true

    let result, _, _ =
        runOp server (session ()) (GitHubStorage.commit (operation "op-00000010" [ Change.Create(path "records/a.json", "{}") ]))

    Assert.Equal(Error(StorageFailure.Refused WriteRefusal.RepositoryArchived), result)
    Assert.Empty(posts server)

[<Fact>]
let ``a failed credential mutates nothing (ARCA-AUTH-004)`` () =
    let server = Server("acme", "data")

    let (result, _), _ =
        Conversation.simulate server.Send (fun () -> Error TokenUnavailable.Revoked) [] (GitHubStorage.commit (operation "op-00000011" [ Change.Create(path "records/a.json", "{}") ]) (session ()))

    Assert.True(Result.isError result)
    Assert.Empty(server.Requests)

[<Fact>]
let ``a short rate limit is waited out with GitHub's retry-after; a long one is typed with its evidence (ARCA-API-003)`` () =
    let server = Server("acme", "data")
    server.Inject(request HttpMethod.Get "/git/ref/heads/main", Answer(403, [ "retry-after", "2"; "x-ratelimit-remaining", "0" ], "{}"))
    let result, next, waits = runOp server (session ()) (GitHubStorage.changeToken chrona)
    Assert.True(Result.isOk result)
    Assert.Equal<TimeSpan list>([ TimeSpan.FromSeconds 2. ], waits)
    Assert.Equal(Some 4999, next.Budget |> Option.bind _.Remaining)

    server.Inject(request HttpMethod.Get "/git/ref/heads/main", Answer(403, [ "x-ratelimit-remaining", "0"; "x-ratelimit-reset", "1791460000" ], "{}"))
    let limited, _, noWaits = runOp server (session ()) (GitHubStorage.changeToken chrona)
    Assert.Equal(Error(StorageFailure.RateLimited(None, Some 1791460000L)), limited)
    Assert.Empty(noWaits)

[<Fact>]
let ``server errors and network failures on safe requests back off exponentially with jitter, then give up`` () =
    let server = Server("acme", "data")

    for _ in 1..4 do
        server.Inject(request HttpMethod.Get "/git/ref/heads/main", Answer(503, [], "{}"))

    let result, _, waits = runOp server (session ()) (GitHubStorage.changeToken chrona)
    Assert.True(Result.isError result)
    Assert.Equal(3, waits.Length)

    let policy = Session.standardPolicy

    waits
    |> List.iteri (fun index wait ->
        let ceiling = policy.BaseDelay.TotalMilliseconds * Math.Pow(2., float index)
        Assert.InRange(wait.TotalMilliseconds, ceiling / 2., ceiling))

    let _, _, again = runOp (Server("acme", "data") |> fun s -> (for _ in 1..4 do s.Inject(request HttpMethod.Get "/git/ref/heads/main", Answer(503, [], "{}"))); s) (session ()) (GitHubStorage.changeToken chrona)
    Assert.Equal<TimeSpan list>(waits, again)

[<Fact>]
let ``immutable reads are cached by commit and mutable ones revalidated with ETags (ARCA-API-002)`` () =
    let server = Server("acme", "data")
    server.CommitDirectly("main", [ "apps/chrona/records/a.json", Some "{}" ], "seed") |> ignore
    let _, warm, _ = runOp server (session ()) (GitHubStorage.read chrona (path "records/a.json"))
    let before = server.Requests.Length
    let again, _, _ = runOp server warm (GitHubStorage.read chrona (path "records/a.json"))
    Assert.True(Result.isOk again)

    let second = server.Requests |> List.skip before
    Assert.Equal(1, second.Length)
    Assert.Contains("/git/ref/heads/main", second.Head.Request.Url)
    Assert.Contains(second.Head.Request.Headers, fun (name, _) -> name = "If-None-Match")

[<Fact>]
let ``a namespace at another location is refused, not silently redirected`` () =
    let server = Server("acme", "data")

    let elsewhere =
        { chrona with
            Location = DataLocation.create "acme" "other" "main" "apps" |> ok }

    match runOp server (session ()) (GitHubStorage.read elsewhere (path "records/a.json")) with
    | Error(StorageFailure.WrongLocation _), _, _ -> ()
    | other -> failwith $"expected WrongLocation, got {other}"

[<Fact>]
let ``growth is measured by walking folders within a budget (ARCA-API-005)`` () =
    let server = Server("acme", "data")

    server.CommitDirectly(
        "main",
        [ "apps/chrona/records/a.json", Some "1234"
          "apps/chrona/records/2026/b.json", Some "12"
          "apps/chrona/records/2026/10/c.json", Some "1" ],
        "seed"
    )
    |> ignore

    let measured, current, _ = runOp server (session ()) (GitHubStorage.measure chrona (path "records") 10)
    Assert.Equal(Ok { Objects = 3; Bytes = 7L; Folders = 3; Complete = true }, measured)

    let bounded, _, _ = runOp server current (GitHubStorage.measure chrona (path "records") 1)
    Assert.Equal(Ok { Objects = 1; Bytes = 4L; Folders = 1; Complete = false }, bounded)

[<Fact>]
let ``the provider-neutral interface runs the adapter through an async host (ARCA-ARCH-003)`` () =
    let server = Server("acme", "data")

    let host =
        { Send = fun sent -> async { return server.Send sent }
          Wait = fun _ -> async { return () }
          Tokens = fun () -> async { return server.ValidToken() } }

    let provider = GitHubStorage.provider host (GitHubConfig.create location)

    let receipt =
        provider.Commit (operation "op-00000012" [ Change.Create(path "records/a.json", "{\"x\":1}") ])
        |> Async.StartAsTask

    Assert.True(Result.isOk receipt.Result)

    let read = provider.Read chrona (path "records/a.json") |> Async.StartAsTask

    match read.Result with
    | Ok(ReadOutcome.Found stored) -> Assert.Equal("{\"x\":1}", stored.Content)
    | other -> failwith $"expected the record, got {other}"
