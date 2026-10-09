namespace Arca.Limen

open System
open System.Text
open Arca
open Arca.GitHub
open Limen.Contract
open Limen.Store

/// The host's executors: Limen's store pack and coordination pack, Limen
/// Core's Storage effect (localStorage), and the host's clock. They are the
/// only effects; everything the adapter decides is a pure function of what
/// they answer.
[<NoEquality; NoComparison>]
type LimenHost =
    { Store: StoreExecutor
      Lock: LockExecutor
      LocalStorage: LocalStorageRequest -> Async<LocalStorageOutcome>
      /// The host's clock, read only for diagnostics' times (LCP-073).
      Now: unit -> DateTimeOffset }

/// One namespace's queue as IndexedDB holds it: the snapshot and the fencing
/// epoch of the tab that owns it, side by side, so one compare-and-put
/// checks both (LCP-059).
type QueueRecord =
    { /// The namespace's queue key, `arca.queue.<app>[.<dataset>]`, as the
      /// localStorage queue names it.
      Namespace: string
      /// Raised by every new owner; a save from an older epoch writes nothing.
      Epoch: int64
      /// The queue's canonical text (`OfflineQueue.encode`); None when no
      /// queue has been saved.
      Queue: string option
      /// The SHA-256 of the localStorage text adopted into this record, when
      /// one was (the migration marker, LCP-066).
      Migrated: string option }

/// The next step of the one-time move of a namespace's localStorage queue
/// into IndexedDB (WI-0020, LCP-066).
[<RequireQualifiedAccess>]
type LegacyStep =
    /// No localStorage queue: nothing to move.
    | NoLegacy
    /// localStorage cannot be read now; try at the next load.
    | LegacyUnavailable
    /// A corrupt or foreign localStorage queue: left in place, never deleted.
    | Unreadable
    /// The copy already committed (the marker matches): remove the source only.
    | RetireOnly
    /// The IndexedDB queue holds entries: wait until it holds none. Two queues
    /// are never merged, and neither is dropped.
    | Pending of entries: int
    /// The IndexedDB queue is empty: copy this queue with this marker, verify,
    /// then remove the source.
    | Adopt of queue: OfflineQueue * marker: string

/// What opening the IndexedDB queue produced, with the lock already held.
[<RequireQualifiedAccess; NoEquality; NoComparison>]
type IndexedDbOpening =
    | Opened of OwnedQueue
    /// Another tab claimed a newer epoch while this one was claiming.
    | Contended
    | Failed of AdapterFailure

