namespace Arca.Limen

open System
open Arca

/// Where unsent changes are kept (Limen LCP-065). The application shows it
/// wherever it shows sync state.
[<RequireQualifiedAccess>]
type DurabilityMode =
    /// IndexedDB through Limen's store pack: survives closing the tab.
    | IndexedDb
    /// The interim localStorage queue: survives closing the tab, within a
    /// small budget.
    | LocalStorage of budget: int64
    /// Memory only: unsent changes do NOT survive closing the tab. The
    /// application warns before accepting an offline write, or refuses
    /// offline writes (DF-LIMEN-2026-0005 section 2).
    | MemoryOnly

/// Whether this tab still owns the queue (LCP-059).
[<RequireQualifiedAccess>]
type OwnershipState =
    /// This tab owns it, at this fencing epoch (0 where no epoch applies).
    | Owner of epoch: int64
    /// Another tab took it over; this tab's saves write nothing.
    | OwnedElsewhere
    /// The browser closed the database under the page; reopen to continue.
    | ConnectionLost
    /// This tab released it.
    | Released

/// Something the application must tell the person (LCP-062, LCP-066).
[<RequireQualifiedAccess>]
type QueueNotice =
    /// The device held unsent changes, and the database was found newly
    /// created: the browser cleared or evicted it, and those changes are
    /// gone. Shown only with that evidence, never on a first use.
    | LocalQueueLost
    /// A localStorage queue from before the move to IndexedDB waits until the
    /// IndexedDB queue is empty; it is adopted then, never merged or dropped.
    | LegacyQueuePending of entries: int
    /// A localStorage queue that cannot be read was left in place, untouched.
    | LegacyQueueUnreadable
    /// The localStorage queue was moved into IndexedDB.
    | LegacyQueueAdopted of entries: int

/// What the application can show about the queue (LCP-073). Every field is a
/// value; an unknown measurement is None, never zero. Times are the host's.
type QueueDiagnostics =
    { Mode: DurabilityMode
      Ownership: OwnershipState
      /// The depth by state of the queue last loaded or saved.
      Depth: SyncStatus option
      /// The size of the snapshot last saved, in UTF-16 code units.
      SnapshotSize: int64 option
      Budget: int64
      /// When a save last succeeded.
      LastSave: DateTimeOffset option
      /// When an entry last became Synchronized, as seen by a save.
      LastSync: DateTimeOffset option
      /// Whether the browser granted persistent storage, once asked.
      Persisted: bool option
      Notices: QueueNotice list
      /// The last failure, with its class and code; never a stored value.
      LastFailure: AdapterFailure option
      /// Entries discarded at sign-out, counted.
      Discarded: int }

[<RequireQualifiedAccess>]
module QueueDiagnostics =

    let create (mode: DurabilityMode) (ownership: OwnershipState) (budget: int64) (notices: QueueNotice list) =
        { Mode = mode
          Ownership = ownership
          Depth = None
          SnapshotSize = None
          Budget = budget
          LastSave = None
          LastSync = None
          Persisted = None
          Notices = notices
          LastFailure = None
          Discarded = 0 }

    let private synchronized (queue: OfflineQueue) =
        queue.Entries
        |> List.filter (fun entry ->
            match entry.State with
            | EntryState.Synchronized _ -> true
            | _ -> false)
        |> List.map _.Sequence
        |> Set.ofList

    /// After a successful save of `queue` (its size given) over `previous`,
    /// at `at`. An entry that became Synchronized moves the last sync.
    let saved (at: DateTimeOffset) (size: int64) (previous: OfflineQueue option) (queue: OfflineQueue) (diagnostics: QueueDiagnostics) =
        let before = previous |> Option.map synchronized |> Option.defaultValue Set.empty
        let newlySynchronized = not (Set.isSubset (synchronized queue) before)

        { diagnostics with
            Depth = Some(OfflineQueue.status queue)
            SnapshotSize = Some size
            LastSave = Some at
            LastSync = if newlySynchronized then Some at else diagnostics.LastSync }

    let loaded (queue: OfflineQueue option) (diagnostics: QueueDiagnostics) =
        { diagnostics with Depth = queue |> Option.map OfflineQueue.status }

    let failed (failure: AdapterFailure) (diagnostics: QueueDiagnostics) = { diagnostics with LastFailure = Some failure }

    let ownership (state: OwnershipState) (diagnostics: QueueDiagnostics) = { diagnostics with Ownership = state }

    let persisted (granted: bool) (diagnostics: QueueDiagnostics) = { diagnostics with Persisted = Some granted }

    let private sameKind (a: QueueNotice) (b: QueueNotice) =
        match a, b with
        | QueueNotice.LocalQueueLost, QueueNotice.LocalQueueLost
        | QueueNotice.LegacyQueueUnreadable, QueueNotice.LegacyQueueUnreadable
        | QueueNotice.LegacyQueuePending _, QueueNotice.LegacyQueuePending _
        | QueueNotice.LegacyQueueAdopted _, QueueNotice.LegacyQueueAdopted _ -> true
        | _ -> false

    /// Adds a notice, replacing an earlier one of the same kind. An adopted
    /// legacy queue is no longer pending.
    let notice (notice: QueueNotice) (diagnostics: QueueDiagnostics) =
        let kept =
            diagnostics.Notices
            |> List.filter (fun existing ->
                not (sameKind existing notice)
                && not (
                    match notice, existing with
                    | QueueNotice.LegacyQueueAdopted _, QueueNotice.LegacyQueuePending _ -> true
                    | _ -> false
                ))

        { diagnostics with Notices = kept @ [ notice ] }

    let discarded (count: int) (diagnostics: QueueDiagnostics) =
        { diagnostics with Discarded = diagnostics.Discarded + count }

