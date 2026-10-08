namespace Arca

open System

/// A provider access token (ARCA-AUTH-001, ARCA-AUTH-002).
///
/// The value is held privately. `ToString` and every formatter show only
/// `AccessToken(redacted)`, and the type has no public property a serializer
/// could read, so a token cannot leak into a record, an operation, the offline
/// queue, a log or a diagnostic by accident. The one way out is
/// `AccessToken.authorization`, which the host transport uses to set the
/// request's Authorization header.
[<Sealed; AllowNullLiteral(false)>]
type AccessToken private (value: string) =

    /// A token from the token provider's text.
    static member Create(value: string) =
        if String.IsNullOrWhiteSpace value || value |> Seq.exists (fun c -> Char.IsWhiteSpace c || Char.IsControl c) then
            Error "a token is non-empty text without whitespace"
        else
            Ok(AccessToken value)

    member internal _.Value = value

    override _.ToString() = "AccessToken(redacted)"

    override _.Equals(other: obj) =
        match other with
        | :? AccessToken as that -> String.Equals(value, that.Value, StringComparison.Ordinal)
        | _ -> false

    override _.GetHashCode() = 0

/// Why the token provider has no usable token. A failed credential never
/// mutates stored data (ARCA-AUTH-004): every operation obtains its token
/// before it sends anything.
[<RequireQualifiedAccess>]
type TokenUnavailable =
    /// No one is signed in.
    | NoToken
    | Expired
    | Revoked
    /// The provider itself failed; the reason holds no secret.
    | ProviderFailed of reason: string

/// The token-provider port (ARCA-AUTH-001). Arca never acquires, refreshes or
/// stores a token: it asks this function each time it needs one. Fides, or
/// anything else, implements it. A message-driven host (a Limen engine)
/// answers the adapter's token step instead of passing a function.
type TokenProvider = unit -> Async<Result<AccessToken, TokenUnavailable>>

/// The only access to a token's value.
[<RequireQualifiedAccess>]
module AccessToken =

    /// A token from text.
    let create value = AccessToken.Create value

    /// The Authorization header for a request, for the host transport only.
    let authorization (token: AccessToken) = "Authorization", "Bearer " + token.Value

/// What kind of principal a credential belongs to.
[<RequireQualifiedAccess>]
type IdentityKind =
    | User
    | Bot
    /// An installation token of an app; it has no user login.
    | Installation

/// Who the provider says the credential belongs to (ARCA-AUTH-003). Arca
/// resolves this from the provider and never trusts a typed username.
type ProviderIdentity =
    { Provider: string
      /// The provider's stable identifier for the principal (GitHub's numeric id).
      Subject: string
      Login: string option
      Kind: IdentityKind }

/// Whether the configured branch accepts direct writes.
[<RequireQualifiedAccess>]
type BranchAccess =
    | Writable
    /// Rules on the branch require something a direct write cannot give,
    /// such as a pull request or status checks (ARCA-COMMIT-006).
    | NotWritable of reasons: string list
    /// The branch does not exist.
    | Missing

/// A non-secret snapshot of what the credential can do at the configured
/// location (ARCA-AUTH-003). It holds no token; it is revalidated after
/// provider errors.
type CapabilitySnapshot =
    { Identity: ProviderIdentity
      /// The provider's stable repository identifier, which survives renames.
      RepositoryId: string
      /// The repository's current `owner/name`, as the provider reports it.
      Repository: RepositoryRef
      Visibility: RepositoryVisibility
      CanRead: bool
      CanWrite: bool
      Archived: bool
      Branch: BranchAccess }

/// Decisions over a capability snapshot. Authentication is who; what an
/// actor may do in the application is the application's decision
/// (ARCA-AUTH-005). Arca only refuses what the provider itself would.
[<RequireQualifiedAccess>]
module CapabilitySnapshot =

    /// Ok when a direct write can succeed; the refusal otherwise.
    let permitsWrite (snapshot: CapabilitySnapshot) =
        if snapshot.Archived then
            Error WriteRefusal.RepositoryArchived
        elif not snapshot.CanWrite then
            Error WriteRefusal.ReadOnlyAccess
        else
            match snapshot.Branch with
            | BranchAccess.Writable -> Ok()
            | BranchAccess.NotWritable _
            | BranchAccess.Missing -> Error WriteRefusal.BranchProtected

    /// Ok when the snapshot is for the repository the session pinned. A
    /// replaced credential that resolves the configured location to a
    /// different repository (a transfer or a re-created repository) is
    /// refused until the application confirms it (ARCA-AUTH-004).
    let checkRepository (pinned: string option) (snapshot: CapabilitySnapshot) =
        match pinned with
        | Some expected when expected <> snapshot.RepositoryId ->
            Error(WriteRefusal.RepositoryIdentityChanged(expected, snapshot.RepositoryId))
        | _ -> Ok()
