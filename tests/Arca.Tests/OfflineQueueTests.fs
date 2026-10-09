/// The offline change queue (ARCA-OFF-001..006): a pure model, write-ahead
/// synchronization against a provider, and the localStorage queue store.
module Arca.Tests.OfflineQueueTests

open System
open System.Collections.Generic
open Arca
open Arca.GitHub
open Xunit
open FsCheck.Xunit
open FsCheck.FSharp
open FsCheck

let private ok result =
    match result with
    | Ok value -> value
    | Error error -> failwith $"expected Ok, got {error}"

let private location = DataLocation.create "acme" "data" "main" "apps" |> ok

let private binding application =
    { Application = AppId.create application |> ok
      Environment = { Kind = EnvironmentKind.Test; Name = "offline" }
      Location = location }

let private chrona = Namespace.ofApplication (binding "chrona") |> ok
let private summa = Namespace.ofApplication (binding "summa") |> ok
let private at = DateTimeOffset(2026, 10, 8, 12, 0, 0, TimeSpan.Zero)
let private path text = RelativePath.parse text |> ok

let private operationIn ns (key: string) (changes: Change list) =
    Operation.create
        ns
        { Summary = $"note {key}"
          Actor = { Kind = ActorKind.Human; Id = ActorId.create "u-1" |> ok }
          ProviderIdentity = None
          ExecutionId = None
          CorrelationId = CorrelationId.create $"c-{key}" |> ok
          IdempotencyKey = IdempotencyKey.create $"offline-{key}" |> ok }
        changes
    |> ok

let private create (key: string) =
    operationIn chrona key [ Change.Create(path $"notes/{key}.json", $"{{\"n\":\"{key}\"}}") ]

let private enqueueAll (operations: Operation list) =
    operations
    |> List.fold (fun queue operation -> OfflineQueue.enqueue at operation queue |> ok |> fst) (OfflineQueue.create OfflinePolicy.QueueWrites)

/// A queue store over a mutable cell, recording every save.
type private MemoryQueueStore() =
    let saves = List<OfflineQueue>()
    member val FailSaves = false with get, set
    member _.Saves = List.ofSeq saves

    member this.Store: QueueStore =
        { Load = fun () -> async { return Ok(Seq.tryLast saves) }
          Save =
            fun queue ->
                async {
                    if this.FailSaves then
                        return Error QueueStoreFailure.Unavailable
                    else
                        saves.Add queue
                        return Ok()
                } }

let private sync (store: InMemoryStore) (queueStore: QueueStore) queue =
    OfflineSync.run store.Provider queueStore chrona 100 queue |> Async.RunSynchronously

/// The commits Arca made (the in-memory store starts with an "initial" commit).
let private arcaCommits (store: InMemoryStore) =
    store.State.History |> List.rev |> List.map _.Message |> List.filter (fun message -> message.StartsWith "chrona:")

let private states (queue: OfflineQueue) = queue.Entries |> List.map _.State

[<Fact>]
let ``an application that did not opt in gets a read-only degraded mode (ARCA-OFF-005)`` () =
    let queue = OfflineQueue.create OfflinePolicy.ReadOnlyWhenOffline
    Assert.Equal(Error QueueError.OfflineWritesDisabled, OfflineQueue.enqueue at (create "a") queue |> Result.map ignore)

[<Fact>]
let ``the queue is plain, inspectable data with keys and expected revisions (ARCA-OFF-001)`` () =
    let update = operationIn chrona "u" [ Change.Update(path "notes/a.json", "{}", Revision "r-1") ] |> Operation.requireChangeToken (ChangeToken "t-1")
    let queue = enqueueAll [ create "a"; update ]

    Assert.Equal<int64 list>([ 1L; 2L ], queue.Entries |> List.map _.Sequence)
    Assert.Equal<EntryState list>([ EntryState.Pending; EntryState.Pending ], states queue)
    let second = queue.Entries[1].Operation
    Assert.Equal("offline-u", second.IdempotencyKey)
    Assert.Equal<QueuedChange list>([ QueuedChange.Update("notes/a.json", "{}", "r-1") ], second.Changes)
    Assert.Equal(Some "t-1", second.ExpectedChangeToken)
    Assert.Equal<Change list>(update.Changes, (OfflineQueue.operationOf chrona second |> ok).Changes)

