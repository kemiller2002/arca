/// Several browser tabs of one application share one localStorage, so they
/// share one persisted offline queue per namespace (ARCA-OFF-002, WI-0024).
/// The QueueStore port saves whole snapshots: a tab that saves a snapshot
/// built from a stale load must never overwrite an entry another tab's save
/// already acknowledged.
module Arca.Tests.MultiTabQueueTests

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

let private run work = Async.RunSynchronously work

let private location = DataLocation.create "acme" "data" "main" "apps" |> ok

let private chrona =
    Namespace.ofApplication
        { Application = AppId.create "chrona" |> ok
          Environment = { Kind = EnvironmentKind.Test; Name = "multi-tab" }
          Location = location }
    |> ok

let private at = DateTimeOffset(2026, 10, 8, 12, 0, 0, TimeSpan.Zero)

let private create (key: string) =
    Operation.create
        chrona
        { Summary = $"note {key}"
          Actor = { Kind = ActorKind.Human; Id = ActorId.create "u-1" |> ok }
          ProviderIdentity = None
          ExecutionId = None
          CorrelationId = CorrelationId.create $"c-{key}" |> ok
          IdempotencyKey = IdempotencyKey.create $"offline-{key}" |> ok }
        [ Change.Create(RelativePath.parse $"notes/{key}.json" |> ok, $"{{\"n\":\"{key}\"}}") ]
    |> ok

let private enqueue key queue = OfflineQueue.enqueue at (create key) queue |> ok |> fst

let private keysOf (queue: OfflineQueue) = queue.Entries |> List.map _.Operation.IdempotencyKey

/// One origin's localStorage, shared by every tab that executes against it.
type SharedLocalStorage() =
    let items = Dictionary<string, string>()
    member _.Items = items

    member _.Execute(request: LocalStorageRequest) =
        async {
            match request with
            | LocalStorageRequest.Get key ->
                match items.TryGetValue key with
                | true, value -> return LocalStorageOutcome.Success(Some value)
                | _ -> return LocalStorageOutcome.Success None
            | LocalStorageRequest.Set(key, value) ->
                items[key] <- value
                return LocalStorageOutcome.Success None
            | LocalStorageRequest.Remove key ->
                items.Remove key |> ignore
                return LocalStorageOutcome.Success None
        }

    /// What a freshly opened tab would load.
    member this.Stored(ns: Namespace) =
        LocalStorageQueue.store this.Execute LocalStorageQueue.DefaultBudget ns |> fun store -> store.Load() |> run

/// Two tabs load the same (empty) queue, each enqueues one change and saves.
/// With one whole snapshot and no ownership, the second save overwrote the
/// first, and tab A's acknowledged entry was gone.
[<Fact>]
let ``a second tab's save never overwrites an entry the first tab's save acknowledged`` () =
    let browser = SharedLocalStorage()
    let tabA = LocalStorageQueue.store browser.Execute LocalStorageQueue.DefaultBudget chrona
    let tabB = LocalStorageQueue.store browser.Execute LocalStorageQueue.DefaultBudget chrona

    let queueA = tabA.Load() |> run |> ok |> Option.defaultValue (OfflineQueue.create OfflinePolicy.QueueWrites)
    let queueB = tabB.Load() |> run |> ok |> Option.defaultValue (OfflineQueue.create OfflinePolicy.QueueWrites)

    Assert.Equal(Ok(), tabA.Save(enqueue "a" queueA) |> run)
    // Tab B's snapshot was built before tab A saved; it does not hold "a".
    let _ = tabB.Save(enqueue "b" queueB) |> run

    match browser.Stored chrona with
    | Ok(Some stored) -> Assert.Contains("offline-a", keysOf stored)
    | other -> failwith $"expected the stored queue, got {other}"

/// One step of one tab.
type private TabStep =
    | Load of tab: int
    | Enqueue of tab: int
    | Save of tab: int

