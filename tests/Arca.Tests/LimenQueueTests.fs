/// EchelonFoundry.Arca.Limen's offline queue (WI-0016; Limen LCP-046,
/// LCP-059, LCP-060, LCP-062, LCP-065, LCP-068, LCP-070, LCP-072, LCP-073),
/// over Limen's own FakeStore.
module Arca.Tests.LimenQueueTests

open System
open Arca
open Arca.GitHub
open Arca.Limen
open Arca.Tests.LimenHarness
open Limen.Store
open Xunit
open FsCheck
open FsCheck.FSharp
open FsCheck.Xunit

let private problems (results: ConformanceResult list) =
    results
    |> List.choose (fun result ->
        match result.Outcome with
        | ConformanceOutcome.Passed -> None
        | ConformanceOutcome.Failed reason -> Some $"{result.Case} failed: {reason}"
        | ConformanceOutcome.Unsupported reason -> Some $"{result.Case} unsupported: {reason}")

/// A fresh origin; the store is tab a's, and a reopen is tab b owning it
/// after tab a closed.
let private subject (budget: int64) () =
    async {
        let origin = Origin()
        let options = { indexedDbOnly with IndexedDbBudget = budget }
        let first = LimenQueue.own (origin.Host "a") options chrona |> run |> owned
        let reopened = ref 0

        return
            { Namespace = chrona
              Store = first.Store
              Reopen =
                fun () ->
                    async {
                        reopened.Value <- reopened.Value + 1
                        let previous = if reopened.Value = 1 then "a" else $"r{reopened.Value - 1}"
                        origin.Close previous
                        let! opening = LimenQueue.own (origin.Host $"r{reopened.Value}") options chrona
                        return (owned opening).Store
                    }
              Budget = budget
              Arrange =
                fun fault ->
                    async {
                        match fault with
                        | QueueStoreFault.Unavailable -> origin.Inject "a" FakeFault.Missing
                        | QueueStoreFault.Stored text -> plantQueueText origin text

                        return true
                    } }
    }

[<Fact>]
let ``the IndexedDB queue store passes the queue-store conformance suite (LCP-046)`` () =
    let results = QueueStoreConformance.run (subject 4096L) |> run
    Assert.Equal(QueueStoreConformance.cases.Length, results.Length)
    let found = problems results
    Assert.True(found.IsEmpty, String.concat "\n" found)

[<Property(MaxTest = 40)>]
let ``any queue round-trips through IndexedDB, order and sequences kept (LCP-060)`` () =
    Prop.forAll (Arb.fromGen OfflineQueueTests.queueGen) (fun queue ->
        let subject = subject IndexedDbQueue.DefaultBudget () |> run
        QueueStoreConformance.roundTrip subject queue |> run = ConformanceOutcome.Passed)

// ---------------------------------------------------------------------------
// Ownership and fencing (LCP-059, LCP-060)
// ---------------------------------------------------------------------------

[<Fact>]
let ``two tabs: exactly one owns the queue, and the other is told another tab holds it`` () =
    let origin = Origin()
    let first = own origin "a"
    let second = own origin "b"

    match first, second with
    | QueueOpening.Owned queue, QueueOpening.OwnedElsewhere ->
        Assert.Equal(DurabilityMode.IndexedDb, queue.Mode)
        Assert.Equal(OwnershipState.Owner 1L, (queue.Diagnostics()).Ownership)
    | _ -> failwith "expected one owner and one OwnedElsewhere"

[<Fact>]
let ``the owner closes: the next tab takes over the exact queue at a higher epoch`` () =
    let origin = Origin()
    let first = own origin "a" |> owned
    first.Store.Load() |> run |> ignore
    let saved = empty |> enqueue "a" |> enqueue "b"
    Assert.Equal(Ok(), first.Store.Save saved |> run)

    origin.Close "a"
    let second = own origin "b" |> owned
    Assert.Equal(Ok(Some saved), second.Store.Load() |> run)
    Assert.Equal(OwnershipState.Owner 2L, (second.Diagnostics()).Ownership)

    // Order continues where the first tab stopped.
    let next = saved |> enqueue "c"
    Assert.Equal(Ok(), second.Store.Save next |> run)
    Assert.Equal<int64 list>([ 1L; 2L; 3L ], (storedQueue origin |> Option.get).Entries |> List.map _.Sequence)