[<Fact>]
let ``a queued namespace condition survives persistence and is enforced when sent (ARCA-CON-005)`` () =
    let store = InMemoryStore()
    let state = store.Provider.NamespaceState chrona |> Async.RunSynchronously |> ok
    let held = create "n" |> Operation.requireNamespaceToken state.NamespaceToken
    let unheld = create "plain"
    let queue = enqueueAll [ held ]
    let (NamespaceToken expected) = state.NamespaceToken
    Assert.Equal(Some expected, queue.Entries.Head.Operation.ExpectedNamespaceToken)

    // Persisted and read back, the condition is still there.
    let reloaded = OfflineQueue.encode queue |> ok |> OfflineQueue.decode |> ok
    Assert.Equal(queue, reloaded)
    Assert.Equal(Some state.NamespaceToken, (OfflineQueue.operationOf chrona reloaded.Entries.Head.Operation |> ok).ExpectedNamespaceToken)

    // A queue without one keeps the exact text earlier Arca wrote: no new field.
    let plainText = OfflineQueue.encode (enqueueAll [ unheld ]) |> ok
    Assert.DoesNotContain("expectedNamespaceToken", plainText)

    // Something changes inside the namespace before the queue drains: the
    // entry is Conflicted, never applied over the newer state.
    store.WriteExternally(location, (Namespace.resolve chrona (path "notes/other.json") |> ok).Path, Some "{}")
    let queueStore = MemoryQueueStore()
    let drained = sync store queueStore.Store reloaded
    Assert.Equal<EntryState list>([ EntryState.Conflicted [] ], states (fst drained))

[<Fact>]
let ``the account id survives persistence, and a queue without one keeps its earlier text (ARCA-OFF-007)`` () =
    let account = AccountId.ProviderSubject("github", "583231")
    let actor = AccountId.ofActor (ActorId.create "chrona:member:42" |> ok)

    for id in [ account; actor ] do
        Assert.Equal(Some id, AccountId.ofWire (AccountId.toWire id))

    Assert.Equal(None, AccountId.ofWire "Alex")
    let queue = OfflineQueue.enqueueFor account at (create "a") (OfflineQueue.create OfflinePolicy.QueueWrites) |> ok |> fst
    Assert.Equal(Some "subject:github:583231", queue.Entries.Head.Operation.AccountId)
    Assert.Equal(queue, OfflineQueue.encode queue |> ok |> OfflineQueue.decode |> ok)
    Assert.DoesNotContain("accountId", OfflineQueue.encode (enqueueAll [ create "b" ]) |> ok)

[<Fact>]
let ``revising an entry keeps its account, sequence and enqueue time (ARCA-OFF-007)`` () =
    let account = AccountId.ProviderSubject("github", "583231")
    let queue, sequence = OfflineQueue.enqueueFor account at (create "a") (OfflineQueue.create OfflinePolicy.QueueWrites) |> ok
    let conflicted = { queue with Entries = queue.Entries |> List.map (fun entry -> { entry with State = EntryState.Conflicted [ "notes/a.json" ] }) }
    let before = conflicted.Entries.Head
    let revised = OfflineQueue.revise sequence (create "a-revised") conflicted |> ok
    let after = revised.Entries.Head

    Assert.Equal(Some(AccountId.toWire account), after.Operation.AccountId)
    Assert.Equal(before.Sequence, after.Sequence)
    Assert.Equal(before.EnqueuedAt, after.EnqueuedAt)
    Assert.Equal(EntryState.Pending, after.State)
    Assert.Equal("offline-a-revised", after.Operation.IdempotencyKey)
    // And through persistence.
    Assert.Equal(revised, OfflineQueue.encode revised |> ok |> OfflineQueue.decode |> ok)

