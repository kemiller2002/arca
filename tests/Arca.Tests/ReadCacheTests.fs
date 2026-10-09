/// The read-cache port (WI-0021; Limen LCP-082..LCP-086): the conformance
/// suite over the in-memory cache, the freshness rules, the Cached/Fresh
/// distinction (with a compile-failure fixture) and the sign-out policy.
module Arca.Tests.ReadCacheTests

open System
open System.Diagnostics
open System.IO
open Arca
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
          Environment = { Kind = EnvironmentKind.Test; Name = "cache" }
          Location = DataLocation.create "acme" "data" "main" "apps" |> ok }
    |> ok

let private at = DateTimeOffset(2026, 10, 8, 12, 0, 0, TimeSpan.Zero)

let private problems (results: ConformanceResult list) =
    results
    |> List.choose (fun result ->
        match result.Outcome with
        | ConformanceOutcome.Passed -> None
        | ConformanceOutcome.Failed reason -> Some $"{result.Case} ({result.Requirement}) failed: {reason}"
        | ConformanceOutcome.Unsupported reason -> Some $"{result.Case} ({result.Requirement}) unsupported: {reason}")

[<Fact>]
let ``the in-memory read cache conforms (LCP-082)`` () =
    let results = ReadCacheConformance.run (fun () -> ReadCacheConformance.inMemory chrona) |> Async.RunSynchronously
    Assert.Equal(ReadCacheConformance.cases.Length, results.Length)
    let found = problems results
    Assert.True(found.IsEmpty, String.concat "\n" found)

[<Fact>]
let ``a fault the harness cannot arrange is Unsupported, never passed`` () =
    let fresh () =
        async {
            let! subject = ReadCacheConformance.inMemory chrona
            return { subject with Arrange = fun _ -> async { return false } }
        }

    let unsupported =
        ReadCacheConformance.run fresh
        |> Async.RunSynchronously
        |> List.filter (fun result ->
            match result.Outcome with
            | ConformanceOutcome.Unsupported _ -> true
            | _ -> false)

    Assert.Equal(4, unsupported.Length)

[<Fact>]
let ``a cache that keeps entries across accounts fails the suite`` () =
    let fresh () =
        async {
            let! subject = ReadCacheConformance.inMemory chrona
            let inner = subject.Store
            // Ignores the account: every account sees one shared entry.
            let shared (key: CacheKey) = { key with Account = "shared" }

            let leaky: ReadCacheStore =
                { inner with
                    Load = fun key -> async {
                        match! inner.Load(shared key) with
                        | Ok(Some entry) -> return Ok(Some { entry with Key = key })
                        | other -> return other }
                    Save = fun entry -> inner.Save { entry with Key = shared entry.Key } }

            return { subject with Store = leaky }
        }

    let failed =
        ReadCacheConformance.run fresh
        |> Async.RunSynchronously
        |> List.filter (fun result ->
            match result.Outcome with
            | ConformanceOutcome.Failed _ -> true
            | _ -> false)
        |> List.map _.Case

    Assert.Contains("accounts are isolated", failed)

let private entry token =
    ReadCacheConformance.sample chrona "alice" "2026-10" token

let private cachedOf token = ReadCache.cached (entry token) |> ok

let private state repository ns =
    { RepositoryToken = ChangeToken repository
      NamespaceToken = NamespaceToken ns }

let private repositoryWide token =
    ReadCacheConformance.sampleRepositoryWide chrona "alice" "2026-10" token

let private cachedRepositoryWide token = ReadCache.cached (repositoryWide token) |> ok

[<Fact>]
let ``an equal namespace token confirms, and the fresh value carries the provider's current tokens (LCP-084, ARCA-CON-005)`` () =
    match ReadCache.revalidate (ProviderObservation.Current(state "r-1" "t-1")) (cachedOf "t-1") with
    | Revalidation.Confirmed fresh ->
        Assert.Equal(ChangeToken "r-1", Fresh.token fresh)
        Assert.Equal(Some(NamespaceToken "t-1"), Fresh.namespaceToken fresh)
        Assert.Equal(entry "t-1", Fresh.value fresh)
    | other -> failwith $"expected Confirmed, got {other}"

[<Fact>]
let ``another application's commit moves only the repository token, and the entry stays current at it (ARCA-CON-005)`` () =
    // The entry was read at repository-t-1; the repository has moved on, the namespace has not.
    match ReadCache.revalidate (ProviderObservation.Current(state "r-7" "t-1")) (cachedOf "t-1") with
    | Revalidation.Confirmed fresh ->
        Assert.Equal(ChangeToken "r-7", Fresh.token fresh)
        Assert.Equal(Some(NamespaceToken "t-1"), Fresh.namespaceToken fresh)
    | other -> failwith $"expected Confirmed, got {other}"