/// Any interleaving of two tabs loading, enqueuing and saving: every entry a
/// Save acknowledged is still stored at the end (nothing here synchronizes or
/// prunes, so nothing may leave the store).
[<Property(MaxTest = 300)>]
let ``no interleaving of two tabs loses an acknowledged entry`` () =
    let step =
        Gen.zip (Gen.elements [ 0; 1 ]) (Gen.elements [ 0; 1; 2 ])
        |> Gen.map (fun (tab, kind) ->
            match kind with
            | 0 -> Load tab
            | 1 -> Enqueue tab
            | _ -> Save tab)

    Prop.forAll (Arb.fromGen (Gen.listOf step)) (fun steps ->
        let browser = SharedLocalStorage()
        let tabs = [| for _ in 0..1 -> LocalStorageQueue.store browser.Execute LocalStorageQueue.DefaultBudget chrona |]
        let empty = OfflineQueue.create OfflinePolicy.QueueWrites

        let _, acknowledged, _ =
            steps
            |> List.fold
                (fun (queues: Map<int, OfflineQueue>, acknowledged: Set<string>, counter: int) step ->
                    match step with
                    | Load tab ->
                        match tabs[tab].Load() |> run with
                        | Ok loaded -> queues |> Map.add tab (loaded |> Option.defaultValue empty), acknowledged, counter
                        | Error _ -> queues, acknowledged, counter
                    | Enqueue tab ->
                        let queue = queues |> Map.tryFind tab |> Option.defaultValue empty
                        queues |> Map.add tab (enqueue $"t{tab}-{counter}" queue), acknowledged, counter + 1
                    | Save tab ->
                        let queue = queues |> Map.tryFind tab |> Option.defaultValue empty

                        match tabs[tab].Save queue |> run with
                        | Ok() -> queues, Set.union acknowledged (Set.ofList (keysOf queue)), counter
                        | Error _ -> queues, acknowledged, counter)
                (Map.empty, Set.empty, 0)

        let stored =
            match browser.Stored chrona with
            | Ok(Some queue) -> Set.ofList (keysOf queue)
            | _ -> Set.empty

        Set.isSubset acknowledged stored |> Prop.label $"acknowledged {acknowledged}, stored {stored}")

/// A tab whose save was refused wrote nothing: what is stored is exactly
/// what the other tab saved.
[<Fact>]
let ``a refused save writes nothing`` () =
    let browser = SharedLocalStorage()
    let tabA = LocalStorageQueue.store browser.Execute LocalStorageQueue.DefaultBudget chrona
    let tabB = LocalStorageQueue.store browser.Execute LocalStorageQueue.DefaultBudget chrona
    let empty = OfflineQueue.create OfflinePolicy.QueueWrites
    tabA.Load() |> run |> ok |> ignore
    tabB.Load() |> run |> ok |> ignore

    Assert.Equal(Ok(), tabA.Save(enqueue "a" empty) |> run)
    let before = browser.Items[LocalStorageQueue.key chrona]
    Assert.Equal(Error QueueStoreFailure.Unavailable, tabB.Save(enqueue "b" empty) |> run)
    Assert.Equal(before, browser.Items[LocalStorageQueue.key chrona])

/// A store that never loaded expects nothing stored, so it cannot overwrite
/// a queue it has not seen; a corrupt queue is never overwritten either.
[<Fact>]
let ``a store never overwrites a queue it has not loaded, nor a corrupt one`` () =
    let browser = SharedLocalStorage()
    let first = LocalStorageQueue.store browser.Execute LocalStorageQueue.DefaultBudget chrona
    Assert.Equal(Ok(), first.Save(enqueue "a" (OfflineQueue.create OfflinePolicy.QueueWrites)) |> run)

    let unloaded = LocalStorageQueue.store browser.Execute LocalStorageQueue.DefaultBudget chrona
    Assert.Equal(Error QueueStoreFailure.Unavailable, unloaded.Save(OfflineQueue.create OfflinePolicy.QueueWrites) |> run)
    Assert.Equal<string list>([ "offline-a" ], browser.Stored chrona |> ok |> Option.get |> keysOf)

    browser.Items[LocalStorageQueue.key chrona] <- "{\"arcaQueue\":2}"
    let reader = LocalStorageQueue.store browser.Execute LocalStorageQueue.DefaultBudget chrona

    match reader.Load() |> run with
    | Error(QueueStoreFailure.Corrupt _) -> ()
    | other -> failwith $"expected Corrupt, got {other}"

    Assert.Equal(Error QueueStoreFailure.Unavailable, reader.Save(OfflineQueue.create OfflinePolicy.QueueWrites) |> run)
    Assert.Equal("{\"arcaQueue\":2}", browser.Items[LocalStorageQueue.key chrona])

