namespace Arca.Limen

open Arca
open Arca.GitHub
open Limen.Store

/// A store the composer may try (Limen LCP-065).
[<RequireQualifiedAccess>]
type DurabilityChoice =
    | IndexedDb
    | LocalStorage
    | MemoryOnly

/// What the composer tries, in order, and the budgets.
[<NoEquality; NoComparison>]
type QueueOptions =
    { Order: DurabilityChoice list
      IndexedDbBudget: int64
      LocalStorageBudget: int64
      MemoryBudget: int64
      /// Frees browser storage the queue needs: when a save fails on the
      /// browser's quota, this runs (for example `IndexedDbCache.FreeSpace`,
      /// evicting the read cache) and the save is tried once more (LCP-087).
      FreeSpace: (unit -> Async<bool>) option }

/// What opening the offline queue produced (LCP-059, LCP-065). Only `Owned`
/// carries a store; every other answer leaves this tab without a queue.
[<RequireQualifiedAccess; NoEquality; NoComparison>]
type QueueOpening =
    /// This tab owns the queue, in the mode it reports.
    | Owned of OwnedQueue
    /// Another tab of this application holds this device's unsent changes.
    /// Tell the person, and offer "use this tab instead" (`takeOver`); while
    /// online this tab may write directly (OQ-LIMEN-IDB-001).
    | OwnedElsewhere
    /// No Web Locks, and no memory-only fallback was declared: ownership
    /// cannot be established, and durable stores are never written by
    /// several tabs at once.
    | OwnershipUnsupported
    /// No declared store could be used; why each failed.
    | NothingUsable of AdapterFailure list

[<RequireQualifiedAccess>]
module QueueOptions =

    /// IndexedDB, then localStorage, then memory, at the default budgets.
    let standard =
        { Order = [ DurabilityChoice.IndexedDb; DurabilityChoice.LocalStorage; DurabilityChoice.MemoryOnly ]
          IndexedDbBudget = IndexedDbQueue.DefaultBudget
          LocalStorageBudget = LocalStorageQueue.DefaultBudget
          MemoryBudget = LocalStorageQueue.DefaultBudget
          FreeSpace = None }