[<Fact>]
let ``an erasure cannot be put into the queue by revising an entry (ARCA-INT-005)`` () =
    let store = InMemoryStore()
    let record =
        { Id = RecordId.create "E-1" |> ok
          Type = RecordType.create "chrona.entry" |> ok
          SchemaVersion = 1
          Mutability = Mutability.Immutable
          Body = Json.objectOf [] }

    let recordPath = Layout.recordPath { Type = record.Type; Partition = []; Id = record.Id } |> ok
    store.Provider.Commit(operationIn chrona "seed-e" [ Change.Create(recordPath, Record.encode Record.DefaultMaxBytes record |> ok) ]) |> Async.RunSynchronously |> ok |> ignore

    let validated =
        match store.Provider.Read chrona recordPath |> Async.RunSynchronously |> ok with
        | ReadOutcome.Found stored -> { Record = record; Revision = stored.Revision; ContentHash = Record.contentHash record }
        | other -> failwith $"expected Found, got {other}"

    let erasure =
        Erasure.operation
            chrona
            (operationIn chrona "erase" [ Change.Create(path "notes/x.json", "{}") ]).Metadata
            [ Erasure.request recordPath validated at "rule" |> ok ]
        |> ok

    let queue, sequence = OfflineQueue.enqueue at (create "a") (OfflineQueue.create OfflinePolicy.QueueWrites) |> ok
    let conflicted = { queue with Entries = queue.Entries |> List.map (fun entry -> { entry with State = EntryState.Conflicted [] }) }

    match OfflineQueue.revise sequence erasure conflicted with
    | Error(QueueError.InvalidOperation _) -> ()
    | other -> failwith $"an erasure was queued through revise: {other}"

[<Fact>]
let ``an entry is replayed only in its own namespace`` () =
    let queue = enqueueAll [ create "a" ]

    match OfflineQueue.operationOf summa queue.Entries.Head.Operation with
    | Error(QueueError.InvalidOperation _) -> ()
    | other -> failwith $"expected InvalidOperation, got {other}"

[<Fact>]
let ``writes made offline synchronize in order, one commit each (ARCA-OFF-001, ARCA-OFF-004)`` () =
    let store = InMemoryStore()
    let queueStore = MemoryQueueStore()
    let queue, result = enqueueAll [ create "a"; create "b"; create "c" ] |> sync store queueStore.Store

    Assert.Equal(SyncStep.Idle, result)
    Assert.True((OfflineQueue.status queue).Synchronized)
    Assert.Equal<string list>([ "chrona: note a"; "chrona: note b"; "chrona: note c" ], arcaCommits store |> List.map (fun m -> m.Split('\n')[0]))
    Assert.Empty((OfflineQueue.prune queue).Entries)

[<Fact>]
let ``each entry is persisted in flight before it is sent (write-ahead, ARCA-OFF-004)`` () =
    let store = InMemoryStore()
    let queueStore = MemoryQueueStore()
    enqueueAll [ create "a" ] |> sync store queueStore.Store |> ignore

    match queueStore.Saves |> List.map (fun q -> q.Entries.Head.State) with
    | [ EntryState.InFlight _; EntryState.Synchronized _ ] -> ()
    | other -> failwith $"expected in-flight then synchronized, got {other}"

[<Fact>]
let ``nothing is sent when the in-flight state cannot be persisted`` () =
    let store = InMemoryStore()
    let queueStore = MemoryQueueStore(FailSaves = true)
    let queue, result = enqueueAll [ create "a" ] |> sync store queueStore.Store

    Assert.Equal(SyncStep.Deferred "the queue could not be saved", result)
    Assert.Equal<EntryState list>([ EntryState.Pending ], states queue)
    Assert.Empty(arcaCommits store)

[<Fact>]
let ``an unknown outcome that landed is reconciled, never sent twice (ARCA-OFF-004)`` () =
    let store = InMemoryStore()
    store.Arrange InMemoryFault.OutcomeUnknownLanded
    let queueStore = MemoryQueueStore()
    let queue, result = enqueueAll [ create "a"; create "b" ] |> sync store queueStore.Store

    Assert.Equal(SyncStep.Idle, result)
    Assert.True((OfflineQueue.status queue).Synchronized)
    Assert.Equal(2, (arcaCommits store).Length)

