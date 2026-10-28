/// ARCA-OFF-009: application code cannot fabricate a commit receipt and
/// feed it to the queue. CommitReceipt's representation is internal to
/// Arca. This must fail with FS1129 (no such label).
module Arca.CompileFailure.ForgeCommitReceipt

open Arca

let forged: CommitReceipt =
    { ChangeToken = ChangeToken "a-change-token-the-provider-never-issued"
      Revisions = Map.empty }

let markSaved (sequence: int64) (queue: OfflineQueue) =
    OfflineQueue.recordResult sequence (Ok forged) queue

/// Nor with the internal field names, nor through the internal minting
/// function a first-party provider uses (FS1093:
/// not accessible; FS0039: the internal module is not visible).
let forgedInternally: CommitReceipt =
    { token = ChangeToken "a-change-token-the-provider-never-issued"
      revisions = Map.empty
      confirms = Unchecked.defaultof<IdempotencyKey> }

let minted (key: IdempotencyKey) =
    CommitReceipt.mint key (ChangeToken "a-change-token-the-provider-never-issued") Map.empty