[<Fact>]
let ``a different namespace token asks for a refresh and is shown stale, never current (LCP-084)`` () =
    match ReadCache.revalidate (ProviderObservation.Current(state "repository-t-1" "t-2")) (cachedOf "t-1") with
    | Revalidation.Refresh cached ->
        Assert.True(Cached.isStale cached)
        Assert.Equal("t-1", CachedToken.text (Cached.asOf cached))
        Assert.Equal(TokenScope.Namespace, Cached.scope cached)
    | other -> failwith $"expected Refresh, got {other}"

[<Fact>]
let ``the repository-wide fallback confirms only an equal repository token (ARCA-CON-005)`` () =
    match ReadCache.revalidate (ProviderObservation.CurrentRepository(ChangeToken "t-1")) (cachedRepositoryWide "t-1") with
    | Revalidation.Confirmed fresh ->
        Assert.Equal(ChangeToken "t-1", Fresh.token fresh)
        Assert.Equal(None, Fresh.namespaceToken fresh)
    | other -> failwith $"expected Confirmed, got {other}"

    match ReadCache.revalidate (ProviderObservation.CurrentRepository(ChangeToken "t-2")) (cachedRepositoryWide "t-1") with
    | Revalidation.Refresh cached -> Assert.True(Cached.isStale cached)
    | other -> failwith $"expected Refresh, got {other}"

[<Fact>]
let ``a repository-scoped entry is compared with the repository token, even in a namespace observation (ARCA-CON-005)`` () =
    match ReadCache.revalidate (ProviderObservation.Current(state "t-1" "n-1")) (cachedRepositoryWide "t-1") with
    | Revalidation.Confirmed fresh -> Assert.Equal(Some(NamespaceToken "n-1"), Fresh.namespaceToken fresh)
    | other -> failwith $"expected Confirmed, got {other}"

    // Its token is a repository token: an equal namespace token says nothing about it.
    match ReadCache.revalidate (ProviderObservation.Current(state "t-2" "t-1")) (cachedRepositoryWide "t-1") with
    | Revalidation.Refresh _ -> ()
    | other -> failwith $"expected Refresh, got {other}"

[<Fact>]
let ``a namespace-scoped entry is never confirmed by a repository token alone (ARCA-CON-005)`` () =
    match ReadCache.revalidate (ProviderObservation.CurrentRepository(ChangeToken "t-1")) (cachedOf "t-1") with
    | Revalidation.Refresh cached -> Assert.True(Cached.isStale cached)
    | other -> failwith $"expected Refresh, got {other}"

[<Fact>]
let ``a partition the provider no longer has is removed (LCP-084)`` () =
    let cached = cachedOf "t-1"
    Assert.Equal(Revalidation.Remove(Cached.key cached), ReadCache.revalidate ProviderObservation.PartitionGone cached)

[<Fact>]
let ``an unreachable provider leaves the entry shown as of its token (LCP-084)`` () =
    match ReadCache.revalidate ProviderObservation.Unreachable (cachedOf "t-1") with
    | Revalidation.Unverified cached ->
        Assert.False(Cached.isStale cached)
        Assert.Equal(at, Cached.readAt cached)
    | other -> failwith $"expected Unverified, got {other}"

[<Fact>]
let ``a failed refresh keeps a visible stale marker (LCP-084)`` () =
    Assert.True(cachedOf "t-1" |> Cached.stale |> Cached.isStale)

let private tokens = [ "t-1"; "t-2"; "t-3" ]

let private observationGen =
    Gen.oneof
        [ Gen.zip (Gen.elements tokens) (Gen.elements tokens) |> Gen.map (fun (r, n) -> ProviderObservation.Current(state r n))
          Gen.elements tokens |> Gen.map (ChangeToken >> ProviderObservation.CurrentRepository)
          Gen.constant ProviderObservation.PartitionGone
          Gen.constant ProviderObservation.Unreachable ]

/// Whether an observation names exactly the state a cached token of `scope` reflects.
let private matches (scope: TokenScope) (stored: string) (observation: ProviderObservation) =
    match observation, scope with
    | ProviderObservation.Current current, TokenScope.Namespace -> current.NamespaceToken = NamespaceToken stored
    | ProviderObservation.Current current, TokenScope.Repository -> current.RepositoryToken = ChangeToken stored
    | ProviderObservation.CurrentRepository token, TokenScope.Repository -> token = ChangeToken stored
    | _ -> false

