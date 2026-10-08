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

    /// The request that saves the queue, or a typed failure when the queue
    /// does not fit the budget. Nothing is truncated or dropped.
    let saveRequest (ns: Namespace) (budget: int64) (queue: OfflineQueue) =
        match OfflineQueue.encode queue with
        | Error error -> Error(QueueStoreFailure.Corrupt error)
        | Ok text when int64 text.Length > budget -> Error(QueueStoreFailure.QuotaExceeded(int64 text.Length, budget))
        | Ok text -> Ok(LocalStorageRequest.Set(key ns, text))

    /// What a save produced.
    let saved (budget: int64) (size: int64) (outcome: LocalStorageOutcome) =
        match outcome with
        | LocalStorageOutcome.Success _ -> Ok()
        | LocalStorageOutcome.Failure LocalStorageFailure.QuotaExceeded -> Error(QueueStoreFailure.QuotaExceeded(size, budget))
        | LocalStorageOutcome.Failure LocalStorageFailure.Unavailable -> Error QueueStoreFailure.Unavailable

    /// The queue store for a namespace, given the host's localStorage executor.
    let store (execute: LocalStorageRequest -> Async<LocalStorageOutcome>) (budget: int64) (ns: Namespace) : QueueStore =
        { Load =
            fun () ->
                async {
                    let! outcome = execute (loadRequest ns)
                    return loaded ns outcome
                }
          Save =
            fun queue ->
                async {
                    match saveRequest ns budget queue with
                    | Error failure -> return Error failure
                    | Ok request ->
                        let size =
                            match request with
                            | LocalStorageRequest.Set(_, value) -> int64 value.Length
                            | _ -> 0L

                        let! outcome = execute request
                        return saved budget size outcome
                } }
