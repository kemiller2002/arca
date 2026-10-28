/// ARCA-OFF-009: application code cannot set an entry's state by record
/// copy, not even to a state taken from another entry, and cannot drop
/// entries from the queue by record copy. Only OfflineQueue's commands
/// change an entry. This must fail with FS1129 (no such label) and FS1093
/// (the representation is not accessible).
module Arca.CompileFailure.MutateEntryState

open Arca

let markSaved (unsaved: QueueEntry) (saved: QueueEntry) : QueueEntry =
    { unsaved with State = saved.State }

/// Nor drop the entries that have not been saved, so the queue reads as
/// synchronized.
let hideUnsaved (queue: OfflineQueue) : OfflineQueue =
    { queue with Entries = queue.Entries |> List.filter (fun entry -> EntryState.isSynchronized entry.State) }
