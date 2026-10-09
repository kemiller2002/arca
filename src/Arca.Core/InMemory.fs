namespace Arca

open System
open System.Security.Cryptography
open System.Text

/// A fault the in-memory provider produces on request, so consumers can test
/// their handling of every typed storage result (ARCA-TEST-001).
[<RequireQualifiedAccess>]
type InMemoryFault =
    /// The next commit lands but reports OutcomeUnknown.
    | OutcomeUnknownLanded
    /// The next commit does not land and reports OutcomeUnknown.
    | OutcomeUnknownLost
    /// The next call is rate limited.
    | RateLimited of retryAfter: TimeSpan option
    /// From now on the credential is revoked.
    | CredentialRevoked
    /// From now on the location is read-only.
    | ReadOnly
    /// From now on a listing returns at most this many entries, as partial.
    | ListingLimit of entries: int
    /// From now on objects larger than this are refused.
    | MaxObjectBytes of bytes: int64

/// One commit in the in-memory history.
type InMemoryCommit =
    { Token: ChangeToken
      Message: string
      /// The objects the commit touched.
      Touched: Set<string> }

/// The in-memory provider's whole state: plain data, changed only by the
/// pure transitions in `InMemory`.
type InMemoryState =
    { /// Objects by `owner/name@branch:path` (case-insensitive repository identity).
      Objects: Map<string, StoredObject>
      /// Newest first.
      History: InMemoryCommit list
      Sequence: int
      /// Faults that apply to the next call only.
      Pending: InMemoryFault list
      /// Faults that apply from now on.
      Standing: InMemoryFault list }

