namespace Arca

open System

/// One stored object as read: its namespace-relative path, its canonical
/// text and the provider's revision of it.
type StoredObject =
    { Path: RelativePath
      Content: string
      Revision: Revision }

/// A record that was erased (ARCA-INT-005): its path, the tombstone that
/// replaced it, and that tombstone's stored form. The erased content is gone
/// from the current tree; Git history still holds it.
type ErasedObject =
    { Path: RelativePath
      Tombstone: Tombstone
      /// The tombstone as stored (its text and revision), so snapshots,
      /// exports and migrations carry it and the record cannot be recreated.
      Stored: StoredObject }

/// The result of reading one object.
[<RequireQualifiedAccess>]
type ReadOutcome =
    | Found of StoredObject
    | Absent
    /// The record was erased; it is not an integrity failure (ARCA-INT-005).
    | Erased of ErasedObject

/// How a provider reports what it read.
[<RequireQualifiedAccess>]
module ReadOutcome =

    /// A stored object as a read outcome: a tombstone is `Erased`, anything
    /// else is `Found` (and validated by the application, ARCA-INT-001).
    let ofStored (stored: StoredObject) =
        match Tombstone.decode stored.Content with
        | Some tombstone ->
            ReadOutcome.Erased
                { Path = stored.Path
                  Tombstone = tombstone
                  Stored = stored }
        | None -> ReadOutcome.Found stored

/// One entry of a listing.
type ListEntry =
    { Path: RelativePath
      /// The object's revision; for a folder, the provider's identifier of it.
      Revision: Revision
      SizeBytes: int64
      IsFolder: bool }

/// The entries directly under a prefix. `Complete` is false when the
/// provider returned a partial listing, which is a typed fact, never hidden
/// (ARCA-API-004).
type Listing =
    { Entries: ListEntry list
      Complete: bool }

/// What a committed operation produced: the new change token and the new
/// revision of every path it touched (None for a deleted path).
type CommitReceipt =
    { ChangeToken: ChangeToken
      Revisions: Map<string, Revision option> }

/// The obligation an unknown outcome creates (ARCA-OUT-001): the operation
/// may or may not have landed, and must be reconciled before it is retried.
type PendingReconciliation =
    { IdempotencyKey: IdempotencyKey
      /// The state the operation was built on.
      Base: ChangeToken
      /// The provider's identifier for the attempted change, when one was created.
      Candidate: string option
      /// The revisions the operation would produce, for the receipt if it landed.
      Revisions: Map<string, Revision option> }

/// What reconciliation found.
[<RequireQualifiedAccess>]
type ReconcileOutcome =
    /// The operation landed; nothing must be resent.
    | Landed of CommitReceipt
    /// The operation did not land; it may be sent again as is.
    | NotLanded
    /// The provider's state still does not tell; the obligation stands.
    | StillUnknown of PendingReconciliation

/// Repository growth under a prefix (ARCA-API-005).
type GrowthMeasure =
    { Objects: int
      Bytes: int64
      Folders: int
      /// False when a listing was partial or the walk hit its budget.
      Complete: bool }

/// Why a write was refused because of what is stored now (ARCA-INT-003,
/// ARCA-INT-004).
[<RequireQualifiedAccess>]
type IntegrityRefusal =
    /// The record to update or delete is not a valid record: its state
    /// cannot be established, so it is not overwritten on a guess.
    | CorruptRecord of DecodeError
    /// The record is declared immutable.
    | ImmutableRecord
    /// The record was erased: nothing may recreate, change or delete it, and
    /// it cannot be erased twice (ARCA-INT-005).
    | ErasedRecord
    /// An erasure whose target is not what it names: absent, not a valid
    /// record, not immutable, or with other content than the erased hash.
    | NotErasable of reason: string

/// Where a commit came from (ARCA-INT-002).
[<RequireQualifiedAccess>]
type CommitOrigin =
    /// A commit Arca wrote, with the trailers it carries.
    | Arca of Commit.Trailers
    /// A commit without Arca's trailers: an edit made outside the
    /// application, which is untrusted.
    | External

/// One commit that touched an object, newest first in a history.
type HistoryEntry =
    { ChangeToken: ChangeToken
      Origin: CommitOrigin }

/// Why a storage call did not produce its result. Every case is a fact the
/// application can act on; none is an exception.
[<RequireQualifiedAccess>]
type StorageFailure =
    /// The provider refused the write before applying it (ARCA-COMMIT-006, ARCA-AUTH-004).
    | Refused of WriteRefusal
    /// Expected state was stale (ARCA-CON-002).
    | Conflicted of Conflict list
    /// The write may have landed (ARCA-OUT-001).
    | OutcomeUnknown of PendingReconciliation
    /// The object exceeds the provider's size limit (ARCA-API-004).
    | ObjectTooLarge of path: string * bytes: int64 * limit: int64
    /// The change token the caller held no longer names the provider's state (ARCA-API-004).
    | StaleChangeToken of expected: ChangeToken * actual: ChangeToken
    /// The namespace token the caller held no longer names the namespace's
    /// state: something inside the namespace changed (ARCA-CON-005).
    | StaleNamespaceToken of expected: NamespaceToken * actual: NamespaceToken
    /// The provider's rate limit is exhausted (ARCA-API-003). `retryAfter`
    /// and `resetAt` (Unix seconds) are the provider's evidence.
    | RateLimited of retryAfter: TimeSpan option * resetAt: int64 option
    /// The namespace is not at the location the provider serves.
    | WrongLocation of expected: string * actual: string
    /// What is stored now makes the write unsafe (ARCA-INT-003, ARCA-INT-004).
    | IntegrityRefused of path: string * reason: IntegrityRefusal
    /// An operational failure. `code` is the Aegis fault code
    /// (ARCA-ARCH-007); `transient` says whether retrying later may help.
    | ProviderFailed of code: string * transient: bool * detail: string

/// The provider-neutral storage interface (ARCA-ARCH-003): what an
/// application programs against, whatever the provider. Each function is one
/// request; the provider holds its own session state.
[<NoEquality; NoComparison>]
type StorageProvider =
    { Capabilities: ProviderCapabilities
      /// The provider's current change token for the namespace's location.
      ChangeToken: Namespace -> Async<Result<ChangeToken, StorageFailure>>
      /// The repository's change token and the namespace's own token, observed
      /// at one state (ARCA-CON-005). Revalidate a namespace's read cache and
      /// condition its writes on the namespace token, so commits by other
      /// applications in a shared repository do not make them stale.
      NamespaceState: Namespace -> Async<Result<NamespaceState, StorageFailure>>
      Read: Namespace -> RelativePath -> Async<Result<ReadOutcome, StorageFailure>>
      List: Namespace -> RelativePath -> Async<Result<Listing, StorageFailure>>
      /// One operation, one atomic commit, conditioned on every change's expectation.
      Commit: Operation -> Async<Result<CommitReceipt, StorageFailure>>
      /// Settles an unknown outcome by inspecting provider state (ARCA-OUT-002).
      Reconcile: Namespace -> PendingReconciliation -> Async<Result<ReconcileOutcome, StorageFailure>>
      /// The recent commits that touched an object, newest first, each with
      /// its origin, so edits made outside Arca are detectable (ARCA-INT-002).
      /// It is evidence, never a source of domain state (ARCA-COMMIT-005).
      History: Namespace -> RelativePath -> Async<Result<HistoryEntry list, StorageFailure>> }
