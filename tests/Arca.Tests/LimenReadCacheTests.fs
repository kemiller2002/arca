/// The IndexedDB read cache (WI-0022; Limen LCP-082..LCP-087) over Limen's
/// FakeStore: the read-cache conformance suite, compound-keyed partitions,
/// clearing by one deleteRange per account, the per-namespace budget with
/// least-recently-used eviction, best-effort writes, and the cache giving way
/// to the queue.
module Arca.Tests.LimenReadCacheTests

open System
open Arca
open Arca.Limen
open Arca.Tests.LimenHarness
open Limen.Store
open Xunit

let private openCache (origin: Origin) (tab: string) (budget: int64) =
    IndexedDbReadCache.openCache (origin.Host tab) budget |> run |> ok

let private sample account partition token =
    ReadCacheConformance.sample chrona account partition token

let private problems (results: ConformanceResult list) =
    results
    |> List.choose (fun result ->
        match result.Outcome with
        | ConformanceOutcome.Passed -> None
        | ConformanceOutcome.Failed reason -> Some $"{result.Case} failed: {reason}"
        | ConformanceOutcome.Unsupported reason -> Some $"{result.Case} unsupported: {reason}")

/// Writes a raw record, as something other than this cache would.
let private plant (origin: Origin) (cacheKey: CacheKey) (text: string) =
    let execute = (origin.Executor "inspector").Execute
    let schema = IndexedDbReadCache.schema |> ok
    let opened = Store.openDatabase execute schema |> run |> ok

    let record =
        { Account = cacheKey.Account
          Namespace = cacheKey.Namespace
          Partition = cacheKey.Partition
          Entry = text
          Size = int64 text.Length
          LastUsed = 0L }

    let transaction =
        Transaction.readWrite IndexedDbReadCache.Database [ Op.put IndexedDbReadCache.Entries IndexedDbReadCache.codec record ] |> ok

    Store.transact execute (Connection.Open opened) transaction |> run |> ok |> ignore

/// How many records IndexedDB holds in a range, counted by the pack.
let private count (origin: Origin) (range: Range option) =
    let execute = (origin.Executor "inspector").Execute
    let opened = Store.openDatabase execute (IndexedDbReadCache.schema |> ok) |> run |> ok
    let transaction = Transaction.readOnly IndexedDbReadCache.Database [ Op.count IndexedDbReadCache.Entries None range ] |> ok

    match Store.transact execute (Connection.Open opened) transaction |> run |> ok with
    | [ result ] -> Read.count result |> Option.get
    | _ -> failwith "expected one count"

[<Fact>]
let ``the IndexedDB read cache passes the read-cache conformance suite (LCP-082)`` () =
    let fresh () =
        async {
            let origin = Origin()
            let cache = openCache origin "a" IndexedDbReadCache.DefaultBudget
            let reopened = ref 0

            return
                { Namespace = chrona
                  Store = cache.Store
                  Reopen =
                    fun () ->
                        async {
                            reopened.Value <- reopened.Value + 1
                            return (openCache origin $"r{reopened.Value}" IndexedDbReadCache.DefaultBudget).Store
                        }
                  Arrange =
                    fun fault ->
                        async {
                            match fault with
                            | ReadCacheFault.Unavailable -> origin.Inject "a" FakeFault.Missing
                            | ReadCacheFault.Stored(cacheKey, text) -> plant origin cacheKey text

                            return true
                        } }
        }

    let results = ReadCacheConformance.run fresh |> run
    Assert.Equal(ReadCacheConformance.cases.Length, results.Length)
    let found = problems results
    Assert.True(found.IsEmpty, String.concat "\n" found)

[<Fact>]
let ``sign-out clears one account with a single deleteRange; the count over its range is 0 (LCP-086)`` () =
    let origin = Origin()
    let cache = openCache origin "a" IndexedDbReadCache.DefaultBudget

    for entry in [ sample "alice" "2026-09" "t-1"; sample "alice" "2026-10" "t-1"; sample "bob" "2026-10" "t-1" ] do
        cache.Store.Save entry |> run |> ok

    let before = (origin.Requests "a").Length
    cache.Store.Clear(CacheScope.Account "alice") |> run |> ok

    let sent = (origin.Requests "a")[before..]

    match sent with
    | [ Wire.Request.Transact(_, _, [ Wire.Operation.DeleteRange _ ]) ] -> ()
    | other -> failwith $"expected one deleteRange, got {other.Length} requests"

    Assert.Equal(0L, count origin (Range.prefix [ Key.Text "alice" ] |> ok |> Some))
    Assert.Equal(1L, count origin (Range.prefix [ Key.Text "bob" ] |> ok |> Some))

[<Fact>]
let ``another account sees none of the first account's entries`` () =
    let origin = Origin()
    let cache = openCache origin "a" IndexedDbReadCache.DefaultBudget
    let alice = sample "alice" "2026-10" "t-1"
    cache.Store.Save alice |> run |> ok
    Assert.Equal(Ok None, cache.Store.Load { alice.Key with Account = "bob" } |> run)
    Assert.Equal(Ok [], cache.Store.Partitions "bob" alice.Key.Namespace |> run)

