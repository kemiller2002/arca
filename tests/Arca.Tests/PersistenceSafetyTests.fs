/// Persistence safety by construction (WI-0036, ARCA-OFF-009,
/// DF-ARCA-2026-0014; Echelon Engineering Improvement Initiative,
/// Workstream D). Application code cannot claim that a write landed: the
/// compile-failure fixtures prove it cannot forge a Synchronized state or a
/// commit receipt, nor set an entry's state; the scenarios below drive the
/// offline queue against the in-memory provider and show that an entry is
/// synchronized only when the provider accepted that entry's operation.
module Arca.Tests.PersistenceSafetyTests

open System
open System.Collections.Generic
open Arca
open Xunit

let private ok result =
    match result with
    | Ok value -> value
    | Error error -> failwith $"expected Ok, got {error}"

let private run work = Async.RunSynchronously work

let private location = DataLocation.create "acme" "data" "main" "apps" |> ok

let private chrona =
    Namespace.ofApplication
        { Application = AppId.create "chrona" |> ok
          Environment = { Kind = EnvironmentKind.Test; Name = "safety" }
          Location = location }
    |> ok

let private at = DateTimeOffset(2026, 10, 28, 12, 0, 0, TimeSpan.Zero)
let private path text = RelativePath.parse text |> ok

let private operation (key: string) (changes: Change list) =
    Operation.create
        chrona
        { Summary = $"investigation {key}"
          Actor = { Kind = ActorKind.Human; Id = ActorId.create "u-1" |> ok }
          ProviderIdentity = None
          ExecutionId = None
          CorrelationId = CorrelationId.create $"c-{key}" |> ok
          IdempotencyKey = IdempotencyKey.create $"safety-{key}" |> ok }
        changes
    |> ok

let private create (key: string) =
    operation key [ Change.Create(path $"investigations/{key}.json", $"{{\"id\":\"{key}\"}}") ]

let private enqueue (operations: Operation list) (queue: OfflineQueue) =
    operations |> List.fold (fun queue op -> OfflineQueue.enqueue at op queue |> ok |> fst) queue

let private queueOf operations =
    enqueue operations (OfflineQueue.create OfflinePolicy.QueueWrites)

/// A queue store over a list, recording every save.
type private Saves() =
    let saves = List<OfflineQueue>()
    member _.All = List.ofSeq saves

    member _.Store: QueueStore =
        { Load = fun () -> async { return Ok(Seq.tryLast saves) }
          Save =
            fun queue ->
                async {
                    saves.Add queue
                    return Ok()
                } }

let private sync (store: InMemoryStore) queue =
    OfflineSync.run store.Provider (Saves().Store) chrona 100 queue |> run

let private step (store: InMemoryStore) queue =
    OfflineSync.step store.Provider (Saves().Store) chrona queue |> run

/// The commits Arca made (the in-memory store starts with an "initial" commit).
let private commits (store: InMemoryStore) =
    store.State.History |> List.filter (fun commit -> commit.Message.StartsWith "chrona:") |> List.length

let private stateOf (sequence: int64) (queue: OfflineQueue) =
    (queue.Entries |> List.find (fun entry -> entry.Sequence = sequence)).State

let private isSynchronized sequence queue = EntryState.isSynchronized (stateOf sequence queue)

let private headToken (store: InMemoryStore) = store.Provider.ChangeToken chrona |> run |> ok

let private read (store: InMemoryStore) (relative: string) =
    match store.Provider.Read chrona (path relative) |> run |> ok with
    | ReadOutcome.Found stored -> Some stored
    | ReadOutcome.Absent
    | ReadOutcome.Erased _ -> None

// ---------------------------------------------------------------------------
// Unrepresentability: the compile-failure fixtures (ARCA-OFF-009).
// ---------------------------------------------------------------------------

[<Fact>]
let ``application code cannot forge a Synchronized entry state (ARCA-OFF-009)`` () =
    let build = CompileFailureBuild.result.Force()
    Assert.NotEqual(0, build.ExitCode)
    // The read-only pattern is no constructor (FS0039), and the internal
    // constructor is not accessible (FS1094).
    Assert.Equal<string list>([ "FS0039"; "FS1094" ], CompileFailureBuild.codesIn "ForgeSynchronizedState.fs" build)

