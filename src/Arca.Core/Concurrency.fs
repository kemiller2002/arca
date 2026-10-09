namespace Arca

open System
open System.Globalization

/// A provider's opaque revision of one object, such as a Git blob SHA. Arca
/// compares revisions; it never interprets them (ARCA-ARCH-004).
type Revision = Revision of string

/// A token naming the provider's whole current state, such as a branch's head
/// commit (ARCA-CON-001, the ChangeToken capability).
type ChangeToken = ChangeToken of string

/// A token naming the state of one namespace's content only, such as the Git
/// tree SHA of the namespace root (ARCA-CON-005, the NamespaceToken
/// capability). A commit outside the namespace, by another application in a
/// shared repository, leaves it unchanged; any change inside it, including in
/// an application namespace's datasets, changes it. It is not a
/// `ChangeToken`, so it cannot be passed where the repository's state is meant.
type NamespaceToken = NamespaceToken of string

/// One observation of the provider: the repository's change token and the
/// namespace's token at that same state. The namespace's content at
/// `RepositoryToken` is exactly what `NamespaceToken` names.
type NamespaceState =
    { RepositoryToken: ChangeToken
      NamespaceToken: NamespaceToken }

/// What an erasure leaves in place of an immutable record (ARCA-INT-005): the
/// erased record's content hash and revision, when and why. Never the content.
type Tombstone =
    { /// `sha256:` of the erased record's canonical content
      /// (`ValidatedRecord.ContentHash`).
      ErasedContentHash: string
      /// The provider's revision of the erased record.
      ErasedRevision: Revision
      ErasedAt: DateTimeOffset
      /// Why it was erased, for example a retention rule's identifier.
      Reason: string }

/// The tombstone's stored form: canonical JSON, recognisable on every read.
[<RequireQualifiedAccess>]
module Tombstone =

    /// The tombstone format this Arca writes and reads.
    [<Literal>]
    let Format = 1

    let private timestamp (at: DateTimeOffset) =
        at.ToUniversalTime().ToString("yyyy-MM-dd'T'HH:mm:ss.fff'Z'", CultureInfo.InvariantCulture)

    /// The tombstone as canonical text.
    let encode (tombstone: Tombstone) =
        let (Revision revision) = tombstone.ErasedRevision

        Json.canonicalText (
            Json.objectOf
                [ "arcaErasure", Json.Number(decimal Format)
                  "erasedContentHash", Json.String tombstone.ErasedContentHash
                  "erasedRevision", Json.String revision
                  "erasedAt", Json.String(timestamp tombstone.ErasedAt)
                  "reason", Json.String tombstone.Reason ]
        )

    /// A tombstone from stored text; None for anything else (a record, or
    /// text that only looks like one).
    let decode (text: string) =
        match Json.parse text with
        | Ok value ->
            let str name =
                match Json.field name value with
                | Some(Json.String found) -> Some found
                | _ -> None

            match Json.field "arcaErasure" value, str "erasedContentHash", str "erasedRevision", str "erasedAt", str "reason" with
            | Some(Json.Number format), Some hash, Some revision, Some at, Some reason when format = decimal Format ->
                match DateTimeOffset.TryParseExact(at, "yyyy-MM-dd'T'HH:mm:ss.fff'Z'", CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal) with
                | true, parsed ->
                    Some
                        { ErasedContentHash = hash
                          ErasedRevision = Revision revision
                          ErasedAt = parsed.ToUniversalTime()
                          Reason = reason }
                | _ -> None
            | _ -> None
        | Error _ -> None

/// A text identifier supplied by the application: `A-Z a-z 0-9 . _ : -`,
/// 8 to 128 characters for idempotency keys and 1 to 128 otherwise.
type IdempotencyKey = private IdempotencyKey of string

/// A correlation identifier that ties a commit to the application's request.
type CorrelationId = private CorrelationId of string

/// Identifier validation shared by the operation metadata.
module internal Identifier =
    let valid minimum (text: string) =
        not (String.IsNullOrEmpty text)
        && text.Length >= minimum
        && text.Length <= 128
        && text |> Seq.forall (fun c -> Char.IsAsciiLetterOrDigit c || c = '.' || c = '_' || c = ':' || c = '-')

/// Construction of idempotency keys (ARCA-OUT-002).
[<RequireQualifiedAccess>]
module IdempotencyKey =

    /// A validated key. At least 8 characters, so accidental collisions are unlikely.
    let create (text: string) =
        if Identifier.valid 8 text then Ok(IdempotencyKey text) else Error text

    /// The key's text.
    let value (IdempotencyKey text) = text

/// Construction of correlation identifiers.
[<RequireQualifiedAccess>]
module CorrelationId =

    /// A validated correlation identifier.
    let create (text: string) =
        if Identifier.valid 1 text then Ok(CorrelationId text) else Error text

    /// The identifier's text.
    let value (CorrelationId text) = text

