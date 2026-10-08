namespace Arca.GitHub

open Arca

/// One browser localStorage request, shaped like Limen Core's `Storage`
/// effect (`get`, `set`, `remove`), so a Limen host forwards it unchanged
/// (DF-ARCA-2026-0005).
[<RequireQualifiedAccess>]
type LocalStorageRequest =
    | Get of key: string
    | Set of key: string * value: string
    | Remove of key: string

/// Why localStorage refused a request, as Limen reports it.
[<RequireQualifiedAccess>]
type LocalStorageFailure =
    | Unavailable
    | QuotaExceeded

/// What one localStorage request produced. A single call is atomic, so there
/// is no dispatched-but-uncertain case.
[<RequireQualifiedAccess>]
type LocalStorageOutcome =
    /// The stored value for a get (None when absent); None for set and remove.
    | Success of value: string option
    | Failure of LocalStorageFailure

/// The lock request that makes one tab the owner of a namespace's queue,
/// shaped like Limen's coordination pack `acquire` (LCP-040), so a Limen host
/// forwards it unchanged: an exclusive Web Lock, taken only if no other tab
/// holds it (`wait: false`), never stolen (`steal: false`). The host keeps
/// the lock until the page goes away.
[<RequireQualifiedAccess>]
type QueueLockRequest = Acquire of name: string

/// What the browser answered to a lock request.
[<RequireQualifiedAccess>]
type QueueLockOutcome =
    /// This tab now holds the lock until it closes.
    | Acquired
    /// Another tab holds it.
    | Busy
    /// This browser has no Web Locks.
    | Unsupported

/// Whether this tab holds a namespace's queue (Limen LCP-059). Adapter API,
/// outside the `QueueStore` port, which is unchanged.
[<RequireQualifiedAccess; NoEquality; NoComparison>]
type QueueOwnership =
    /// This tab owns the queue: the only store for it, in any tab.
    | Owned of QueueStore
    /// Another tab of this application holds the queue; this tab keeps no
    /// queue in the browser. Tell the person their changes are kept in the
    /// other tab.
    | OwnedElsewhere
    /// Web Locks are unavailable, so ownership cannot be established.
    | OwnershipUnsupported

