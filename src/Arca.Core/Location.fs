namespace Arca

open System

/// Why a location, namespace or path was refused (ARCA-LOC).
[<RequireQualifiedAccess>]
type LocationError =
    /// A path segment was empty (a doubled, leading or trailing separator).
    | EmptySegment of path: string
    /// A `.` or `..` segment, or one starting with `.` (which would reach
    /// `.git`, `.github` or another repository-control path).
    | DotSegment of segment: string
    /// A character outside `A-Z a-z 0-9 . _ -` in a segment.
    | InvalidCharacter of segment: string * character: char
    /// A segment longer than the limit.
    | SegmentTooLong of segment: string * limit: int
    /// A path deeper than the limit.
    | PathTooDeep of path: string * limit: int
    /// Not a valid GitHub user or organization name.
    | InvalidOwner of owner: string
    /// Not a valid GitHub repository name.
    | InvalidRepository of name: string
    /// Not a valid branch name.
    | InvalidBranch of branch: string
    /// Not a valid application or dataset identifier.
    | InvalidIdentifier of kind: string * value: string
    /// The resolved path would leave the application's namespace (ARCA-LOC-003).
    | EscapesNamespace of path: string * root: string
    /// A write to the namespace root itself instead of a path inside it (ARCA-LOC-003).
    | NamespaceRootWrite of root: string
    /// Two applications in one deployment share a repository and branch, and
    /// one's namespace contains the other's (ARCA-LOC-002).
    | NamespaceOverlap of first: string * second: string
    /// The same application is configured twice in one deployment.
    | DuplicateApplication of application: string

/// Human-readable text for location errors (no reflection, so it works in a
/// trimmed WebAssembly host compiled with --reflectionfree).
[<RequireQualifiedAccess>]
module LocationError =

    /// The error as one sentence.
    let describe =
        function
        | LocationError.EmptySegment path -> $"'{path}' has an empty path segment"
        | LocationError.DotSegment segment -> $"'{segment}' is a dot or hidden segment"
        | LocationError.InvalidCharacter(segment, character) -> $"'{segment}' contains the character '{character}'"
        | LocationError.SegmentTooLong(segment, limit) -> $"'{segment}' is longer than {limit} characters"
        | LocationError.PathTooDeep(path, limit) -> $"'{path}' is deeper than {limit} segments"
        | LocationError.InvalidOwner owner -> $"'{owner}' is not a GitHub owner name"
        | LocationError.InvalidRepository name -> $"'{name}' is not a GitHub repository name"
        | LocationError.InvalidBranch branch -> $"'{branch}' is not a valid branch name"
        | LocationError.InvalidIdentifier(kind, value) -> $"'{value}' is not a valid {kind} identifier"
        | LocationError.EscapesNamespace(path, root) -> $"'{path}' is outside the namespace '{root}'"
        | LocationError.NamespaceRootWrite root -> $"a write to the namespace root '{root}' itself"
        | LocationError.NamespaceOverlap(first, second) -> $"namespaces '{first}' and '{second}' overlap"
        | LocationError.DuplicateApplication application -> $"application '{application}' is configured twice"

/// One validated path segment: `A-Z a-z 0-9 . _ -`, 1 to 128 characters,
/// starting with a letter or digit. It cannot be `.`, `..`, or a hidden name.
type Segment = private Segment of string

/// Construction and access for path segments.
[<RequireQualifiedAccess>]
module Segment =

    /// The longest segment accepted.
    [<Literal>]
    let MaxLength = 128

    let private allowed (c: char) =
        (c >= 'a' && c <= 'z') || (c >= 'A' && c <= 'Z') || (c >= '0' && c <= '9') || c = '.' || c = '_' || c = '-'

    /// A validated segment, or why the text is not one.
    let create (text: string) =
        if String.IsNullOrEmpty text then
            Error(LocationError.EmptySegment text)
        elif text.StartsWith '.' then
            Error(LocationError.DotSegment text)
        elif text.Length > MaxLength then
            Error(LocationError.SegmentTooLong(text, MaxLength))
        else
            match text |> Seq.tryFind (allowed >> not) with
            | Some bad -> Error(LocationError.InvalidCharacter(text, bad))
            | None when not (Char.IsAsciiLetterOrDigit text[0]) -> Error(LocationError.InvalidCharacter(text, text[0]))
            | None -> Ok(Segment text)

    /// The segment's text.
    let value (Segment text) = text

