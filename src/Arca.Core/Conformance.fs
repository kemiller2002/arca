namespace Arca

open System

/// A fault the conformance suite asks a provider's harness to arrange.
[<RequireQualifiedAccess>]
type ConformanceFault =
    /// The next commit lands but reports OutcomeUnknown.
    | OutcomeUnknownLanded
    /// The next commit does not land and reports OutcomeUnknown.
    | OutcomeUnknownLost
    /// The next call is rate limited.
    | RateLimited
    /// The credential is revoked.
    | CredentialRevoked
    /// The location is read-only.
    | ReadOnly
    /// Listings return at most this many entries.
    | ListingLimit of entries: int
    /// Objects larger than this are refused.
    | MaxObjectBytes of bytes: int64

/// One provider under test, fresh and empty, with the means to act on it
/// from outside Arca.
[<NoEquality; NoComparison>]
type ConformanceSubject =
    { Provider: StorageProvider
      Namespace: Namespace
      /// Writes (Some) or removes (None) an object at a namespace-relative
      /// path, as a concurrent writer or a manual edit outside Arca would.
      WriteExternally: RelativePath -> string option -> Async<unit>
      /// Arranges a fault; false when the harness cannot produce it, which is
      /// reported as Unsupported, never as passed.
      Arrange: ConformanceFault -> Async<bool> }

/// One case's verdict.
[<RequireQualifiedAccess>]
type ConformanceOutcome =
    | Passed
    | Failed of reason: string
    /// The harness could not arrange what the case needs.
    | Unsupported of reason: string

/// One case's result.
type ConformanceResult =
    { Case: string
      /// The requirement the case proves.
      Requirement: string
      Outcome: ConformanceOutcome }

