/// The control for ARCA-OFF-009: application code reads entry states with
/// the EntryState patterns, reads receipts, and moves entries only through
/// OfflineQueue's commands with a provider's result. This file compiles, so
/// the project's only errors are the forging fixtures'.
module Arca.CompileFailure.LegitimateQueueReads

open Arca

let describe (entry: QueueEntry) =
    match entry.State with
    | EntryState.Pending -> "waiting"
    | EntryState.InFlight baseToken -> $"sending on {baseToken}"
    | EntryState.Synchronized changeToken -> $"saved as {changeToken}"
    | EntryState.Conflicted paths -> $"conflicted on {paths.Length} paths"
    | EntryState.OutcomeUnknown baseToken -> $"may have landed on {baseToken}"
    | EntryState.Refused reason -> $"refused: {reason}"
    | EntryState.Abandoned reason -> $"abandoned: {reason}"

let saved (queue: OfflineQueue) =
    queue.Entries
    |> List.filter (fun entry -> EntryState.isSynchronized entry.State)
    |> List.map (fun entry -> entry.Sequence, EntryState.name entry.State)

let tokenOf (receipt: CommitReceipt) =
    let (ChangeToken token) = receipt.ChangeToken
    token, receipt.Revisions.Count, IdempotencyKey.value receipt.IdempotencyKey

/// The only way to Synchronized: send the entry and record what the
/// provider answered.
let send (provider: StorageProvider) (operation: Operation) (sequence: int64) (baseToken: ChangeToken) (queue: OfflineQueue) =
    async {
        match OfflineQueue.markInFlight sequence baseToken queue with
        | Error error -> return Error error
        | Ok inFlight ->
            let! result = provider.Commit operation
            return OfflineQueue.recordResult sequence result inFlight
    }