/// One tab's own saves, one after another, always succeed: the fence only
/// refuses what another tab wrote.
[<Fact>]
let ``one tab saves repeatedly, and a reload continues where it left off`` () =
    let browser = SharedLocalStorage()
    let tab = LocalStorageQueue.store browser.Execute LocalStorageQueue.DefaultBudget chrona
    let empty = OfflineQueue.create OfflinePolicy.QueueWrites
    Assert.Equal(Ok None, tab.Load() |> run)
    let one = enqueue "a" empty
    let two = enqueue "b" one
    Assert.Equal(Ok(), tab.Save one |> run)
    Assert.Equal(Ok(), tab.Save two |> run)
    Assert.Equal(Ok(), tab.Save(OfflineQueue.prune two) |> run)

    let reloaded = LocalStorageQueue.store browser.Execute LocalStorageQueue.DefaultBudget chrona
    let loaded = reloaded.Load() |> run |> ok |> Option.get
    Assert.Equal(two, loaded)
    Assert.Equal(Ok(), reloaded.Save(enqueue "c" loaded) |> run)

// ---------------------------------------------------------------------------
// One owner per namespace through a Web Lock (Limen LCP-059, LCP-060).
// ---------------------------------------------------------------------------

/// The browser's Web Locks for one origin: an exclusive lock is held by at
/// most one tab, and released when that tab closes.
type SharedLocks() =
    let held = Dictionary<string, int>()
    member val Supported = true with get, set

    member this.For (tab: int) (request: QueueLockRequest) =
        async {
            match request with
            | _ when not this.Supported -> return QueueLockOutcome.Unsupported
            | QueueLockRequest.Acquire name ->
                match held.TryGetValue name with
                | true, holder when holder <> tab -> return QueueLockOutcome.Busy
                | _ ->
                    held[name] <- tab
                    return QueueLockOutcome.Acquired
        }

    /// The tab closed, crashed or navigated away.
    member _.Close(tab: int) =
        [ for pair in held do
              if pair.Value = tab then
                  pair.Key ]
        |> List.iter (held.Remove >> ignore)

let private own (locks: SharedLocks) (browser: SharedLocalStorage) tab ns =
    LocalStorageQueue.own (locks.For tab) browser.Execute LocalStorageQueue.DefaultBudget ns |> run

let private owned =
    function
    | QueueOwnership.Owned store -> store
    | QueueOwnership.OwnedElsewhere -> failwith "expected Owned, got OwnedElsewhere"
    | QueueOwnership.OwnershipUnsupported -> failwith "expected Owned, got OwnershipUnsupported"

[<Fact>]
let ``each namespace has its own lock`` () =
    let dataset =
        Namespace.ofDataset
            { Application = AppId.create "summa" |> ok
              Environment = { Kind = EnvironmentKind.Test; Name = "multi-tab" }
              Location = location }
            (DatasetId.create "org-1" |> ok)
            None
        |> ok

    Assert.Equal("arca.queue/chrona", LocalStorageQueue.lockName chrona)
    Assert.Equal("arca.queue/summa/org-1", LocalStorageQueue.lockName dataset)

[<Fact>]
let ``two tabs: exactly one owns the queue, and the other is told another tab holds it`` () =
    let locks = SharedLocks()
    let browser = SharedLocalStorage()
    let first = own locks browser 1 chrona
    let second = own locks browser 2 chrona

    match first, second with
    | QueueOwnership.Owned _, QueueOwnership.OwnedElsewhere -> ()
    | _ -> failwith "expected one owner and one OwnedElsewhere"