/// The provider conformance suite (ARCA-TEST-001). Every provider, the
/// GitHub adapter and the in-memory provider alike, runs the same cases
/// through the provider-neutral interface. Each case gets a fresh subject.
[<RequireQualifiedAccess>]
module Conformance =

    let private recordWith (mutability: Mutability) (id: string) (body: string) =
        let record =
            { Id = RecordId.create id |> Result.defaultWith (fun _ -> invalidOp "fixture id")
              Type = RecordType.create "conformance.note" |> Result.defaultWith (fun _ -> invalidOp "fixture type")
              SchemaVersion = 1
              Mutability = mutability
              Body = Json.objectOf [ "text", Json.String body ] }

        match Record.encode Int64.MaxValue record with
        | Ok text -> text
        | Error _ -> invalidOp "fixture record"

    let private recordText id body = recordWith Mutability.Mutable id body

    let private path (text: string) =
        RelativePath.parse text |> Result.defaultWith (fun _ -> invalidOp "fixture path")

    let private metadata (key: string) =
        { Summary = "conformance"
          Actor =
            { Kind = ActorKind.Service
              Id = ActorId.create "arca/conformance" |> Result.defaultWith (fun _ -> invalidOp "fixture actor") }
          ProviderIdentity = None
          ExecutionId = None
          CorrelationId = CorrelationId.create "conformance" |> Result.defaultWith (fun _ -> invalidOp "fixture correlation")
          IdempotencyKey = IdempotencyKey.create key |> Result.defaultWith (fun _ -> invalidOp "fixture key") }

    let private operation (subject: ConformanceSubject) key changes =
        Operation.create subject.Namespace (metadata key) changes
        |> Result.defaultWith (fun _ -> invalidOp "fixture operation")

    let private commit subject key changes =
        subject.Provider.Commit(operation subject key changes)

    let private read subject (text: string) =
        subject.Provider.Read subject.Namespace (path text)

    let private failed reason = ConformanceOutcome.Failed reason

    let private expect condition reason =
        if condition then ConformanceOutcome.Passed else failed reason

    let private describe (result: Result<'a, StorageFailure>) =
        match result with
        | Ok _ -> "Ok"
        | Error(StorageFailure.Refused _) -> "Refused"
        | Error(StorageFailure.Conflicted _) -> "Conflicted"
        | Error(StorageFailure.OutcomeUnknown _) -> "OutcomeUnknown"
        | Error(StorageFailure.ObjectTooLarge _) -> "ObjectTooLarge"
        | Error(StorageFailure.StaleChangeToken _) -> "StaleChangeToken"
        | Error(StorageFailure.StaleNamespaceToken _) -> "StaleNamespaceToken"
        | Error(StorageFailure.RateLimited _) -> "RateLimited"
        | Error(StorageFailure.WrongLocation _) -> "WrongLocation"
        | Error(StorageFailure.IntegrityRefused _) -> "IntegrityRefused"
        | Error(StorageFailure.ProviderFailed(code, _, _)) -> $"ProviderFailed {code}"

    let private revisionOf subject text =
        async {
            match! read subject text with
            | Ok(ReadOutcome.Found stored) -> return Some stored.Revision
            | _ -> return None
        }

    let private arranged (subject: ConformanceSubject) fault (body: Async<ConformanceOutcome>) =
        async {
            match! subject.Arrange fault with
            | false -> return ConformanceOutcome.Unsupported "the harness cannot arrange this fault"
            | true -> return! body
        }

    let private roundTrip (subject: ConformanceSubject) =
        async {
            let content = recordText "R-1" "first"
            let! committed = commit subject "conf-roundtrip-1" [ Change.Create(path "records/r1.json", content) ]

            match committed with
            | Error _ -> return failed $"create: {describe committed}"
            | Ok receipt ->
                match! read subject "records/r1.json" with
                | Ok(ReadOutcome.Found stored) ->
                    return
                        expect
                            (stored.Content = content && receipt.Revisions["records/r1.json"] = Some stored.Revision)
                            "the read content or revision differs from what was written"
                | other -> return failed $"read: {describe other}"
        }

    let private conditionalWrite subject =
        async {
            let! _ = commit subject "conf-conditional-1" [ Change.Create(path "records/c.json", recordText "C" "v1") ]

            match! revisionOf subject "records/c.json" with
            | None -> return failed "the created record cannot be read"
            | Some current ->
                let! updated = commit subject "conf-conditional-2" [ Change.Update(path "records/c.json", recordText "C" "v2", current) ]
                let! stale = commit subject "conf-conditional-3" [ Change.Update(path "records/c.json", recordText "C" "v3", current) ]

                match updated, stale with
                | Ok _, Error(StorageFailure.Conflicted [ conflict ]) when conflict.Expected = Some current && conflict.Actual <> Some current ->
                    return ConformanceOutcome.Passed
                | _ -> return failed $"update with the current revision: {describe updated}; with a stale one: {describe stale}"
        }

    let private compareAndSwap subject =
        async {
            let! _ = commit subject "conf-cas-1" [ Change.Create(path "records/x.json", recordText "X" "one") ]
            let! duplicate = commit subject "conf-cas-2" [ Change.Create(path "records/x.json", recordText "X" "two") ]
            let! staleDelete = commit subject "conf-cas-3" [ Change.Delete(path "records/x.json", Revision "not-the-revision") ]

            match duplicate, staleDelete with
            | Error(StorageFailure.Conflicted _), Error(StorageFailure.Conflicted _) -> return ConformanceOutcome.Passed
            | _ -> return failed $"create over an existing record: {describe duplicate}; stale delete: {describe staleDelete}"
        }

    let private concurrentModification (subject: ConformanceSubject) =
        async {
            let! _ = commit subject "conf-concurrent-1" [ Change.Create(path "records/m.json", recordText "M" "base") ]

            match! revisionOf subject "records/m.json" with
            | None -> return failed "the created record cannot be read"
            | Some seen ->
                do! subject.WriteExternally (path "records/m.json") (Some(recordText "M" "someone else"))
                let! mine = commit subject "conf-concurrent-2" [ Change.Update(path "records/m.json", recordText "M" "mine", seen) ]
                let! actual = revisionOf subject "records/m.json"

                match mine with
                | Error(StorageFailure.Conflicted [ conflict ]) when conflict.Actual = actual && conflict.Expected = Some seen ->
                    return ConformanceOutcome.Passed
                | _ -> return failed $"a write over a concurrent change: {describe mine}"
        }

    let private independentChanges (subject: ConformanceSubject) =
        async {
            let! _ = commit subject "conf-independent-1" [ Change.Create(path "records/a.json", recordText "A" "a") ]
            do! subject.WriteExternally (path "records/other.json") (Some(recordText "O" "other"))
            let! mine = commit subject "conf-independent-2" [ Change.Create(path "records/b.json", recordText "B" "b") ]
            let! other = revisionOf subject "records/other.json"

            return expect (Result.isOk mine && other.IsSome) $"a write beside an unrelated concurrent change: {describe mine}"
        }

    let private idempotentRepeat (subject: ConformanceSubject) =
        async {
            let! token = subject.Provider.ChangeToken subject.Namespace
            let changes = [ Change.Create(path "records/i.json", recordText "I" "once") ]
            let! first = commit subject "conf-idempotent-1" changes
            let! repeated = commit subject "conf-idempotent-1" changes

            match token, first, repeated with
            | Ok baseToken, Ok _, Error(StorageFailure.Conflicted _) ->
                let pending =
                    { IdempotencyKey = (metadata "conf-idempotent-1").IdempotencyKey
                      Base = baseToken
                      Candidate = None
                      Revisions = Map.empty }

                match! subject.Provider.Reconcile subject.Namespace pending with
                | Ok(ReconcileOutcome.Landed _) -> return ConformanceOutcome.Passed
                | other -> return failed $"reconciling a landed key: {describe other}"
            | _ -> return failed $"first: {describe first}; repeat: {describe repeated} (a repeat must never duplicate)"
        }

    let private unknownLanded (subject: ConformanceSubject) =
        arranged
            subject
            ConformanceFault.OutcomeUnknownLanded
            (async {
                match! commit subject "conf-unknown-landed" [ Change.Create(path "records/u.json", recordText "U" "landed") ] with
                | Error(StorageFailure.OutcomeUnknown pending) ->
                    match! subject.Provider.Reconcile subject.Namespace pending with
                    | Ok(ReconcileOutcome.Landed _) ->
                        let! present = revisionOf subject "records/u.json"
                        return expect present.IsSome "reconciled as landed, but the record is absent"
                    | other -> return failed $"reconciling a landed write: {describe other}"
                | other -> return failed $"expected OutcomeUnknown, got {describe other}"
            })

    let private unknownLost (subject: ConformanceSubject) =
        arranged
            subject
            ConformanceFault.OutcomeUnknownLost
            (async {
                let changes = [ Change.Create(path "records/l.json", recordText "L" "lost") ]

                match! commit subject "conf-unknown-lost" changes with
                | Error(StorageFailure.OutcomeUnknown pending) ->
                    match! subject.Provider.Reconcile subject.Namespace pending with
                    | Ok ReconcileOutcome.NotLanded ->
                        let! resent = commit subject "conf-unknown-lost" changes
                        return expect (Result.isOk resent) $"resending after NotLanded: {describe resent}"
                    | other -> return failed $"reconciling a lost write: {describe other}"
                | other -> return failed $"expected OutcomeUnknown, got {describe other}"
            })

    let private missingObject subject =
        async {
            let! absent = read subject "records/never-written.json"
            return expect (absent = Ok ReadOutcome.Absent) $"reading a missing object: {describe absent}"
        }

    let private corruptObject (subject: ConformanceSubject) =
        async {
            do! subject.WriteExternally (path "records/corrupt.json") (Some "{ \"arcaRecord\": 1, not json")

            match! read subject "records/corrupt.json" with
            | Ok(ReadOutcome.Found stored) ->
                return expect (Result.isError (Record.decode Record.DefaultMaxBytes stored.Content)) "corrupt content decoded as a record"
            | other -> return failed $"reading a corrupt object: {describe other}"
        }

    let private unsupportedCapability (subject: ConformanceSubject) =
        async {
            let capabilities = subject.Provider.Capabilities
            let declaredAll = ProviderCapabilities.all |> List.forall capabilities.States.ContainsKey

            return
                expect
                    (declaredAll && Result.isError (ProviderCapabilities.require Capability.AtRestEncryption capabilities))
                    "the provider must declare every capability, with at-rest encryption explicitly unavailable"
        }

    let private pagination (subject: ConformanceSubject) =
        arranged
            subject
            (ConformanceFault.ListingLimit 2)
            (async {
                let changes =
                    [ for i in 1..3 -> Change.Create(path $"records/p{i}.json", recordText $"P{i}" "page") ]

                let! _ = commit subject "conf-pagination" changes

                match! subject.Provider.List subject.Namespace (path "records") with
                | Ok listing -> return expect (not listing.Complete && listing.Entries.Length <= 2) "a listing beyond the limit is not marked partial"
                | other -> return failed $"listing: {describe other}"
            })

    let private rateLimit (subject: ConformanceSubject) =
        arranged
            subject
            ConformanceFault.RateLimited
            (async {
                let! limited = subject.Provider.ChangeToken subject.Namespace

                match limited with
                | Error(StorageFailure.RateLimited _) -> return ConformanceOutcome.Passed
                | other -> return failed $"a rate-limited call: {describe other}"
            })

    let private oversizedObject (subject: ConformanceSubject) =
        arranged
            subject
            (ConformanceFault.MaxObjectBytes 64L)
            (async {
                let! big = commit subject "conf-oversized" [ Change.Create(path "records/big.json", recordText "BIG" (String('x', 200))) ]

                match big with
                | Error(StorageFailure.ObjectTooLarge _) -> return ConformanceOutcome.Passed
                | other -> return failed $"an oversized object: {describe other}"
            })

    let private authenticationFailure (subject: ConformanceSubject) =
        async {
            let! before = subject.Provider.ChangeToken subject.Namespace

            match! subject.Arrange ConformanceFault.CredentialRevoked with
            | false -> return ConformanceOutcome.Unsupported "the harness cannot revoke the credential"
            | true ->
                let! refused = commit subject "conf-auth" [ Change.Create(path "records/a1.json", recordText "A1" "x") ]

                match before, refused with
                | Ok _, Error(StorageFailure.Refused(WriteRefusal.CredentialUnavailable _)) -> return ConformanceOutcome.Passed
                | _ -> return failed $"a write with a revoked credential: {describe refused}"
        }

    let private readOnlyMode (subject: ConformanceSubject) =
        arranged
            subject
            ConformanceFault.ReadOnly
            (async {
                let! refused = commit subject "conf-readonly" [ Change.Create(path "records/ro.json", recordText "RO" "x") ]
                let! absent = read subject "records/ro.json"

                match refused with
                | Error(StorageFailure.Refused _) -> return expect (absent = Ok ReadOutcome.Absent) "a refused write changed the store"
                | other -> return failed $"a write to a read-only location: {describe other}"
            })

    let private staleChangeToken (subject: ConformanceSubject) =
        async {
            match! subject.Provider.ChangeToken subject.Namespace with
            | Error failure -> return failed $"change token: {describe (Error failure: Result<unit, StorageFailure>)}"
            | Ok token ->
                do! subject.WriteExternally (path "records/elsewhere.json") (Some(recordText "E" "moved"))

                let guarded =
                    operation subject "conf-stale-token" [ Change.Create(path "records/s.json", recordText "S" "x") ]
                    |> Operation.requireChangeToken token

                match! subject.Provider.Commit guarded with
                | Error(StorageFailure.StaleChangeToken(expected, actual)) when expected = token && actual <> token ->
                    return ConformanceOutcome.Passed
                | other -> return failed $"a write held to a stale change token: {describe other}"
        }

    /// Another application's namespace beside the subject's, at the same
    /// location, as in a repository several applications share (ARCA-LOC-002).
    let private neighbour (subject: ConformanceSubject) =
        let ns = subject.Namespace
        let segments = RelativePath.segments ns.Root
        let own = segments |> List.tryLast |> Option.map Segment.value
        let name = if own = Some "conformance-neighbour" then "conformance-neighbour-b" else "conformance-neighbour"
        let fixture result = result |> Result.defaultWith (fun _ -> invalidOp "fixture neighbour")

        { ns with
            Application = AppId.create name |> fixture
            Dataset = None
            Root = RelativePath.ofSegments (List.truncate (segments.Length - 1) segments @ [ Segment.create name |> fixture ]) |> fixture }

    let private namespaceState (subject: ConformanceSubject) = subject.Provider.NamespaceState subject.Namespace

    let private describeState (result: Result<NamespaceState, StorageFailure>) =
        match result with
        | Ok state ->
            let (ChangeToken repository) = state.RepositoryToken
            let (NamespaceToken ns) = state.NamespaceToken
            $"repository {repository}, namespace {ns}"
        | Error failure -> describe (Error failure: Result<unit, StorageFailure>)

    /// Another application's commit moves the repository's token but not this
    /// namespace's, and a write conditioned on the namespace token still
    /// applies; this namespace's own commit then moves its token (ARCA-CON-005).
    let private namespaceTokenIgnoresNeighbours (subject: ConformanceSubject) =
        async {
            let other = neighbour subject
            let! before = namespaceState subject

            let elsewhere =
                Operation.create other (metadata "conf-neighbour-1") [ Change.Create(path "records/n.json", recordText "N" "theirs") ]
                |> Result.defaultWith (fun _ -> invalidOp "fixture operation")

            let! theirs = subject.Provider.Commit elsewhere
            let! after = namespaceState subject

            match before, theirs, after with
            | Ok before, Ok _, Ok after when after.RepositoryToken <> before.RepositoryToken && after.NamespaceToken = before.NamespaceToken ->
                let guarded =
                    operation subject "conf-namespace-token" [ Change.Create(path "records/mine.json", recordText "M" "mine") ]
                    |> Operation.requireNamespaceToken before.NamespaceToken

                match! subject.Provider.Commit guarded with
                | Ok _ ->
                    let! mine = namespaceState subject

                    match mine with
                    | Ok mine when mine.NamespaceToken <> before.NamespaceToken -> return ConformanceOutcome.Passed
                    | other -> return failed $"this namespace's own commit left its token at {describeState other}"
                | other -> return failed $"a write held to a namespace token, after another namespace's commit: {describe other}"
            | _, Error _, _ -> return failed $"another namespace's commit: {describe theirs}"
            | _ -> return failed $"another namespace's commit took the state from {describeState before} to {describeState after}"
        }

    /// A change inside the namespace makes its token stale: a write held to it
    /// is refused with StaleNamespaceToken and nothing is written (ARCA-CON-005).
    let private staleNamespaceToken (subject: ConformanceSubject) =
        async {
            match! namespaceState subject with
            | Error failure -> return failed $"namespace state: {describe (Error failure: Result<unit, StorageFailure>)}"
            | Ok state ->
                do! subject.WriteExternally (path "records/inside.json") (Some(recordText "I" "moved"))

                let guarded =
                    operation subject "conf-stale-namespace" [ Change.Create(path "records/s.json", recordText "S" "x") ]
                    |> Operation.requireNamespaceToken state.NamespaceToken

                let! refused = subject.Provider.Commit guarded
                let! absent = read subject "records/s.json"

                match refused with
                | Error(StorageFailure.StaleNamespaceToken(expected, actual)) when expected = state.NamespaceToken && actual <> expected ->
                    return expect (absent = Ok ReadOutcome.Absent) "a write refused as stale changed the store"
                | other -> return failed $"a write held to a namespace token after a change inside the namespace: {describe other}"
        }

    let private atomicity (subject: ConformanceSubject) =
        async {
            let! _ = commit subject "conf-atomic-1" [ Change.Create(path "records/t1.json", recordText "T1" "x") ]

            let! partial =
                commit
                    subject
                    "conf-atomic-2"
                    [ Change.Create(path "records/t2.json", recordText "T2" "x")
                      Change.Create(path "records/t1.json", recordText "T1" "again") ]

            let! t2 = read subject "records/t2.json"

            match partial with
            | Error(StorageFailure.Conflicted _) -> return expect (t2 = Ok ReadOutcome.Absent) "a refused operation was partly applied"
            | other -> return failed $"an operation with one stale change: {describe other}"
        }

    let private immutableProtected (subject: ConformanceSubject) =
        async {
            let! _ = commit subject "conf-immutable-1" [ Change.Create(path "records/e.json", recordWith Mutability.Immutable "E" "posted") ]

            match! revisionOf subject "records/e.json" with
            | None -> return failed "the immutable record cannot be read"
            | Some current ->
                let! changed = commit subject "conf-immutable-2" [ Change.Update(path "records/e.json", recordWith Mutability.Immutable "E" "edited", current) ]
                let! deleted = commit subject "conf-immutable-3" [ Change.Delete(path "records/e.json", current) ]

                match changed, deleted with
                | Error(StorageFailure.IntegrityRefused(_, IntegrityRefusal.ImmutableRecord)), Error(StorageFailure.IntegrityRefused(_, IntegrityRefusal.ImmutableRecord)) ->
                    let! still = revisionOf subject "records/e.json"
                    return expect (still = Some current) "a refused write changed the immutable record"
                | _ -> return failed $"changing an immutable record: {describe changed}; deleting it: {describe deleted}"
        }

    let private corruptNotOverwritten (subject: ConformanceSubject) =
        async {
            do! subject.WriteExternally (path "records/broken.json") (Some "{\"edited\":\"by hand\"}")

            match! revisionOf subject "records/broken.json" with
            | None -> return failed "the corrupt record cannot be read"
            | Some current ->
                let! guessed = commit subject "conf-corrupt-write" [ Change.Update(path "records/broken.json", recordText "B" "guess", current) ]

                match guessed with
                | Error(StorageFailure.IntegrityRefused(_, IntegrityRefusal.CorruptRecord _)) -> return ConformanceOutcome.Passed
                | other -> return failed $"overwriting a record whose state cannot be established: {describe other}"
        }

    let private externalEditDetected (subject: ConformanceSubject) =
        async {
            let! _ = commit subject "conf-history-1" [ Change.Create(path "records/h.json", recordText "H" "mine") ]
            do! subject.WriteExternally (path "records/h.json") (Some(recordText "H" "edited outside"))

            match! subject.Provider.History subject.Namespace (path "records/h.json") with
            | Ok(newest :: older) ->
                let arcaKey =
                    older
                    |> List.exists (fun entry ->
                        match entry.Origin with
                        | CommitOrigin.Arca trailers -> IdempotencyKey.value trailers.IdempotencyKey = "conf-history-1"
                        | CommitOrigin.External -> false)

                return expect (newest.Origin = CommitOrigin.External && arcaKey) "the history does not show the external edit after Arca's commit"
            | other -> return failed $"history: {describe other}"
        }

    // -----------------------------------------------------------------------
    // Erasure (ARCA-INT-005)
    // -----------------------------------------------------------------------

    /// Text in the erased record that must never survive in the tombstone.
    let private personalData = "personal-data-7f3a91"

    let private erasedAt = DateTimeOffset(2026, 10, 9, 8, 30, 0, TimeSpan.Zero)

    /// Creates an immutable record, reads it back and validates it as the
    /// application would before erasing it.
    let private erasable (subject: ConformanceSubject) (text: string) key =
        async {
            let! _ = commit subject key [ Change.Create(path text, recordWith Mutability.Immutable "X1" personalData) ]

            match! read subject text with
            | Ok(ReadOutcome.Found stored) ->
                match Record.decode Record.DefaultMaxBytes stored.Content with
                | Ok record ->
                    return
                        Some
                            { Record = record
                              Revision = stored.Revision
                              ContentHash = Record.contentHash record }
                | Error _ -> return None
            | _ -> return None
        }

    let private erase (subject: ConformanceSubject) (text: string) key (record: ValidatedRecord) =
        match Erasure.request (path text) record erasedAt "retention rule R-7" with
        | Error error -> async { return Error(StorageFailure.ProviderFailed("ARCA.CONFORMANCE", false, $"request refused: {error}")) }
        | Ok request ->
            match Erasure.operation subject.Namespace (metadata key) [ request ] with
            | Error error -> async { return Error(StorageFailure.ProviderFailed("ARCA.CONFORMANCE", false, $"operation refused: {error}")) }
            | Ok operation -> Erasure.commit subject.Provider operation

    /// The record's blob is replaced by a tombstone holding its hash, revision,
    /// time and reason, never its content, in one Arca commit; reads report
    /// Erased, not an integrity failure.
    let private erasureLeavesTombstone (subject: ConformanceSubject) =
        async {
            match! erasable subject "records/x.json" "conf-erase-create" with
            | None -> return failed "the immutable record to erase could not be read back"
            | Some record ->
                match! erase subject "records/x.json" "conf-erase-1" record with
                | Error _ as other -> return failed $"erasing an immutable record: {describe other}"
                | Ok _ ->
                    let! after = read subject "records/x.json"
                    let! history = subject.Provider.History subject.Namespace (path "records/x.json")

                    match after, history with
                    | Ok(ReadOutcome.Erased erased), Ok(newest :: _) ->
                        let tombstone = erased.Tombstone

                        if erased.Stored.Content.Contains personalData then
                            return failed "the tombstone carries the erased content"
                        elif tombstone.ErasedContentHash <> record.ContentHash || tombstone.ErasedRevision <> record.Revision then
                            return failed "the tombstone does not name the erased record's hash and revision"
                        elif tombstone.ErasedAt <> erasedAt || tombstone.Reason <> "retention rule R-7" then
                            return failed "the tombstone does not record when and why"
                        else
                            match newest.Origin with
                            | CommitOrigin.Arca trailers when IdempotencyKey.value trailers.IdempotencyKey = "conf-erase-1" -> return ConformanceOutcome.Passed
                            | _ -> return failed "the erasure is not the newest Arca commit on the record"
                    | other, _ -> return failed $"reading an erased record: {describe other}"
        }

    /// Nothing recreates, changes, deletes or erases again an erased record.
    let private erasedIsFinal (subject: ConformanceSubject) =
        async {
            match! erasable subject "records/y.json" "conf-erase-create-2" with
            | None -> return failed "the immutable record to erase could not be read back"
            | Some record ->
                let! _ = erase subject "records/y.json" "conf-erase-2" record

                match! read subject "records/y.json" with
                | Ok(ReadOutcome.Erased erased) ->
                    let tombstoneRevision = erased.Stored.Revision
                    let! recreated = commit subject "conf-erase-recreate" [ Change.Create(path "records/y.json", recordWith Mutability.Immutable "Y1" "again") ]
                    let! changed = commit subject "conf-erase-change" [ Change.Update(path "records/y.json", recordText "Y1" "changed", tombstoneRevision) ]
                    let! deleted = commit subject "conf-erase-delete" [ Change.Delete(path "records/y.json", tombstoneRevision) ]
                    let! again = erase subject "records/y.json" "conf-erase-again" record
                    let! still = read subject "records/y.json"

                    let erasedRefusal result =
                        match result with
                        | Error(StorageFailure.IntegrityRefused(_, IntegrityRefusal.ErasedRecord)) -> true
                        | _ -> false

                    match recreated, again, still with
                    | Ok _, _, _ -> return failed "an erased record was recreated"
                    | _, Ok _, _ -> return failed "an erased record was erased again"
                    | _, _, Ok(ReadOutcome.Erased after) when erasedRefusal changed && erasedRefusal deleted && after.Stored.Revision = tombstoneRevision ->
                        return ConformanceOutcome.Passed
                    | _ -> return failed $"writing over a tombstone: change {describe changed}, delete {describe deleted}, then read {describe still}"
                | other -> return failed $"reading an erased record: {describe other}"
        }

    /// An erasure must name what is stored: a mutable record, or content other
    /// than the hash it names, is refused and left as it was.
    let private erasureMustMatch (subject: ConformanceSubject) =
        async {
            let! _ = commit subject "conf-erase-mutable" [ Change.Create(path "records/m.json", recordText "M1" personalData) ]

            match! read subject "records/m.json" with
            | Ok(ReadOutcome.Found stored) ->
                match Record.decode Record.DefaultMaxBytes stored.Content with
                | Error _ -> return failed "the mutable record does not decode"
                | Ok record ->
                    // Claims the record is immutable, which the stored content denies.
                    let claimed =
                        { Record = { record with Mutability = Mutability.Immutable }
                          Revision = stored.Revision
                          ContentHash = Record.contentHash record }

                    let! refused = erase subject "records/m.json" "conf-erase-mutable-1" claimed
                    let! after = read subject "records/m.json"

                    match refused, after with
                    | Error(StorageFailure.IntegrityRefused(_, IntegrityRefusal.NotErasable _)), Ok(ReadOutcome.Found kept) when kept.Content = stored.Content ->
                        return ConformanceOutcome.Passed
                    | _ -> return failed $"erasing a mutable record: {describe refused}, then read {describe after}"
            | other -> return failed $"reading the mutable record: {describe other}"
        }

    /// Every case: name, requirement and check.
    let cases: (string * string * (ConformanceSubject -> Async<ConformanceOutcome>)) list =
        [ "round-trip", "ARCA-REC-001", roundTrip
          "conditional write", "ARCA-CON-001", conditionalWrite
          "compare-and-swap", "ARCA-CON-001", compareAndSwap
          "concurrent modification", "ARCA-CON-002", concurrentModification
          "independent concurrent changes", "ARCA-CON-003", independentChanges
          "atomic operation", "ARCA-COMMIT-001", atomicity
          "idempotent repeat", "ARCA-OUT-002", idempotentRepeat
          "OutcomeUnknown that landed", "ARCA-OUT-001", unknownLanded
          "OutcomeUnknown that was lost", "ARCA-OUT-001", unknownLost
          "missing object", "ARCA-API-001", missingObject
          "corrupt object", "ARCA-INT-001", corruptObject
          "unsupported capability", "ARCA-ARCH-003", unsupportedCapability
          "pagination", "ARCA-API-004", pagination
          "rate limit", "ARCA-API-003", rateLimit
          "oversized object", "ARCA-API-004", oversizedObject
          "authentication failure", "ARCA-AUTH-004", authenticationFailure
          "read-only mode", "ARCA-COMMIT-006", readOnlyMode
          "stale change token", "ARCA-API-004", staleChangeToken
          "namespace token ignores other namespaces", "ARCA-CON-005", namespaceTokenIgnoresNeighbours
          "stale namespace token", "ARCA-CON-005", staleNamespaceToken
          "immutable record protected", "ARCA-INT-003", immutableProtected
          "corrupt record not overwritten", "ARCA-INT-004", corruptNotOverwritten
          "external edit detected", "ARCA-INT-002", externalEditDetected
          "erasure leaves a tombstone, never the content", "ARCA-INT-005", erasureLeavesTombstone
          "an erased record is final", "ARCA-INT-005", erasedIsFinal
          "an erasure must match what is stored", "ARCA-INT-005", erasureMustMatch ]

    /// Runs every case, each against a fresh subject.
    let run (fresh: unit -> Async<ConformanceSubject>) : Async<ConformanceResult list> =
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

    /// The in-memory provider as a conformance subject.
    let inMemory (ns: Namespace) : Async<ConformanceSubject> =
        async {
            let store = InMemoryStore()

            return
                { Provider = store.Provider
                  Namespace = ns
                  WriteExternally =
                    fun relative content ->
                        async {
                            match Namespace.resolve ns relative with
                            | Ok address -> store.WriteExternally(ns.Location, address.Path, content)
                            | Error _ -> ()
                        }
                  Arrange =
                    fun fault ->
                        async {
                            store.Arrange(
                                match fault with
                                | ConformanceFault.OutcomeUnknownLanded -> InMemoryFault.OutcomeUnknownLanded
                                | ConformanceFault.OutcomeUnknownLost -> InMemoryFault.OutcomeUnknownLost
                                | ConformanceFault.RateLimited -> InMemoryFault.RateLimited None
                                | ConformanceFault.CredentialRevoked -> InMemoryFault.CredentialRevoked
                                | ConformanceFault.ReadOnly -> InMemoryFault.ReadOnly
                                | ConformanceFault.ListingLimit entries -> InMemoryFault.ListingLimit entries
                                | ConformanceFault.MaxObjectBytes bytes -> InMemoryFault.MaxObjectBytes bytes
                            )

                            return true
                        } }
        }
