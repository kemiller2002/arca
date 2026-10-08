/// The one-time move of the localStorage queue into IndexedDB (WI-0020;
/// Limen LCP-066, LCP-067; DF-ARCA-2026-0008: copy, verify, retire), over
/// Limen's FakeStore and a scripted localStorage: absent, present, corrupt,
/// foreign, IndexedDB non-empty (deferred, then adopted), a rerun, and a
/// fault at every cut point.
module Arca.Tests.LimenMigrationTests

open Arca
open Arca.GitHub
open Arca.Limen
open Arca.Tests.LimenHarness
open Limen.Store
open Xunit
open FsCheck
open FsCheck.FSharp
open FsCheck.Xunit

let private legacyKey = LocalStorageQueue.key chrona

/// What a 0.2.x tab saved through LocalStorageQueue.
let private seedLegacy (origin: Origin) (queue: OfflineQueue) =
    let text = OfflineQueue.encode queue |> ok
    origin.LocalStorage.Items[legacyKey] <- text
    text

let private legacyStored (origin: Origin) =
    match origin.LocalStorage.Items.TryGetValue legacyKey with
    | true, text -> Some text
    | _ -> None

let private notices (queue: OwnedQueue) = (queue.Diagnostics()).Notices

let private legacy = empty |> enqueue "l1" |> enqueue "l2"

/// Every entry exactly once, across both stores.
let private exactlyOnce (origin: Origin) (expected: string list) =
    let inIndexedDb = storedQueue origin |> Option.map keysOf |> Option.defaultValue []

    let inLocalStorage =
        legacyStored origin |> Option.map (fun text -> OfflineQueue.decode text |> ok |> keysOf) |> Option.defaultValue []

    Assert.Equal<string list>(expected, inIndexedDb @ inLocalStorage)

[<Fact>]
let ``no localStorage queue: nothing moves and nothing is reported`` () =
    let origin = Origin()
    let queue = own origin "a" |> owned
    Assert.Equal(Ok None, queue.Store.Load() |> run)
    Assert.Empty(notices queue)

[<Fact>]
let ``a localStorage queue is copied, verified and retired; the load returns it`` () =
    let origin = Origin()
    let text = seedLegacy origin legacy
    let queue = own origin "a" |> owned

    Assert.Equal(Ok(Some legacy), queue.Store.Load() |> run)
    Assert.Equal(None, legacyStored origin)
    let record = storedRecord origin |> Option.get
    Assert.Equal(Some(IndexedDbQueue.marker text), record.Migrated)
    Assert.Equal(Some legacy, storedQueue origin)
    Assert.Equal<QueueNotice list>([ QueueNotice.LegacyQueueAdopted 2 ], notices queue)
    exactlyOnce origin [ "offline-l1"; "offline-l2" ]

    // Order continues after the adopted entries.
    let next = legacy |> enqueue "n3"
    Assert.Equal(Ok(), queue.Store.Save next |> run)
    Assert.Equal<int64 list>([ 1L; 2L; 3L ], (storedQueue origin |> Option.get).Entries |> List.map _.Sequence)

[<Fact>]
let ``running the move again changes nothing`` () =
    let origin = Origin()
    seedLegacy origin legacy |> ignore
    let first = own origin "a" |> owned
    first.Store.Load() |> run |> ok |> ignore
    let before = storedRecord origin
    origin.Close "a"

    let second = own origin "b" |> owned
    Assert.Equal(Ok(Some legacy), second.Store.Load() |> run)
    Assert.Equal(before |> Option.map (fun r -> r.Queue, r.Migrated), storedRecord origin |> Option.map (fun r -> r.Queue, r.Migrated))
    Assert.Empty(notices second)
    exactlyOnce origin [ "offline-l1"; "offline-l2" ]

[<Fact>]
let ``a corrupt localStorage queue is left in place and reported, never deleted`` () =
    let origin = Origin()
    origin.LocalStorage.Items[legacyKey] <- "{\"arcaQueue\":9}"
    let queue = own origin "a" |> owned
    Assert.Equal(Ok None, queue.Store.Load() |> run)
    Assert.Equal(Some "{\"arcaQueue\":9}", legacyStored origin)
    Assert.Equal<QueueNotice list>([ QueueNotice.LegacyQueueUnreadable ], notices queue)