[<Fact>]
let ``a tab that takes over fences the old owner: its save writes nothing (OQ-LIMEN-IDB-001)`` () =
    let origin = Origin()
    let first = own origin "a" |> owned
    first.Store.Load() |> run |> ignore
    Assert.Equal(Ok(), first.Store.Save(empty |> enqueue "a") |> run)

    let second = LimenQueue.takeOver (origin.Host "b") indexedDbOnly chrona |> run |> owned
    let adopted = second.Store.Load() |> run |> ok |> Option.get
    Assert.Equal<string list>([ "offline-a" ], keysOf adopted)
    Assert.Equal(1, (origin.Locks.Lost "a").Length)

    // The pre-empted owner still runs, and its save is refused.
    Assert.Equal(Error QueueStoreFailure.Unavailable, first.Store.Save(adopted |> enqueue "stale") |> run)
    Assert.Equal(OwnershipState.OwnedElsewhere, (first.Diagnostics()).Ownership)
    Assert.Equal(Some "arca.limen.owned-elsewhere.fenced", (first.Diagnostics()).LastFailure |> Option.map _.Code)
    Assert.Equal(Error QueueStoreFailure.Unavailable, first.Store.Load() |> run)
    Assert.Equal<string list>([ "offline-a" ], storedQueue origin |> Option.get |> keysOf)

    Assert.Equal(Ok(), second.Store.Save(adopted |> enqueue "b") |> run)
    Assert.Equal<string list>([ "offline-a"; "offline-b" ], storedQueue origin |> Option.get |> keysOf)

/// The owner persisted an entry in flight; its commit landed (or not); then
/// another tab took the queue over before the owner recorded the answer. The
/// new owner reconciles; the provider holds exactly one commit, and the old
/// owner's late save writes nothing (LCP-060).
[<Theory>]
[<InlineData(true)>]
[<InlineData(false)>]
let ``ownership moved mid-send produces exactly one commit`` (landed: bool) =
    let origin = Origin()
    let provider = InMemoryStore()
    let first = own origin "a" |> owned
    first.Store.Load() |> run |> ignore
    let queue = empty |> enqueue "a"
    Assert.Equal(Ok(), first.Store.Save queue |> run)

    let token = provider.Provider.ChangeToken chrona |> run |> ok
    let inFlight = OfflineQueue.markInFlight 1L token queue |> ok
    Assert.Equal(Ok(), first.Store.Save inFlight |> run)

    let receipt =
        if landed then
            let operation = OfflineQueue.operationOf chrona inFlight.Entries.Head.Operation |> ok
            Some(provider.Provider.Commit operation |> run |> ok)
        else
            None

    let second = LimenQueue.takeOver (origin.Host "b") indexedDbOnly chrona |> run |> owned

    // The old owner now records the answer: fenced.
    match receipt with
    | Some receipt ->
        let recorded = OfflineQueue.recordResult 1L (Ok receipt) inFlight |> ok
        Assert.Equal(Error QueueStoreFailure.Unavailable, first.Store.Save recorded |> run)
    | None -> ()

    let resumed = second.Store.Load() |> run |> ok |> Option.get
    let finished, _ = OfflineSync.run provider.Provider second.Store chrona 10 resumed |> run

    let commits = provider.State.History |> List.filter (fun commit -> commit.Message.StartsWith "chrona: note a")
    Assert.Equal(1, commits.Length)
    Assert.True((OfflineQueue.status finished).Synchronized)
    Assert.True((storedQueue origin |> Option.get |> OfflineQueue.status).Synchronized)