[<Fact>]
let ``application code cannot forge a commit receipt (ARCA-OFF-009)`` () =
    let build = CompileFailureBuild.result.Force()
    // The 0.5 record syntax has no such labels (FS1129, FS0764), the internal
    // fields are not accessible (FS1093), and the minting module is not
    // visible (FS0039).
    Assert.Equal<string list>([ "FS0039"; "FS0764"; "FS1093"; "FS1129" ], CompileFailureBuild.codesIn "ForgeCommitReceipt.fs" build)

[<Fact>]
let ``application code cannot set an entry's state or a queue's entries by record copy (ARCA-OFF-009)`` () =
    let build = CompileFailureBuild.result.Force()
    Assert.Equal<string list>([ "FS1093"; "FS1129" ], CompileFailureBuild.codesIn "MutateEntryState.fs" build)
    let lines = build.Errors |> List.filter (fun error -> error.File = "MutateEntryState.fs") |> List.map _.Line |> List.distinct
    // Both the entry copy and the queue copy fail.
    Assert.Equal(2, lines.Length)

[<Fact>]
let ``legitimate reads compile, and only the forging fixtures fail (ARCA-OFF-009)`` () =
    let build = CompileFailureBuild.result.Force()
    Assert.Equal<string list>([], CompileFailureBuild.codesIn "LegitimateQueueReads.fs" build)

    Assert.Equal<string list>(
        [ "CachedTokenAsWriteCondition.fs"; "ForgeCommitReceipt.fs"; "ForgeSynchronizedState.fs"; "MutateEntryState.fs" ],
        CompileFailureBuild.failingFiles build
    )

// ---------------------------------------------------------------------------
// Application-level scenarios against the in-memory provider.
// ---------------------------------------------------------------------------

[<Fact>]
let ``a duplicate write with the same idempotency key never commits twice and never reads as a second save`` () =
    let store = InMemoryStore()
    let first, _ = queueOf [ create "a" ] |> sync store
    Assert.True(isSynchronized 1L first)

    // The same write submitted again (a double submit): the provider refuses
    // to re-create it, so the duplicate is a visible conflict, not a second
    // commit and not a second "saved".
    let again, result = enqueue [ create "a" ] first |> sync store

    match result, stateOf 2L again with
    | SyncStep.Blocked _, EntryState.Conflicted _ -> ()
    | other -> failwith $"expected the duplicate to be blocked as conflicted, got {other}"

    Assert.Equal(1, commits store)
    Assert.False((OfflineQueue.status again).Synchronized)

    // And the provider confirms the key landed exactly once.
    let pending =
        { IdempotencyKey = IdempotencyKey.create "safety-a" |> ok
          Base = ChangeToken "mem-0"
          Candidate = None
          Revisions = Map.empty }

    match store.Provider.Reconcile chrona pending |> run with
    | Ok(ReconcileOutcome.Landed receipt) -> Assert.Equal("safety-a", IdempotencyKey.value receipt.IdempotencyKey)
    | other -> failwith $"expected Landed, got {other}"

[<Fact>]
let ``recording the same acknowledgment twice changes nothing the second time`` () =
    let store = InMemoryStore()
    let inFlight = queueOf [ create "a" ] |> OfflineQueue.markInFlight 1L (headToken store) |> ok
    let receipt = store.Provider.Commit(create "a") |> run
    let synchronized = OfflineQueue.recordResult 1L receipt inFlight |> ok

    Assert.True(isSynchronized 1L synchronized)
    Assert.Equal(Error(QueueError.NotApplicable 1L), OfflineQueue.recordResult 1L receipt synchronized)

[<Fact>]
let ``concurrent writes from two devices to different records both synchronize, each exactly once`` () =
    let store = InMemoryStore()
    let base' = headToken store

    // Both devices mark their entry in flight on the same base, then both send.
    let deviceA = queueOf [ create "a" ] |> OfflineQueue.markInFlight 1L base' |> ok
    let deviceB = queueOf [ create "b" ] |> OfflineQueue.markInFlight 1L base' |> ok
    let sentA = store.Provider.Commit(create "a") |> run
    let sentB = store.Provider.Commit(create "b") |> run

    let doneA = OfflineQueue.recordResult 1L sentA deviceA |> ok
    let doneB = OfflineQueue.recordResult 1L sentB deviceB |> ok

    Assert.True(isSynchronized 1L doneA)
    Assert.True(isSynchronized 1L doneB)
    Assert.Equal(2, commits store)
    Assert.True((read store "investigations/a.json").IsSome && (read store "investigations/b.json").IsSome)