/// A relative path of validated segments. The empty path is the repository
/// root. It can never hold `..`, `.` or an absolute or backslash form, so a
/// path built from it cannot escape whatever it is joined to.
type RelativePath = private RelativePath of Segment list

/// Construction, rendering and joining of relative paths.
[<RequireQualifiedAccess>]
module RelativePath =

    /// The deepest path accepted.
    [<Literal>]
    let MaxDepth = 32

    /// The empty path.
    let empty = RelativePath []

    let private traverse (results: Result<Segment, LocationError> list) =
        List.foldBack
            (fun next state ->
                match next, state with
                | Ok segment, Ok segments -> Ok(segment :: segments)
                | Error error, _ -> Error error
                | Ok _, Error error -> Error error)
            results
            (Ok [])

    /// A path from `/`-separated text. The empty string is the empty path. A
    /// leading or trailing `/`, a doubled `/`, a backslash, `.`, `..` and hidden
    /// segments are all refused.
    let parse (text: string) =
        if text = "" then
            Ok empty
        else
            let parts = text.Split '/' |> List.ofArray

            if parts.Length > MaxDepth then
                Error(LocationError.PathTooDeep(text, MaxDepth))
            elif parts |> List.exists String.IsNullOrEmpty then
                Error(LocationError.EmptySegment text)
            else
                parts |> List.map Segment.create |> traverse |> Result.map RelativePath

    /// A path of already validated segments.
    let ofSegments (segments: Segment list) =
        if segments.Length > MaxDepth then
            Error(LocationError.PathTooDeep(segments |> List.map Segment.value |> String.concat "/", MaxDepth))
        else
            Ok(RelativePath segments)

    /// The path's segments.
    let segments (RelativePath segments) = segments

    /// The `/`-separated text, empty for the empty path.
    let render (RelativePath segments) =
        segments |> List.map Segment.value |> String.concat "/"

    /// `first` followed by `second`.
    let append (RelativePath first) (RelativePath second) =
        ofSegments (first @ second)

    /// True when `path` is `prefix` or lies beneath it.
    let isWithin (RelativePath prefix) (RelativePath path) =
        path.Length >= prefix.Length && List.take prefix.Length path = prefix

/// A GitHub repository, by owner and name. GitHub treats both
/// case-insensitively, so equality does too; the spelling given is kept for
/// display and URLs.
[<CustomEquality; CustomComparison>]
type RepositoryRef =
    private
        { owner: string
          name: string }

    /// The user or organization that owns the repository.
    member this.Owner = this.owner
    /// The repository's name.
    member this.Name = this.name
    /// `owner/name`.
    override this.ToString() = $"{this.owner}/{this.name}"

    override this.Equals(other: obj) =
        match other with
        | :? RepositoryRef as that ->
            String.Equals(this.owner, that.owner, StringComparison.OrdinalIgnoreCase)
            && String.Equals(this.name, that.name, StringComparison.OrdinalIgnoreCase)
        | _ -> false

    override this.GetHashCode() =
        HashCode.Combine(this.owner.ToLowerInvariant(), this.name.ToLowerInvariant())

    interface IComparable with
        member this.CompareTo(other: obj) =
            match other with
            | :? RepositoryRef as that ->
                match String.Compare(this.owner, that.owner, StringComparison.OrdinalIgnoreCase) with
                | 0 -> String.Compare(this.name, that.name, StringComparison.OrdinalIgnoreCase)
                | order -> order
            | _ -> invalidArg (nameof other) "not a RepositoryRef"