[<Fact>]
let ``an unknown outcome that was lost is sent again with the same key (ARCA-OFF-004)`` () =
    let store = InMemoryStore()
    store.Arrange InMemoryFault.OutcomeUnknownLost
    let queueStore = MemoryQueueStore()
    let queue, _ = enqueueAll [ create "a" ] |> sync store queueStore.Store

    Assert.True((OfflineQueue.status queue).Synchronized)
    Assert.Equal(1, (arcaCommits store).Length)

    let unknown =
        queueStore.Saves |> List.exists (fun q -> match q.Entries.Head.State with EntryState.OutcomeUnknown _ -> true | _ -> false)

    Assert.True(unknown, "the unknown outcome must be persisted before reconciliation")

[<Fact>]
let ``after a restart an in-flight entry is reconciled, not resent blindly (ARCA-OFF-004)`` () =
    // The entry was sent and landed, but the app stopped before recording it.
    let store = InMemoryStore()
    let queue = enqueueAll [ create "a"; create "b" ]
    let token = store.Provider.ChangeToken chrona |> Async.RunSynchronously |> ok
    let inFlight = OfflineQueue.markInFlight 1L token queue |> ok
    store.Provider.Commit(create "a") |> Async.RunSynchronously |> ok |> ignore

    let reloaded = OfflineQueue.encode inFlight |> ok |> OfflineQueue.decode |> ok |> OfflineQueue.recover

    match reloaded.Entries.Head.State with
    | EntryState.OutcomeUnknown _ -> ()
    | other -> failwith $"expected OutcomeUnknown after recovery, got {other}"

    let queue, _ = reloaded |> sync store (MemoryQueueStore().Store)
    Assert.True((OfflineQueue.status queue).Synchronized)
    Assert.Equal(2, (arcaCommits store).Length)

[<Fact>]
let ``an entry in flight when the app stopped, which never landed, is sent after reconciliation`` () =
    let store = InMemoryStore()
    let token = store.Provider.ChangeToken chrona |> Async.RunSynchronously |> ok
    let inFlight = enqueueAll [ create "a" ] |> OfflineQueue.markInFlight 1L token |> ok

    let queue, _ = inFlight |> sync store (MemoryQueueStore().Store)
    Assert.True((OfflineQueue.status queue).Synchronized)
    Assert.Equal(1, (arcaCommits store).Length)

[<Fact>]
let ``a conflict blocks the entries after it and becomes the application's decision (ARCA-OFF-004)`` () =
    let store = InMemoryStore()
    let queueStore = MemoryQueueStore()
    let seeded, _ = enqueueAll [ create "a" ] |> sync store queueStore.Store
    let stale = operationIn chrona "u" [ Change.Update(path "notes/a.json", "{\"n\":\"u\"}", Revision "not-current") ]

    let queue =
        [ stale; create "b" ]
        |> List.fold (fun queue operation -> OfflineQueue.enqueue at operation queue |> ok |> fst) (OfflineQueue.prune seeded)

    let blocked, result = queue |> sync store queueStore.Store

    match result with
    | SyncStep.Blocked entry -> Assert.Equal(EntryState.Conflicted [ "notes/a.json" ], entry.State)
    | other -> failwith $"expected Blocked, got {other}"

    Assert.Equal(EntryState.Pending, blocked.Entries[1].State)
    Assert.Equal(1, (arcaCommits store).Length)
    let status = OfflineQueue.status blocked
    Assert.False(status.Synchronized)
    Assert.Equal(1, status.Conflicted)
    Assert.Equal(Some blocked.Entries[0], OfflineQueue.blocked blocked)

    // Running again does not send anything past the conflict.
    let again, _ = blocked |> sync store queueStore.Store
    Assert.Equal(1, (arcaCommits store).Length)

    // The application reloads, revalidates and revises the entry in place.
    let current =
        match store.Provider.Read chrona (path "notes/a.json") |> Async.RunSynchronously |> ok with
        | ReadOutcome.Found stored -> stored.Revision
        | ReadOutcome.Absent
        | ReadOutcome.Erased _ -> failwith "seeded"

    let revised =
        OfflineQueue.revise again.Entries[0].Sequence (operationIn chrona "u2" [ Change.Update(path "notes/a.json", "{\"n\":\"u\"}", current) ]) again
        |> ok

    let finished, _ = revised |> sync store queueStore.Store
    Assert.True((OfflineQueue.status finished).Synchronized)
    Assert.Equal(3, (arcaCommits store).Length)