[<Property(MaxTest = 20)>]
let ``synchronization through the IndexedDB store keeps order and loses nothing (ARCA-OFF-004)`` () =
    let faults =
        Gen.elements [ None; Some InMemoryFault.OutcomeUnknownLanded; Some InMemoryFault.OutcomeUnknownLost; Some(InMemoryFault.RateLimited None) ]
        |> Gen.listOfLength 3

    Prop.forAll (Arb.fromGen (Gen.zip (Gen.choose (1, 4)) faults)) (fun (count, arranged) ->
        let origin = Origin()
        let provider = InMemoryStore()
        let queue = own origin "a" |> owned
        let keys = [ for i in 1..count -> $"k{i}" ]
        let start = keys |> List.fold (fun queue key -> enqueue key queue) empty
        queue.Store.Save start |> run |> ok

        let rec drive current (faults: InMemoryFault option list) =
            match faults with
            | Some fault :: rest ->
                provider.Arrange fault
                drive (OfflineSync.run provider.Provider queue.Store chrona 100 current |> run |> fst) rest
            | None :: rest -> drive current rest
            | [] -> OfflineSync.run provider.Provider queue.Store chrona 100 current |> run |> fst

        let finished = drive start arranged
        let subjects = provider.State.History |> List.rev |> List.map (fun c -> c.Message.Split('\n')[0]) |> List.filter (fun m -> m.StartsWith "chrona:")

        (OfflineQueue.status finished).Synchronized
        && subjects = (keys |> List.map (fun key -> $"chrona: note {key}"))
        && storedQueue origin = Some finished)

[<Fact>]
let ``the IndexedDB queue and the localStorage queue share one lock name, so they exclude each other`` () =
    Assert.Equal("arca.queue/chrona", QueueLock.name chrona)
    Assert.Equal(LocalStorageQueue.lockName chrona, QueueLock.name chrona)

// ---------------------------------------------------------------------------
// The durability-mode composer (LCP-065)
// ---------------------------------------------------------------------------

[<Fact>]
let ``with IndexedDB the composer obtains IndexedDb and reports it`` () =
    let origin = Origin()
    let queue = LimenQueue.own (origin.Host "a") QueueOptions.standard chrona |> run |> owned
    Assert.Equal(DurabilityMode.IndexedDb, queue.Mode)
    Assert.Equal(DurabilityMode.IndexedDb, (queue.Diagnostics()).Mode)

[<Fact>]
let ``IndexedDB missing gives LocalStorage, and the queue lives only there`` () =
    let origin = Origin()
    origin.Inject "a" FakeFault.Missing
    let queue = LimenQueue.own (origin.Host "a") QueueOptions.standard chrona |> run |> owned
    Assert.Equal(DurabilityMode.LocalStorage LocalStorageQueue.DefaultBudget, queue.Mode)
    queue.Store.Load() |> run |> ok |> ignore
    Assert.Equal(Ok(), queue.Store.Save(empty |> enqueue "a") |> run)
    Assert.True(origin.LocalStorage.Items.ContainsKey "arca.queue.chrona")
    // Never written to two stores at once.
    Assert.Empty(origin.Requests "a" |> List.filter (function Wire.Request.Transact _ -> true | _ -> false))

[<Fact>]
let ``IndexedDB missing and localStorage unavailable gives MemoryOnly, reported`` () =
    let origin = Origin()
    origin.Inject "a" FakeFault.Missing
    origin.LocalStorage.Unavailable <- true
    let queue = LimenQueue.own (origin.Host "a") QueueOptions.standard chrona |> run |> owned
    Assert.Equal(DurabilityMode.MemoryOnly, queue.Mode)
    Assert.Equal(DurabilityMode.MemoryOnly, (queue.Diagnostics()).Mode)

[<Fact>]
let ``an IndexedDB open that fails falls through to the next declared store`` () =
    let origin = Origin()
    origin.Inject "a" (FakeFault.OpenFails "UnknownError")
    // availability consumes the injected failure as Broken; inject again for the open.
    let queue = LimenQueue.own (origin.Host "a") QueueOptions.standard chrona |> run |> owned
    Assert.Equal(DurabilityMode.LocalStorage LocalStorageQueue.DefaultBudget, queue.Mode)

[<Fact>]
let ``nothing usable reports why, and releases the lock`` () =
    let origin = Origin()
    origin.Inject "a" FakeFault.Missing
    origin.LocalStorage.Unavailable <- true
    let options = { QueueOptions.standard with Order = [ DurabilityChoice.IndexedDb; DurabilityChoice.LocalStorage ] }

    match LimenQueue.own (origin.Host "a") options chrona |> run with
    | QueueOpening.NothingUsable failures ->
        Assert.Equal<string list>([ "arca.limen.unavailable.missing"; "arca.limen.unavailable.storage" ], failures |> List.map _.Code)
        Assert.Equal(None, origin.Locks.Holder "arca.queue/chrona")
    | _ -> failwith "expected NothingUsable"