/// One change to one object, conditioned on what the writer last saw
/// (ARCA-CON-001). There is no unconditional write.
[<RequireQualifiedAccess>]
type Change =
    /// Create an object that must not exist yet.
    | Create of path: RelativePath * content: string
    /// Replace an object that must still be at `expected`.
    | Update of path: RelativePath * content: string * expected: Revision
    /// Delete an object that must still be at `expected`.
    | Delete of path: RelativePath * expected: Revision

/// Total functions over changes.
[<RequireQualifiedAccess>]
module Change =

    /// The namespace-relative path a change touches.
    let path =
        function
        | Change.Create(path, _)
        | Change.Update(path, _, _)
        | Change.Delete(path, _) -> path

    /// The revision the writer expects the object to be at; None when it
    /// expects the object to be absent.
    let expected =
        function
        | Change.Create _ -> None
        | Change.Update(_, _, revision)
        | Change.Delete(_, revision) -> Some revision

    /// The content the change writes; None for a delete.
    let content =
        function
        | Change.Create(_, content)
        | Change.Update(_, content, _) -> Some content
        | Change.Delete _ -> None

/// What the application says about one operation, carried into the commit
/// trailers (ARCA-COMMIT-002, ARCA-COMMIT-003).
type OperationMetadata =
    { /// One line, for example "issue invoice INV-2026-0042". It becomes the
      /// commit subject after the namespace prefix.
      Summary: string
      Actor: Actor
      /// The identity the provider resolved for the credential in use, for
      /// example a GitHub login (ARCA-AUTH-003). Never a token.
      ProviderIdentity: string option
      /// The execution that performed the operation, for agents.
      ExecutionId: string option
      CorrelationId: CorrelationId
      IdempotencyKey: IdempotencyKey }

/// One logical application operation: several changes that become visible
/// together, as one atomic commit, or not at all (ARCA-COMMIT-001).
type Operation =
    private
        { ns: Namespace
          changes: Change list
          metadata: OperationMetadata
          expectedToken: ChangeToken option
          expectedNamespaceToken: NamespaceToken option
          /// For an erasure: the tombstone each erased path receives.
          erasures: Map<string, Tombstone> }

    /// The namespace every change is in.
    member this.Namespace = this.ns
    /// The changes, in the order given; at least one, each path once.
    member this.Changes = this.changes
    /// What the application says about the operation.
    member this.Metadata = this.metadata
    /// When set, the operation applies only if the provider's whole state is
    /// still exactly this change token; otherwise StaleChangeToken, even when
    /// the touched records are unchanged (ARCA-CON-001).
    member this.ExpectedChangeToken = this.expectedToken
    /// When set, the operation applies only if its own namespace's content is
    /// still exactly this namespace token; otherwise StaleNamespaceToken.
    /// Commits elsewhere in the repository do not make it stale (ARCA-CON-005).
    member this.ExpectedNamespaceToken = this.expectedNamespaceToken
    /// True for an erasure (`Erasure.operation`), never for an ordinary write.
    /// A provider applies it only if it declares the Erase capability.
    member this.IsErasure = not this.erasures.IsEmpty
    /// The tombstone an erasure writes at `path`, if it erases that path.
    member this.TombstoneAt(path: RelativePath) = this.erasures.TryFind(RelativePath.render path)

/// Why an operation was refused before anything was sent.
[<RequireQualifiedAccess>]
type OperationError =
    | NoChanges
    | DuplicatePath of path: string
    /// A change addresses the namespace root, or the manifest through the record API.
    | InvalidPath of LocationError
    /// The summary is empty, longer than 120 characters, or not one line.
    | InvalidSummary of summary: string
    /// Something in the operation or its content looks like a credential (ARCA-AUTH-002).
    | CredentialInContent of field: string
    /// A metadata value is not a single-line identifier.
    | InvalidMetadata of field: string

