namespace Arca

/// Why a stored object is not a record the application may use (ARCA-INT-001).
[<RequireQualifiedAccess>]
type IntegrityFailure =
    /// The bytes are not a valid canonical record envelope within the size limit.
    | Invalid of DecodeError
    /// The id inside the record is not the id its path names: the record was
    /// moved or forged outside Arca (ARCA-REC-003).
    | IdentityMismatch of expected: string * found: string
    /// The record's type is not the type its path names.
    | TypeMismatch of expected: string * found: string
    /// The running software cannot read the record's schema version (ARCA-REC-005).
    | UnsupportedSchema of SchemaAccess
    /// The content hash is not the one the application expected: the record
    /// changed since it was last seen (ARCA-INT-002).
    | HashMismatch of expected: string * found: string
    /// A record declared immutable has different content from when it was
    /// first seen (ARCA-INT-003).
    | ImmutableChanged of path: string

/// A record read from storage and proven usable.
[<NoComparison>]
type ValidatedRecord =
    { Record: Record
      Revision: Revision
      /// `sha256:` of the canonical bytes; keep it to detect later changes.
      ContentHash: string }

/// Storage content is untrusted input (ARCA-INT-001..004). These checks are
/// total and pure; the providers apply the write guard, and the application
/// applies `validate` to everything it reads.
[<RequireQualifiedAccess>]
module Integrity =

    /// Validates a stored object as the record `key` names: size, canonical
    /// envelope, identity, type and schema version (ARCA-INT-001). Path
    /// validity and namespace identity are already guaranteed by how the
    /// object was addressed (ARCA-LOC-003).
    let validate (key: RecordKey) (support: SchemaSupport) (maxBytes: int64) (stored: StoredObject) =
        match Record.decode maxBytes stored.Content with
        | Error error -> Error(IntegrityFailure.Invalid error)
        | Ok record when record.Id <> key.Id -> Error(IntegrityFailure.IdentityMismatch(RecordId.value key.Id, RecordId.value record.Id))
        | Ok record when record.Type <> key.Type -> Error(IntegrityFailure.TypeMismatch(RecordType.value key.Type, RecordType.value record.Type))
        | Ok record when record.Type <> support.Type -> Error(IntegrityFailure.TypeMismatch(RecordType.value support.Type, RecordType.value record.Type))
        | Ok record ->
            match SchemaSupport.canRead support record.SchemaVersion with
            | Error access -> Error(IntegrityFailure.UnsupportedSchema access)
            | Ok() ->
                Ok
                    { Record = record
                      Revision = stored.Revision
                      ContentHash = Record.contentHash record }

    /// Ok when a validated record still has the content hash the application
    /// recorded; a change made since is reported, never accepted silently
    /// (ARCA-INT-002).
    let unchanged (expectedHash: string) (record: ValidatedRecord) =
        if record.ContentHash = expectedHash then Ok() else Error(IntegrityFailure.HashMismatch(expectedHash, record.ContentHash))

    /// Ok unless a record first seen as immutable now has other content
    /// (ARCA-INT-003). The check compares content hashes, so a later
    /// tamper-evident hash chain can be built on the same values.
    let immutableUnchanged (first: ValidatedRecord) (now: ValidatedRecord) =
        match first.Record.Mutability with
        | Mutability.Immutable when first.ContentHash <> now.ContentHash ->
            Error(IntegrityFailure.ImmutableChanged(RecordId.value first.Record.Id))
        | _ -> Ok()

    /// Where a commit came from, by its message (ARCA-INT-002).
    let origin (message: string) =
        match Commit.trailers message with
        | Some trailers -> CommitOrigin.Arca trailers
        | None -> CommitOrigin.External

    /// The external edits in a history, newest first.
    let externalEdits (history: HistoryEntry list) =
        history |> List.filter (fun entry -> entry.Origin = CommitOrigin.External)

    /// Whether a change may be written on top of what is stored now
    /// (ARCA-INT-003, ARCA-INT-004). An authoritative record whose current
    /// content cannot be validated is not overwritten or deleted on a guess,
    /// and a record declared immutable is never changed or deleted. Writes
    /// to other objects (derived data, manifests) are not records and pass.
    ///
    /// An erased record's tombstone is never written over, by any change
    /// (ARCA-INT-005).
    let guard (change: Change) (current: string option) =
        match current |> Option.bind Tombstone.decode with
        | Some _ -> Error IntegrityRefusal.ErasedRecord
        | None ->
            match Layout.authorityOf (Change.path change), change, current with
            | Some Authority.Authoritative, (Change.Update _ | Change.Delete _), Some content ->
                match Record.decode System.Int64.MaxValue content with
                | Error error -> Error(IntegrityRefusal.CorruptRecord error)
                | Ok record when record.Mutability = Mutability.Immutable -> Error IntegrityRefusal.ImmutableRecord
                | Ok _ -> Ok()
            | _ -> Ok()

    /// Whether an erasure may replace what is stored now with its tombstone
    /// (ARCA-INT-005): it must be a valid, immutable, authoritative record
    /// whose content hash is the one the tombstone names.
    let private erasable (change: Change) (tombstone: Tombstone) (current: string option) =
        let path = Change.path change

        match Layout.authorityOf path, current with
        | _, Some content when (Tombstone.decode content).IsSome -> Error IntegrityRefusal.ErasedRecord
        | Some Authority.Authoritative, Some content ->
            match Record.decode System.Int64.MaxValue content with
            | Error error -> Error(IntegrityRefusal.CorruptRecord error)
            | Ok record when record.Mutability <> Mutability.Immutable ->
                Error(IntegrityRefusal.NotErasable "the record is mutable; delete it instead")
            | Ok record when Record.contentHash record <> tombstone.ErasedContentHash ->
                Error(IntegrityRefusal.NotErasable "the record's content is not the content the erasure names")
            | Ok _ -> Ok()
        | Some Authority.Authoritative, None -> Error(IntegrityRefusal.NotErasable "the record is absent")
        | _ -> Error(IntegrityRefusal.NotErasable "only authoritative records are erased")

    /// The write guard for one change of an operation: `erasable` for the
    /// paths an erasure names, `guard` for everything else.
    let guardIn (operation: Operation) (change: Change) (current: string option) =
        match operation.TombstoneAt(Change.path change) with
        | Some tombstone -> erasable change tombstone current
        | None -> guard change current