[<Fact>]
let ``over the namespace budget, the least recently used partition is evicted first (LCP-087)`` () =
    let origin = Origin()
    let one = sample "alice" "p1" "t-1"
    let size = (IndexedDbReadCache.recordOf at one |> ok).Size
    // Room for two partitions, not three.
    let cache = openCache origin "a" (size * 2L + size / 2L)

    origin.Clock <- at
    cache.Store.Save one |> run |> ok
    origin.Clock <- at.AddMinutes 1.0
    cache.Store.Save(sample "alice" "p2" "t-1") |> run |> ok

    // p1 is shown again, so p2 is now the least recently used.
    origin.Clock <- at.AddMinutes 2.0
    cache.Show one.Key |> run |> ok |> ignore

    origin.Clock <- at.AddMinutes 3.0
    cache.Store.Save(sample "alice" "p3" "t-1") |> run |> ok

    let ns = ReadCache.namespaceId chrona
    let kept = cache.Store.Partitions "alice" ns |> run |> ok |> List.map _.Partition
    Assert.Equal<string list>([ "p1"; "p3" ], kept)
    Assert.Equal(1, (cache.Diagnostics()).Evictions)
    Assert.True((cache.Diagnostics()).Size.Value <= (cache.Diagnostics()).Budget)

[<Fact>]
let ``a partition larger than the whole budget is refused, and nothing else is evicted`` () =
    let origin = Origin()
    let small = { sample "alice" "p1" "t-1" with Records = [] }
    let large = sample "alice" "p2" "t-1"
    let sizeOf entry = (IndexedDbReadCache.recordOf at entry |> ok).Size
    Assert.True(sizeOf large > sizeOf small + 16L)
    // The small partition fits; the large one does not fit even alone.
    let cache = openCache origin "a" (sizeOf small + 16L)
    cache.Store.Save small |> run |> ok
    Assert.Equal(Error ReadCacheFailure.QuotaExceeded, cache.Store.Save large |> run)
    Assert.Equal(Ok(Some small), cache.Store.Load small.Key |> run)

[<Fact>]
let ``a cache write that fails never fails the read that produced it; it is reported (LCP-087)`` () =
    let origin = Origin()
    let cache = openCache origin "a" IndexedDbReadCache.DefaultBudget
    let provider = InMemoryStore()
    provider.Provider.Commit(operationAs "alice" "seed") |> run |> ok |> ignore
    let token = provider.Provider.ChangeToken chrona |> run |> ok

    let read =
        match provider.Provider.Read chrona (RelativePath.parse "notes/seed.json" |> ok) |> run |> ok with
        | ReadOutcome.Found stored -> stored
        | ReadOutcome.Absent -> failwith "seeded"

    let entry = ReadCache.entry (ReadCache.key "alice" chrona "notes" |> ok) 1 at (Fresh.read token [ read ])

    // The browser's quota fails the cache write.
    origin.Inject "a" FakeFault.Quota
    cache.Keep entry |> run
    Assert.Equal(Some FailureClass.Quota, (cache.Diagnostics()).LastFailure |> Option.map _.Class)
    Assert.Equal(Ok None, cache.Store.Load entry.Key |> run)
    // The read's value is untouched; the next keep succeeds.
    cache.Keep entry |> run
    Assert.Equal(Ok(Some entry), cache.Store.Load entry.Key |> run)

[<Fact>]
let ``a queue save that hits the quota evicts the cache first, then succeeds (LCP-087)`` () =
    let origin = Origin()
    let cache = openCache origin "a" IndexedDbReadCache.DefaultBudget
    cache.Store.Save(sample "alice" "2026-10" "t-1") |> run |> ok

    let queue =
        LimenQueue.own (origin.Host "a") { indexedDbOnly with FreeSpace = Some cache.FreeSpace } chrona |> run |> owned

    origin.Inject "a" FakeFault.Quota
    Assert.Equal(Ok(), queue.Store.Save(empty |> enqueue "a") |> run)
    Assert.Equal<string list>([ "offline-a" ], storedQueue origin |> Option.get |> keysOf)
    Assert.Equal(0L, count origin None)
    Assert.Equal(1, (cache.Diagnostics()).Evictions)

[<Fact>]
let ``without a cache to evict, the queue's quota failure is reported as QuotaExceeded`` () =
    let origin = Origin()
    let cache = openCache origin "a" IndexedDbReadCache.DefaultBudget
    let queue = LimenQueue.own (origin.Host "a") { indexedDbOnly with FreeSpace = Some cache.FreeSpace } chrona |> run |> owned
    origin.Inject "a" FakeFault.Quota

    match queue.Store.Save(empty |> enqueue "a") |> run with
    | Error(QueueStoreFailure.QuotaExceeded _) -> ()
    | other -> failwith $"expected QuotaExceeded, got {other}"