/// Construction of repository references.
[<RequireQualifiedAccess>]
module RepositoryRef =

    let private validOwner (owner: string) =
        not (String.IsNullOrEmpty owner)
        && owner.Length <= 39
        && owner |> Seq.forall (fun c -> Char.IsAsciiLetterOrDigit c || c = '-')
        && not (owner.StartsWith '-')
        && not (owner.EndsWith '-')
        && not (owner.Contains "--")

    let private validName (name: string) =
        not (String.IsNullOrEmpty name)
        && name.Length <= 100
        && name <> "."
        && name <> ".."
        && name |> Seq.forall (fun c -> Char.IsAsciiLetterOrDigit c || c = '.' || c = '_' || c = '-')

    /// A validated repository reference.
    let create owner name =
        if not (validOwner owner) then Error(LocationError.InvalidOwner owner)
        elif not (validName name) then Error(LocationError.InvalidRepository name)
        else Ok { owner = owner; name = name }

/// A branch name, validated against Git's reference-name rules (a strict subset).
type BranchName = private BranchName of string

/// Construction of branch names.
[<RequireQualifiedAccess>]
module BranchName =

    let private forbidden = [ ' '; '~'; '^'; ':'; '?'; '*'; '['; '\\' ]

    /// A validated branch name.
    let create (text: string) =
        let valid =
            not (String.IsNullOrEmpty text)
            && text.Length <= 255
            && not (text.StartsWith '/' || text.EndsWith '/' || text.EndsWith '.')
            && not (text.StartsWith '-')
            && not (text.Contains "..")
            && not (text.Contains "//")
            && not (text.Contains "@{")
            && text <> "@"
            && not (text.EndsWith ".lock")
            && text |> Seq.forall (fun c -> not (Char.IsControl c) && not (List.contains c forbidden))
            && text.Split '/' |> Array.forall (fun part -> not (part.StartsWith '.'))

        if valid then Ok(BranchName text) else Error(LocationError.InvalidBranch text)

    /// The branch's name.
    let value (BranchName text) = text

/// Where a deployment keeps data: repository, branch and base path
/// (ARCA-LOC-001). Nothing about it is hard-coded; every field comes from
/// deployment configuration.
type DataLocation =
    { Repository: RepositoryRef
      Branch: BranchName
      /// The folder applications' namespaces live under; empty for the repository root.
      BasePath: RelativePath }

/// Construction of data locations from configuration text.
[<RequireQualifiedAccess>]
module DataLocation =

    /// A location from configuration text, every part validated.
    let create owner repository branch basePath =
        RepositoryRef.create owner repository
        |> Result.bind (fun repository ->
            BranchName.create branch
            |> Result.bind (fun branch ->
                RelativePath.parse basePath
                |> Result.map (fun basePath ->
                    { Repository = repository
                      Branch = branch
                      BasePath = basePath })))

    /// True when both name the same repository and branch, where namespaces could collide.
    let sharesBranch (first: DataLocation) (second: DataLocation) =
        first.Repository = second.Repository && first.Branch = second.Branch

/// A lower-case identifier: `a-z 0-9 -`, 1 to 40 characters, starting with a
/// letter. Used for application namespaces.
type AppId = private AppId of string

/// Construction of application identifiers.
[<RequireQualifiedAccess>]
module AppId =

    /// A validated application identifier, for example `chrona`.
    let create (text: string) =
        let valid =
            not (String.IsNullOrEmpty text)
            && text.Length <= 40
            && Char.IsAsciiLetterLower text[0]
            && text |> Seq.forall (fun c -> Char.IsAsciiLetterLower c || Char.IsAsciiDigit c || c = '-')
            && not (text.EndsWith '-')

        if valid then Ok(AppId text) else Error(LocationError.InvalidIdentifier("application", text))

    /// The identifier's text.
    let value (AppId text) = text

/// An organization's or dataset's immutable identifier (ARCA-LOC-005). It is
/// independent of display names and slugs: renaming an organization never
/// changes it. `A-Z a-z 0-9 _ -`, 1 to 64 characters, starting with a letter
/// or digit.
type DatasetId = private DatasetId of string

/// Construction of dataset identifiers.
[<RequireQualifiedAccess>]
module DatasetId =

    /// A validated dataset identifier.
    let create (text: string) =
        let valid =
            not (String.IsNullOrEmpty text)
            && text.Length <= 64
            && Char.IsAsciiLetterOrDigit text[0]
            && text |> Seq.forall (fun c -> Char.IsAsciiLetterOrDigit c || c = '_' || c = '-')

        if valid then Ok(DatasetId text) else Error(LocationError.InvalidIdentifier("dataset", text))

    /// The identifier's text.
    let value (DatasetId text) = text