/// The offline queue over Limen (WI-0016): one owner per namespace, in the
/// most durable store this browser offers, reported as a mode.
///
///     match! LimenQueue.own host QueueOptions.standard ns with
///     | QueueOpening.Owned queue -> // queue.Store is the QueueStore; show queue.Mode
///     | QueueOpening.OwnedElsewhere -> // "another tab holds this device's unsent changes"
///     | QueueOpening.OwnershipUnsupported
///     | QueueOpening.NothingUsable _ -> // no offline writes
[<RequireQualifiedAccess>]
module LimenQueue =

    /// A store in localStorage or memory, with diagnostics tracked the same
    /// way as the IndexedDB store's. The lock, when held, is released by
    /// `Release`.
    let tracked (host: LimenHost) (mode: DurabilityMode) (budget: int64) (handle: Limen.Contract.Coordination.Types.LockHandle option) (inner: QueueStore) =
        let epoch = 0L
        let diagnostics = ref (QueueDiagnostics.create mode (OwnershipState.Owner epoch) budget [])
        let last = ref None
        let diagnose f = diagnostics.Value <- f diagnostics.Value

        let failure operation (failure: QueueStoreFailure) =
            let failureClass, specific =
                match failure with
                | QueueStoreFailure.Unavailable -> FailureClass.Unavailable, "storage"
                | QueueStoreFailure.QuotaExceeded _ -> FailureClass.Quota, "exceeded"
                | QueueStoreFailure.Corrupt _ -> FailureClass.Undecodable, "queue"

            let store =
                match mode with
                | DurabilityMode.LocalStorage _ -> "localStorage"
                | DurabilityMode.MemoryOnly -> "memory"
                | DurabilityMode.IndexedDb -> IndexedDbQueue.Database

            diagnose (QueueDiagnostics.failed (AdapterFailure.create operation store None failureClass specific "the store refused the request"))

        let load () =
            async {
                match! inner.Load() with
                | Ok queue ->
                    last.Value <- queue
                    diagnose (QueueDiagnostics.loaded queue)
                    return Ok queue
                | Error error ->
                    failure "load" error
                    return Error error
            }

        let save (queue: OfflineQueue) =
            async {
                match! inner.Save queue with
                | Ok() ->
                    let size =
                        match OfflineQueue.encode queue with
                        | Ok text -> int64 text.Length
                        | Error _ -> 0L

                    diagnose (QueueDiagnostics.saved (host.Now()) size last.Value queue)
                    last.Value <- Some queue
                    return Ok()
                | Error error ->
                    failure "save" error
                    return Error error
            }

        { Mode = mode
          Store = { Load = load; Save = save }
          Notices = []
          Diagnostics = fun () -> diagnostics.Value
          Discard =
            fun account queue ->
                async {
                    let kept, count = QueueSignOut.discard account queue

                    match! save kept with
                    | Ok() ->
                        diagnose (QueueDiagnostics.discarded count)
                        return Ok(kept, count)
                    | Error error -> return Error error
                }
          Release =
            fun () ->
                async {
                    match handle with
                    | Some held -> do! QueueLock.release host.Lock held
                    | None -> ()

                    diagnose (QueueDiagnostics.ownership OwnershipState.Released)
                } }

    let private memory (host: LimenHost) (options: QueueOptions) (ns: Namespace) handle =
        tracked host DurabilityMode.MemoryOnly options.MemoryBudget handle (MemoryQueueStore.create options.MemoryBudget ns)

    /// Tries each declared store in order, with the namespace's lock held.
    let private tryChoices (host: LimenHost) (options: QueueOptions) (ns: Namespace) (handle: Limen.Contract.Coordination.Types.LockHandle) =
        let unavailable store specific detail =
            AdapterFailure.create "open" store None FailureClass.Unavailable specific detail

        let rec attempt choices (failures: AdapterFailure list) =
            async {
                match choices with
                | [] ->
                    do! QueueLock.release host.Lock handle
                    return QueueOpening.NothingUsable(List.rev failures)
                | DurabilityChoice.IndexedDb :: rest ->
                    match! Store.availability host.Store with
                    | Ok Availability.Available ->
                        match! IndexedDbQueue.openOwned host options.IndexedDbBudget options.FreeSpace ns handle with
                        | IndexedDbOpening.Opened queue -> return QueueOpening.Owned queue
                        | IndexedDbOpening.Contended ->
                            do! QueueLock.release host.Lock handle
                            return QueueOpening.OwnedElsewhere
                        | IndexedDbOpening.Failed failure -> return! attempt rest (failure :: failures)
                    | Ok Availability.Missing -> return! attempt rest (unavailable IndexedDbQueue.Database "missing" "this browser has no IndexedDB" :: failures)
                    | Ok(Availability.Refused reason) -> return! attempt rest (unavailable IndexedDbQueue.Database "refused" reason :: failures)
                    | Ok(Availability.Broken reason) -> return! attempt rest (unavailable IndexedDbQueue.Database "broken" reason :: failures)
                    | Error _ -> return! attempt rest (unavailable IndexedDbQueue.Database "no-store-pack" "the host offers no limen.store version 2" :: failures)
                | DurabilityChoice.LocalStorage :: rest ->
                    match! host.LocalStorage(LocalStorageQueue.loadRequest ns) with
                    | LocalStorageOutcome.Success _ ->
                        let store = LocalStorageQueue.store host.LocalStorage options.LocalStorageBudget ns
                        return QueueOpening.Owned(tracked host (DurabilityMode.LocalStorage options.LocalStorageBudget) options.LocalStorageBudget (Some handle) store)
                    | LocalStorageOutcome.Failure _ -> return! attempt rest (unavailable "localStorage" "storage" "localStorage is unavailable" :: failures)
                | DurabilityChoice.MemoryOnly :: _ -> return QueueOpening.Owned(memory host options ns (Some handle))
            }

        attempt options.Order []

    let private opening (steal: bool) (host: LimenHost) (options: QueueOptions) (ns: Namespace) =
        async {
            match! QueueLock.acquire host.Lock steal ns with
            | QueueLock.Busy -> return QueueOpening.OwnedElsewhere
            | QueueLock.Unsupported ->
                // Memory is per tab, so it is never written by several tabs:
                // it needs no lock. A durable store does.
                if options.Order |> List.contains DurabilityChoice.MemoryOnly then
                    return QueueOpening.Owned(memory host options ns None)
                else
                    return QueueOpening.OwnershipUnsupported
            | QueueLock.Held handle -> return! tryChoices host options ns handle
        }

    /// Opens the namespace's offline queue in this tab, unless another tab
    /// holds it (LCP-059, LCP-065).
    let own (host: LimenHost) (options: QueueOptions) (ns: Namespace) = opening false host options ns

    /// "Use this tab instead" (OQ-LIMEN-IDB-001): takes the queue over from
    /// the tab that holds it. That tab hears `LockLost`, and its next save is
    /// fenced and writes nothing; whatever it sent is reconciled by this
    /// tab, never sent twice (LCP-060).
    let takeOver (host: LimenHost) (options: QueueOptions) (ns: Namespace) = opening true host options ns