[<Fact>]
let ``an abandoned entry stays visible until pruned, and unblocks the rest`` () =
    let store = InMemoryStore()
    let stale = operationIn chrona "u" [ Change.Update(path "notes/x.json", "{}", Revision "gone") ]
    let blocked, _ = enqueueAll [ stale; create "b" ] |> sync store (MemoryQueueStore().Store)
    let abandoned = OfflineQueue.abandon 1L "superseded" blocked |> ok
    Assert.Equal(EntryState.Abandoned "superseded", abandoned.Entries.Head.State)

    let finished, _ = abandoned |> sync store (MemoryQueueStore().Store)
    Assert.True((OfflineQueue.status finished).Synchronized)
    Assert.Equal(2, finished.Entries.Length)
    Assert.Empty((OfflineQueue.prune finished).Entries)

[<Fact>]
let ``a rate limit or revoked credential defers without losing anything`` () =
    let store = InMemoryStore()
    store.Arrange(InMemoryFault.RateLimited None)
    let queue, result = enqueueAll [ create "a" ] |> sync store (MemoryQueueStore().Store)
    Assert.Equal(SyncStep.Deferred "the provider is unavailable", result)
    Assert.Equal<EntryState list>([ EntryState.Pending ], states queue)

    store.Arrange InMemoryFault.CredentialRevoked
    let queue, _ = queue |> sync store (MemoryQueueStore().Store)
    Assert.Equal<EntryState list>([ EntryState.Pending ], states queue)
    Assert.Empty(arcaCommits store)

[<Fact>]
let ``a read-only location refuses the entry for the application to decide`` () =
    let store = InMemoryStore()
    store.Arrange InMemoryFault.ReadOnly
    let queue, result = enqueueAll [ create "a" ] |> sync store (MemoryQueueStore().Store)

    match result, queue.Entries.Head.State with
    | SyncStep.Blocked _, EntryState.Refused _ -> ()
    | other -> failwith $"expected a refused, blocked entry, got {other}"

    Assert.Equal(1, (OfflineQueue.status queue).Refused)

[<Fact>]
let ``transitions apply only to the entry and state they name`` () =
    let queue = enqueueAll [ create "a"; create "b" ]
    Assert.Equal(Error(QueueError.NotApplicable 2L), OfflineQueue.markInFlight 2L (ChangeToken "t") queue |> Result.map ignore)
    Assert.Equal(Error(QueueError.UnknownEntry 9L), OfflineQueue.abandon 9L "x" queue |> Result.map ignore)
    Assert.Equal(Error(QueueError.NotApplicable 1L), OfflineQueue.recordResult 1L (Error(StorageFailure.Conflicted [])) queue |> Result.map ignore)
    Assert.Equal(Error(QueueError.NotApplicable 1L), OfflineQueue.revise 1L (create "z") queue |> Result.map ignore)

// ---------------------------------------------------------------------------
// Properties
// ---------------------------------------------------------------------------

/// Any entry state, with text that needs escaping.
let stateGen =
    Gen.oneof
        [ Gen.constant EntryState.Pending
          Gen.elements [ "t1"; "t2" ] |> Gen.map EntryState.InFlight
          Gen.elements [ "t3"; "t4" ] |> Gen.map EntryState.Synchronized
          Gen.elements [ []; [ "notes/a.json" ]; [ "a"; "b/c" ] ] |> Gen.map EntryState.Conflicted
          Gen.elements [ "t5" ] |> Gen.map EntryState.OutcomeUnknown
          Gen.elements [ "nope"; "\"quoted\"\n" ] |> Gen.map EntryState.Refused
          Gen.elements [ "superseded"; "é 😀" ] |> Gen.map EntryState.Abandoned ]