[<Property>]
let ``no cache operation yields a Fresh value without a matching provider token of the entry's scope (LCP-085, ARCA-CON-005)`` () =
    let gen =
        Gen.zip3 (Gen.elements [ "t-1"; "t-2" ]) (Gen.elements [ TokenScope.Namespace; TokenScope.Repository ]) (Gen.listOf observationGen)

    Prop.forAll (Arb.fromGen gen) (fun (stored, scope, observations) ->
        let start =
            match scope with
            | TokenScope.Namespace -> cachedOf stored
            | TokenScope.Repository -> cachedRepositoryWide stored

        // Any sequence of revalidations; a refresh failure marks it stale.
        let outcomes =
            observations
            |> List.scan
                (fun (cached, _) observation ->
                    match ReadCache.revalidate observation cached with
                    | Revalidation.Refresh next -> next, Some(observation, false)
                    | Revalidation.Confirmed _ -> cached, Some(observation, true)
                    | Revalidation.Remove _
                    | Revalidation.Unverified _ -> cached, Some(observation, false))
                (start, None)
            |> List.choose snd

        outcomes
        |> List.forall (fun (observation, confirmed) -> confirmed = matches scope stored observation))

[<Fact>]
let ``an entry records the token and its scope, every content hash, the schema version and the read time (LCP-083, ARCA-CON-005)`` () =
    let objects =
        [ { Path = RelativePath.parse "records/a.json" |> ok
            Content = "{\"a\":1}"
            Revision = Revision "r" } ]

    let key = ReadCache.key "alice" chrona "index/activities" |> ok
    let made = ReadCache.entry key 3 at (Fresh.read (state "r-9" "t-9") objects)
    Assert.Equal("t-9", made.ChangeToken)
    Assert.Equal(TokenScope.Namespace, made.Scope)
    Assert.Equal(3, made.SchemaVersion)
    Assert.Equal(at, made.ReadAt)
    Assert.Equal<CachedRecord list>([ { Path = "records/a.json"; Content = "{\"a\":1}"; ContentHash = ReadCache.hash "{\"a\":1}" } ], made.Records)
    Assert.Equal(Ok made, ReadCache.encode made |> Result.bind ReadCache.decode)

    let fallback = ReadCache.entry key 3 at (Fresh.readRepositoryWide (ChangeToken "r-9") objects)
    Assert.Equal("r-9", fallback.ChangeToken)
    Assert.Equal(TokenScope.Repository, fallback.Scope)
    Assert.Equal(Ok fallback, ReadCache.encode fallback |> Result.bind ReadCache.decode)

[<Fact>]
let ``an unknown token scope is Corrupt`` () =
    let text =
        match ReadCache.encode (entry "t-1") |> Result.map Json.parse with
        | Ok(Ok(Json.Object members)) ->
            members
            |> List.map (fun (name, value) -> if name = "tokenScope" then name, Json.String "galaxy" else name, value)
            |> Json.objectOf
            |> Json.canonicalText
        | other -> failwith $"the entry did not encode: {other}"

    match ReadCache.decode text with
    | Error(ReadCacheFailure.Corrupt _) -> ()
    | other -> failwith $"expected Corrupt, got {other}"

// ---------------------------------------------------------------------------
// End to end over the in-memory provider: a shared repository (ARCA-CON-005)
// ---------------------------------------------------------------------------

let private signal =
    Namespace.ofApplication
        { Application = AppId.create "signal" |> ok
          Environment = { Kind = EnvironmentKind.Test; Name = "cache" }
          Location = chrona.Location }
    |> ok

let private metadata key =
    { Summary = "cache test"
      Actor = { Kind = ActorKind.Service; Id = ActorId.create "arca/tests" |> ok }
      ProviderIdentity = None
      ExecutionId = None
      CorrelationId = CorrelationId.create "cache-test" |> ok
      IdempotencyKey = IdempotencyKey.create key |> ok }

let private create ns key (path: string) content =
    Operation.create ns (metadata key) [ Change.Create(RelativePath.parse path |> ok, content) ] |> ok

