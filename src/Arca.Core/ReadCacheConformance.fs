namespace Arca

open System

/// What a read-cache harness is asked to arrange.
[<RequireQualifiedAccess>]
type ReadCacheFault =
    /// Storage stops answering.
    | Unavailable
    /// Storage holds this text under the key, as if written outside the cache
    /// (a manual edit, another version, tampering).
    | Stored of key: CacheKey * text: string

/// One read cache under test, fresh and empty.
[<NoEquality; NoComparison>]
type ReadCacheSubject =
    { Namespace: Namespace
      Store: ReadCacheStore
      /// Another cache over the same storage, as after a reload.
      Reopen: unit -> Async<ReadCacheStore>
      /// Arranges a fault; false when the harness cannot, which is reported as
      /// Unsupported, never as passed.
      Arrange: ReadCacheFault -> Async<bool> }

/// The read-cache conformance suite (WI-0021; Limen LCP-082..LCP-086): one
/// executable contract for every `ReadCacheStore`, the in-memory one and
/// the IndexedDB adapter alike. Each case gets a fresh subject.
[<RequireQualifiedAccess>]
module ReadCacheConformance =

    let private fixture result =
        match result with
        | Ok value -> value
        | Error _ -> invalidOp "read-cache conformance fixture"

    let private at = DateTimeOffset(2026, 10, 8, 12, 0, 0, TimeSpan.Zero)

    let private objects (partition: string) =
        [ "records/a.json", "{\"text\":\"é 😀 \\\"quoted\\\"\"}"
          "records/b.json", "{}" ]
        |> List.map (fun (path, content) ->
            { Path = RelativePath.parse path |> fixture
              Content = $"{partition}:{content}"
              Revision = Revision $"r-{partition}-{path}" })

    /// An entry for `partition`, read with `account` when the namespace's
    /// token was `token` (namespace-scoped, ARCA-CON-005).
    let sample (ns: Namespace) (account: string) (partition: string) (token: string) =
        let key = ReadCache.key account ns partition |> fixture

        let state =
            { RepositoryToken = ChangeToken $"repository-{token}"
              NamespaceToken = NamespaceToken token }

        ReadCache.entry key 1 at (Fresh.read state (objects partition))

    /// The same, read through the explicit repository-wide fallback at the
    /// repository's change token `token`.
    let sampleRepositoryWide (ns: Namespace) (account: string) (partition: string) (token: string) =
        let key = ReadCache.key account ns partition |> fixture
        ReadCache.entry key 1 at (Fresh.readRepositoryWide (ChangeToken token) (objects partition))

    let private failure =
        function
        | ReadCacheFailure.Unavailable -> "Unavailable"
        | ReadCacheFailure.QuotaExceeded -> "QuotaExceeded"
        | ReadCacheFailure.Blocked -> "Blocked"
        | ReadCacheFailure.Corrupt reason -> $"Corrupt ({reason})"

    let private loaded (result: Result<CacheEntry option, ReadCacheFailure>) =
        match result with
        | Ok None -> "nothing"
        | Ok(Some entry) -> $"the entry for {entry.Key.Partition}"
        | Error error -> failure error

    let private arranged (subject: ReadCacheSubject) fault (body: Async<ConformanceOutcome>) =
        async {
            match! subject.Arrange fault with
            | true -> return! body
            | false -> return ConformanceOutcome.Unsupported "the harness cannot arrange this fault"
        }

    let private saveAll (store: ReadCacheStore) (entries: CacheEntry list) =
        let rec go entries =
            async {
                match entries with
                | [] -> return Ok()
                | entry :: rest ->
                    match! store.Save entry with
                    | Ok() -> return! go rest
                    | Error error -> return Error error
            }

        go entries

    let private absent (subject: ReadCacheSubject) =
        async {
            let entry = sample subject.Namespace "alice" "2026-10" "t-1"

            match! subject.Store.Load entry.Key with
            | Ok None -> return ConformanceOutcome.Passed
            | other -> return ConformanceOutcome.Failed $"a fresh cache loaded {loaded other}"
        }

    let private roundTrip (subject: ReadCacheSubject) =
        async {
            let entry = sample subject.Namespace "alice" "2026-10" "t-1"

            match! subject.Store.Save entry with
            | Error error -> return ConformanceOutcome.Failed $"save: {failure error}"
            | Ok() ->
                let! direct = subject.Store.Load entry.Key
                let! reopened = subject.Reopen()
                let! reloaded = reopened.Load entry.Key

                match direct, reloaded with
                | Ok(Some a), Ok(Some b) when a = entry && b = entry -> return ConformanceOutcome.Passed
                | _ -> return ConformanceOutcome.Failed $"load gave {loaded direct}, reopened gave {loaded reloaded}"
        }

    let private replaces (subject: ReadCacheSubject) =
        async {
            let first = sample subject.Namespace "alice" "2026-10" "t-1"
            let second = { sample subject.Namespace "alice" "2026-10" "t-2" with Records = [] }

            match! saveAll subject.Store [ first; second ] with
            | Error error -> return ConformanceOutcome.Failed $"save: {failure error}"
            | Ok() ->
                match! subject.Store.Load first.Key with
                | Ok(Some entry) when entry = second -> return ConformanceOutcome.Passed
                | other -> return ConformanceOutcome.Failed $"after replacing, load gave {loaded other}"
        }

    let private accountsIsolated (subject: ReadCacheSubject) =
        async {
            let alice = sample subject.Namespace "alice" "2026-10" "t-1"
            let ns = ReadCache.namespaceId subject.Namespace

            match! subject.Store.Save alice with
            | Error error -> return ConformanceOutcome.Failed $"save: {failure error}"
            | Ok() ->
                let! asBob = subject.Store.Load { alice.Key with Account = "bob" }
                let! bobs = subject.Store.Partitions "bob" ns

                match asBob, bobs with
                | Ok None, Ok [] -> return ConformanceOutcome.Passed
                | Ok None, Ok keys -> return ConformanceOutcome.Failed $"another account lists {keys.Length} partitions"
                | other, _ -> return ConformanceOutcome.Failed $"another account loaded {loaded other}"
        }

    let private partitions (subject: ReadCacheSubject) =
        async {
            let ns = ReadCache.namespaceId subject.Namespace

            let entries =
                [ sample subject.Namespace "alice" "2026-10" "t-1"
                  sample subject.Namespace "alice" "2026-09" "t-1"
                  sample subject.Namespace "alice" "roster" "t-1"
                  sample subject.Namespace "bob" "2026-10" "t-1" ]

            match! saveAll subject.Store entries with
            | Error error -> return ConformanceOutcome.Failed $"save: {failure error}"
            | Ok() ->
                match! subject.Store.Partitions "alice" ns with
                | Ok keys when (keys |> List.map _.Partition) = [ "2026-09"; "2026-10"; "roster" ] -> return ConformanceOutcome.Passed
                | Ok keys -> return ConformanceOutcome.Failed $"""listed {keys |> List.map _.Partition |> String.concat ","}"""
                | Error error -> return ConformanceOutcome.Failed $"partitions: {failure error}"
        }

    let private clears (subject: ReadCacheSubject) (scope: CacheScope) (expectKept: CacheKey -> bool) =
        async {
            let other =
                Namespace.ofApplication
                    { Application = AppId.create "read-cache-other" |> fixture
                      Environment = { Kind = EnvironmentKind.Test; Name = "read-cache-conformance" }
                      Location = subject.Namespace.Location }
                |> fixture

            let entries =
                [ sample subject.Namespace "alice" "2026-10" "t-1"
                  sample subject.Namespace "alice" "roster" "t-1"
                  sample other "alice" "2026-10" "t-1"
                  sample subject.Namespace "bob" "2026-10" "t-1" ]

            match! saveAll subject.Store entries with
            | Error error -> return ConformanceOutcome.Failed $"save: {failure error}"
            | Ok() ->
                match! subject.Store.Clear scope with
                | Error error -> return ConformanceOutcome.Failed $"clear: {failure error}"
                | Ok() ->
                    let! reopened = subject.Reopen()

                    let rec check entries =
                        async {
                            match entries with
                            | [] -> return ConformanceOutcome.Passed
                            | (entry: CacheEntry) :: rest ->
                                let! found = reopened.Load entry.Key
                                let kept = expectKept entry.Key

                                match found with
                                | Ok(Some _) when kept -> return! check rest
                                | Ok None when not kept -> return! check rest
                                | other ->
                                    let expected = if kept then "kept" else "cleared"
                                    return ConformanceOutcome.Failed $"{entry.Key.Account}/{entry.Key.Partition} should be {expected}, loaded {loaded other}"
                        }

                    return! check entries
        }

    let private clearAccount (subject: ReadCacheSubject) =
        clears subject (CacheScope.Account "alice") (fun key -> key.Account <> "alice")

    let private clearAccountNamespace (subject: ReadCacheSubject) =
        let ns = ReadCache.namespaceId subject.Namespace
        clears subject (CacheScope.AccountNamespace("alice", ns)) (fun key -> not (key.Account = "alice" && key.Namespace = ns))

    let private clearEverything (subject: ReadCacheSubject) = clears subject CacheScope.Everything (fun _ -> false)

    let private removes (subject: ReadCacheSubject) =
        async {
            let first = sample subject.Namespace "alice" "2026-10" "t-1"
            let second = sample subject.Namespace "alice" "2026-09" "t-1"

            match! saveAll subject.Store [ first; second ] with
            | Error error -> return ConformanceOutcome.Failed $"save: {failure error}"
            | Ok() ->
                match! subject.Store.Remove first.Key with
                | Error error -> return ConformanceOutcome.Failed $"remove: {failure error}"
                | Ok() ->
                    let! gone = subject.Store.Load first.Key
                    let! kept = subject.Store.Load second.Key

                    match gone, kept with
                    | Ok None, Ok(Some _) -> return ConformanceOutcome.Passed
                    | _ -> return ConformanceOutcome.Failed $"after removing one partition: {loaded gone}, the other {loaded kept}"
        }

    let private refusedOnLoad (subject: ReadCacheSubject) (tamper: CacheEntry -> CacheEntry) =
        let entry = tamper (sample subject.Namespace "alice" "2026-10" "t-1")

        // A tampered entry encodes; only its content no longer matches.
        let text =
            match ReadCache.encode entry with
            | Ok text -> text
            | Error _ -> "{}"

        arranged
            subject
            (ReadCacheFault.Stored(entry.Key, text))
            (async {
                match! ReadCache.load subject.Store entry.Key with
                | Error(ReadCacheFailure.Corrupt _) ->
                    match! subject.Store.Load entry.Key with
                    | Ok None -> return ConformanceOutcome.Passed
                    | other -> return ConformanceOutcome.Failed $"the refused entry was not dropped: {loaded other}"
                | Ok _ -> return ConformanceOutcome.Failed "an entry that fails validation was used"
                | Error error -> return ConformanceOutcome.Failed $"gave {failure error}, not Corrupt"
            })

    let private tampered (subject: ReadCacheSubject) =
        refusedOnLoad subject (fun entry ->
            { entry with Records = entry.Records |> List.map (fun record -> { record with Content = record.Content + " edited" }) })

    let private tokenless (subject: ReadCacheSubject) = refusedOnLoad subject (fun entry -> { entry with ChangeToken = "" })

    let private unavailable (subject: ReadCacheSubject) =
        arranged
            subject
            ReadCacheFault.Unavailable
            (async {
                let entry = sample subject.Namespace "alice" "2026-10" "t-1"
                let! load = subject.Store.Load entry.Key
                let! save = subject.Store.Save entry

                match load, save with
                | Error ReadCacheFailure.Unavailable, Error ReadCacheFailure.Unavailable -> return ConformanceOutcome.Passed
                | _, Ok() -> return ConformanceOutcome.Failed "a save succeeded on unavailable storage"
                | _ -> return ConformanceOutcome.Failed $"unavailable storage loaded {loaded load}"
            })

    let private credentialFree (subject: ReadCacheSubject) =
        async {
            let entry = sample subject.Namespace "alice" "2026-10" "t-1"
            let token = "ghp_" + String('A', 36)
            let leaking = { entry with Records = [ { Path = "records/a.json"; Content = token; ContentHash = ReadCache.hash token } ] }

            match! subject.Store.Save leaking with
            | Error(ReadCacheFailure.Corrupt _) ->
                match! subject.Store.Load entry.Key with
                | Ok None -> return ConformanceOutcome.Passed
                | other -> return ConformanceOutcome.Failed $"after refusing it, the cache holds {loaded other}"
            | Error error -> return ConformanceOutcome.Failed $"gave {failure error}, not Corrupt"
            | Ok() -> return ConformanceOutcome.Failed "an entry carrying a credential was saved"
        }

    /// Both token scopes survive a save and a reopen, so revalidation compares
    /// each entry with the right token (ARCA-CON-005).
    let private scopesKept (subject: ReadCacheSubject) =
        async {
            let scoped = sample subject.Namespace "alice" "2026-10" "t-1"
            let repositoryWide = sampleRepositoryWide subject.Namespace "alice" "2026-09" "t-1"

            match! saveAll subject.Store [ scoped; repositoryWide ] with
            | Error error -> return ConformanceOutcome.Failed $"save: {failure error}"
            | Ok() ->
                let! reopened = subject.Reopen()
                let! first = ReadCache.load reopened scoped.Key
                let! second = ReadCache.load reopened repositoryWide.Key

                match first, second with
                | Ok(Some a), Ok(Some b) when
                    Cached.value a = scoped
                    && Cached.scope a = TokenScope.Namespace
                    && Cached.value b = repositoryWide
                    && Cached.scope b = TokenScope.Repository
                    ->
                    return ConformanceOutcome.Passed
                | Ok(Some a), Ok(Some b) -> return ConformanceOutcome.Failed $"reopened as {Cached.scope a} and {Cached.scope b}"
                | _ ->
                    let describe (result: Result<Cached<CacheEntry> option, ReadCacheFailure>) =
                        result |> Result.map (Option.map Cached.value) |> loaded

                    return ConformanceOutcome.Failed $"reopened, loaded {describe first} and {describe second}"
        }

    /// An entry written before token scopes existed has no scope: it is
    /// loaded, and its token is taken as the repository's, so it is never
    /// confirmed by a namespace token it does not carry (ARCA-CON-005).
    let private unscopedIsRepositoryWide (subject: ReadCacheSubject) =
        let entry = sampleRepositoryWide subject.Namespace "alice" "2026-10" "t-1"

        let text =
            match ReadCache.encode entry |> Result.toOption |> Option.map Json.parse with
            | Some(Ok(Json.Object members)) ->
                members |> List.filter (fun (name, _) -> name <> "tokenScope") |> Json.objectOf |> Json.canonicalText
            | _ -> "{}"

        arranged
            subject
            (ReadCacheFault.Stored(entry.Key, text))
            (async {
                match! ReadCache.load subject.Store entry.Key with
                | Ok(Some cached) when Cached.value cached = entry && Cached.scope cached = TokenScope.Repository ->
                    let state =
                        { RepositoryToken = ChangeToken "t-2"
                          NamespaceToken = NamespaceToken "t-1" }

                    match ReadCache.revalidate (ProviderObservation.Current state) cached with
                    | Revalidation.Refresh _ -> return ConformanceOutcome.Passed
                    | _ -> return ConformanceOutcome.Failed "an unscoped entry was confirmed by a namespace token"
                | Ok(Some cached) -> return ConformanceOutcome.Failed $"an unscoped entry loaded as {Cached.scope cached}"
                | Ok None -> return ConformanceOutcome.Failed "an unscoped entry was not loaded"
                | Error error -> return ConformanceOutcome.Failed $"an unscoped entry gave {failure error}"
            })

    /// A partition that holds an erased record is removed from the device at
    /// once; partitions without it, and other accounts, are kept (ARCA-INT-005).
    let private erasedPurged (subject: ReadCacheSubject) =
        async {
            let holding = sample subject.Namespace "alice" "2026-10" "t-1"

            let without =
                { sample subject.Namespace "alice" "2026-09" "t-1" with
                    Records = [ { Path = "records/c.json"; Content = "{}"; ContentHash = ReadCache.hash "{}" } ] }

            let bobs = sample subject.Namespace "bob" "2026-10" "t-1"

            match! saveAll subject.Store [ holding; without; bobs ] with
            | Error error -> return ConformanceOutcome.Failed $"save: {failure error}"
            | Ok() ->
                match! ReadCache.purgeErased subject.Store "alice" subject.Namespace [ RelativePath.parse "records/a.json" |> fixture ] with
                | Error error -> return ConformanceOutcome.Failed $"purge: {failure error}"
                | Ok removed ->
                    let! reopened = subject.Reopen()
                    let! gone = reopened.Load holding.Key
                    let! kept = reopened.Load without.Key
                    let! other = reopened.Load bobs.Key

                    match removed, gone, kept, other with
                    | 1, Ok None, Ok(Some _), Ok(Some _) -> return ConformanceOutcome.Passed
                    | _ -> return ConformanceOutcome.Failed $"removed {removed}; then {loaded gone}, {loaded kept}, {loaded other}"
        }

    /// Every case: its name, the requirement it proves and its check.
    let cases: (string * string * (ReadCacheSubject -> Async<ConformanceOutcome>)) list =
        [ "absent partition loads nothing", "LCP-082", absent
          "round-trip keeps the whole entry", "LCP-083", roundTrip
          "a save replaces the partition", "LCP-087", replaces
          "accounts are isolated", "LCP-086", accountsIsolated
          "partitions are listed per account and namespace", "LCP-084", partitions
          "removing one partition keeps the others", "LCP-084", removes
          "clearing an account keeps other accounts", "LCP-086", clearAccount
          "clearing an account's namespace keeps the rest", "LCP-086", clearAccountNamespace
          "clearing everything leaves nothing", "LCP-070", clearEverything
          "a tampered entry is refused and dropped", "LCP-083", tampered
          "an entry without a token is refused and dropped", "LCP-083", tokenless
          "unavailable storage is Unavailable", "LCP-082", unavailable
          "an entry carrying a credential is refused", "LCP-068", credentialFree
          "token scopes survive a reopen", "ARCA-CON-005", scopesKept
          "an entry without a scope is repository-wide", "ARCA-CON-005", unscopedIsRepositoryWide
          "a partition holding an erased record is purged", "ARCA-INT-005", erasedPurged ]

    /// Runs every case, each against a fresh subject.
    let run (fresh: unit -> Async<ReadCacheSubject>) : Async<ConformanceResult list> =
        async {
            let results = Collections.Generic.List<ConformanceResult>()

            for name, requirement, check in cases do
                let! subject = fresh ()
                let! outcome = check subject

                results.Add
                    { Case = name
                      Requirement = requirement
                      Outcome = outcome }

            return List.ofSeq results
        }

    /// The in-memory cache as a conformance subject.
    let inMemory (ns: Namespace) : Async<ReadCacheSubject> =
        async {
            let cell = MemoryReadCache.cell ()

            return
                { Namespace = ns
                  Store = MemoryReadCache.over cell
                  Reopen = fun () -> async { return MemoryReadCache.over cell }
                  Arrange =
                    fun fault ->
                        async {
                            match fault with
                            | ReadCacheFault.Unavailable -> cell.Unavailable <- true
                            | ReadCacheFault.Stored(key, text) -> cell.Entries <- cell.Entries.Add(key, text)

                            return true
                        } }
        }