/// Arbitrary queues of up to six entries in every state, under either policy.
let queueGen =
    gen {
        let! count = Gen.choose (0, 6)
        let! chosen = Gen.listOfLength count stateGen
        let! policy = Gen.elements [ OfflinePolicy.QueueWrites; OfflinePolicy.ReadOnlyWhenOffline ]
        let queue = enqueueAll [ for i in 1..count -> create $"k{i}" ]

        return
            { queue with
                Policy = policy
                Entries = List.map2 (fun entry state -> { entry with State = state }) queue.Entries chosen }
    }

[<Property>]
let ``a persisted queue reads back exactly (ARCA-OFF-002)`` () =
    Prop.forAll (Arb.fromGen queueGen) (fun queue -> OfflineQueue.decode (OfflineQueue.encode queue |> ok) = Ok queue)

[<Property>]
let ``unsynchronized work is never reported as synchronized (ARCA-OFF-003)`` () =
    Prop.forAll (Arb.fromGen queueGen) (fun queue ->
        let settled =
            queue.Entries
            |> List.forall (fun entry ->
                match entry.State with
                | EntryState.Synchronized _
                | EntryState.Abandoned _ -> true
                | _ -> false)

        (OfflineQueue.status queue).Synchronized = settled)

[<Property(MaxTest = 30)>]
let ``synchronization keeps order and loses nothing, whatever faults are arranged (ARCA-OFF-004)`` () =
    let faults =
        Gen.elements
            [ None
              Some InMemoryFault.OutcomeUnknownLanded
              Some InMemoryFault.OutcomeUnknownLost
              Some(InMemoryFault.RateLimited None) ]
        |> Gen.listOfLength 4

    Prop.forAll (Arb.fromGen (Gen.zip (Gen.choose (1, 5)) faults)) (fun (count, arranged) ->
        let store = InMemoryStore()
        let queueStore = MemoryQueueStore()
        let keys = [ for i in 1..count -> $"k{i}" ]

        // Faults strike at different points; synchronization is retried until settled.
        let rec drive queue (faults: InMemoryFault option list) rounds =
            match faults with
            | Some fault :: rest ->
                store.Arrange fault
                let next, _ = sync store queueStore.Store queue
                drive next rest (rounds + 1)
            | None :: rest -> drive queue rest rounds
            | [] -> sync store queueStore.Store queue |> fst

        let finished = drive (enqueueAll (keys |> List.map create)) arranged 0
        let subjects = arcaCommits store |> List.map (fun m -> m.Split('\n')[0])

        (OfflineQueue.status finished).Synchronized
        && subjects = (keys |> List.map (fun key -> $"chrona: note {key}")))

// ---------------------------------------------------------------------------
// The localStorage queue store (DF-ARCA-2026-0005)
// ---------------------------------------------------------------------------

/// A localStorage double that executes requests against a dictionary.
type private FakeLocalStorage() =
    let items = Dictionary<string, string>()
    member val Failure: LocalStorageFailure option = None with get, set
    member val Requests: LocalStorageRequest list = [] with get, set
    member _.Items = items

    member this.Execute(request: LocalStorageRequest) =
        async {
            this.Requests <- this.Requests @ [ request ]

            match this.Failure, request with
            | Some failure, _ -> return LocalStorageOutcome.Failure failure
            | None, LocalStorageRequest.Get key ->
                match items.TryGetValue key with
                | true, value -> return LocalStorageOutcome.Success(Some value)
                | _ -> return LocalStorageOutcome.Success None
            | None, LocalStorageRequest.Set(key, value) ->
                items[key] <- value
                return LocalStorageOutcome.Success None
            | None, LocalStorageRequest.Remove key ->
                items.Remove key |> ignore
                return LocalStorageOutcome.Success None
        }

[<Fact>]
let ``each namespace has its own localStorage key`` () =
    let dataset = Namespace.ofDataset (binding "summa") (DatasetId.create "org-1" |> ok) None |> ok
    Assert.Equal("arca.queue.chrona", LocalStorageQueue.key chrona)
    Assert.Equal("arca.queue.summa.org-1", LocalStorageQueue.key dataset)