[<Fact>]
let ``without Web Locks, durable stores are never used; memory is, when declared (LCP-059)`` () =
    let origin = Origin()
    origin.Locks.Supported <- false

    match LimenQueue.own (origin.Host "a") indexedDbOnly chrona |> run with
    | QueueOpening.OwnershipUnsupported -> ()
    | _ -> failwith "expected OwnershipUnsupported"

    let queue = LimenQueue.own (origin.Host "a") QueueOptions.standard chrona |> run |> owned
    Assert.Equal(DurabilityMode.MemoryOnly, queue.Mode)
    Assert.Empty(origin.Requests "a")

[<Fact>]
let ``a second tab is told OwnedElsewhere whatever store the owner obtained`` () =
    let origin = Origin()
    origin.Inject "a" FakeFault.Missing
    let first = LimenQueue.own (origin.Host "a") QueueOptions.standard chrona |> run |> owned
    Assert.Equal(DurabilityMode.LocalStorage LocalStorageQueue.DefaultBudget, first.Mode)

    match LimenQueue.own (origin.Host "b") QueueOptions.standard chrona |> run with
    | QueueOpening.OwnedElsewhere -> ()
    | _ -> failwith "expected OwnedElsewhere"

// ---------------------------------------------------------------------------
// Eviction and connection loss (LCP-062)
// ---------------------------------------------------------------------------

[<Fact>]
let ``a database found recreated after the device held unsent changes reports LocalQueueLost`` () =
    let origin = Origin()
    let first = own origin "a" |> owned
    first.Store.Load() |> run |> ignore
    Assert.Equal(Ok(), first.Store.Save(empty |> enqueue "a") |> run)
    Assert.True(origin.LocalStorage.Items.ContainsKey(IndexedDbQueue.heldKey chrona))

    // Site data cleared (IndexedDB only), under the open connection.
    origin.Inject "a" FakeFault.StorageCleared
    Assert.Equal(Error QueueStoreFailure.Unavailable, first.Store.Save(empty |> enqueue "a" |> enqueue "b") |> run)
    Assert.Equal(OwnershipState.ConnectionLost, (first.Diagnostics()).Ownership)

    origin.Close "a"
    let second = own origin "b" |> owned
    Assert.Equal<QueueNotice list>([ QueueNotice.LocalQueueLost ], second.Notices)
    Assert.Equal(Ok None, second.Store.Load() |> run)

[<Fact>]
let ``a first use is never reported as a loss`` () =
    let origin = Origin()
    Assert.Empty((own origin "a" |> owned).Notices)

[<Fact>]
let ``once everything is synchronized, a later clear is not reported as a loss`` () =
    let origin = Origin()
    let provider = InMemoryStore()
    let first = own origin "a" |> owned
    first.Store.Save(empty |> enqueue "a") |> run |> ok
    let finished, _ = OfflineSync.run provider.Provider first.Store chrona 10 (empty |> enqueue "a") |> run
    Assert.True((OfflineQueue.status finished).Synchronized)
    Assert.False(origin.LocalStorage.Items.ContainsKey(IndexedDbQueue.heldKey chrona))

    origin.Inject "a" FakeFault.StorageCleared
    origin.Close "a"
    Assert.Empty((own origin "b" |> owned).Notices)

// ---------------------------------------------------------------------------
// Persistence (OQ-LIMEN-IDB-004), secrets (LCP-068), diagnostics (LCP-073)
// ---------------------------------------------------------------------------

let private persistRequests (origin: Origin) tab =
    origin.Requests tab |> List.filter (function Wire.Request.Persist -> true | _ -> false) |> List.length

[<Fact>]
let ``persistence is asked for after the first offline write, never at open, and once`` () =
    let origin = Origin()
    let queue = own origin "a" |> owned
    queue.Store.Load() |> run |> ignore
    Assert.Equal(0, persistRequests origin "a")
    Assert.Equal(Ok(), queue.Store.Save empty |> run)
    Assert.Equal(0, persistRequests origin "a")
    Assert.Equal(Ok(), queue.Store.Save(empty |> enqueue "a") |> run)
    Assert.Equal(1, persistRequests origin "a")
    Assert.Equal(Some true, (queue.Diagnostics()).Persisted)
    Assert.Equal(Ok(), queue.Store.Save(empty |> enqueue "a" |> enqueue "b") |> run)
    Assert.Equal(1, persistRequests origin "a")