/// Who is signing out, for matching their queued entries (LCP-070,
/// ARCA-OFF-007).
type SignOutAccount =
    { /// The stable account id entries were queued with
      /// (`OfflineQueue.enqueueFor`).
      Account: AccountId
      /// The identity that entries queued without an account id were recorded
      /// with (their provider identity, else their actor). None: such entries
      /// never match. Set it only while older entries may remain, and only to
      /// a value that is unique to this account.
      Legacy: string option }

/// A queue this tab holds, in whatever durability mode the composer
/// obtained (LCP-059, LCP-065). Only this tab may load, save and run
/// `OfflineSync` on it.
[<NoEquality; NoComparison>]
type OwnedQueue =
    { Mode: DurabilityMode
      /// The unchanged `QueueStore` port.
      Store: QueueStore
      /// What the application must tell the person, at open.
      Notices: QueueNotice list
      /// The current diagnostics, as a value.
      Diagnostics: unit -> QueueDiagnostics
      /// Discards an account's unsent entries at sign-out, after the
      /// application showed the count and the person confirmed (LCP-070).
      /// Entries that may have landed (in flight, outcome unknown) are never
      /// discarded; they stay to be reconciled. Answers the saved queue and
      /// how many were discarded; counted in diagnostics.
      Discard: string -> OfflineQueue -> Async<Result<OfflineQueue * int, QueueStoreFailure>>
      /// `Discard`, matching entries by the account's stable id (ARCA-OFF-007):
      /// two people who share a display name never discard each other's
      /// entries. The same entries are never discarded.
      DiscardAccount: SignOutAccount -> OfflineQueue -> Async<Result<OfflineQueue * int, QueueStoreFailure>>
      /// Releases ownership (sign-out, switching namespace). Closing the tab
      /// releases it too.
      Release: unit -> Async<unit> }

/// Sign-out helpers over the queue (LCP-070). Unsent entries belong to the
/// account that made them.
[<RequireQualifiedAccess>]
module QueueSignOut =

    /// True when the entry was made by `account` (its provider identity, or
    /// its actor where no provider identity was recorded). These are display
    /// values that two accounts may share; prefer `belongsToAccount`.
    let belongsTo (account: string) (entry: QueueEntry) =
        match entry.Operation.ProviderIdentity with
        | Some identity -> identity = account
        | None -> entry.Operation.ActorId = account

    /// True when the entry was made by the account signing out: by its stable
    /// id when the entry records one (ARCA-OFF-007); an entry without one
    /// only when the caller names the legacy identity it was recorded with.
    let belongsToAccount (who: SignOutAccount) (entry: QueueEntry) =
        match entry.Operation.AccountId, who.Legacy with
        | Some recorded, _ -> recorded = AccountId.toWire who.Account
        | None, Some legacy -> belongsTo legacy entry
        | None, None -> false

    let private unsent (entry: QueueEntry) =
        match entry.State with
        | EntryState.Synchronized _
        | EntryState.Abandoned _ -> false
        | _ -> true

    /// How many of the account's entries have not reached the provider: the
    /// count the application names at sign-out.
    let unsentOf (account: string) (queue: OfflineQueue) =
        queue.Entries |> List.filter (fun entry -> belongsTo account entry && unsent entry) |> List.length

    /// `unsentOf`, matched by the account's stable id.
    let unsentOfAccount (who: SignOutAccount) (queue: OfflineQueue) =
        queue.Entries |> List.filter (fun entry -> belongsToAccount who entry && unsent entry) |> List.length

    /// Entries that have not reached the provider and cannot have: these may
    /// be discarded. In-flight and outcome-unknown entries may have landed,
    /// so they are reconciled, never dropped.
    let private discardableState (entry: QueueEntry) =
        match entry.State with
        | EntryState.Pending
        | EntryState.Conflicted _
        | EntryState.Refused _ -> true
        | _ -> false

    let private discardWhere (owned: QueueEntry -> bool) (queue: OfflineQueue) =
        let discardable entry = owned entry && discardableState entry
        let count = queue.Entries |> List.filter discardable |> List.length
        { queue with Entries = queue.Entries |> List.filter (discardable >> not) }, count

    /// The queue without the account's discardable entries (pending,
    /// conflicted, refused), and how many were removed. In-flight and
    /// outcome-unknown entries stay: they may have landed, so they are
    /// reconciled, never dropped. Other accounts' entries are untouched.
    let discard (account: string) (queue: OfflineQueue) = discardWhere (belongsTo account) queue

    /// `discard`, matched by the account's stable id (ARCA-OFF-007).
    let discardAccount (who: SignOutAccount) (queue: OfflineQueue) = discardWhere (belongsToAccount who) queue
