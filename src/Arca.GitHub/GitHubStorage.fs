namespace Arca.GitHub

open System
open System.Security.Cryptography
open System.Text
open System.Text.Json
open Arca

/// The GitHub storage operations (ARCA-API-001..005, ARCA-OUT-001..002,
/// ARCA-COMMIT-001, ARCA-COMMIT-006).
///
/// Reads go by deterministic path through the contents API, never by
/// scanning the repository. A commit is built with the Git data API on top of
/// the current branch head and published by one fast-forward-only ref update,
/// so an operation is visible completely or not at all. The ref update is the
/// only request whose effect matters; when its outcome is unknown the adapter
/// inspects GitHub before doing anything else.
[<RequireQualifiedAccess>]
module GitHubStorage =

    /// Git's blob SHA-1 of UTF-8 content: the revision GitHub will report for it.
    let blobRevision (content: string) =
        let bytes = Encoding.UTF8.GetBytes content
        let header = Encoding.UTF8.GetBytes $"blob {bytes.Length}\u0000"
        Revision(Convert.ToHexStringLower(SHA1.HashData(Array.append header bytes)))

    let private isCommitSha (text: string) =
        text.Length = 40 && text |> Seq.forall (fun c -> Char.IsAsciiDigit c || (c >= 'a' && c <= 'f'))

    let private unavailableReason =
        function
        | TokenUnavailable.NoToken -> "no token"
        | TokenUnavailable.Expired -> "expired"
        | TokenUnavailable.Revoked -> "revoked"
        | TokenUnavailable.ProviderFailed detail -> $"token provider failed: {detail}"

    let private token: Op<AccessToken> =
        fun session ->
            Conversation.token
            |> Conversation.map (fun result ->
                match result with
                | Ok token -> Ok token, session
                | Error unavailable ->
                    Error(StorageFailure.Refused(WriteRefusal.CredentialUnavailable(unavailableReason unavailable))), session)

    let private parseBody (parse: JsonElement -> 'a option) (response: Response) : Op<'a> =
        fun session ->
            match Api.json parse response.Body with
            | Ok value -> Done(Ok value, session)
            | Error(CallFailure.Failed failure) -> Done(Error(Session.failed failure), session)
            | Error _ -> Done(Error(StorageFailure.ProviderFailed("AEGIS.GITHUB.INVALID_RESPONSE", false, "unreadable response")), session)

    let private unexpected (response: Response) : Op<'a> =
        fun session -> Done(Error(Session.unexpected session response), session)

    let private rejected: Op<'a> =
        fun session ->
            Done(Error(StorageFailure.Refused(WriteRefusal.CredentialUnavailable "rejected by GitHub")), { session with Snapshot = None })

    let private checkLocation (ns: Namespace) : Op<unit> =
        fun session ->
            if ns.Location = session.Config.Location then
                Done(Ok(), session)
            else
                let describe (location: DataLocation) = $"{location.Repository}@{BranchName.value location.Branch}"
                Done(Error(StorageFailure.WrongLocation(describe session.Config.Location, describe ns.Location)), session)

    let private address (ns: Namespace) (path: RelativePath) : Op<ObjectAddress> =
        fun session ->
            match Namespace.resolve ns path with
            | Ok address -> Done(Ok address, session)
            | Error error -> Done(Error(StorageFailure.ProviderFailed("ARCA.INVALID_PATH", false, LocationError.describe error)), session)

    /// The branch head: the change token for the configured location.
    let private head (credential: AccessToken) : Op<ChangeToken> =
        op {
            let! session = Op.session
            let! response = Session.get credential false $"{Api.repositoryPath session.Config}/git/ref/heads/{Api.branchPath session.Config}"

            match response.Status with
            | 200 ->
                let! sha = parseBody (Api.child "object" >> Option.bind (Api.text "sha")) response
                return ChangeToken sha
            | 401 -> return! rejected
            | 404 -> return! Op.fail (StorageFailure.Refused WriteRefusal.BranchProtected)
            | _ -> return! unexpected response
        }

    /// The current change token of the namespace's location.
    let changeToken (ns: Namespace) : Op<ChangeToken> =
        op {
            do! checkLocation ns
            let! credential = token
            return! head credential
        }

    type private Contents =
        | File of revision: string * size: int64 * content: string option
        | Folder of entries: ListEntry list
        | Missing

    let private contentsAt (credential: AccessToken) (reference: string) (repositoryPath: string) (root: RelativePath) : Op<Contents> =
        op {
            let! session = Op.session
            let url = $"{Api.repositoryPath session.Config}/contents/{Api.filePath repositoryPath}?ref={Uri.EscapeDataString reference}"
            let! response = Session.get credential (isCommitSha reference) url

            match response.Status with
            | 404 -> return Missing
            | 401 -> return! rejected
            | 200 ->
                let parseFile (element: JsonElement) =
                    match Api.text "type" element, Api.text "sha" element, Api.integer "size" element with
                    | Some "file", Some sha, Some size ->
                        let content =
                            match Api.text "encoding" element, Api.text "content" element with
                            | Some "base64", Some encoded ->
                                try
                                    Some(UTF8Encoding(false, true).GetString(Convert.FromBase64String encoded))
                                with
                                | :? FormatException
                                | :? ArgumentException -> None
                            | _ -> None

                        Some(File(sha, size, content))
                    | _ -> None

                let parseFolder (element: JsonElement) =
                    Api.items element
                    |> Option.map (fun items ->
                        items
                        |> List.choose (fun item ->
                            match Api.text "name" item, Api.text "sha" item, Api.text "type" item with
                            | Some name, Some sha, Some kind ->
                                match Segment.create name |> Result.bind (fun segment -> RelativePath.ofSegments (RelativePath.segments root @ [ segment ])) with
                                | Ok path ->
                                    Some
                                        { Path = path
                                          Revision = Revision sha
                                          SizeBytes = Api.integer "size" item |> Option.defaultValue 0L
                                          IsFolder = (kind = "dir") }
                                | Error _ -> None
                            | _ -> None)
                        |> Folder)

                let parse (element: JsonElement) =
                    match parseFile element with
                    | Some file -> Some file
                    | None -> parseFolder element

                return! parseBody parse response
            | _ -> return! unexpected response
        }

    /// Reads one object at the branch head (ARCA-API-001). An object over the
    /// provider's size limit is ObjectTooLarge, never truncated (ARCA-API-004).
    let read (ns: Namespace) (path: RelativePath) : Op<ReadOutcome> =
        op {
            let! session = Op.session
            do! checkLocation ns
            let! target = address ns path
            let! credential = token
            let! tip = head credential
            let (ChangeToken commit) = tip
            let! contents = contentsAt credential commit target.Path RelativePath.empty

            match contents with
            | Missing -> return ReadOutcome.Absent
            | Folder _ -> return! Op.fail (StorageFailure.ProviderFailed("ARCA.NOT_AN_OBJECT", false, $"{target.Path} is a folder"))
            | File(_, size, _) when size > session.Config.MaxObjectBytes ->
                return! Op.fail (StorageFailure.ObjectTooLarge(target.Path, size, session.Config.MaxObjectBytes))
            | File(_, _, None) -> return! Op.fail (StorageFailure.ProviderFailed("AEGIS.GITHUB.INVALID_RESPONSE", false, $"{target.Path} has no readable UTF-8 content"))
            | File(sha, _, Some content) ->
                return
                    ReadOutcome.Found
                        { Path = path
                          Content = content
                          Revision = Revision sha }
        }

    /// Lists what lies directly under a namespace-relative prefix (the
    /// namespace root for the empty path). A listing at GitHub's limit is
    /// reported as partial (ARCA-API-004).
    let list (ns: Namespace) (prefix: RelativePath) : Op<Listing> =
        op {
            do! checkLocation ns

            let! full =
                fun session ->
                    match RelativePath.append ns.Root prefix with
                    | Ok full -> Done(Ok full, session)
                    | Error error -> Done(Error(StorageFailure.ProviderFailed("ARCA.INVALID_PATH", false, LocationError.describe error)), session)

            let! credential = token
            let! tip = head credential
            let (ChangeToken commit) = tip
            let! contents = contentsAt credential commit (RelativePath.render full) prefix

            match contents with
            | Missing -> return { Entries = []; Complete = true }
            | File _ -> return! Op.fail (StorageFailure.ProviderFailed("ARCA.NOT_A_FOLDER", false, $"{RelativePath.render full} is an object"))
            | Folder entries ->
                let! session = Op.session

                return
                    { Entries = entries
                      Complete = entries.Length < session.Config.ListingLimit }
        }

    /// Measures object count and size under a prefix by walking its folders,
    /// visiting at most `folderBudget` folders (ARCA-API-005).
    let measure (ns: Namespace) (prefix: RelativePath) (folderBudget: int) : Op<GrowthMeasure> =
        let rec walk (pending: RelativePath list) visited (total: GrowthMeasure) : Op<GrowthMeasure> =
            match pending with
            | [] -> Op.ret total
            | _ when visited >= folderBudget -> Op.ret { total with Complete = false }
            | folder :: rest ->
                op {
                    let! listing = list ns folder
                    let files = listing.Entries |> List.filter (fun entry -> not entry.IsFolder)
                    let folders = listing.Entries |> List.filter _.IsFolder |> List.map _.Path

                    return!
                        walk
                            (rest @ folders)
                            (visited + 1)
                            { Objects = total.Objects + files.Length
                              Bytes = total.Bytes + (files |> List.sumBy _.SizeBytes)
                              Folders = total.Folders + 1
                              Complete = total.Complete && listing.Complete }
                }

        walk [ prefix ] 0 { Objects = 0; Bytes = 0L; Folders = 0; Complete = true }

    let private resolveError (error: ResolveError) =
        match error with
        | ResolveError.CredentialUnavailable unavailable ->
            StorageFailure.Refused(WriteRefusal.CredentialUnavailable(unavailableReason unavailable))
        | ResolveError.CredentialRejected -> StorageFailure.Refused(WriteRefusal.CredentialUnavailable "rejected by GitHub")
        | ResolveError.RepositoryNotFound repository ->
            StorageFailure.ProviderFailed("AEGIS.GITHUB.REPOSITORY_NOT_FOUND", false, $"{repository} was not found or is not visible")
        | ResolveError.Call(CallFailure.Failed failure) -> Session.failed failure
        | ResolveError.Call _ -> StorageFailure.ProviderFailed("ARCA.GITHUB.UNKNOWN", true, "identity resolution did not complete")

    /// The capability snapshot, resolved once per session and again after a
    /// refusal; a write is refused before anything is sent when the snapshot
    /// says GitHub would refuse it (ARCA-AUTH-003, ARCA-AUTH-004, ARCA-COMMIT-006).
    let snapshot: Op<CapabilitySnapshot> =
        fun session ->
            match session.Snapshot with
            | Some known -> Done(Ok known, session)
            | None ->
                Identity.resolve session.Config
                |> Conversation.map (fun result ->
                    match result with
                    | Error error -> Error(resolveError error), session
                    | Ok resolved ->
                        match CapabilitySnapshot.checkRepository session.PinnedRepositoryId resolved with
                        | Error refusal -> Error(StorageFailure.Refused refusal), session
                        | Ok() ->
                            Ok resolved,
                            { session with
                                Snapshot = Some resolved
                                PinnedRepositoryId = Some resolved.RepositoryId })

    let private permitsWrite (snapshot: CapabilitySnapshot) : Op<unit> =
        fun session ->
            match CapabilitySnapshot.permitsWrite snapshot with
            | Ok() -> Done(Ok(), session)
            | Error refusal -> Done(Error(StorageFailure.Refused refusal), { session with Snapshot = None })

    /// Confirms (when a key is supplied) that the repository's id is still
    /// the one the application pinned, for example from a previous session.
    let pin (repositoryId: string) : Op<unit> =
        Op.update (fun session -> { session with PinnedRepositoryId = Some repositoryId; Snapshot = None })

    let private treeEntry (path: string) (change: Change) =
        match Change.content change with
        | Some content ->
            Json.objectOf
                [ "path", Json.String path
                  "mode", Json.String "100644"
                  "type", Json.String "blob"
                  "content", Json.String content ]
        | None ->
            Json.objectOf
                [ "path", Json.String path
                  "mode", Json.String "100644"
                  "type", Json.String "blob"
                  "sha", Json.Null ]

    let private post (credential: AccessToken) (path: string) (body: Json) (field: string) : Op<string> =
        op {
            let! session = Op.session
            let request = Api.request session.Config HttpMethod.Post path (Some(Json.canonicalText body))
            let! response = Session.sendSafe credential request

            match response.Status with
            | 200
            | 201 -> return! parseBody (Api.text field) response
            | 401 -> return! rejected
            | _ -> return! unexpected response
        }

    /// What the ref update did.
    type private Published =
        | Published
        | Raced
        | Refused of StorageFailure
        | Unknown

    let private publish (credential: AccessToken) (commit: string) : Op<Published> =
        fun session ->
            let config = session.Config

            let request =
                Api.request
                    config
                    HttpMethod.Patch
                    $"{Api.repositoryPath config}/git/refs/heads/{Api.branchPath config}"
                    (Some(Json.canonicalText (Json.objectOf [ "sha", Json.String commit; "force", Json.Bool false ])))

            Conversation.send (Some credential) request
            |> Conversation.map (fun outcome ->
                match outcome with
                | HttpOutcome.Response(200, _, _) -> Ok Published, session
                | HttpOutcome.Response(status, headers, body) ->
                    let message = body.ToLowerInvariant()

                    if Faults.isRateLimited status headers then
                        let retryAfter =
                            match Http.header "retry-after" headers |> Option.map Int32.TryParse with
                            | Some(true, seconds) -> Some(TimeSpan.FromSeconds(float seconds))
                            | _ -> None

                        Ok(Refused(StorageFailure.RateLimited(retryAfter, None))), session
                    elif status = 422 && message.Contains "fast forward" then
                        Ok Raced, session
                    elif (status = 422 || status = 403) && (message.Contains "protected branch" || message.Contains "repository rule") then
                        Ok(Refused(StorageFailure.Refused WriteRefusal.BranchProtected)), { session with Snapshot = None }
                    elif status = 403 && message.Contains "archived" then
                        Ok(Refused(StorageFailure.Refused WriteRefusal.RepositoryArchived)), { session with Snapshot = None }
                    elif status = 401 then
                        Ok(Refused(StorageFailure.Refused(WriteRefusal.CredentialUnavailable "rejected by GitHub"))), { session with Snapshot = None }
                    elif status = 403 || status = 404 then
                        Ok(Refused(StorageFailure.Refused WriteRefusal.ReadOnlyAccess)), { session with Snapshot = None }
                    else
                        Ok(
                            Refused(
                                Session.unexpected
                                    session
                                    { Status = status
                                      Headers = headers
                                      Body = body }
                            )
                        ),
                        session
                // A failure that cannot have changed anything (Limen's network
                // failure for a mutating request means the browser was offline).
                | HttpOutcome.Failed _
                | HttpOutcome.Cancelled ->
                    Ok(Refused(StorageFailure.ProviderFailed("AEGIS.NETWORK.UNAVAILABLE", true, "GitHub could not be reached; nothing was changed"))),
                    session
                | HttpOutcome.OutcomeUnknown _ -> Ok Unknown, session)

    /// Commits whose message carries the key, walking back from the head to
    /// the operation's base commit (at most one page of history).
    let private searchHistory (credential: AccessToken) (tip: string) (pending: PendingReconciliation) : Op<ReconcileOutcome> =
        op {
            let! session = Op.session
            let (ChangeToken baseCommit) = pending.Base
            let! response = Session.get credential false $"{Api.repositoryPath session.Config}/commits?sha={tip}&per_page=100"

            if response.Status <> 200 then
                return! unexpected response
            else
                let! commits =
                    parseBody
                        (fun element ->
                            Api.items element
                            |> Option.map (
                                List.choose (fun item ->
                                    match Api.text "sha" item, Api.child "commit" item |> Option.bind (Api.text "message") with
                                    | Some sha, Some message -> Some(sha, message)
                                    | _ -> None)
                            ))
                        response

                let beforeBase = commits |> List.takeWhile (fun (sha, _) -> sha <> baseCommit)
                let reachedBase = beforeBase.Length < commits.Length

                match beforeBase |> List.tryFind (fun (_, message) -> Commit.carriesKey pending.IdempotencyKey message) with
                | Some(sha, _) ->
                    return
                        ReconcileOutcome.Landed
                            { ChangeToken = ChangeToken sha
                              Revisions = pending.Revisions }
                | None when reachedBase -> return ReconcileOutcome.NotLanded
                | None -> return ReconcileOutcome.StillUnknown pending
        }

    let private reconcileWith (credential: AccessToken) (pending: PendingReconciliation) : Op<ReconcileOutcome> =
        op {
            let! (ChangeToken tip) = head credential
            let! session = Op.session

            match pending.Candidate with
            | Some candidate when candidate = tip ->
                return
                    ReconcileOutcome.Landed
                        { ChangeToken = ChangeToken candidate
                          Revisions = pending.Revisions }
            | Some candidate ->
                let! response = Session.get credential true $"{Api.repositoryPath session.Config}/compare/{candidate}...{tip}"

                match response.Status with
                | 200 ->
                    let! status = parseBody (Api.text "status") response

                    match status with
                    // The head is the candidate or descends from it.
                    | "identical"
                    | "ahead" ->
                        return
                            ReconcileOutcome.Landed
                                { ChangeToken = ChangeToken candidate
                                  Revisions = pending.Revisions }
                    | _ -> return! searchHistory credential tip pending
                | 404 -> return! searchHistory credential tip pending
                | _ -> return! unexpected response
            | None -> return! searchHistory credential tip pending
        }

    /// Settles an unknown outcome by inspecting GitHub (ARCA-OUT-002): the
    /// candidate commit is in the branch history, or a commit carrying the
    /// operation's idempotency key is, or neither is.
    let reconcile (ns: Namespace) (pending: PendingReconciliation) : Op<ReconcileOutcome> =
        op {
            do! checkLocation ns
            let! credential = token
            return! reconcileWith credential pending
        }

    /// Commits an operation as one atomic commit on the configured branch
    /// (ARCA-COMMIT-001), conditioned on every change's expected revision at
    /// the current head (ARCA-CON-001). A concurrent commit that leaves this
    /// operation's records untouched is built upon; one that changed them is a
    /// Conflicted result (ARCA-CON-002). An unknown outcome is reconciled at
    /// once; if GitHub still cannot tell, it is returned as OutcomeUnknown
    /// with the reconciliation obligation (ARCA-OUT-001).
    let commit (operation: Operation) : Op<CommitReceipt> =
        let ns = operation.Namespace

        let revisions =
            operation.Changes
            |> List.map (fun change ->
                RelativePath.render (Change.path change), Change.content change |> Option.map blobRevision)
            |> Map.ofList

        let rec attempt (credential: AccessToken) (addresses: (Change * ObjectAddress) list) (number: int) : Op<CommitReceipt> =
            op {
                let! session = Op.session
                let repo = Api.repositoryPath session.Config
                let! tip = head credential
                let (ChangeToken headCommit) = tip

                do!
                    match operation.ExpectedChangeToken with
                    | Some expected when expected <> tip -> Op.fail (StorageFailure.StaleChangeToken(expected, tip))
                    | _ -> Op.ret ()

                // Expectations are checked at this exact head (ARCA-CON-001).
                let rec current (pending: (Change * ObjectAddress) list) found : Op<Map<string, Revision option>> =
                    match pending with
                    | [] -> Op.ret found
                    | (change, target) :: rest ->
                        op {
                            let! contents = contentsAt credential headCommit target.Path RelativePath.empty

                            let actual =
                                match contents with
                                | File(sha, _, _) -> Some(Revision sha)
                                | Folder _ -> Some(Revision "folder")
                                | Missing -> None

                            return! current rest (Map.add (RelativePath.render (Change.path change)) actual found)
                        }

                let! actual = current addresses Map.empty
                let lookup path = Map.tryFind (RelativePath.render path) actual |> Option.flatten

                match Concurrency.conflicts lookup operation.Changes with
                | _ :: _ as conflicts -> return! Op.fail (StorageFailure.Conflicted conflicts)
                | [] ->
                    let! headTree =
                        op {
                            let! response = Session.get credential true $"{repo}/git/commits/{headCommit}"
                            if response.Status <> 200 then return! unexpected response
                            else return! parseBody (Api.child "tree" >> Option.bind (Api.text "sha")) response
                        }

                    let tree =
                        Json.objectOf
                            [ "base_tree", Json.String headTree
                              "tree", Json.Array(addresses |> List.map (fun (change, target) -> treeEntry target.Path change)) ]

                    let! treeSha = post credential $"{repo}/git/trees" tree "sha"

                    let commitBody =
                        Json.objectOf
                            [ "message", Json.String(Commit.message operation)
                              "tree", Json.String treeSha
                              "parents", Json.Array [ Json.String headCommit ] ]

                    let! commitSha = post credential $"{repo}/git/commits" commitBody "sha"
                    let! published = publish credential commitSha

                    let receipt =
                        { ChangeToken = ChangeToken commitSha
                          Revisions = revisions }

                    let pending =
                        { IdempotencyKey = operation.Metadata.IdempotencyKey
                          Base = tip
                          Candidate = Some commitSha
                          Revisions = revisions }

                    match published with
                    | Published -> return receipt
                    | Refused failure -> return! Op.fail failure
                    | Raced when number < session.Policy.MaxCommitAttempts -> return! attempt credential addresses (number + 1)
                    | Raced ->
                        return! Op.fail (StorageFailure.ProviderFailed("ARCA.GITHUB.CONTENTION", true, "the branch kept moving; try again"))
                    | Unknown ->
                        let! settled = Op.attempt (reconcileWith credential pending)

                        match settled with
                        | Ok(ReconcileOutcome.Landed landed) -> return landed
                        | Ok ReconcileOutcome.NotLanded when number < session.Policy.MaxCommitAttempts ->
                            return! attempt credential addresses (number + 1)
                        | Ok(ReconcileOutcome.StillUnknown obligation) -> return! Op.fail (StorageFailure.OutcomeUnknown obligation)
                        | Ok ReconcileOutcome.NotLanded
                        | Error _ -> return! Op.fail (StorageFailure.OutcomeUnknown pending)
            }

        let oversized (limit: int64) =
            operation.Changes
            |> List.tryPick (fun change ->
                match Change.content change with
                | Some content when int64 (Encoding.UTF8.GetByteCount content) > limit ->
                    Some(StorageFailure.ObjectTooLarge(RelativePath.render (Change.path change), int64 (Encoding.UTF8.GetByteCount content), limit))
                | _ -> None)

        op {
            do! checkLocation ns
            let! session = Op.session

            do!
                match oversized session.Config.MaxObjectBytes with
                | Some failure -> Op.fail failure
                | None -> Op.ret ()

            let! known = snapshot
            do! permitsWrite known
            let! credential = token

            let! addresses =
                fun session ->
                    operation.Changes
                    |> List.fold
                        (fun state change ->
                            state
                            |> Result.bind (fun found ->
                                Namespace.resolve ns (Change.path change)
                                |> Result.map (fun target -> (change, target) :: found)))
                        (Ok [])
                    |> function
                        | Ok found -> Done(Ok(List.rev found), session)
                        | Error error -> Done(Error(StorageFailure.ProviderFailed("ARCA.INVALID_PATH", false, LocationError.describe error)), session)

            return! attempt credential addresses 1
        }

    /// The rate-limit evidence from the last response (ARCA-API-003).
    let budget (session: GitHubSession) = session.Budget

    /// The GitHub provider behind Arca's provider-neutral interface
    /// (ARCA-ARCH-003). The session lives in the closure; each call runs its
    /// conversation through the host.
    let provider (host: Host) (config: GitHubConfig) : StorageProvider =
        let session = ref (Session.create config)

        let run (step: Op<'a>) =
            async {
                let! result, next = Conversation.run host (step session.Value)
                session.Value <- next
                return result
            }

        { Capabilities = Provider.capabilities
          ChangeToken = fun ns -> run (changeToken ns)
          Read = fun ns path -> run (read ns path)
          List = fun ns prefix -> run (list ns prefix)
          Commit = fun operation -> run (commit operation)
          Reconcile = fun ns pending -> run (reconcile ns pending) }
