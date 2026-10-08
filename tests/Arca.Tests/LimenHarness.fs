/// One browser origin for Arca.Limen's tests: Limen's own in-memory
/// `limen.store` (FakeStore, which passes Limen's shared conformance
/// vectors), Web Locks as the coordination pack answers them, and one
/// localStorage, all shared by named tabs.
module Arca.Tests.LimenHarness

open System
open System.Collections.Generic
open Arca
open Arca.GitHub
open Arca.Limen
open Limen.Contract.Coordination
open Limen.Store

/// Web Locks shared by the tabs of one origin, as the coordination pack
/// answers `acquire` and `release` (LCP-040).
type FakeLocks() =
    let holders = Dictionary<string, string * LockHandle>()
    let lost = Dictionary<string, LockHandle list>()
    let mutable issued = 0
    member val Supported = true with get, set

    /// The tab holding a lock, if any.
    member _.Holder(name: string) =
        match holders.TryGetValue name with
        | true, (tab, _) -> Some tab
        | _ -> None

    /// The locks a tab lost to a steal.
    member _.Lost(tab: string) =
        match lost.TryGetValue tab with
        | true, handles -> handles
        | _ -> []

    member this.For(tab: string) : LockExecutor =
        fun request ->
            async {
                if not this.Supported then
                    return CoordinationResult.Unsupported
                else
                    match request with
                    | CoordinationRequest.Acquire(name, _, _, steal) ->
                        match holders.TryGetValue name with
                        | true, (holder, _) when holder <> tab && not steal -> return CoordinationResult.Busy
                        | true, (holder, _) when holder = tab -> return CoordinationResult.Busy
                        | true, (holder, handle) ->
                            lost[holder] <- this.Lost holder @ [ handle ]
                            issued <- issued + 1
                            let next = LockHandle $"lock-{issued}"
                            holders[name] <- (tab, next)
                            return CoordinationResult.Acquired next
                        | _ ->
                            issued <- issued + 1
                            let next = LockHandle $"lock-{issued}"
                            holders[name] <- (tab, next)
                            return CoordinationResult.Acquired next
                    | CoordinationRequest.Release handle ->
                        match holders |> Seq.tryFind (fun pair -> snd pair.Value = handle) with
                        | Some pair ->
                            holders.Remove pair.Key |> ignore
                            return CoordinationResult.Released
                        | None -> return CoordinationResult.UnknownLock
                    | _ -> return CoordinationResult.Unsupported
            }

    /// The browser releases a closed tab's locks.
    member _.Close(tab: string) =
        for name in holders |> Seq.filter (fun pair -> fst pair.Value = tab) |> Seq.map _.Key |> List.ofSeq do
            holders.Remove name |> ignore

/// One localStorage, shared by the origin's tabs.
type FakeLocalStorage() =
    let items = Dictionary<string, string>()
    member val Unavailable = false with get, set
    member _.Items = items

    member this.Execute(request: LocalStorageRequest) =
        async {
            match this.Unavailable, request with
            | true, _ -> return LocalStorageOutcome.Failure LocalStorageFailure.Unavailable
            | false, LocalStorageRequest.Get key ->
                match items.TryGetValue key with
                | true, value -> return LocalStorageOutcome.Success(Some value)
                | _ -> return LocalStorageOutcome.Success None
            | false, LocalStorageRequest.Set(key, value) ->
                items[key] <- value
                return LocalStorageOutcome.Success None
            | false, LocalStorageRequest.Remove key ->
                items.Remove key |> ignore
                return LocalStorageOutcome.Success None
        }

/// One origin: its tabs share IndexedDB, Web Locks and localStorage. Every
/// tab registers the store pack with the same application namespace.
type Origin(?storage: FakeStorage option, ?limits: Limits) =
    let storage = defaultArg storage (Some { Persist = true; Persisted = false; Usage = 0L; Quota = 1_000_000_000L })
    let tabs = FakeStore.origin storage
    let executors = Dictionary<string, FakeExecutor>()
    let requests = Dictionary<string, List<Limen.Store.Wire.Request>>()
    member val Locks = FakeLocks()
    member val LocalStorage = FakeLocalStorage()
    member val Clock = DateTimeOffset(2026, 10, 8, 12, 0, 0, TimeSpan.Zero) with get, set

    /// The tab's executor over the shared IndexedDB, registered on first use.
    member _.Executor(tab: string) =
        match executors.TryGetValue tab with
        | true, executor -> executor
        | _ ->
            let config =
                match limits with
                | Some limits -> { FakeTabConfig.create "chrona" with Limits = limits }
                | None -> FakeTabConfig.create "chrona"

            let executor = tabs tab config
            executors[tab] <- executor
            requests[tab] <- List()
            executor

    /// Every store request a tab sent.
    member this.Requests(tab: string) =
        this.Executor tab |> ignore
        List.ofSeq requests[tab]

    /// A tab's host: its store, lock and localStorage executors and the clock.
    member this.Host(tab: string) : LimenHost =
        let executor = this.Executor tab

        { Store =
            fun request ->
                requests[tab].Add request
                executor.Execute request
          Lock = this.Locks.For tab
          LocalStorage = this.LocalStorage.Execute
          Now = fun () -> this.Clock }

    member this.Inject (tab: string) (fault: FakeFault) = (this.Executor tab).Inject fault

    /// The tab closes: the browser releases its locks.
    member this.Close(tab: string) = this.Locks.Close tab