[<Fact>]
let ``concurrent writes from two devices to the same record: the second is conflicted, never synchronized`` () =
    let store = InMemoryStore()
    let seeded, _ = queueOf [ create "a" ] |> sync store
    let revision = (read store "investigations/a.json").Value.Revision
    let edit key = operation key [ Change.Update(path "investigations/a.json", $"{{\"by\":\"{key}\"}}", revision) ]

    let deviceA = enqueue [ edit "x" ] (OfflineQueue.prune seeded)
    let deviceB = queueOf [ edit "y" ]
    let doneA, _ = deviceA |> sync store
    let doneB, result = deviceB |> sync store

    Assert.True((OfflineQueue.status doneA).Synchronized)

    match result, stateOf 1L doneB with
    | SyncStep.Blocked _, EntryState.Conflicted [ "investigations/a.json" ] -> ()
    | other -> failwith $"expected device B to be conflicted on the record, got {other}"

    Assert.False((OfflineQueue.status doneB).Synchronized)
    Assert.Equal(2, commits store)

[<Fact>]
let ``a network timeout leaves the entry outcome-unknown and blocks everything after it until reconciled`` () =
    let store = InMemoryStore()
    store.Arrange InMemoryFault.OutcomeUnknownLost
    let saves = Saves()
    let queue, result = OfflineSync.step store.Provider saves.Store chrona (queueOf [ create "a"; create "b" ]) |> run

    match result, stateOf 1L queue with
    | SyncStep.Progressed _, EntryState.OutcomeUnknown _ -> ()
    | other -> failwith $"expected an unknown outcome, got {other}"

    // Persisted as unknown, reported as unsynchronized, and blocking.
    Assert.True(saves.All |> List.exists (fun saved -> match stateOf 1L saved with EntryState.OutcomeUnknown _ -> true | _ -> false))
    let status = OfflineQueue.status queue
    Assert.False(status.Synchronized)
    Assert.Equal(1, status.OutcomeUnknown)
    Assert.Equal(Some 1L, OfflineQueue.blocked queue |> Option.map _.Sequence)
    Assert.Equal(Error(QueueError.NotApplicable 2L), OfflineQueue.markInFlight 2L (headToken store) queue |> Result.map ignore)

[<Fact>]
let ``an unknown outcome that landed is reconciled to synchronized from the provider's receipt, without a resend`` () =
    let store = InMemoryStore()
    store.Arrange InMemoryFault.OutcomeUnknownLanded
    let unknown, _ = step store (queueOf [ create "a" ])
    Assert.Equal(1, commits store)

    let reconciled, result = step store unknown

    match result, stateOf 1L reconciled with
    | SyncStep.Progressed _, EntryState.Synchronized token -> Assert.Equal(store.State.History.Head.Token, ChangeToken token)
    | other -> failwith $"expected Synchronized after reconciliation, got {other}"

    Assert.Equal(1, commits store)

[<Fact>]
let ``an unknown outcome that was lost is reconciled to pending, then sent once with the same key`` () =
    let store = InMemoryStore()
    store.Arrange InMemoryFault.OutcomeUnknownLost
    let unknown, _ = step store (queueOf [ create "a" ])
    Assert.Equal(0, commits store)

    let reconciled, _ = step store unknown

    match stateOf 1L reconciled with
    | EntryState.Pending -> ()
    | other -> failwith $"expected Pending after a lost outcome, got {other}"

    let finished, _ = sync store reconciled
    Assert.True(isSynchronized 1L finished)
    Assert.Equal(1, commits store)

[<Fact>]
let ``a credential change mid-queue defers the rest without losing or confirming them, and the new credential sends each once`` () =
    let store = InMemoryStore()
    let account = AccountId.ofActor (ActorId.create "u-1" |> ok)

    let queue =
        [ create "a"; create "b"; create "c" ]
        |> List.fold (fun queue op -> OfflineQueue.enqueueFor account at op queue |> ok |> fst) (OfflineQueue.create OfflinePolicy.QueueWrites)

    let first, _ = step store queue
    Assert.True(isSynchronized 1L first)

    // The credential is revoked (signed out, rotated, expired) mid-queue.
    store.Arrange InMemoryFault.CredentialRevoked
    let deferred, result = sync store first

    match result with
    | SyncStep.Deferred _ -> ()
    | other -> failwith $"expected Deferred, got {other}"

    Assert.Equal<string list>([ "synchronized"; "pending"; "pending" ], deferred.Entries |> List.map (_.State >> EntryState.name))
    Assert.False((OfflineQueue.status deferred).Synchronized)

    // A new credential for the same location: a new provider over the same
    // repository state.
    let renewed = InMemoryStore({ store.State with Standing = [] })
    let finished, _ = sync renewed deferred

    Assert.True((OfflineQueue.status finished).Synchronized)
    Assert.Equal(3, commits renewed)
    // The entries still belong to the account that made them.
    Assert.All(finished.Entries, fun entry -> Assert.Equal(Some(AccountId.toWire account), entry.Operation.AccountId))