[<Fact>]
let ``when the owner closes, the next tab owns exactly the last saved queue and continues its order`` () =
    let locks = SharedLocks()
    let browser = SharedLocalStorage()
    let ownerStore = own locks browser 1 chrona |> owned
    Assert.Equal(Ok None, ownerStore.Load() |> run)
    let saved = OfflineQueue.create OfflinePolicy.QueueWrites |> enqueue "a" |> enqueue "b"
    Assert.Equal(Ok(), ownerStore.Save saved |> run)

    match own locks browser 2 chrona with
    | QueueOwnership.OwnedElsewhere -> ()
    | _ -> failwith "expected OwnedElsewhere while the owner is open"

    locks.Close 1
    let nextStore = own locks browser 2 chrona |> owned
    let loaded = nextStore.Load() |> run |> ok |> Option.get
    Assert.Equal(saved, loaded)

    let continued = enqueue "c" loaded
    Assert.Equal(Ok(), nextStore.Save continued |> run)
    let stored = browser.Stored chrona |> ok |> Option.get
    Assert.Equal<int64 list>([ 1L; 2L; 3L ], stored.Entries |> List.map _.Sequence)
    Assert.Equal<string list>([ "offline-a"; "offline-b"; "offline-c" ], keysOf stored)

[<Fact>]
let ``without Web Locks, ownership is unsupported, never an unfenced writer`` () =
    let locks = SharedLocks(Supported = false)

    match own locks (SharedLocalStorage()) 1 chrona with
    | QueueOwnership.OwnershipUnsupported -> ()
    | _ -> failwith "expected OwnershipUnsupported"

/// LCP-060's fault test: the owner persisted an entry in flight, its commit
/// landed, and the tab died before it recorded the answer. The next owner
/// reconciles the entry and the provider holds exactly one commit.
[<Theory>]
[<InlineData(true)>]
[<InlineData(false)>]
let ``ownership moved mid-send produces exactly one commit`` (landed: bool) =
    let locks = SharedLocks()
    let browser = SharedLocalStorage()
    let provider = InMemoryStore()
    let first = own locks browser 1 chrona |> owned
    first.Load() |> run |> ignore
    let queue = OfflineQueue.create OfflinePolicy.QueueWrites |> enqueue "a"
    Assert.Equal(Ok(), first.Save queue |> run)

    // Write-ahead: the in-flight state is durable before the commit is sent.
    let token = provider.Provider.ChangeToken chrona |> run |> ok
    let inFlight = OfflineQueue.markInFlight 1L token queue |> ok
    Assert.Equal(Ok(), first.Save inFlight |> run)

    if landed then
        let operation = OfflineQueue.operationOf chrona inFlight.Entries.Head.Operation |> ok
        provider.Provider.Commit operation |> run |> ok |> ignore

    // The tab dies before it records the answer.
    locks.Close 1

    let second = own locks browser 2 chrona |> owned
    let resumed = second.Load() |> run |> ok |> Option.get
    let finished, _ = OfflineSync.run provider.Provider second chrona 10 resumed |> run

    let commits =
        provider.State.History |> List.filter (fun commit -> commit.Message.StartsWith "chrona: note a")

    Assert.Equal(1, commits.Length)
    Assert.True((OfflineQueue.status finished).Synchronized)
    Assert.True((browser.Stored chrona |> ok |> Option.get |> OfflineQueue.status).Synchronized)