[<Fact>]
let ``another namespace's localStorage queue is left in place and reported`` () =
    let origin = Origin()

    let summa =
        Namespace.ofApplication
            { Application = AppId.create "summa" |> ok
              Environment = { Kind = EnvironmentKind.Test; Name = "limen" }
              Location = chrona.Location }
        |> ok

    let foreign =
        OfflineQueue.enqueue
            at
            (Operation.create
                summa
                { Summary = "s"
                  Actor = { Kind = ActorKind.Human; Id = ActorId.create "u" |> ok }
                  ProviderIdentity = None
                  ExecutionId = None
                  CorrelationId = CorrelationId.create "c-s" |> ok
                  IdempotencyKey = IdempotencyKey.create "offline-summa" |> ok }
                [ Change.Create(RelativePath.parse "notes/s.json" |> ok, "{}") ]
             |> ok)
            empty
        |> ok
        |> fst

    let text = seedLegacy origin foreign
    let queue = own origin "a" |> owned
    Assert.Equal(Ok None, queue.Store.Load() |> run)
    Assert.Equal(Some text, legacyStored origin)
    Assert.Equal<QueueNotice list>([ QueueNotice.LegacyQueueUnreadable ], notices queue)

[<Fact>]
let ``with entries in IndexedDB the move waits, then adopts once IndexedDB is empty; never a merge`` () =
    let origin = Origin()
    let provider = InMemoryStore()
    let queue = own origin "a" |> owned
    queue.Store.Load() |> run |> ok |> ignore
    let current = empty |> enqueue "i1"
    queue.Store.Save current |> run |> ok

    // A legacy queue appears (written by a 0.2.x tab before this one existed).
    let text = seedLegacy origin legacy
    Assert.Equal(Ok(Some current), queue.Store.Load() |> run)
    Assert.Equal<QueueNotice list>([ QueueNotice.LegacyQueuePending 2 ], notices queue)
    Assert.Equal(Some text, legacyStored origin)

    // The IndexedDB queue drains and is pruned.
    let synced, _ = OfflineSync.run provider.Provider queue.Store chrona 10 current |> run
    Assert.Equal(Ok(), queue.Store.Save(OfflineQueue.prune synced) |> run)

    // The next load adopts the legacy queue whole, in its own order.
    Assert.Equal(Ok(Some legacy), queue.Store.Load() |> run)
    Assert.Equal(None, legacyStored origin)
    Assert.Equal<QueueNotice list>([ QueueNotice.LegacyQueueAdopted 2 ], notices queue)
    exactlyOnce origin [ "offline-l1"; "offline-l2" ]

// ---------------------------------------------------------------------------
// Cut points (LCP-067)
// ---------------------------------------------------------------------------

[<Fact>]
let ``cut before the IndexedDB commit (quota): both stores as they were, QuotaExceeded reported, the next run adopts`` () =
    let origin = Origin()
    let text = seedLegacy origin legacy
    let host = origin.Host "a"
    let quotaNext = ref false

    // The browser's quota fails the copy's commit; reads are not affected.
    let store request =
        match request with
        | Wire.Request.Transact(_, Limen.Contract.Store.Types.TransactionMode.Readwrite, _) when quotaNext.Value ->
            quotaNext.Value <- false
            async { return Limen.Contract.Store.Types.StoreResult.Aborted(Limen.Contract.Store.Types.AbortReason.Quota, None, None) }
        | _ -> host.Store request

    let queue = LimenQueue.own { host with Store = store } indexedDbOnly chrona |> run |> owned
    quotaNext.Value <- true

    Assert.Equal(Ok None, queue.Store.Load() |> run)
    Assert.Equal(Some text, legacyStored origin)
    Assert.Equal(None, storedRecord origin |> Option.bind _.Queue)
    Assert.Equal(Some FailureClass.Quota, (queue.Diagnostics()).LastFailure |> Option.map _.Class)

    Assert.Equal(Ok(Some legacy), queue.Store.Load() |> run)
    exactlyOnce origin [ "offline-l1"; "offline-l2" ]