let ok result =
    match result with
    | Ok value -> value
    | Error error -> failwith $"expected Ok, got {error}"

let run work = Async.RunSynchronously work

let chrona =
    Namespace.ofApplication
        { Application = AppId.create "chrona" |> ok
          Environment = { Kind = EnvironmentKind.Test; Name = "limen" }
          Location = DataLocation.create "acme" "data" "main" "apps" |> ok }
    |> ok

let at = DateTimeOffset(2026, 10, 8, 12, 0, 0, TimeSpan.Zero)

let operationAs (account: string) (key: string) =
    Operation.create
        chrona
        { Summary = $"note {key}"
          Actor = { Kind = ActorKind.Human; Id = ActorId.create account |> ok }
          ProviderIdentity = Some account
          ExecutionId = None
          CorrelationId = CorrelationId.create $"c-{key}" |> ok
          IdempotencyKey = IdempotencyKey.create $"offline-{key}" |> ok }
        [ Change.Create(RelativePath.parse $"notes/{key}.json" |> ok, $"{{\"n\":\"{key}\"}}") ]
    |> ok

let enqueueAs account key queue = OfflineQueue.enqueue at (operationAs account key) queue |> ok |> fst
let enqueue key queue = enqueueAs "alice" key queue
let empty = OfflineQueue.create OfflinePolicy.QueueWrites
let keysOf (queue: OfflineQueue) = queue.Entries |> List.map _.Operation.IdempotencyKey

/// IndexedDB only: no fallback.
let indexedDbOnly =
    { QueueOptions.standard with Order = [ DurabilityChoice.IndexedDb ] }

/// The tab's owned queue, or a failure naming what it got instead.
let owned (opening: QueueOpening) =
    match opening with
    | QueueOpening.Owned queue -> queue
    | QueueOpening.OwnedElsewhere -> failwith "expected Owned, got OwnedElsewhere"
    | QueueOpening.OwnershipUnsupported -> failwith "expected Owned, got OwnershipUnsupported"
    | QueueOpening.NothingUsable failures -> failwith $"""expected Owned, got NothingUsable ({failures |> List.map _.Code |> String.concat ", "})"""

let own (origin: Origin) (tab: string) = LimenQueue.own (origin.Host tab) indexedDbOnly chrona |> run

/// What IndexedDB holds for the namespace, read by a tab of its own.
let storedRecord (origin: Origin) =
    let execute = (origin.Executor "inspector").Execute

    match IndexedDbQueue.openDatabase execute |> run with
    | Error failure -> failwith $"inspector could not open: {failure.Code}"
    | Ok opened -> IndexedDbQueue.read execute (Connection.Open opened) (IndexedDbQueue.recordKey chrona) |> run |> ok

/// The queue IndexedDB holds for the namespace.
let storedQueue (origin: Origin) =
    storedRecord origin
    |> Option.bind _.Queue
    |> Option.map (fun text -> OfflineQueue.decode text |> ok)

/// Writes a record directly, as something other than the owner would.
let writeRecord (origin: Origin) (record: QueueRecord) =
    let execute = (origin.Executor "inspector").Execute

    match IndexedDbQueue.openDatabase execute |> run with
    | Error failure -> failwith $"inspector could not open: {failure.Code}"
    | Ok opened ->
        let transaction =
            Transaction.readWrite IndexedDbQueue.Database [ Op.put IndexedDbQueue.Records IndexedDbQueue.codec record ] |> ok

        Store.transact execute (Connection.Open opened) transaction |> run |> ok |> ignore

/// Replaces the stored queue text, keeping the owner's epoch.
let plantQueueText (origin: Origin) (text: string) =
    match storedRecord origin with
    | Some record -> writeRecord origin { record with Queue = Some text }
    | None -> failwith "no record to plant into"