[<Fact>]
let ``another application's commit leaves Chrona's cache current; the repository-wide fallback would refresh it (ARCA-CON-005)`` () =
    let store = InMemoryStore()
    let run work = work |> Async.RunSynchronously |> ok
    let note = "{\"note\":1}"
    run (store.Provider.Commit(create chrona "chrona-write-1" "notes/a.json" note)) |> ignore

    // Chrona reads its partition and caches it, namespace-scoped.
    let before = run (store.Provider.NamespaceState chrona)
    let stored = run (store.Provider.Read chrona (RelativePath.parse "notes/a.json" |> ok))

    let objects =
        match stored with
        | ReadOutcome.Found found -> [ found ]
        | ReadOutcome.Absent
        | ReadOutcome.Erased _ -> failwith "the note is absent"

    let key = ReadCache.key "alice" chrona "notes" |> ok
    let cached = ReadCache.cached (ReadCache.entry key 1 at (Fresh.read before objects)) |> ok

    // Signal commits to its own namespace in the same repository.
    run (store.Provider.Commit(create signal "signal-write-1" "responses/r.json" "{}")) |> ignore
    let after = run (store.Provider.NamespaceState chrona)
    Assert.NotEqual(before.RepositoryToken, after.RepositoryToken)
    Assert.Equal(before.NamespaceToken, after.NamespaceToken)

    match ReadCache.revalidate (ProviderObservation.Current after) cached with
    | Revalidation.Confirmed fresh ->
        // A write conditioned on the confirmed value's namespace token applies.
        let write =
            create chrona "chrona-write-2" "notes/b.json" note
            |> Operation.requireNamespaceToken (Fresh.namespaceToken fresh |> Option.get)

        run (store.Provider.Commit write) |> ignore
    | other -> failwith $"expected Confirmed, got {other}"

    // The same entry, cached through the fallback, is made stale by Signal's commit.
    let fallback = ReadCache.cached (ReadCache.entry key 1 at (Fresh.readRepositoryWide before.RepositoryToken objects)) |> ok

    match ReadCache.revalidate (ProviderObservation.CurrentRepository after.RepositoryToken) fallback with
    | Revalidation.Refresh _ -> ()
    | other -> failwith $"expected Refresh, got {other}"

[<Fact>]
let ``a change inside the namespace makes the cache stale and a write held to its token is refused (ARCA-CON-005)`` () =
    let store = InMemoryStore()
    let run work = work |> Async.RunSynchronously |> ok
    let before = run (store.Provider.NamespaceState chrona)
    run (store.Provider.Commit(create chrona "chrona-write-3" "notes/c.json" "{}")) |> ignore
    let after = run (store.Provider.NamespaceState chrona)
    Assert.NotEqual(before.NamespaceToken, after.NamespaceToken)

    let held =
        create chrona "chrona-write-4" "notes/d.json" "{}"
        |> Operation.requireNamespaceToken before.NamespaceToken

    match store.Provider.Commit held |> Async.RunSynchronously with
    | Error(StorageFailure.StaleNamespaceToken(expected, actual)) ->
        Assert.Equal(before.NamespaceToken, expected)
        Assert.Equal(after.NamespaceToken, actual)
    | other -> failwith $"expected StaleNamespaceToken, got {other}"

[<Fact>]
let ``cache keys carry the account, the full namespace identity and the partition`` () =
    let dataset = Namespace.ofDataset { Application = AppId.create "summa" |> ok; Environment = { Kind = EnvironmentKind.Test; Name = "cache" }; Location = chrona.Location } (DatasetId.create "org-1" |> ok) None |> ok
    Assert.Equal("chrona@acme/data/main/apps", ReadCache.namespaceId chrona)
    Assert.Equal("summa.org-1@acme/data/main/apps", ReadCache.namespaceId dataset)

    match ReadCache.key ("ghp_" + String('A', 36)) chrona "p" with
    | Error(ReadCacheFailure.Corrupt _) -> ()
    | other -> failwith $"a credential as account was accepted: {other}"

    Assert.True(ReadCache.key "" chrona "p" |> Result.isError)
    Assert.True(ReadCache.key "alice" chrona " " |> Result.isError)

[<Fact>]
let ``an unknown cache format is Corrupt`` () =
    match ReadCache.decode "{\"arcaCache\":2}" with
    | Error(ReadCacheFailure.Corrupt _) -> ()
    | other -> failwith $"expected Corrupt, got {other}"

// ---------------------------------------------------------------------------
// Sign-out (LCP-070, LCP-086, OQ-LIMEN-IDB-002)
// ---------------------------------------------------------------------------

[<Fact>]
let ``what each policy offers`` () =
    Assert.Equal<SignOutChoice list>([ SignOutChoice.SendNow; SignOutChoice.Keep; SignOutChoice.Discard ], SignOut.offered SharedDevicePolicy.Ask)
    Assert.Equal<SignOutChoice list>([ SignOutChoice.SendNow; SignOutChoice.Discard ], SignOut.offered SharedDevicePolicy.DiscardOnSignOut)