[<Fact>]
let ``without navigator.storage, persistence stays unknown and saving still works`` () =
    let origin = Origin(storage = None)
    let queue = own origin "a" |> owned
    Assert.Equal(Ok(), queue.Store.Save(empty |> enqueue "a") |> run)
    Assert.Equal(None, (queue.Diagnostics()).Persisted)

[<Fact>]
let ``diagnostics report mode, depth, size against budget, last save, last sync and ownership`` () =
    let origin = Origin()
    let provider = InMemoryStore()
    let queue = own origin "a" |> owned
    let initial = queue.Diagnostics()
    Assert.Equal(None, initial.Depth)
    Assert.Equal(None, initial.LastSave)
    Assert.Equal(None, initial.SnapshotSize)

    let saved = empty |> enqueue "a" |> enqueue "b"
    origin.Clock <- at.AddMinutes 1.0
    queue.Store.Save saved |> run |> ok
    let afterSave = queue.Diagnostics()
    Assert.Equal(Some(at.AddMinutes 1.0), afterSave.LastSave)
    Assert.Equal(None, afterSave.LastSync)
    Assert.Equal(2, afterSave.Depth.Value.Pending)
    Assert.Equal(Some(int64 (OfflineQueue.encode saved |> ok).Length), afterSave.SnapshotSize)
    Assert.Equal(IndexedDbQueue.DefaultBudget, afterSave.Budget)

    origin.Clock <- at.AddMinutes 2.0
    OfflineSync.run provider.Provider queue.Store chrona 10 saved |> run |> ignore
    let afterSync = queue.Diagnostics()
    Assert.Equal(Some(at.AddMinutes 2.0), afterSync.LastSync)
    Assert.True(afterSync.Depth.Value.Synchronized)
    Assert.Equal(OwnershipState.Owner 1L, afterSync.Ownership)

[<Fact>]
let ``a persisted snapshot never holds the access token (LCP-068)`` () =
    let server = FakeGitHub.Server("acme", "data")
    let sentinel = server.Token

    let host: Arca.GitHub.Host =
        { Send = fun sent -> async { return server.Send sent }
          Wait = fun _ -> async { return () }
          Tokens = fun () -> async { return server.ValidToken() } }

    let provider = GitHubStorage.provider host (GitHubConfig.create chrona.Location)
    let origin = Origin()
    let queue = own origin "a" |> owned
    let pending = empty |> enqueue "a" |> enqueue "b"
    queue.Store.Save pending |> run |> ok
    let finished, _ = OfflineSync.run provider queue.Store chrona 10 pending |> run
    Assert.True((OfflineQueue.status finished).Synchronized)

    let record = storedRecord origin |> Option.get
    let (Limen.Contract.RawJson raw) = Codec.encode IndexedDbQueue.codec record |> ok
    Assert.DoesNotContain(sentinel, raw)
    Assert.False(Secrets.looksLikeCredential raw)
    Assert.DoesNotContain(origin.LocalStorage.Items.Values, fun value -> value.Contains sentinel)

[<Fact>]
let ``failures carry a class and a stable code, never the stored value (LCP-072)`` () =
    let origin = Origin()
    let queue = own origin "a" |> owned
    let secretish = "a stored value that must not leak"
    queue.Store.Save(empty |> enqueue (secretish.Replace(" ", "-"))) |> run |> ok
    plantQueueText origin $"{{\"arcaQueue\":7,\"note\":\"{secretish}\"}}"

    match queue.Store.Load() |> run with
    | Error(QueueStoreFailure.Corrupt _) -> ()
    | other -> failwith $"expected Corrupt, got {other}"

    let failure = (queue.Diagnostics()).LastFailure |> Option.get
    Assert.Equal(FailureClass.Undecodable, failure.Class)
    Assert.Equal("arca.limen.undecodable.queue", failure.Code)
    Assert.DoesNotContain(secretish, failure.Detail)