/// A queue that Arca 0.2.0 saved stays where it is, in the same format: the
/// 0.2.1 adapter adopts it in place. Taking ownership and loading write
/// nothing, so there is no migration step to interrupt, and doing them again
/// changes nothing.
[<Fact>]
let ``a queue saved by 0.2.0 is adopted in place, and adopting it writes nothing`` () =
    let locks = SharedLocks()
    let browser = SharedLocalStorage()
    let legacy = OfflineQueue.create OfflinePolicy.QueueWrites |> enqueue "a" |> enqueue "b"
    // What 0.2.0's saveRequest stored: the canonical queue text, under the same key.
    let legacyText = OfflineQueue.encode legacy |> ok
    browser.Items["arca.queue.chrona"] <- legacyText
    let writes = ref 0

    let counting request =
        match request with
        | LocalStorageRequest.Set _
        | LocalStorageRequest.Remove _ -> writes.Value <- writes.Value + 1
        | LocalStorageRequest.Get _ -> ()

        browser.Execute request

    let adopt tab =
        match LocalStorageQueue.own (locks.For tab) counting LocalStorageQueue.DefaultBudget chrona |> run with
        | QueueOwnership.Owned store -> store.Load() |> run |> ok
        | _ -> failwith "expected Owned"

    Assert.Equal(Some legacy, adopt 1)
    locks.Close 1
    Assert.Equal(Some legacy, adopt 2)
    Assert.Equal(0, writes.Value)
    Assert.Equal<string list>([ "arca.queue.chrona" ], List.ofSeq browser.Items.Keys)
    Assert.Equal(legacyText, browser.Items["arca.queue.chrona"])

/// One step of one tab that must own the queue to keep it.
type private OwnerStep =
    | Open of tab: int
    | Add of tab: int
    | Persist of tab: int
    | Close of tab: int

/// Tabs open (take ownership if they can, and load), enqueue, save and close
/// in any order: at most one tab ever holds a store, and every acknowledged
/// entry is still stored at the end, in sequence order.
[<Property(MaxTest = 300)>]
let ``no interleaving of owning tabs loses an acknowledged entry`` () =
    let step =
        Gen.zip (Gen.elements [ 0; 1; 2 ]) (Gen.elements [ 0; 1; 1; 2; 2; 3 ])
        |> Gen.map (fun (tab, kind) ->
            match kind with
            | 0 -> Open tab
            | 1 -> Add tab
            | 2 -> Persist tab
            | _ -> Close tab)

    Prop.forAll (Arb.fromGen (Gen.listOf step)) (fun steps ->
        let locks = SharedLocks()
        let browser = SharedLocalStorage()

        let _, acknowledged, _, maxOwners =
            steps
            |> List.fold
                (fun (tabs: Map<int, QueueStore * OfflineQueue>, acknowledged: Set<string>, counter: int, maxOwners: int) step ->
                    match step with
                    | Open tab when not (tabs.ContainsKey tab) ->
                        match own locks browser tab chrona with
                        | QueueOwnership.Owned store ->
                            let queue = store.Load() |> run |> ok |> Option.defaultValue (OfflineQueue.create OfflinePolicy.QueueWrites)
                            let tabs = tabs |> Map.add tab (store, queue)
                            tabs, acknowledged, counter, max maxOwners tabs.Count
                        | _ -> tabs, acknowledged, counter, maxOwners
                    | Add tab when tabs.ContainsKey tab ->
                        let store, queue = tabs[tab]
                        tabs |> Map.add tab (store, enqueue $"t{tab}-{counter}" queue), acknowledged, counter + 1, maxOwners
                    | Persist tab when tabs.ContainsKey tab ->
                        let store, queue = tabs[tab]

                        match store.Save queue |> run with
                        | Ok() -> tabs, Set.union acknowledged (Set.ofList (keysOf queue)), counter, maxOwners
                        | Error _ -> tabs, acknowledged, counter, maxOwners
                    | Close tab ->
                        locks.Close tab
                        tabs |> Map.remove tab, acknowledged, counter, maxOwners
                    | _ -> tabs, acknowledged, counter, maxOwners)
                (Map.empty, Set.empty, 0, 0)

        let stored =
            match browser.Stored chrona with
            | Ok(Some queue) -> queue
            | _ -> OfflineQueue.create OfflinePolicy.QueueWrites

        let sequences = stored.Entries |> List.map _.Sequence

        (maxOwners <= 1 |> Prop.label $"{maxOwners} owners at once")
        .&. (Set.isSubset acknowledged (Set.ofList (keysOf stored)) |> Prop.label $"acknowledged {acknowledged}, stored {keysOf stored}")
        .&. ((sequences = List.sort sequences && sequences = List.distinct sequences) |> Prop.label $"sequences {sequences}"))
