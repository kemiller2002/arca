namespace Arca

open System

/// One record to erase, validated: it is immutable and its tombstone names
/// its content hash and revision (ARCA-INT-005).
type ErasureRequest =
    private
        { ErasedPath: RelativePath
          ErasedTombstone: Tombstone }

    member this.Path = this.ErasedPath
    member this.Tombstone = this.ErasedTombstone

/// Why an erasure request was refused before anything was sent.
[<RequireQualifiedAccess>]
type ErasureError =
    /// A mutable record is deleted, not erased.
    | NotImmutable of path: string
    /// The path is not an authoritative record path.
    | NotARecord of path: string
    /// The reason is empty, longer than `Erasure.MaxReasonLength`, not one
    /// line, or looks like a credential.
    | InvalidReason of reason: string

/// Explicit erasure of immutable records (ARCA-INT-005), for retention.
///
/// An erasure replaces each record's blob in the **current tree** with a
/// tombstone that records the erased content hash and revision, when and why,
/// never the content, in one atomic commit. Later reads report
/// `ReadOutcome.Erased`; creating, changing, deleting or erasing the path
/// again is refused. It is separate from `Change.Delete`, which never touches
/// an immutable record, and it is applied only by a provider that declares
/// `Capability.Erase`.
///
/// What it does not do: Git history still holds the erased content. Removing
/// it permanently needs a history rewrite by the repository owner (and every
/// clone and fork purged), which Arca never does.
[<RequireQualifiedAccess>]
module Erasure =

    /// The longest reason accepted.
    [<Literal>]
    let MaxReasonLength = 200

    let private validReason (reason: string) =
        not (String.IsNullOrWhiteSpace reason)
        && reason.Length <= MaxReasonLength
        && reason = reason.Trim()
        && not (reason |> Seq.exists Char.IsControl)
        && not (Secrets.looksLikeCredential reason)

    /// A request to erase the record at `path`, as the application last read
    /// and validated it (`Integrity.validate`), at `at`, for `reason`.
    let request (path: RelativePath) (record: ValidatedRecord) (at: DateTimeOffset) (reason: string) =
        if Layout.authorityOf path <> Some Authority.Authoritative then
            Error(ErasureError.NotARecord(RelativePath.render path))
        elif record.Record.Mutability <> Mutability.Immutable then
            Error(ErasureError.NotImmutable(RelativePath.render path))
        elif not (validReason reason) then
            Error(ErasureError.InvalidReason reason)
        else
            Ok
                { ErasedPath = path
                  ErasedTombstone =
                    { ErasedContentHash = record.ContentHash
                      ErasedRevision = record.Revision
                      ErasedAt = at.ToUniversalTime()
                      Reason = reason } }

    /// One atomic erasure of every requested record, with the operation's
    /// metadata in the commit trailers (ARCA-COMMIT-002).
    let operation (ns: Namespace) (metadata: OperationMetadata) (requests: ErasureRequest list) =
        Operation.erasure ns metadata (requests |> List.map (fun request -> request.ErasedPath, request.ErasedTombstone))

    /// Sends an erasure, refused before anything is sent when the provider
    /// does not declare `Capability.Erase`.
    let commit (provider: StorageProvider) (operation: Operation) =
        async {
            match ProviderCapabilities.require Capability.Erase provider.Capabilities with
            | Error refusal -> return Error(StorageFailure.Refused(WriteRefusal.CapabilityUnavailable refusal))
            | Ok _ -> return! provider.Commit operation
        }

    /// The tombstone a read found, if the record was erased.
    let erased (outcome: ReadOutcome) =
        match outcome with
        | ReadOutcome.Erased erased -> Some erased.Tombstone
        | ReadOutcome.Found _
        | ReadOutcome.Absent -> None