[<Fact>]
let ``reloading while an entry is in flight round-trips exactly, and recovery never reads it as saved`` () =
    let store = InMemoryStore()
    let inFlight = queueOf [ create "a"; create "b" ] |> OfflineQueue.markInFlight 1L (headToken store) |> ok

    let text = OfflineQueue.encode inFlight |> ok
    let reloaded = OfflineQueue.decode text |> ok
    Assert.Equal(inFlight, reloaded)
    Assert.Equal(text, OfflineQueue.encode reloaded |> ok)

    match stateOf 1L reloaded with
    | EntryState.InFlight _ -> ()
    | other -> failwith $"expected InFlight after reload, got {other}"

    Assert.False((OfflineQueue.status reloaded).Synchronized)

    // After the restart the in-flight entry is an unknown outcome, never
    // assumed to have landed.
    let recovered = OfflineQueue.recover reloaded

    match stateOf 1L recovered with
    | EntryState.OutcomeUnknown _ -> ()
    | other -> failwith $"expected OutcomeUnknown after recovery, got {other}"

    let finished, _ = sync store recovered
    Assert.True((OfflineQueue.status finished).Synchronized)
    Assert.Equal(2, commits store)

[<Fact>]
let ``a queued update of a record deleted remotely is conflicted, never synchronized`` () =
    let store = InMemoryStore()
    let seeded, _ = queueOf [ create "a" ] |> sync store
    let revision = (read store "investigations/a.json").Value.Revision

    // Someone deletes the record on the provider while the update waits offline.
    store.WriteExternally(location, "apps/chrona/investigations/a.json", None)

    let update = operation "u" [ Change.Update(path "investigations/a.json", "{\"id\":\"a\",\"v\":2}", revision) ]
    let queue, result = enqueue [ update ] (OfflineQueue.prune seeded) |> sync store

    match result, stateOf 2L queue with
    | SyncStep.Blocked _, EntryState.Conflicted [ "investigations/a.json" ] -> ()
    | other -> failwith $"expected the update to be conflicted on the deleted record, got {other}"

    Assert.False((OfflineQueue.status queue).Synchronized)
    Assert.True((read store "investigations/a.json").IsNone, "the deleted record is not silently re-created")

[<Fact>]
let ``a queued update over a record changed remotely is conflicted, and synchronizes only after the application revises it`` () =
    let store = InMemoryStore()
    let seeded, _ = queueOf [ create "a" ] |> sync store
    let revision = (read store "investigations/a.json").Value.Revision
    store.WriteExternally(location, "apps/chrona/investigations/a.json", Some "{\"id\":\"a\",\"by\":\"them\"}")

    let mine = operation "m" [ Change.Update(path "investigations/a.json", "{\"id\":\"a\",\"by\":\"me\"}", revision) ]
    let conflicted, _ = enqueue [ mine ] (OfflineQueue.prune seeded) |> sync store

    match stateOf 2L conflicted with
    | EntryState.Conflicted _ -> ()
    | other -> failwith $"expected Conflicted, got {other}"

    let current = (read store "investigations/a.json").Value.Revision
    let revised = OfflineQueue.revise 2L (operation "m2" [ Change.Update(path "investigations/a.json", "{\"id\":\"a\",\"by\":\"both\"}", current) ]) conflicted |> ok
    let finished, _ = sync store revised

    Assert.True(isSynchronized 2L finished)
    Assert.Equal("{\"id\":\"a\",\"by\":\"both\"}", (read store "investigations/a.json").Value.Content)

[<Fact>]
let ``a queued write beside an unrelated remote change synchronizes without a conflict`` () =
    let store = InMemoryStore()
    let base' = headToken store
    let inFlight = queueOf [ create "a" ] |> OfflineQueue.markInFlight 1L base' |> ok

    // Another writer changes a different record after the entry was built.
    store.WriteExternally(location, "apps/chrona/investigations/other.json", Some "{\"id\":\"other\"}")

    let result = store.Provider.Commit(create "a") |> run
    let finished = OfflineQueue.recordResult 1L result inFlight |> ok

    Assert.True(isSynchronized 1L finished)
    Assert.True((read store "investigations/other.json").IsSome, "the unrelated change is kept")