[<Fact>]
let ``with nothing unsent, sign-out clears the cache under both policies`` () =
    for policy in [ SharedDevicePolicy.Ask; SharedDevicePolicy.DiscardOnSignOut ] do
        Assert.Equal(Ok { ClearCache = true; SendUnsent = false; DiscardUnsent = 0 }, SignOut.plan policy 0 None)

[<Fact>]
let ``unsent changes need a choice that names how many`` () =
    Assert.Equal(
        Error(SignOutRefusal.ChoiceRequired(3, SignOut.offered SharedDevicePolicy.Ask)),
        SignOut.plan SharedDevicePolicy.Ask 3 None
    )

[<Fact>]
let ``under ask, keeping the unsent changes keeps the cache too (OQ-LIMEN-IDB-002)`` () =
    Assert.Equal(Ok { ClearCache = false; SendUnsent = false; DiscardUnsent = 0 }, SignOut.plan SharedDevicePolicy.Ask 2 (Some SignOutChoice.Keep))

[<Fact>]
let ``under ask, sending or discarding clears the cache`` () =
    Assert.Equal(Ok { ClearCache = true; SendUnsent = true; DiscardUnsent = 0 }, SignOut.plan SharedDevicePolicy.Ask 2 (Some SignOutChoice.SendNow))
    Assert.Equal(Ok { ClearCache = true; SendUnsent = false; DiscardUnsent = 2 }, SignOut.plan SharedDevicePolicy.Ask 2 (Some SignOutChoice.Discard))

[<Fact>]
let ``under discardOnSignOut, keeping is not offered and the cache is always cleared`` () =
    Assert.Equal(Error(SignOutRefusal.NotOffered SignOutChoice.Keep), SignOut.plan SharedDevicePolicy.DiscardOnSignOut 2 (Some SignOutChoice.Keep))
    Assert.Equal(Ok { ClearCache = true; SendUnsent = true; DiscardUnsent = 0 }, SignOut.plan SharedDevicePolicy.DiscardOnSignOut 2 (Some SignOutChoice.SendNow))
    Assert.Equal(Ok { ClearCache = true; SendUnsent = false; DiscardUnsent = 2 }, SignOut.plan SharedDevicePolicy.DiscardOnSignOut 2 (Some SignOutChoice.Discard))

[<Fact>]
let ``clearing a signed-out account leaves no entry of it and keeps another account's`` () =
    let cache = MemoryReadCache.create ()
    let alice = ReadCacheConformance.sample chrona "alice" "2026-10" "t-1"
    let bob = ReadCacheConformance.sample chrona "bob" "2026-10" "t-1"
    cache.Save alice |> Async.RunSynchronously |> ok
    cache.Save bob |> Async.RunSynchronously |> ok

    match SignOut.plan SharedDevicePolicy.DiscardOnSignOut 0 None with
    | Ok plan when plan.ClearCache -> cache.Clear(CacheScope.Account "alice") |> Async.RunSynchronously |> ok
    | other -> failwith $"unexpected plan {other}"

    Assert.Equal<CacheKey list>([], cache.Partitions "alice" (ReadCache.namespaceId chrona) |> Async.RunSynchronously |> ok)
    Assert.Equal(Ok(Some bob), cache.Load bob.Key |> Async.RunSynchronously)

// ---------------------------------------------------------------------------
// LCP-085: a cached token does not compile as a write condition.
// ---------------------------------------------------------------------------

[<Fact>]
let ``a cached token passed as a write's change-token condition does not compile (LCP-085)`` () =
    let root =
        match RequirementsTraceability.repositoryRoot (DirectoryInfo AppContext.BaseDirectory) with
        | Some root -> root
        | None -> failwith "the repository root (Arca.slnx) was not found"

    let project = Path.Combine(root, "tests", "Arca.CompileFailure", "Arca.CompileFailure.fsproj")
    let start = ProcessStartInfo("dotnet", $"build \"{project}\" -c Debug -nologo -clp:NoSummary")
    start.RedirectStandardOutput <- true
    start.RedirectStandardError <- true
    start.UseShellExecute <- false

    use build =
        match Process.Start start with
        | null -> failwith "dotnet did not start"
        | started -> started

    let output = build.StandardOutput.ReadToEnd() + build.StandardError.ReadToEnd()
    build.WaitForExit()

    let errors =
        output.Split('\n')
        |> Array.filter (fun line -> line.Contains ": error ")
        |> Array.map (fun line -> line.Trim())
        |> Array.distinct

    Assert.NotEqual(0, build.ExitCode)
    Assert.True(errors.Length = 1, $"expected exactly one error, got:\n{output}")
    Assert.Contains("CachedTokenAsWriteCondition.fs(", errors[0])
    Assert.Contains("FS0001", errors[0])