/// The IndexedDB implementation of Arca's `QueueStore` port over Limen's
/// `limen.store` (WI-0016; Limen LCP-046, LCP-059, LCP-060, LCP-062). The
/// port is unchanged. The host registers the store pack with its application
/// namespace (`storeCapability({ namespace })`); this adapter's database is
/// `arca-queue` inside it.
[<RequireQualifiedAccess>]
module IndexedDbQueue =

    [<Literal>]
    let Database = "arca-queue"

    [<Literal>]
    let Records = "queues"

    /// The default budget, in UTF-16 code units of the queue's canonical
    /// text: the localStorage queue's, so a queue moved from localStorage
    /// always fits. Within the pack's default 1 MiB value limit for ASCII
    /// queues; register the pack with a larger `maxValueBytes` for a larger
    /// budget. A queue over a limit is refused whole, never truncated.
    [<Literal>]
    let DefaultBudget = 1_000_000L

    /// The database's schema: one store, keyed by namespace.
    let schema =
        Schema.create
            Database
            1L
            [ { Name = Records
                KeyPath = KeyPath.Path "namespace"
                Indexes = [] } ]
            []

    /// How a record is stored.
    let codec: Codec<QueueRecord> =
        Codec.record
            (fun record ->
                Codec.fields
                    [ Codec.field "namespace" Codec.string record.Namespace
                      Codec.field "epoch" Codec.int64 record.Epoch
                      Codec.field "queue" (Codec.option Codec.string) record.Queue
                      Codec.field "migrated" (Codec.option Codec.string) record.Migrated ])
            (fun fields ->
                match
                    Codec.required "namespace" Codec.string fields,
                    Codec.required "epoch" Codec.int64 fields,
                    Codec.optional "queue" Codec.string fields,
                    Codec.optional "migrated" Codec.string fields
                with
                | Ok ns, Ok epoch, Ok queue, Ok migrated ->
                    Ok
                        { Namespace = ns
                          Epoch = epoch
                          Queue = queue
                          Migrated = migrated }
                | Error e, _, _, _
                | _, Error e, _, _
                | _, _, Error e, _
                | _, _, _, Error e -> Error e)

    /// The record's key for a namespace.
    let recordKey (ns: Namespace) = LocalStorageQueue.key ns

    /// The localStorage key of the evidence that this device held unsent
    /// changes for the namespace, kept outside IndexedDB so a recreated
    /// database can be told from a first use (LCP-062).
    let heldKey (ns: Namespace) =
        match ns.Dataset with
        | None -> $"arca.queue-held.{AppId.value ns.Application}"
        | Some dataset -> $"arca.queue-held.{AppId.value ns.Application}.{DatasetId.value dataset}"

    // -----------------------------------------------------------------------
    // Pure decisions.
    // -----------------------------------------------------------------------

    /// The record a new owner writes: the next epoch, the queue untouched.
    let claim (key: string) (current: QueueRecord option) =
        match current with
        | None ->
            { Namespace = key
              Epoch = 1L
              Queue = None
              Migrated = None }
        | Some record -> { record with Epoch = record.Epoch + 1L }

    /// The UTF-8 size of a record as the pack measures a stored value.
    let size (record: QueueRecord) =
        match Codec.encode codec record with
        | Ok(RawJson text) -> int64 (Encoding.UTF8.GetByteCount text)
        | Error _ -> Int64.MaxValue

    /// Whether a compare-and-put of `next` over `expected` fits the limits the
    /// pack reported, decided before anything is sent. A compare-and-put
    /// carries both values.
    let fits (limits: Limits option) (next: QueueRecord) (expected: QueueRecord option) =
        match limits with
        | None -> Ok()
        | Some limits ->
            let value = size next
            let transaction = value + (expected |> Option.map size |> Option.defaultValue 4L) + 256L

            if value > limits.MaxValueBytes then Error(value, limits.MaxValueBytes)
            elif transaction > limits.MaxTransactionBytes then Error(transaction, limits.MaxTransactionBytes)
            else Ok()

    /// Whether a queue holds anything that has not reached the provider.
    let holdsUnsent (queue: OfflineQueue) = not (OfflineQueue.status queue).Synchronized

    // -----------------------------------------------------------------------
    // Requests through the host's executor.
    // -----------------------------------------------------------------------

    let private connected (opened: Opened) = Connection.Open opened

    let private transactFailure operation failure =
        AdapterFailure.ofTransact operation Database Records failure

    /// Reads a namespace's record.
    let read (execute: StoreExecutor) (connection: Connection) (key: string) =
        async {
            match Transaction.readOnly Database [ Op.get Records (Key.Text key) ] with
            | Error problem -> return Error(AdapterFailure.create "read" Database (Some Records) FailureClass.Invalid "request" problem.Problem)
            | Ok transaction ->
                match! Store.transact execute connection transaction with
                | Error failure -> return Error(transactFailure "read" failure)
                | Ok [ result ] ->
                    match Read.value codec Database Records (Key.Text key) result with
                    | Ok record -> return Ok record
                    | Error error -> return Error(AdapterFailure.create "read" Database (Some Records) FailureClass.Undecodable "record" error.Detail)
                | Ok _ -> return Error(AdapterFailure.create "read" Database (Some Records) FailureClass.Unavailable "unexpected" "not one result")
        }

    /// Compare-and-puts a record over what this tab expects is stored. A
    /// conflict answers what is stored instead, when it decodes.
    let write (execute: StoreExecutor) (connection: Connection) (operation: string) (next: QueueRecord) (expected: QueueRecord option) =
        async {
            match Transaction.readWrite Database [ Op.putIf Records codec next expected ] with
            | Error problem -> return Error(AdapterFailure.create operation Database (Some Records) FailureClass.Invalid "request" problem.Problem, None)
            | Ok transaction ->
                match! Store.transact execute connection transaction with
                | Ok _ -> return Ok()
                | Error(TransactFailure.Aborted(AbortCause.Conflict, _, current) as failure) ->
                    let stored = current |> Option.bind (fun raw -> Codec.decode codec raw |> Result.toOption)
                    return Error(transactFailure operation failure, stored)
                | Error failure -> return Error(transactFailure operation failure, None)
        }

    /// Opens the database: the connection, and whether this open created it.
    let openDatabase (execute: StoreExecutor) =
        async {
            match schema with
            | Error problem -> return Error(AdapterFailure.create "open" Database None FailureClass.Invalid "schema" problem.Problem)
            | Ok schema ->
                match! Store.openDatabase execute schema with
                | Ok opened -> return Ok opened
                | Error failure -> return Error(AdapterFailure.ofOpen Database failure)
        }

    // -----------------------------------------------------------------------
    // The one-time move from the localStorage queue (WI-0020; LCP-066,
    // LCP-067; DF-ARCA-2026-0008: copy, verify, retire the source).
    // -----------------------------------------------------------------------

    /// The migration marker of a localStorage text: its SHA-256.
    let marker (text: string) = Export.hash text

    /// What the move does next, decided from the localStorage answer, the
    /// IndexedDB record and the queue it holds.
    let legacyStep (ns: Namespace) (legacy: LocalStorageOutcome) (record: QueueRecord) (current: OfflineQueue option) =
        match legacy with
        | LocalStorageOutcome.Failure _ -> LegacyStep.LegacyUnavailable
        | LocalStorageOutcome.Success None -> LegacyStep.NoLegacy
        | LocalStorageOutcome.Success(Some text) when record.Migrated = Some(marker text) ->
            // The copy committed and was verified; only the removal remains.
            LegacyStep.RetireOnly
        | LocalStorageOutcome.Success(Some text) as outcome ->
            match LocalStorageQueue.loaded ns outcome with
            | Error _ -> LegacyStep.Unreadable
            | Ok None -> LegacyStep.NoLegacy
            | Ok(Some legacyQueue) ->
                match current with
                | Some queue when not queue.Entries.IsEmpty -> LegacyStep.Pending legacyQueue.Entries.Length
                | _ -> LegacyStep.Adopt(legacyQueue, marker text)

    // -----------------------------------------------------------------------
    // The owned store: the one stateful edge, an immutable state in one cell.
    // -----------------------------------------------------------------------

    type private State =
        { Connection: Connection
          Limits: Limits option
          /// What this owner last read or wrote; every save expects it.
          Observed: QueueRecord
          /// The queue last loaded or saved.
          Last: OfflineQueue option
          /// Whether the localStorage evidence of unsent changes is set.
          HeldMarked: bool
          /// Whether persistence was asked for (once per owner, OQ-LIMEN-IDB-004).
          AskedPersist: bool
          Diagnostics: QueueDiagnostics }

    let private queueFailure (failure: AdapterFailure) =
        match failure.Class with
        | FailureClass.Quota -> QueueStoreFailure.QuotaExceeded(0L, 0L)
        | _ -> QueueStoreFailure.Unavailable

    /// The owned store over an opened, claimed database. `handle` is the
    /// namespace's lock, released by `Release`.
    let private owned (host: LimenHost) (budget: int64) (freeSpace: (unit -> Async<bool>) option) (ns: Namespace) (handle: Limen.Contract.Coordination.Types.LockHandle) (initial: State) =
        let cell = ref initial
        let update f = cell.Value <- f cell.Value
        let diagnose f = update (fun state -> { state with Diagnostics = f state.Diagnostics })

        let fail (failure: AdapterFailure) =
            diagnose (QueueDiagnostics.failed failure)

            match failure.Class with
            | FailureClass.ConnectionLost -> update (fun state -> { state with Connection = Connection.Lost; Diagnostics = QueueDiagnostics.ownership OwnershipState.ConnectionLost state.Diagnostics })
            | FailureClass.OwnedElsewhere -> diagnose (QueueDiagnostics.ownership OwnershipState.OwnedElsewhere)
            | _ -> ()

        let fenced () =
            fail (AdapterFailure.create "save" Database (Some Records) FailureClass.OwnedElsewhere "fenced" "another tab owns the queue at a newer epoch")

        /// After a save: the evidence of unsent changes outside IndexedDB, and
        /// persistence asked for after the first offline write.
        let afterSave (queue: OfflineQueue) =
            async {
                let unsent = holdsUnsent queue
                let state = cell.Value

                match unsent, state.HeldMarked with
                | true, false ->
                    match! host.LocalStorage(LocalStorageRequest.Set(heldKey ns, "1")) with
                    | LocalStorageOutcome.Success _ -> update (fun state -> { state with HeldMarked = true })
                    | LocalStorageOutcome.Failure _ -> ()
                | false, true ->
                    match! host.LocalStorage(LocalStorageRequest.Remove(heldKey ns)) with
                    | LocalStorageOutcome.Success _ -> update (fun state -> { state with HeldMarked = false })
                    | LocalStorageOutcome.Failure _ -> ()
                | _ -> ()

                if unsent && not cell.Value.AskedPersist then
                    update (fun state -> { state with AskedPersist = true })

                    match! Store.persisted host.Store with
                    | Ok true -> diagnose (QueueDiagnostics.persisted true)
                    | Ok false
                    | Error _ ->
                        match! Store.persist host.Store with
                        | Ok granted -> diagnose (QueueDiagnostics.persisted granted)
                        | Error _ -> ()
            }

        let notify (notice: QueueNotice) = diagnose (QueueDiagnostics.notice notice)

        /// Runs the move's next step during a load, with the record and the
        /// queue just read; answers the queue the application should hold.
        let migrate (record: QueueRecord) (current: OfflineQueue option) =
            async {
                let! legacy = host.LocalStorage(LocalStorageQueue.loadRequest ns)

                match legacyStep ns legacy record current with
                | LegacyStep.NoLegacy
                | LegacyStep.LegacyUnavailable -> return Ok current
                | LegacyStep.Unreadable ->
                    notify QueueNotice.LegacyQueueUnreadable
                    return Ok current
                | LegacyStep.Pending entries ->
                    notify (QueueNotice.LegacyQueuePending entries)
                    return Ok current
                | LegacyStep.RetireOnly ->
                    let! _ = host.LocalStorage(LocalStorageRequest.Remove(LocalStorageQueue.key ns))
                    return Ok current
                | LegacyStep.Adopt(legacyQueue, digest) ->
                    match MemoryQueueStore.encodeWithin budget legacyQueue with
                    | Error _ ->
                        diagnose (QueueDiagnostics.failed (AdapterFailure.create "migrate" Database (Some Records) FailureClass.Quota "budget" "the localStorage queue is over the IndexedDB budget"))
                        return Ok current
                    | Ok text ->
                        let next = { record with Queue = Some text; Migrated = Some digest }

                        match fits cell.Value.Limits next (Some record) with
                        | Error(bytes, limit) ->
                            diagnose (QueueDiagnostics.failed (AdapterFailure.create "migrate" Database (Some Records) FailureClass.Quota "limit" $"{bytes} bytes over the pack's limit of {limit}"))
                            return Ok current
                        | Ok() ->
                            // Copy.
                            match! write host.Store cell.Value.Connection "migrate" next (Some record) with
                            | Error(_, Some stored) when stored.Epoch > record.Epoch ->
                                fenced ()
                                return Error QueueStoreFailure.Unavailable
                            | Error(failure, _) ->
                                // Nothing was applied: both stores are as they were.
                                fail failure
                                return Ok current
                            | Ok() ->
                                update (fun state -> { state with Observed = next })
                                // Verify.
                                match! read host.Store cell.Value.Connection next.Namespace with
                                | Ok(Some stored) when stored = next && (stored.Queue |> Option.map (MemoryQueueStore.decodeFor ns)) = Some(Ok legacyQueue) ->
                                    // Retire the source. If this fails, the marker
                                    // makes the next load remove it only.
                                    let! _ = host.LocalStorage(LocalStorageRequest.Remove(LocalStorageQueue.key ns))
                                    notify (QueueNotice.LegacyQueueAdopted legacyQueue.Entries.Length)
                                    return Ok(Some legacyQueue)
                                | _ ->
                                    fail (AdapterFailure.create "migrate" Database (Some Records) FailureClass.Unavailable "verify" "the copied queue did not read back as written; the source is kept")
                                    return Error QueueStoreFailure.Unavailable
            }

        let load () =
            async {
                let state = cell.Value

                match! read host.Store state.Connection state.Observed.Namespace with
                | Error failure ->
                    fail failure
                    return Error QueueStoreFailure.Unavailable
                | Ok None ->
                    fail (AdapterFailure.create "load" Database (Some Records) FailureClass.ConnectionLost "record-gone" "the queue record is gone")
                    return Error QueueStoreFailure.Unavailable
                | Ok(Some record) when record.Epoch <> state.Observed.Epoch ->
                    fenced ()
                    return Error QueueStoreFailure.Unavailable
                | Ok(Some record) ->
                    let current =
                        match record.Queue with
                        | None -> Ok None
                        | Some text -> MemoryQueueStore.decodeFor ns text |> Result.map Some

                    match current with
                    | Error failure ->
                        fail (AdapterFailure.create "load" Database (Some Records) FailureClass.Undecodable "queue" "the stored queue is not one this Arca reads")
                        return Error failure
                    | Ok current ->
                        update (fun state -> { state with Observed = record })

                        match! migrate record current with
                        | Ok queue ->
                            update (fun state -> { state with Last = queue; Diagnostics = QueueDiagnostics.loaded queue state.Diagnostics })
                            return Ok queue
                        | Error failure -> return Error failure
            }

        let save (queue: OfflineQueue) =
            async {
                let state = cell.Value

                match MemoryQueueStore.encodeWithin budget queue with
                | Error failure ->
                    match failure with
                    | QueueStoreFailure.QuotaExceeded(bytes, limit) ->
                        diagnose (QueueDiagnostics.failed (AdapterFailure.create "save" Database (Some Records) FailureClass.Quota "budget" $"{bytes} over the budget of {limit}"))
                    | _ -> ()

                    return Error failure
                | Ok text ->
                    let next = { state.Observed with Queue = Some text }
                    let textSize = int64 text.Length

                    match fits state.Limits next (Some state.Observed) with
                    | Error(bytes, limit) ->
                        diagnose (QueueDiagnostics.failed (AdapterFailure.create "save" Database (Some Records) FailureClass.Quota "limit" $"{bytes} bytes over the pack's limit of {limit}"))
                        return Error(QueueStoreFailure.QuotaExceeded(bytes, limit))
                    | Ok() ->
                        let attempt () = write host.Store state.Connection "save" next (Some state.Observed)

                        let! written =
                            async {
                                match! attempt () with
                                | Error({ Class = FailureClass.Quota }, _) as refused ->
                                    // The queue holds the person's unsent work; the cache
                                    // is rebuildable, so it gives up its space first.
                                    match freeSpace with
                                    | Some free ->
                                        match! free () with
                                        | true -> return! attempt ()
                                        | false -> return refused
                                    | None -> return refused
                                | other -> return other
                            }

                        match written with
                        | Ok() ->
                            let at = host.Now()

                            update (fun state ->
                                { state with
                                    Observed = next
                                    Last = Some queue
                                    Diagnostics = QueueDiagnostics.saved at textSize state.Last queue state.Diagnostics })

                            do! afterSave queue
                            return Ok()
                        | Error(_, Some stored) when stored.Epoch > state.Observed.Epoch ->
                            fenced ()
                            return Error QueueStoreFailure.Unavailable
                        | Error(failure, _) ->
                            fail failure

                            match queueFailure failure with
                            | QueueStoreFailure.QuotaExceeded _ -> return Error(QueueStoreFailure.QuotaExceeded(textSize, budget))
                            | other -> return Error other
            }

        let store: QueueStore = { Load = load; Save = save }

        let discardWith (select: OfflineQueue -> OfflineQueue * int) (queue: OfflineQueue) =
            async {
                let kept, count = select queue

                match! save kept with
                | Ok() ->
                    diagnose (QueueDiagnostics.discarded count)
                    return Ok(kept, count)
                | Error failure -> return Error failure
            }

        { Mode = DurabilityMode.IndexedDb
          Store = store
          Notices = initial.Diagnostics.Notices
          Diagnostics = fun () -> cell.Value.Diagnostics
          Discard = fun account queue -> discardWith (QueueSignOut.discard account) queue
          DiscardAccount = fun who queue -> discardWith (QueueSignOut.discardAccount who) queue
          Release =
            fun () ->
                async {
                    do! QueueLock.release host.Lock handle
                    let! _ = Store.close host.Store Database
                    update (fun state -> { state with Connection = Connection.Closed; Diagnostics = QueueDiagnostics.ownership OwnershipState.Released state.Diagnostics })
                } }

    /// Opens and claims the namespace's queue in IndexedDB, with the
    /// namespace's lock already held by this tab (LCP-059): opens the
    /// database, raises the fencing epoch with a compare-and-put, and returns
    /// a store bound to that epoch. A save from an older epoch writes nothing
    /// and fails as Unavailable. When the database was found newly created
    /// and this device's evidence says it held unsent changes, the queue
    /// opens with `LocalQueueLost` (LCP-062).
    let openOwned (host: LimenHost) (budget: int64) (freeSpace: (unit -> Async<bool>) option) (ns: Namespace) (handle: Limen.Contract.Coordination.Types.LockHandle) =
        async {
            match! openDatabase host.Store with
            | Error failure -> return IndexedDbOpening.Failed failure
            | Ok opened ->
                let connection = connected opened
                let key = recordKey ns

                match! read host.Store connection key with
                | Error failure -> return IndexedDbOpening.Failed failure
                | Ok current ->
                    let claimed = claim key current

                    match! write host.Store connection "claim" claimed current with
                    | Error({ Class = FailureClass.Conflict }, _) -> return IndexedDbOpening.Contended
                    | Error(failure, _) -> return IndexedDbOpening.Failed failure
                    | Ok() ->
                        let! held = host.LocalStorage(LocalStorageRequest.Get(heldKey ns))

                        let heldMarked =
                            match held with
                            | LocalStorageOutcome.Success(Some _) -> true
                            | _ -> false

                        let lost = opened.Created = Some true && heldMarked

                        let notices = if lost then [ QueueNotice.LocalQueueLost ] else []

                        let state =
                            { Connection = connection
                              Limits = opened.Limits
                              Observed = claimed
                              Last = None
                              HeldMarked = heldMarked
                              AskedPersist = false
                              Diagnostics = QueueDiagnostics.create DurabilityMode.IndexedDb (OwnershipState.Owner claimed.Epoch) budget notices }

                        return IndexedDbOpening.Opened(owned host budget freeSpace ns handle state)
        }