[<Fact>]
let ``the queue survives a reload through localStorage (ARCA-OFF-002)`` () =
    let browser = FakeLocalStorage()
    let store = LocalStorageQueue.store browser.Execute LocalStorageQueue.DefaultBudget chrona
    Assert.Equal(Ok None, store.Load() |> Async.RunSynchronously)

    let queue = enqueueAll [ create "a"; create "b" ]
    Assert.Equal(Ok(), store.Save queue |> Async.RunSynchronously)

    let reloaded = LocalStorageQueue.store browser.Execute LocalStorageQueue.DefaultBudget chrona
    Assert.Equal(Ok(Some queue), reloaded.Load() |> Async.RunSynchronously)
    Assert.True(Json.isCanonical browser.Items["arca.queue.chrona"])

[<Fact>]
let ``a queue over budget is refused whole, never truncated (ARCA-OFF-002)`` () =
    let browser = FakeLocalStorage()
    let store = LocalStorageQueue.store browser.Execute 100L chrona
    let queue = enqueueAll [ create "a"; create "b" ]

    match store.Save queue |> Async.RunSynchronously with
    | Error(QueueStoreFailure.QuotaExceeded(bytes, 100L)) -> Assert.True(bytes > 100L)
    | other -> failwith $"expected QuotaExceeded, got {other}"

    Assert.Empty(browser.Requests)

[<Fact>]
let ``browser quota and unavailability are typed failures`` () =
    let browser = FakeLocalStorage(Failure = Some LocalStorageFailure.QuotaExceeded)
    let store = LocalStorageQueue.store browser.Execute LocalStorageQueue.DefaultBudget chrona

    match store.Save(enqueueAll [ create "a" ]) |> Async.RunSynchronously with
    | Error(QueueStoreFailure.QuotaExceeded _) -> ()
    | other -> failwith $"expected QuotaExceeded, got {other}"

    browser.Failure <- Some LocalStorageFailure.Unavailable
    Assert.Equal(Error QueueStoreFailure.Unavailable, store.Save(enqueueAll [ create "a" ]) |> Async.RunSynchronously)
    Assert.Equal(Error QueueStoreFailure.Unavailable, store.Load() |> Async.RunSynchronously)

[<Fact>]
let ``a corrupt or foreign stored queue is refused, never used`` () =
    let browser = FakeLocalStorage()
    browser.Items["arca.queue.chrona"] <- "{\"arcaQueue\":2}"
    let store = LocalStorageQueue.store browser.Execute LocalStorageQueue.DefaultBudget chrona

    match store.Load() |> Async.RunSynchronously with
    | Error(QueueStoreFailure.Corrupt _) -> ()
    | other -> failwith $"expected Corrupt, got {other}"

    let summaQueue =
        OfflineQueue.enqueue at (operationIn summa "s" [ Change.Create(path "notes/s.json", "{}") ]) (OfflineQueue.create OfflinePolicy.QueueWrites)
        |> ok
        |> fst

    browser.Items["arca.queue.chrona"] <- OfflineQueue.encode summaQueue |> ok

    match store.Load() |> Async.RunSynchronously with
    | Error(QueueStoreFailure.Corrupt _) -> ()
    | other -> failwith $"expected Corrupt, got {other}"

[<Fact>]
let ``the queue holds operations, not records: reads go to the provider (ARCA-OFF-006)`` () =
    // Queued work is invisible to reads until it synchronizes; the provider
    // stays the only authority on record state.
    let store = InMemoryStore()
    let queue = enqueueAll [ create "a" ]
    Assert.Equal(Ok ReadOutcome.Absent, store.Provider.Read chrona (path "notes/a.json") |> Async.RunSynchronously)
    let _ = queue |> sync store (MemoryQueueStore().Store)

    match store.Provider.Read chrona (path "notes/a.json") |> Async.RunSynchronously with
    | Ok(ReadOutcome.Found _) -> ()
    | other -> failwith $"expected the synchronized object, got {other}"