/// What kind of environment a deployment is (ARCA-LOC-007).
[<RequireQualifiedAccess>]
type EnvironmentKind =
    | Local
    | Test
    | Staging
    | Production

/// The deployment environment's identity, which applications can display
/// (ARCA-LOC-007).
type DeploymentEnvironment =
    { Kind: EnvironmentKind
      /// A display name, for example "staging-eu".
      Name: string }

/// One application's storage binding in one deployment: its environment and
/// where its data lives. An application may point at its own repository or a
/// shared one (ARCA-LOC-004).
type ApplicationBinding =
    { Application: AppId
      Environment: DeploymentEnvironment
      Location: DataLocation }

/// A concrete object address: the repository and branch, and the full path
/// inside the repository. Only the adapter sees it (ARCA-ARCH-004).
type ObjectAddress =
    { Repository: RepositoryRef
      Branch: BranchName
      Path: string }

/// A namespace: the root under which one application, or one of its datasets,
/// keeps every object (ARCA-LOC-002).
type Namespace =
    { Application: AppId
      /// The dataset, for a per-organization or per-dataset sub-namespace.
      Dataset: DatasetId option
      Location: DataLocation
      /// The namespace root, relative to the repository root.
      Root: RelativePath }

/// Namespaces and the path safety rules (ARCA-LOC-002, 003, 005).
[<RequireQualifiedAccess>]
module Namespace =

    /// The folder, inside an application namespace, that holds its datasets.
    [<Literal>]
    let DatasetsFolder = "datasets"

    let private segment text =
        match Segment.create text with
        | Ok segment -> segment
        | Error error -> invalidOp ("internal: identifier is not a valid segment: " + LocationError.describe error)

    /// The application's namespace: `<base path>/<application>`.
    let ofApplication (binding: ApplicationBinding) =
        RelativePath.append binding.Location.BasePath (RelativePath [ segment (AppId.value binding.Application) ])
        |> Result.map (fun root ->
            { Application = binding.Application
              Dataset = None
              Location = binding.Location
              Root = root })

    /// A dataset's sub-namespace: `<base path>/<application>/datasets/<dataset>`.
    /// The dataset may live in a different repository from the application's
    /// other data (`location`, ARCA-LOC-005); by default it shares the
    /// application's location.
    let ofDataset (binding: ApplicationBinding) (dataset: DatasetId) (location: DataLocation option) =
        let location = location |> Option.defaultValue binding.Location

        RelativePath.append
            location.BasePath
            (RelativePath
                [ segment (AppId.value binding.Application)
                  segment DatasetsFolder
                  segment (DatasetId.value dataset) ])
        |> Result.map (fun root ->
            { Application = binding.Application
              Dataset = Some dataset
              Location = location
              Root = root })

    /// The address of a path inside the namespace. The path must name
    /// something inside it, never the root itself (ARCA-LOC-003). Because
    /// relative paths cannot hold `..`, the result never escapes; the final
    /// check states that invariant rather than relying on it.
    let resolve (ns: Namespace) (path: RelativePath) =
        if RelativePath.segments path |> List.isEmpty then
            Error(LocationError.NamespaceRootWrite(RelativePath.render ns.Root))
        else
            RelativePath.append ns.Root path
            |> Result.bind (fun full ->
                if RelativePath.isWithin ns.Root full && full <> ns.Root then
                    Ok
                        { Repository = ns.Location.Repository
                          Branch = ns.Location.Branch
                          Path = RelativePath.render full }
                else
                    Error(LocationError.EscapesNamespace(RelativePath.render full, RelativePath.render ns.Root)))

    /// `resolve` for path text, which is parsed first.
    let resolveText (ns: Namespace) (path: string) =
        RelativePath.parse path |> Result.bind (resolve ns)

    /// The path relative to the namespace root, for an address inside it; None
    /// for an address outside it (another application's, or the root).
    let relativeOf (ns: Namespace) (address: ObjectAddress) =
        if address.Repository <> ns.Location.Repository || address.Branch <> ns.Location.Branch then
            None
        else
            match RelativePath.parse address.Path with
            | Ok full when RelativePath.isWithin ns.Root full && full <> ns.Root ->
                RelativePath.segments full
                |> List.skip (RelativePath.segments ns.Root).Length
                |> RelativePath.ofSegments
                |> Result.toOption
            | _ -> None