[<Fact>]
let ``every failure class maps to an Aegis fault with its code (ARCA-ARCH-007)`` () =
    let classes =
        [ FailureClass.Unavailable
          FailureClass.Quota
          FailureClass.Conflict
          FailureClass.Version
          FailureClass.Invalid
          FailureClass.Undecodable
          FailureClass.ConnectionLost
          FailureClass.OwnedElsewhere ]

    let codes =
        classes
        |> List.map (fun failureClass ->
            let failure = AdapterFailure.create "save" IndexedDbQueue.Database None failureClass "x" "detail"

            let fault =
                Aegis.Translation.toFault
                    AdapterFailure.mapping
                    (Aegis.FaultId "f-1", Aegis.CorrelationId "c-1", at)
                    ("chrona", None)
                    "queue.save"
                    Map.empty
                    None
                    failure

            Assert.Equal(Aegis.FaultCode failure.Code, fault.Code)
            failure.Code)

    Assert.Equal(classes.Length, codes |> List.distinct |> List.length)

[<Fact>]
let ``a queue over the pack's value limit is refused before anything is sent`` () =
    let origin = Origin(limits = { MaxValueBytes = 2048L; MaxTransactionBytes = 8192L })
    let queue = own origin "a" |> owned
    let large = [ 1..20 ] |> List.fold (fun queue i -> enqueue $"entry-{i}" queue) empty
    let before = (origin.Requests "a").Length

    match queue.Store.Save large |> run with
    | Error(QueueStoreFailure.QuotaExceeded(bytes, 2048L)) -> Assert.True(bytes > 2048L)
    | other -> failwith $"expected QuotaExceeded, got {other}"

    Assert.Equal(before, (origin.Requests "a").Length)
    Assert.Equal(Some FailureClass.Quota, (queue.Diagnostics()).LastFailure |> Option.map _.Class)

[<Fact>]
let ``the browser's quota at commit is QuotaExceeded, with nothing applied`` () =
    let origin = Origin()
    let queue = own origin "a" |> owned
    queue.Store.Save(empty |> enqueue "a") |> run |> ok
    origin.Inject "a" FakeFault.Quota

    match queue.Store.Save(empty |> enqueue "a" |> enqueue "b") |> run with
    | Error(QueueStoreFailure.QuotaExceeded _) -> ()
    | other -> failwith $"expected QuotaExceeded, got {other}"

    Assert.Equal<string list>([ "offline-a" ], storedQueue origin |> Option.get |> keysOf)
    // The next save still goes through: nothing was fenced.
    Assert.Equal(Ok(), queue.Store.Save(empty |> enqueue "a" |> enqueue "b") |> run)

// ---------------------------------------------------------------------------
// Sign-out (LCP-070)
// ---------------------------------------------------------------------------

[<Fact>]
let ``discarding at sign-out removes only the account's discardable entries, counted`` () =
    let origin = Origin()
    let queue = own origin "a" |> owned
    let provider = InMemoryStore()
    let token = provider.Provider.ChangeToken chrona |> run |> ok

    let held =
        empty
        |> enqueueAs "alice" "a1"
        |> enqueueAs "bob" "b1"
        |> enqueueAs "alice" "a2"
        |> OfflineQueue.markInFlight 1L token
        |> ok

    queue.Store.Save held |> run |> ok
    Assert.Equal(2, QueueSignOut.unsentOf "alice" held)

    let kept, count = queue.Discard "alice" held |> run |> ok
    // a1 is in flight: it may have landed, so it stays to be reconciled.
    Assert.Equal(1, count)
    Assert.Equal<string list>([ "offline-a1"; "offline-b1" ], keysOf kept)
    Assert.Equal(Some kept, storedQueue origin)
    Assert.Equal(1, (queue.Diagnostics()).Discarded)

// Two people who share a display name ("Alex"), told apart by their GitHub
// numeric user ids (ARCA-OFF-007).
let private alexA = AccountId.ProviderSubject("github", "1001")
let private alexB = AccountId.ProviderSubject("github", "2002")
let private enqueueFor account display key queue = OfflineQueue.enqueueFor account at (operationAs display key) queue |> ok |> fst
let private signingOut account = { Account = account; Legacy = None }