[<Fact>]
let ``any tab may write the cache; two tabs writing one partition both succeed`` () =
    let origin = Origin()
    let first = openCache origin "a" IndexedDbReadCache.DefaultBudget
    let second = openCache origin "b" IndexedDbReadCache.DefaultBudget
    let older = sample "alice" "2026-10" "t-1"
    let newer = sample "alice" "2026-10" "t-2"
    Assert.Equal(Ok(), first.Store.Save older |> run)
    Assert.Equal(Ok(), second.Store.Save newer |> run)
    Assert.Equal(Ok(Some newer), first.Store.Load older.Key |> run)

[<Fact>]
let ``revalidated online: equal token kept, changed token refreshed, removed partition dropped, unreachable stays as of its token (LCP-084)`` () =
    let origin = Origin()
    let cache = openCache origin "a" IndexedDbReadCache.DefaultBudget
    let provider = InMemoryStore()
    provider.Provider.Commit(operationAs "alice" "seed") |> run |> ok |> ignore
    let path = RelativePath.parse "notes/seed.json" |> ok
    let cacheKey = ReadCache.key "alice" chrona "notes" |> ok

    let readPartition () =
        let token = provider.Provider.ChangeToken chrona |> run |> ok

        match provider.Provider.Read chrona path |> run |> ok with
        | ReadOutcome.Found stored -> ReadCache.entry cacheKey 1 (origin.Clock) (Fresh.read token [ stored ])
        | ReadOutcome.Absent -> failwith "absent"

    let observe () =
        match provider.Provider.ChangeToken chrona |> run with
        | Ok token -> ProviderObservation.Current token
        | Error _ -> ProviderObservation.Unreachable

    cache.Keep(readPartition ()) |> run
    let shown = cache.Show cacheKey |> run |> ok |> Option.get

    // Equal token: confirmed, fresh at the provider's token.
    match ReadCache.revalidate (observe ()) shown with
    | Revalidation.Confirmed fresh -> Assert.Equal(provider.Provider.ChangeToken chrona |> run |> ok, Fresh.token fresh)
    | other -> failwith $"expected Confirmed, got {other}"

    // The provider moves on: refresh, and replace the entry.
    provider.Provider.Commit(operationAs "alice" "later") |> run |> ok |> ignore

    match ReadCache.revalidate (observe ()) shown with
    | Revalidation.Refresh stale ->
        Assert.True(Cached.isStale stale)
        cache.Keep(readPartition ()) |> run
        let refreshed = cache.Show cacheKey |> run |> ok |> Option.get
        Assert.Equal(provider.Provider.ChangeToken chrona |> run |> ok, ChangeToken(CachedToken.text (Cached.asOf refreshed)))
    | other -> failwith $"expected Refresh, got {other}"

    // The partition is gone: remove it.
    match ReadCache.revalidate ProviderObservation.PartitionGone shown with
    | Revalidation.Remove gone -> cache.Store.Remove gone |> run |> ok
    | other -> failwith $"expected Remove, got {other}"

    Assert.Equal(Ok None, cache.Store.Load cacheKey |> run)

[<Fact>]
let ``clear this device deletes the queue and the cache databases`` () =
    let origin = Origin()
    let cache = openCache origin "a" IndexedDbReadCache.DefaultBudget
    cache.Store.Save(sample "alice" "p" "t-1") |> run |> ok
    (own origin "q" |> owned).Store.Save(empty |> enqueue "a") |> run |> ok

    Assert.Equal(Ok(), LimenDevice.clear (origin.Host "a") |> run)
    Assert.Equal(None, storedRecord origin)
    Assert.Equal(0L, count origin None)

[<Fact>]
let ``clearing the device is typed Blocked while another page will not let go`` () =
    let host = Origin().Host "a"

    let blocked =
        { host with
            Store =
                fun request ->
                    match request with
                    | Wire.Request.DeleteDatabase _ -> async { return Limen.Contract.Store.Types.StoreResult.Blocked }
                    | other -> host.Store other }

    Assert.Equal(Error(IndexedDbQueue.Database, ReadCacheFailure.Blocked), LimenDevice.clear blocked |> run)

[<Fact>]
let ``eviction picks the least recently used partitions until the new one fits`` () =
    let record partition size lastUsed =
        { Account = "alice"
          Namespace = "chrona@x"
          Partition = partition
          Entry = ""
          Size = size
          LastUsed = lastUsed }

    let held = [ record "old" 40L 1L; record "mid" 40L 2L; record "new" 40L 3L ]
    let writing = { Account = "alice"; Namespace = "chrona@x"; Partition = "next" }
    Assert.Equal<string list>([], IndexedDbReadCache.evictions 200L 40L writing held |> List.map _.Partition)
    Assert.Equal<string list>([ "old" ], IndexedDbReadCache.evictions 140L 40L writing held |> List.map _.Partition)
    Assert.Equal<string list>([ "old"; "mid" ], IndexedDbReadCache.evictions 100L 40L writing held |> List.map _.Partition)
    // Rewriting a partition does not count its old size.
    Assert.Equal<string list>([], IndexedDbReadCache.evictions 120L 40L { writing with Partition = "mid" } held |> List.map _.Partition)