[<Fact>]
let ``a stale acknowledgment for an entry no longer in flight is refused and changes nothing (ARCA-OFF-009)`` () =
    let store = InMemoryStore()
    let inFlight = queueOf [ create "a" ] |> OfflineQueue.markInFlight 1L (headToken store) |> ok
    let late = store.Provider.Commit(create "a") |> run

    // The app restarted before the answer arrived: the entry is now an
    // unknown outcome, to be reconciled through the provider.
    let recovered = OfflineQueue.recover inFlight
    Assert.Equal(Error(QueueError.NotApplicable 1L), OfflineQueue.recordResult 1L late recovered)

    // Nor does a pending entry take a receipt it never asked for.
    let pending = queueOf [ create "a" ]
    Assert.Equal(Error(QueueError.NotApplicable 1L), OfflineQueue.recordResult 1L late pending)

    // A reconciliation outcome for an entry that is not unknown is refused too.
    match late with
    | Ok receipt -> Assert.Equal(Error(QueueError.NotApplicable 1L), OfflineQueue.recordReconciliation 1L (ReconcileOutcome.Landed receipt) inFlight)
    | Error failure -> failwith $"expected the commit to land, got {failure}"

[<Fact>]
let ``out-of-order responses: a receipt for another entry's operation is refused and changes nothing (ARCA-OFF-009)`` () =
    let store = InMemoryStore()
    let queue = queueOf [ create "a"; create "b" ]
    let inFlight = OfflineQueue.markInFlight 1L (headToken store) queue |> ok

    // The answer for b (sent earlier by another tab or a previous attempt)
    // arrives where the answer for a was expected.
    let forB = store.Provider.Commit(create "b") |> run
    Assert.Equal(Error(QueueError.ReceiptMismatch 1L), OfflineQueue.recordResult 1L forB inFlight)
    // Entry b is pending, not in flight: its own receipt is not applicable yet.
    Assert.Equal(Error(QueueError.NotApplicable 2L), OfflineQueue.recordResult 2L forB inFlight)

    // Reconciliation is held to the same rule.
    let unknown = OfflineQueue.recover inFlight

    match forB with
    | Ok receipt -> Assert.Equal(Error(QueueError.ReceiptMismatch 1L), OfflineQueue.recordReconciliation 1L (ReconcileOutcome.Landed receipt) unknown)
    | Error failure -> failwith $"expected b to land, got {failure}"

    // The right answer is still accepted, and the queue then finishes in order.
    let forA = store.Provider.Commit(create "a") |> run
    let first = OfflineQueue.recordResult 1L forA inFlight |> ok
    Assert.True(isSynchronized 1L first)

[<Fact>]
let ``only a provider's receipt for the entry's own operation synchronizes it (ARCA-OFF-009)`` () =
    let store = InMemoryStore()
    let inFlight = queueOf [ create "a" ] |> OfflineQueue.markInFlight 1L (headToken store) |> ok

    // A first-party mint for another key (only Arca's own assemblies can do
    // this) is still refused: the receipt must confirm this entry.
    let other = CommitReceipt.mint (IdempotencyKey.create "safety-zz" |> ok) (ChangeToken "x") Map.empty
    Assert.Equal(Error(QueueError.ReceiptMismatch 1L), OfflineQueue.recordResult 1L (Ok other) inFlight)

    // Every failure the provider can report leaves the entry unsynchronized.
    let failures =
        [ StorageFailure.Conflicted []
          StorageFailure.RateLimited(None, None)
          StorageFailure.Refused(WriteRefusal.CredentialUnavailable "revoked")
          StorageFailure.StaleChangeToken(ChangeToken "a", ChangeToken "b")
          StorageFailure.ObjectTooLarge("investigations/a.json", 2L, 1L)
          StorageFailure.ProviderFailed("X", true, "transient")
          StorageFailure.ProviderFailed("X", false, "permanent") ]

    for failure in failures do
        let after = OfflineQueue.recordResult 1L (Error failure) inFlight |> ok
        Assert.False(isSynchronized 1L after, $"{failure} must not synchronize")
        Assert.False((OfflineQueue.status after).Synchronized)