/// A deployment's applications, checked together: every application appears
/// once, and applications that share a repository and branch never overlap
/// (ARCA-LOC-002, ARCA-LOC-003). Applications that need a permission boundary
/// use different repositories (ARCA-LOC-004, DF-ARCA-2026-0002).
[<RequireQualifiedAccess>]
module Deployment =

    /// The deployment's namespaces, or every reason it is refused.
    let namespaces (bindings: ApplicationBinding list) =
        let duplicates =
            bindings
            |> List.countBy (fun binding -> binding.Application)
            |> List.filter (fun (_, count) -> count > 1)
            |> List.map (fun (application, _) -> LocationError.DuplicateApplication(AppId.value application))

        let resolved = bindings |> List.map Namespace.ofApplication

        let resolutionErrors =
            resolved
            |> List.choose (function
                | Error error -> Some error
                | Ok _ -> None)

        let spaces =
            resolved
            |> List.choose (function
                | Ok ns -> Some ns
                | Error _ -> None)

        let overlaps =
            [ for i, first in List.indexed spaces do
                  for second in List.skip (i + 1) spaces do
                      if
                          first.Application <> second.Application
                          && DataLocation.sharesBranch first.Location second.Location
                          && (RelativePath.isWithin first.Root second.Root || RelativePath.isWithin second.Root first.Root)
                      then
                          LocationError.NamespaceOverlap(RelativePath.render first.Root, RelativePath.render second.Root) ]

        match duplicates @ resolutionErrors @ overlaps with
        | [] -> Ok spaces
        | errors -> Error errors

/// A repository's visibility, as GitHub reports it (ARCA-LOC-008).
[<RequireQualifiedAccess>]
type RepositoryVisibility =
    | Public
    | Private
    | Internal

/// An explicit, recorded decision to keep production data in a public
/// repository. It needs a reason; there is no implicit override.
type PublicProductionOverride = { Reason: string }

/// Why initializing data at a location was refused.
[<RequireQualifiedAccess>]
type VisibilityRefusal =
    /// Production data in a public repository, without an explicit override.
    | PublicProductionRepository
    /// The override's reason was empty.
    | OverrideWithoutReason

/// The visibility policy (ARCA-LOC-008).
[<RequireQualifiedAccess>]
module Visibility =

    /// Whether production data may be initialized at a location of this
    /// visibility. Only production in a public repository is refused, unless
    /// the user explicitly overrides with a reason.
    let permitsInitialization (environment: EnvironmentKind) (visibility: RepositoryVisibility) (overrideDecision: PublicProductionOverride option) =
        match environment, visibility, overrideDecision with
        | EnvironmentKind.Production, RepositoryVisibility.Public, None -> Error VisibilityRefusal.PublicProductionRepository
        | EnvironmentKind.Production, RepositoryVisibility.Public, Some decision when String.IsNullOrWhiteSpace decision.Reason ->
            Error VisibilityRefusal.OverrideWithoutReason
        | _ -> Ok()

/// The configured location of an existing dataset differs from the one its
/// manifest records (ARCA-LOC-009).
type RelocationRequired =
    { Recorded: DataLocation
      Configured: DataLocation }

/// Relocation is a migration, never a configuration edit (ARCA-LOC-009).
[<RequireQualifiedAccess>]
module Relocation =

    /// Ok when the configured location is where the dataset's manifest says it
    /// lives; otherwise the dataset must be moved with the migration workflow
    /// (ARCA-MIG-002), not by editing the configured path.
    let check (recorded: DataLocation) (configured: DataLocation) =
        if recorded = configured then
            Ok()
        else
            Error
                { Recorded = recorded
                  Configured = configured }