/// A pure, deterministic implementation of Arca's storage contract, for
/// consumers' tests and as the reference the conformance suite is proven
/// against (ARCA-TEST-001). Revisions are content hashes, so equal content
/// has an equal revision, as in Git.
[<RequireQualifiedAccess>]
module InMemory =

    /// The capabilities it declares: everything but at-rest encryption, as GitHub.
    let capabilities: ProviderCapabilities =
        { Provider = "in-memory"
          ContractVersion = StorageContract.Version
          States =
            Map.ofList
                [ Capability.ReadObject, CapabilityState.Available 1
                  Capability.ConditionalWrite, CapabilityState.Available 1
                  Capability.ListPrefix, CapabilityState.Available 1
                  Capability.BatchWrite, CapabilityState.Available 1
                  Capability.ChangeToken, CapabilityState.Available 1
                  Capability.NamespaceToken, CapabilityState.Available 1
                  Capability.Erase, CapabilityState.Available 1
                  Capability.MaxObjectSize, CapabilityState.Available 1
                  Capability.AtRestEncryption, CapabilityState.Unavailable "at-rest encryption is deferred (DF-ARCA-2026-0002)" ]
          MaxObjectBytes = Some Record.DefaultMaxBytes }

    /// An empty store.
    let empty =
        { Objects = Map.empty
          History = [ { Token = ChangeToken "mem-0"; Message = "initial"; Touched = Set.empty } ]
          Sequence = 0
          Pending = []
          Standing = [] }

    /// The revision of content: its SHA-256.
    let revision (content: string) =
        Revision("sha256:" + Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes content)))

    let private key (location: DataLocation) (path: string) =
        let repository = location.Repository
        $"{repository.Owner.ToLowerInvariant()}/{repository.Name.ToLowerInvariant()}@{BranchName.value location.Branch}:{path}"

    let private head (state: InMemoryState) = state.History.Head.Token

    let private maxBytes (state: InMemoryState) =
        state.Standing
        |> List.tryPick (function
            | InMemoryFault.MaxObjectBytes limit -> Some limit
            | _ -> None)
        |> Option.defaultValue Record.DefaultMaxBytes

    let private size (content: string) = int64 (Encoding.UTF8.GetByteCount content)

    /// Arranges a fault.
    let arrange (fault: InMemoryFault) (state: InMemoryState) =
        match fault with
        | InMemoryFault.OutcomeUnknownLanded
        | InMemoryFault.OutcomeUnknownLost
        | InMemoryFault.RateLimited _ -> { state with Pending = state.Pending @ [ fault ] }
        | InMemoryFault.CredentialRevoked
        | InMemoryFault.ReadOnly
        | InMemoryFault.ListingLimit _
        | InMemoryFault.MaxObjectBytes _ -> { state with Standing = fault :: state.Standing }

    /// The refusal every call meets first: a revoked credential, then a
    /// pending rate limit (which it consumes).
    let private gate (state: InMemoryState) =
        if state.Standing |> List.contains InMemoryFault.CredentialRevoked then
            Error(StorageFailure.Refused(WriteRefusal.CredentialUnavailable "revoked")), state
        else
            match state.Pending with
            | InMemoryFault.RateLimited retryAfter :: rest -> Error(StorageFailure.RateLimited(retryAfter, None)), { state with Pending = rest }
            | _ -> Ok(), state

    let private resolve (ns: Namespace) (path: RelativePath) =
        Namespace.resolve ns path
        |> Result.mapError (fun error -> StorageFailure.ProviderFailed("ARCA.INVALID_PATH", false, LocationError.describe error))

    /// The current change token.
    let changeToken (_: Namespace) (state: InMemoryState) =
        match gate state with
        | Error failure, next -> Error failure, next
        | Ok(), next -> Ok(head next), next

    /// The namespace's token: a hash of every object under its root, by path
    /// and revision. Like a Git tree SHA it is content-addressed, so a commit
    /// outside the namespace leaves it unchanged (ARCA-CON-005).
    let namespaceToken (ns: Namespace) (state: InMemoryState) =
        let root = key ns.Location (RelativePath.render ns.Root) + "/"

        state.Objects
        |> Map.toList
        |> List.filter (fun (objectKey, _) -> objectKey.StartsWith(root, StringComparison.Ordinal))
        |> List.map (fun (objectKey, stored) ->
            let (Revision revision) = stored.Revision
            $"{objectKey.Substring root.Length}\u0000{revision}\n")
        |> String.concat ""
        |> fun manifest -> NamespaceToken("mem-ns:" + Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes manifest)))

    /// The current change token and the namespace's token at it.
    let namespaceState (ns: Namespace) (state: InMemoryState) =
        match gate state with
        | Error failure, next -> Error failure, next
        | Ok(), next ->
            Ok
                { RepositoryToken = head next
                  NamespaceToken = namespaceToken ns next },
            next

    /// Reads one object.
    let read (ns: Namespace) (path: RelativePath) (state: InMemoryState) =
        match gate state with
        | Error failure, next -> Error failure, next
        | Ok(), next ->
            match resolve ns path with
            | Error failure -> Error failure, next
            | Ok address ->
                match Map.tryFind (key ns.Location address.Path) next.Objects with
                | None -> Ok ReadOutcome.Absent, next
                | Some stored when size stored.Content > maxBytes next ->
                    Error(StorageFailure.ObjectTooLarge(address.Path, size stored.Content, maxBytes next)), next
                | Some stored -> Ok(ReadOutcome.ofStored { stored with Path = path }), next

    /// What lies directly under a namespace-relative prefix.
    let list (ns: Namespace) (prefix: RelativePath) (state: InMemoryState) =
        match gate state with
        | Error failure, next -> Error failure, next
        | Ok(), next ->
            match RelativePath.append ns.Root prefix with
            | Error error -> Error(StorageFailure.ProviderFailed("ARCA.INVALID_PATH", false, LocationError.describe error)), next
            | Ok full ->
                let start = key ns.Location (RelativePath.render full)
                let start = if RelativePath.segments full |> List.isEmpty then start else start + "/"

                let entries =
                    next.Objects
                    |> Map.toList
                    |> List.filter (fun (objectKey, _) -> objectKey.StartsWith(start, StringComparison.Ordinal))
                    |> List.map (fun (objectKey, stored) ->
                        let rest = objectKey.Substring start.Length

                        match rest.IndexOf '/' with
                        | -1 -> rest, Some stored
                        | slash -> rest.Substring(0, slash), None)
                    |> List.distinctBy fst
                    |> List.choose (fun (name, stored) ->
                        Segment.create name
                        |> Result.bind (fun segment -> RelativePath.ofSegments (RelativePath.segments prefix @ [ segment ]))
                        |> Result.toOption
                        |> Option.map (fun path ->
                            match stored with
                            | Some file ->
                                { Path = path
                                  Revision = file.Revision
                                  SizeBytes = size file.Content
                                  IsFolder = false }
                            | None ->
                                { Path = path
                                  Revision = Revision("folder:" + RelativePath.render path)
                                  SizeBytes = 0L
                                  IsFolder = true }))

                let limit =
                    next.Standing
                    |> List.tryPick (function
                        | InMemoryFault.ListingLimit entries -> Some entries
                        | _ -> None)

                match limit with
                | Some most when entries.Length > most -> Ok { Entries = List.truncate most entries; Complete = false }, next
                | _ -> Ok { Entries = entries; Complete = true }, next

    let private apply (operation: Operation) (state: InMemoryState) =
        let ns = operation.Namespace

        let touched =
            operation.Changes
            |> List.choose (fun change -> Namespace.resolve ns (Change.path change) |> Result.toOption)
            |> List.map (fun address -> key ns.Location address.Path)
            |> Set.ofList

        let objects =
            operation.Changes
            |> List.fold
                (fun objects change ->
                    match Namespace.resolve ns (Change.path change) with
                    | Error _ -> objects
                    | Ok address ->
                        let objectKey = key ns.Location address.Path

                        match Change.content change with
                        | Some content ->
                            Map.add
                                objectKey
                                { Path = Change.path change
                                  Content = content
                                  Revision = revision content }
                                objects
                        | None -> Map.remove objectKey objects)
                state.Objects

        let sequence = state.Sequence + 1
        let token = ChangeToken $"mem-{sequence}"

        { state with
            Objects = objects
            Sequence = sequence
            History = { Token = token; Message = Commit.message operation; Touched = touched } :: state.History },
        token

    /// Commits one operation atomically, conditioned on every change's
    /// expectation and, when given, the expected change token and the
    /// expected namespace token.
    let commit (operation: Operation) (state: InMemoryState) =
        match gate state with
        | Error failure, next -> Error failure, next
        | Ok(), next ->
            let ns = operation.Namespace

            let addresses =
                operation.Changes
                |> List.map (fun change -> change, resolve ns (Change.path change))

            let revisions =
                operation.Changes
                |> List.map (fun change -> RelativePath.render (Change.path change), Change.content change |> Option.map revision)
                |> Map.ofList

            match addresses |> List.tryPick (fun (_, address) -> match address with Error failure -> Some failure | Ok _ -> None) with
            | Some failure -> Error failure, next
            | None ->
                let oversized =
                    operation.Changes
                    |> List.tryPick (fun change ->
                        match Change.content change with
                        | Some content when size content > maxBytes next ->
                            Some(StorageFailure.ObjectTooLarge(RelativePath.render (Change.path change), size content, maxBytes next))
                        | _ -> None)

                let current path =
                    match resolve ns path with
                    | Ok address -> Map.tryFind (key ns.Location address.Path) next.Objects |> Option.map _.Revision
                    | Error _ -> None

                let erasureRefused =
                    match operation.IsErasure, ProviderCapabilities.missing [ Capability.Erase ] capabilities with
                    | true, refusal :: _ -> Some refusal
                    | _ -> None

                if next.Standing |> List.contains InMemoryFault.ReadOnly then
                    Error(StorageFailure.Refused WriteRefusal.ReadOnlyAccess), next
                elif erasureRefused.IsSome then
                    Error(StorageFailure.Refused(WriteRefusal.CapabilityUnavailable erasureRefused.Value)), next
                else
                    let actualNamespace = namespaceToken ns next

                    match oversized, operation.ExpectedChangeToken, operation.ExpectedNamespaceToken with
                    | Some failure, _, _ -> Error failure, next
                    | None, Some expected, _ when expected <> head next -> Error(StorageFailure.StaleChangeToken(expected, head next)), next
                    | None, _, Some expected when expected <> actualNamespace ->
                        Error(StorageFailure.StaleNamespaceToken(expected, actualNamespace)), next
                    | None, _, _ ->
                        let currentContent path =
                            match resolve ns path with
                            | Ok address -> Map.tryFind (key ns.Location address.Path) next.Objects |> Option.map _.Content
                            | Error _ -> None

                        let refused =
                            operation.Changes
                            |> List.tryPick (fun change ->
                                match Integrity.guardIn operation change (currentContent (Change.path change)) with
                                | Error refusal -> Some(StorageFailure.IntegrityRefused(RelativePath.render (Change.path change), refusal))
                                | Ok() -> None)

                        match Concurrency.conflicts current operation.Changes, refused with
                        | _ :: _ as conflicts, _ -> Error(StorageFailure.Conflicted conflicts), next
                        | [], Some failure -> Error failure, next
                        | [], None ->
                            let pending =
                                { IdempotencyKey = operation.Metadata.IdempotencyKey
                                  Base = head next
                                  Candidate = None
                                  Revisions = revisions }

                            match next.Pending with
                            | InMemoryFault.OutcomeUnknownLanded :: rest ->
                                let landed, _ = apply operation { next with Pending = rest }
                                Error(StorageFailure.OutcomeUnknown pending), landed
                            | InMemoryFault.OutcomeUnknownLost :: rest -> Error(StorageFailure.OutcomeUnknown pending), { next with Pending = rest }
                            | _ ->
                                let committed, token = apply operation next
                                Ok { ChangeToken = token; Revisions = revisions }, committed

    /// Settles an unknown outcome: the operation landed when a commit since
    /// its base carries its idempotency key.
    let reconcile (_: Namespace) (pending: PendingReconciliation) (state: InMemoryState) =
        match gate state with
        | Error failure, next -> Error failure, next
        | Ok(), next ->
            let sinceBase = next.History |> List.takeWhile (fun commit -> commit.Token <> pending.Base)

            match sinceBase |> List.tryFind (fun commit -> Commit.carriesKey pending.IdempotencyKey commit.Message) with
            | Some landed ->
                Ok(ReconcileOutcome.Landed { ChangeToken = landed.Token; Revisions = pending.Revisions }), next
            | None -> Ok ReconcileOutcome.NotLanded, next

    /// Writes or removes an object as something outside Arca would (a
    /// concurrent writer or a manual edit): a commit without Arca trailers.
    let writeExternally (location: DataLocation) (path: string) (content: string option) (state: InMemoryState) =
        let objectKey = key location path

        let objects =
            match content with
            | Some text ->
                match RelativePath.parse path with
                | Ok relative -> Map.add objectKey { Path = relative; Content = text; Revision = revision text } state.Objects
                | Error _ -> state.Objects
            | None -> Map.remove objectKey state.Objects

        let sequence = state.Sequence + 1

        { state with
            Objects = objects
            Sequence = sequence
            History =
                { Token = ChangeToken $"mem-{sequence}"
                  Message = "edited outside Arca"
                  Touched = Set.singleton objectKey }
                :: state.History }

    /// The commits that touched an object, newest first (at most 100).
    let history (ns: Namespace) (path: RelativePath) (state: InMemoryState) =
        match gate state with
        | Error failure, next -> Error failure, next
        | Ok(), next ->
            match resolve ns path with
            | Error failure -> Error failure, next
            | Ok address ->
                let objectKey = key ns.Location address.Path

                next.History
                |> List.filter (fun commit -> commit.Touched.Contains objectKey)
                |> List.truncate 100
                |> List.map (fun commit -> { ChangeToken = commit.Token; Origin = Integrity.origin commit.Message })
                |> Ok,
                next