/// The queue-store port over browser localStorage (ARCA-OFF-002,
/// DF-ARCA-2026-0005). The adapter describes each request as data; the host
/// carries it out through its Limen Storage effect. A Limen IndexedDB adapter
/// can replace this one later without touching the core.
[<RequireQualifiedAccess>]
module LocalStorageQueue =

    /// The default size budget: 1,000,000 UTF-16 code units, about 2 MB,
    /// well inside the usual 5 MB origin quota, which is shared with the rest
    /// of the application.
    [<Literal>]
    let DefaultBudget = 1_000_000L

    /// The key a namespace's queue is stored under: `arca.queue.<app>` or
    /// `arca.queue.<app>.<dataset>`. Application and dataset identifiers do
    /// not contain dots, so keys never collide.
    let key (ns: Namespace) =
        match ns.Dataset with
        | None -> $"arca.queue.{AppId.value ns.Application}"
        | Some dataset -> $"arca.queue.{AppId.value ns.Application}.{DatasetId.value dataset}"

    /// The request that loads the queue.
    let loadRequest (ns: Namespace) = LocalStorageRequest.Get(key ns)

    /// What a load produced: the queue, None when nothing was stored, or a
    /// typed failure. A stored queue of another namespace is refused.
    let loaded (ns: Namespace) (outcome: LocalStorageOutcome) =
        match outcome with
        | LocalStorageOutcome.Failure LocalStorageFailure.Unavailable
        | LocalStorageOutcome.Failure LocalStorageFailure.QuotaExceeded -> Error QueueStoreFailure.Unavailable
        | LocalStorageOutcome.Success None -> Ok None
        | LocalStorageOutcome.Success(Some text) ->
            match OfflineQueue.decode text with
            | Error error -> Error(QueueStoreFailure.Corrupt error)
            | Ok queue when queue.Entries |> List.forall (fun entry -> OfflineQueue.belongsTo ns entry.Operation) -> Ok(Some queue)
            | Ok _ -> Error(QueueStoreFailure.Corrupt(QueueError.Corrupt "the stored queue holds another namespace's entries"))

    /// The queue as the text a save stores, or a typed failure when it does
    /// not fit the budget. Nothing is truncated or dropped.
    let encodeWithin (budget: int64) (queue: OfflineQueue) =
        match OfflineQueue.encode queue with
        | Error error -> Error(QueueStoreFailure.Corrupt error)
        | Ok text when int64 text.Length > budget -> Error(QueueStoreFailure.QuotaExceeded(int64 text.Length, budget))
        | Ok text -> Ok text

    /// The request that saves the queue, or a typed failure when the queue
    /// does not fit the budget. Nothing is truncated or dropped.
    let saveRequest (ns: Namespace) (budget: int64) (queue: OfflineQueue) =
        encodeWithin budget queue |> Result.map (fun text -> LocalStorageRequest.Set(key ns, text))

    /// What a save produced.
    let saved (budget: int64) (size: int64) (outcome: LocalStorageOutcome) =
        match outcome with
        | LocalStorageOutcome.Success _ -> Ok()
        | LocalStorageOutcome.Failure LocalStorageFailure.QuotaExceeded -> Error(QueueStoreFailure.QuotaExceeded(size, budget))
        | LocalStorageOutcome.Failure LocalStorageFailure.Unavailable -> Error QueueStoreFailure.Unavailable

    /// Whether a save may replace what is stored (WI-0024, DF-ARCA-2026-0009).
    ///
    /// Several tabs share one localStorage, and the port saves whole
    /// snapshots. A tab may write only over exactly the text it last loaded
    /// or saved (`None`: nothing stored). Anything else means another tab
    /// wrote since, and this tab's snapshot does not hold what that tab
    /// saved: the save is refused, nothing is written, and the entries stay
    /// with the tab that holds them. A tab that never loaded expects nothing
    /// stored, so it cannot overwrite a queue it has not seen, nor a corrupt
    /// one, which is always left in place.
    let mayReplace (observed: string option) (current: string option) = observed = current

    /// What the stored text was before a save, as a save decides on it.
    let private beforeSave (budget: int64) (size: int64) (observed: string option) (outcome: LocalStorageOutcome) =
        match outcome with
        | LocalStorageOutcome.Success current when mayReplace observed current -> Ok()
        // Another tab saved since this one loaded or saved.
        | LocalStorageOutcome.Success _ -> Error QueueStoreFailure.Unavailable
        | LocalStorageOutcome.Failure _ -> saved budget size outcome

    /// The queue store for a namespace, given the host's localStorage executor.
    ///
    /// Every save is fenced by `mayReplace`, so one tab's save never
    /// overwrites an entry another tab's save acknowledged. Two saves whose
    /// read and write interleave between tabs are not excluded by this
    /// alone, because localStorage has no compare-and-set: `own` adds a Web
    /// Lock so that only one tab holds the store at all.
    let store (execute: LocalStorageRequest -> Async<LocalStorageOutcome>) (budget: int64) (ns: Namespace) : QueueStore =
        // The one piece of state: the text this store last loaded or saved.
        let observed = ref None

        { Load =
            fun () ->
                async {
                    let! outcome = execute (loadRequest ns)
                    let result = loaded ns outcome

                    match outcome, result with
                    | LocalStorageOutcome.Success stored, Ok _ -> observed.Value <- stored
                    | _ -> ()

                    return result
                }
          Save =
            fun queue ->
                async {
                    match encodeWithin budget queue with
                    | Error failure -> return Error failure
                    | Ok text ->
                        let size = int64 text.Length
                        let! current = execute (loadRequest ns)

                        match beforeSave budget size observed.Value current with
                        | Error failure -> return Error failure
                        | Ok() ->
                            let! outcome = execute (LocalStorageRequest.Set(key ns, text))
                            let result = saved budget size outcome

                            if result = Ok() then
                                observed.Value <- Some text

                            return result
                } }

    /// The Web Lock that makes one tab the owner of a namespace's queue:
    /// `arca.queue/<app>` or `arca.queue/<app>/<dataset>` (Limen LCP-059).
    let lockName (ns: Namespace) =
        match ns.Dataset with
        | None -> $"arca.queue/{AppId.value ns.Application}"
        | Some dataset -> $"arca.queue/{AppId.value ns.Application}/{DatasetId.value dataset}"

    /// What a lock answer means for the queue.
    let ownership (store: unit -> QueueStore) (outcome: QueueLockOutcome) =
        match outcome with
        | QueueLockOutcome.Acquired -> QueueOwnership.Owned(store ())
        | QueueLockOutcome.Busy -> QueueOwnership.OwnedElsewhere
        | QueueLockOutcome.Unsupported -> QueueOwnership.OwnershipUnsupported

    /// Takes ownership of a namespace's queue in this tab (WI-0024, Limen
    /// LCP-059, DF-ARCA-2026-0009): one tab at a time holds the store, and
    /// only that tab may load, save and synchronize the queue. Another tab
    /// is told `OwnedElsewhere`, and may ask again later (for example when
    /// it is focused): the browser releases the lock when the owner closes,
    /// crashes or navigates away. Where Web Locks are unsupported the answer
    /// is `OwnershipUnsupported`; the application then chooses `store`
    /// (fenced, not exclusive), memory only, or no offline writes.
    let own
        (lock: QueueLockRequest -> Async<QueueLockOutcome>)
        (execute: LocalStorageRequest -> Async<LocalStorageOutcome>)
        (budget: int64)
        (ns: Namespace)
        : Async<QueueOwnership> =
        async {
            let! outcome = lock (QueueLockRequest.Acquire(lockName ns))
            return ownership (fun () -> store execute budget ns) outcome
        }