let private sharedName (token: ChangeToken) =
    empty
    |> enqueueFor alexA "Alex" "a1"
    |> enqueueFor alexB "Alex" "b1"
    |> enqueueFor alexA "Alex" "a2"
    |> enqueueFor alexA "Alex" "a3"
    |> OfflineQueue.markInFlight 1L token
    |> ok

[<Fact>]
let ``discarding by stable account id never touches another account with the same display name (ARCA-OFF-007)`` () =
    let origin = Origin()
    let queue = own origin "a" |> owned
    let token = InMemoryStore().Provider.ChangeToken chrona |> run |> ok
    let held = sharedName token
    queue.Store.Save held |> run |> ok

    // The display name cannot tell them apart; the stable id can.
    Assert.Equal(4, QueueSignOut.unsentOf "Alex" held)
    Assert.Equal(3, QueueSignOut.unsentOfAccount (signingOut alexA) held)
    Assert.Equal(1, QueueSignOut.unsentOfAccount (signingOut alexB) held)

    let kept, count = queue.DiscardAccount (signingOut alexA) held |> run |> ok
    // a1 is in flight: it may have landed, so it stays to be reconciled.
    Assert.Equal(2, count)
    Assert.Equal<string list>([ "offline-a1"; "offline-b1" ], keysOf kept)
    Assert.Equal(Some kept, storedQueue origin)
    Assert.Equal(2, (queue.Diagnostics()).Discarded)

[<Fact>]
let ``outcome-unknown entries are never discarded, whoever signs out (ARCA-OFF-007)`` () =
    let token = InMemoryStore().Provider.ChangeToken chrona |> run |> ok

    let held =
        sharedName token
        |> OfflineQueue.recordResult 1L (Error(StorageFailure.OutcomeUnknown { IdempotencyKey = IdempotencyKey.create "offline-a1" |> ok; Base = token; Candidate = None; Revisions = Map.empty }))
        |> ok

    let kept, count = QueueSignOut.discardAccount (signingOut alexA) held
    Assert.Equal(2, count)
    Assert.Equal<EntryState list>([ EntryState.OutcomeUnknown(let (ChangeToken t) = token in t); EntryState.Pending ], kept.Entries |> List.map _.State)

[<Fact>]
let ``entries queued without an account id match only the legacy identity the caller names (ARCA-OFF-007)`` () =
    let legacy = empty |> enqueueAs "Alex" "old1" |> enqueueFor alexA "Alex" "new1"

    // Without a legacy identity, an entry with no id is never matched.
    let kept, count = QueueSignOut.discardAccount (signingOut alexA) legacy
    Assert.Equal(1, count)
    Assert.Equal<string list>([ "offline-old1" ], keysOf kept)

    // Naming it is the caller's explicit decision.
    let _, both = QueueSignOut.discardAccount { Account = alexA; Legacy = Some "Alex" } legacy
    Assert.Equal(2, both)

    // Another account's id never matches an entry recorded for alexA.
    let _, none = QueueSignOut.discardAccount (signingOut alexB) (empty |> enqueueFor alexA "Alex" "x")
    Assert.Equal(0, none)

[<Fact>]
let ``a memory-only queue discards by account id too`` () =
    let origin = Origin()
    let queue = LimenQueue.own (origin.Host "a") { QueueOptions.standard with Order = [ DurabilityChoice.MemoryOnly ] } chrona |> run |> owned
    Assert.Equal(DurabilityMode.MemoryOnly, queue.Mode)
    let held = empty |> enqueueFor alexA "Alex" "a1" |> enqueueFor alexB "Alex" "b1"
    queue.Store.Save held |> run |> ok
    let kept, count = queue.DiscardAccount (signingOut alexB) held |> run |> ok
    Assert.Equal(1, count)
    Assert.Equal<string list>([ "offline-a1" ], keysOf kept)

[<Fact>]
let ``releasing ownership lets the next tab own the queue`` () =
    let origin = Origin()
    let first = own origin "a" |> owned
    first.Release() |> run
    Assert.Equal(OwnershipState.Released, (first.Diagnostics()).Ownership)
    Assert.Equal(OwnershipState.Owner 2L, ((own origin "b" |> owned).Diagnostics()).Ownership)