[<Fact>]
let ``cut before the commit (the tab closes, the lock moves): the old owner writes nothing and the new owner adopts`` () =
    let origin = Origin()
    seedLegacy origin legacy |> ignore
    let first = own origin "a" |> owned
    let second = LimenQueue.takeOver (origin.Host "b") indexedDbOnly chrona |> run |> owned

    Assert.Equal(Error QueueStoreFailure.Unavailable, first.Store.Load() |> run)
    Assert.True(legacyStored origin |> Option.isSome)
    Assert.Equal(Ok(Some legacy), second.Store.Load() |> run)
    exactlyOnce origin [ "offline-l1"; "offline-l2" ]

[<Fact>]
let ``cut after the commit, before the removal: the marker matches and the next run removes only`` () =
    let origin = Origin()
    let text = seedLegacy origin legacy
    origin.LocalStorage.FailRemoves <- true
    let first = own origin "a" |> owned
    Assert.Equal(Ok(Some legacy), first.Store.Load() |> run)

    // Both stores hold the entries until the source is retired.
    Assert.Equal(Some text, legacyStored origin)
    Assert.Equal(Some(IndexedDbQueue.marker text), (storedRecord origin |> Option.get).Migrated)

    // The owner keeps working on the adopted queue meanwhile.
    let worked = legacy |> enqueue "n3"
    Assert.Equal(Ok(), first.Store.Save worked |> run)

    origin.LocalStorage.FailRemoves <- false
    origin.Close "a"
    let second = own origin "b" |> owned
    Assert.Equal(Ok(Some worked), second.Store.Load() |> run)
    Assert.Equal(None, legacyStored origin)
    exactlyOnce origin [ "offline-l1"; "offline-l2"; "offline-n3" ]

[<Fact>]
let ``cut after the removal: done, and nothing more happens`` () =
    let origin = Origin()
    seedLegacy origin legacy |> ignore
    let first = own origin "a" |> owned
    first.Store.Load() |> run |> ok |> ignore
    let writes = (origin.Requests "a").Length
    Assert.Equal(Ok(Some legacy), first.Store.Load() |> run)
    // A further load only reads.
    Assert.True((origin.Requests "a")[writes..] |> List.forall (function Wire.Request.Transact(_, Limen.Contract.Store.Types.TransactionMode.Readonly, _) -> true | _ -> false))
    exactlyOnce origin [ "offline-l1"; "offline-l2" ]

[<Fact>]
let ``a localStorage queue over the IndexedDB budget is kept where it is`` () =
    let origin = Origin()
    let text = seedLegacy origin legacy
    let queue = LimenQueue.own (origin.Host "a") { indexedDbOnly with IndexedDbBudget = 100L } chrona |> run |> owned
    Assert.Equal(Ok None, queue.Store.Load() |> run)
    Assert.Equal(Some text, legacyStored origin)
    Assert.Equal(Some "arca.limen.quota.budget", (queue.Diagnostics()).LastFailure |> Option.map _.Code)

/// Any sequence of interruptions (quota at the commit, a failed removal, a
/// tab replaced) ends, once a run completes, with every entry exactly once
/// in IndexedDB and the localStorage key removed (LCP-067).
[<Property(MaxTest = 40)>]
let ``whatever interrupts it, the move ends with every entry exactly once`` () =
    let interruption = Gen.elements [ "quota"; "removal"; "replace"; "none" ] |> Gen.listOf

    Prop.forAll (Arb.fromGen (Gen.zip (Gen.choose (1, 4)) interruption)) (fun (count, cuts) ->
        let origin = Origin()
        let queue = [ 1..count ] |> List.fold (fun queue i -> enqueue $"l{i}" queue) empty
        seedLegacy origin queue |> ignore
        let tab = ref 0

        let next () =
            origin.Close $"t{tab.Value}"
            tab.Value <- tab.Value + 1
            own origin $"t{tab.Value}" |> owned

        let mutable current = next ()

        for cut in cuts do
            match cut with
            | "quota" -> origin.Inject $"t{tab.Value}" FakeFault.Quota
            | "removal" -> origin.LocalStorage.FailRemoves <- true
            | "replace" -> current <- next ()
            | _ -> ()

            current.Store.Load() |> run |> ignore
            origin.LocalStorage.FailRemoves <- false

        // A run that completes.
        let final = (next ()).Store.Load() |> run

        final = Ok(Some queue)
        && legacyStored origin = None
        && storedQueue origin = Some queue)
