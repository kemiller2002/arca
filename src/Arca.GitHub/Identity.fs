namespace Arca.GitHub

open System.Text.Json
open Arca

/// Why the credential's identity or capabilities could not be resolved.
[<RequireQualifiedAccess>]
type ResolveError =
    /// The token provider had no token. Nothing was sent.
    | CredentialUnavailable of TokenUnavailable
    /// GitHub rejected the token (401).
    | CredentialRejected
    /// The repository does not exist, or this credential cannot see it.
    | RepositoryNotFound of repository: string
    | Call of CallFailure

/// Identity resolution and the capability snapshot (ARCA-AUTH-001..005).
[<RequireQualifiedAccess>]
module Identity =

    /// Ruleset rule types a direct, fast-forward commit through the API cannot satisfy.
    let blockingRules =
        set [ "pull_request"; "required_status_checks"; "update"; "required_signatures"; "required_deployments"; "merge_queue"; "workflows" ]

    /// The identity in a `GET /user` response.
    let parseUser (user: JsonElement) =
        match Api.integer "id" user, Api.text "login" user with
        | Some id, Some login ->
            Some
                { Provider = Provider.Name
                  Subject = string id
                  Login = Some login
                  Kind =
                    match Api.text "type" user with
                    | Some "Bot" -> IdentityKind.Bot
                    | _ -> IdentityKind.User }
        | _ -> None

    /// An installation token's identity: GitHub answers `GET /user` with 403
    /// for it, and it has no login.
    let installation =
        { Provider = Provider.Name
          Subject = "installation"
          Login = None
          Kind = IdentityKind.Installation }

    /// Repository facts from a `GET /repos/{owner}/{repo}` response:
    /// id, current owner/name, visibility, archived, read and write permission.
    let parseRepository (repository: JsonElement) =
        let visibility =
            match Api.text "visibility" repository, Api.flag "private" repository with
            | Some "public", _ -> Some RepositoryVisibility.Public
            | Some "internal", _ -> Some RepositoryVisibility.Internal
            | Some "private", _ -> Some RepositoryVisibility.Private
            | None, Some false -> Some RepositoryVisibility.Public
            | None, Some true -> Some RepositoryVisibility.Private
            | _ -> None

        let owner =
            Api.child "owner" repository |> Option.bind (Api.text "login")

        match Api.integer "id" repository, owner, Api.text "name" repository, visibility with
        | Some id, Some owner, Some name, Some visibility ->
            match RepositoryRef.create owner name with
            | Ok reference ->
                let permissions = Api.child "permissions" repository
                let permission name = permissions |> Option.bind (Api.flag name) |> Option.defaultValue false

                Some(
                    string id,
                    reference,
                    visibility,
                    Api.flag "archived" repository |> Option.defaultValue false,
                    permission "pull",
                    permission "push"
                )
            | Error _ -> None
        | _ -> None

    /// The branch's access from the rules that apply to it
    /// (`GET .../rules/branches/{branch}`, readable by anyone who can read the
    /// repository). Classic branch protection's details are visible only to
    /// administrators, so a write it refuses is reported as BranchProtected
    /// when the write is attempted (ARCA-COMMIT-006).
    let branchAccess (rules: string list) =
        match rules |> List.filter blockingRules.Contains |> List.distinct |> List.sort with
        | [] -> BranchAccess.Writable
        | blocking -> BranchAccess.NotWritable blocking

    let private parseRules (rules: JsonElement) =
        Api.items rules |> Option.map (List.choose (Api.text "type"))

    let private expect (config: GitHubConfig) statuses (response: Response) =
        if List.contains response.Status statuses then Ok response else Error(ResolveError.Call(Api.unexpected config response))

    /// Resolves who the credential is and what it can do at the configured
    /// location. It obtains the token first; without one it sends nothing.
    let resolve (config: GitHubConfig) : Conversation<Result<CapabilitySnapshot, ResolveError>> =
        let repo = Api.repositoryPath config
        let branch = Api.branchPath config
        let get path = Api.request config HttpMethod.Get path None

        conversation {
            match! Conversation.token with
            | Error unavailable -> return Error(ResolveError.CredentialUnavailable unavailable)
            | Ok token ->
                match! Api.call token (get "/user") with
                | Error failure -> return Error(ResolveError.Call failure)
                | Ok user when user.Status = 401 -> return Error ResolveError.CredentialRejected
                | Ok user ->
                    let identity =
                        match user.Status with
                        | 200 -> Api.json parseUser user.Body |> Result.mapError ResolveError.Call
                        | 403 when not (Faults.isRateLimited 403 user.Headers) -> Ok installation
                        | _ -> Error(ResolveError.Call(Api.unexpected config user))

                    match identity with
                    | Error error -> return Error error
                    | Ok identity ->
                        match! Api.call token (get repo) with
                        | Error failure -> return Error(ResolveError.Call failure)
                        | Ok response when response.Status = 401 -> return Error ResolveError.CredentialRejected
                        | Ok response when response.Status = 404 ->
                            return Error(ResolveError.RepositoryNotFound(string config.Location.Repository))
                        | Ok response ->
                            match expect config [ 200 ] response |> Result.bind (fun ok -> Api.json parseRepository ok.Body |> Result.mapError ResolveError.Call) with
                            | Error error -> return Error error
                            | Ok(repositoryId, reference, visibility, archived, canRead, canWrite) ->
                                let snapshot branchAccess =
                                    { Identity = identity
                                      RepositoryId = repositoryId
                                      Repository = reference
                                      Visibility = visibility
                                      CanRead = canRead
                                      CanWrite = canWrite
                                      Archived = archived
                                      Branch = branchAccess }

                                match! Api.call token (get $"{repo}/branches/{branch}") with
                                | Error failure -> return Error(ResolveError.Call failure)
                                | Ok response when response.Status = 404 -> return Ok(snapshot BranchAccess.Missing)
                                | Ok response ->
                                    match expect config [ 200 ] response with
                                    | Error error -> return Error error
                                    | Ok _ ->
                                        match! Api.call token (get $"{repo}/rules/branches/{branch}") with
                                        | Error failure -> return Error(ResolveError.Call failure)
                                        | Ok rules ->
                                            match expect config [ 200 ] rules |> Result.bind (fun ok -> Api.json parseRules ok.Body |> Result.mapError ResolveError.Call) with
                                            | Error error -> return Error error
                                            | Ok ruleTypes -> return Ok(snapshot (branchAccess ruleTypes))
        }