/// Construction and validation of operations.
[<RequireQualifiedAccess>]
module Operation =

    /// The longest summary accepted.
    [<Literal>]
    let MaxSummaryLength = 120

    let private singleLineIdentifier (text: string) =
        text.Length > 0
        && text.Length <= 200
        && text |> Seq.forall (fun c -> not (Char.IsControl c) && not (Char.IsWhiteSpace c))

    let private checkMetadata (metadata: OperationMetadata) =
        let summary = metadata.Summary

        if String.IsNullOrWhiteSpace summary || summary.Length > MaxSummaryLength || summary |> Seq.exists Char.IsControl || summary <> summary.Trim() then
            Error(OperationError.InvalidSummary summary)
        elif Secrets.looksLikeCredential summary then
            Error(OperationError.CredentialInContent "summary")
        else
            [ "providerIdentity", metadata.ProviderIdentity; "executionId", metadata.ExecutionId ]
            |> List.tryPick (fun (field, value) ->
                match value with
                | Some text when not (singleLineIdentifier text) -> Some(OperationError.InvalidMetadata field)
                | Some text when Secrets.looksLikeCredential text -> Some(OperationError.CredentialInContent field)
                | _ -> None)
            |> function
                | Some error -> Error error
                | None -> Ok()

    let private checkChanges (changes: Change list) =
        let paths = changes |> List.map (Change.path >> RelativePath.render)

        match changes, paths |> List.countBy id |> List.tryFind (fun (_, count) -> count > 1) with
        | [], _ -> Error OperationError.NoChanges
        | _, Some(path, _) -> Error(OperationError.DuplicatePath path)
        | _ ->
            changes
            |> List.tryPick (fun change ->
                let path = Change.path change

                if RelativePath.segments path |> List.isEmpty then
                    Some(OperationError.InvalidPath(LocationError.NamespaceRootWrite ""))
                else
                    match Change.content change with
                    | Some content when Secrets.looksLikeCredential content ->
                        Some(OperationError.CredentialInContent(RelativePath.render path))
                    | _ -> None)
            |> function
                | Some error -> Error error
                | None -> Ok()

    /// A validated operation in one namespace.
    let create (ns: Namespace) (metadata: OperationMetadata) (changes: Change list) =
        checkMetadata metadata
        |> Result.bind (fun () -> checkChanges changes)
        |> Result.map (fun () ->
            { ns = ns
              changes = changes
              metadata = metadata
              expectedToken = None
              expectedNamespaceToken = None
              erasures = Map.empty })

    /// Conditions the operation on the provider's whole state as well: it
    /// applies only while the change token is still `token`.
    let requireChangeToken (token: ChangeToken) (operation: Operation) =
        { operation with expectedToken = Some token }

    /// Conditions the operation on its own namespace's state: it applies only
    /// while the namespace token is still `token`, whatever other
    /// applications commit elsewhere in the repository (ARCA-CON-005). The
    /// commit is still one atomic commit on the branch head, with the same
    /// OutcomeUnknown semantics; this is the condition a shared repository
    /// should use, and `requireChangeToken` the repository-wide fallback.
    let requireNamespaceToken (token: NamespaceToken) (operation: Operation) =
        { operation with expectedNamespaceToken = Some token }

    /// An erasure: each path's blob is replaced by its tombstone, conditioned
    /// on the erased revision. Built only by `Erasure.operation`, which
    /// validates every request first.
    let internal erasure (ns: Namespace) (metadata: OperationMetadata) (erased: (RelativePath * Tombstone) list) =
        create
            ns
            metadata
            (erased
             |> List.map (fun (path, tombstone) -> Change.Update(path, Tombstone.encode tombstone, tombstone.ErasedRevision)))
        |> Result.map (fun operation ->
            { operation with
                erasures =
                    erased
                    |> List.map (fun (path, tombstone) -> RelativePath.render path, tombstone)
                    |> Map.ofList })

/// A write found the provider's state different from what it expected
/// (ARCA-CON-002). It names the object and its actual revision; Arca never
/// resolves it. The application reloads, reruns its domain validation and
/// decides.
type Conflict =
    { Path: RelativePath
      Expected: Revision option
      /// The object's current revision; None when it no longer exists.
      Actual: Revision option }

/// Why a provider refused a write outright, before or instead of applying it
/// (ARCA-COMMIT-006, ARCA-AUTH-004).
[<RequireQualifiedAccess>]
type WriteRefusal =
    /// The data branch does not allow direct writes (protection rules).
    | BranchProtected
    | RepositoryArchived
    /// The credential can read but not write.
    | ReadOnlyAccess
    /// The credential is missing, expired or revoked; nothing was changed.
    | CredentialUnavailable of reason: string
    /// The configured location now resolves to a different repository
    /// identity (for example after a transfer) and must be confirmed.
    | RepositoryIdentityChanged of expected: string * actual: string
    /// The provider does not offer a capability the write needs.
    | CapabilityUnavailable of CapabilityRefusal

/// Optimistic concurrency checks (ARCA-CON-001, ARCA-CON-002).
[<RequireQualifiedAccess>]
module Concurrency =

    /// Every change whose expectation does not hold against the current
    /// revisions (`current` gives an object's revision, None when absent).
    /// Empty means the operation may be applied on top of the current state.
    let conflicts (current: RelativePath -> Revision option) (changes: Change list) =
        changes
        |> List.choose (fun change ->
            let path = Change.path change
            let expected = Change.expected change
            let actual = current path

            if expected = actual then
                None
            else
                Some
                    { Path = path
                      Expected = expected
                      Actual = actual })