/// A stateful handle on an in-memory store, for tests: the provider, external
/// writes and faults all act on the same state.
[<Sealed>]
type InMemoryStore(initial: InMemoryState) =
    let mutable state = initial

    let step (transition: InMemoryState -> 'a * InMemoryState) =
        async {
            let result, next = transition state
            state <- next
            return result
        }

    /// An empty store.
    new() = InMemoryStore(InMemory.empty)

    /// The current state.
    member _.State = state

    /// The provider-neutral interface over this store (ARCA-ARCH-003).
    member _.Provider: StorageProvider =
        { Capabilities = InMemory.capabilities
          ChangeToken = fun ns -> step (InMemory.changeToken ns)
          NamespaceState = fun ns -> step (InMemory.namespaceState ns)
          Read = fun ns path -> step (InMemory.read ns path)
          List = fun ns prefix -> step (InMemory.list ns prefix)
          Commit = fun operation -> step (InMemory.commit operation)
          Reconcile = fun ns pending -> step (InMemory.reconcile ns pending)
          History = fun ns path -> step (InMemory.history ns path) }

    /// Arranges a fault.
    member _.Arrange(fault: InMemoryFault) = state <- InMemory.arrange fault state

    /// Writes or removes an object at a repository path, as something outside Arca would.
    member _.WriteExternally(location: DataLocation, path: string, content: string option) =
        state <- InMemory.writeExternally location path content state
