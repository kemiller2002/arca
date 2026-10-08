/// An in-process simulation of the GitHub REST endpoints Arca.GitHub uses,
/// answering requests synchronously as the host would. It keeps real Git
/// semantics where Arca depends on them: blob SHAs are Git's, a ref update
/// that is not a fast-forward is refused, and commits form a history.
module Arca.Tests.FakeGitHub

open System
open System.Security.Cryptography
open System.Text
open System.Text.Json
open Arca
open Arca.GitHub

/// Git's blob SHA-1 for UTF-8 content.
let blobSha (content: string) =
    let bytes = Encoding.UTF8.GetBytes content
    let header = Encoding.UTF8.GetBytes $"blob {bytes.Length}\u0000"
    Convert.ToHexStringLower(SHA1.HashData(Array.append header bytes))

let private sha (text: string) =
    Convert.ToHexStringLower(SHA1.HashData(Encoding.UTF8.GetBytes text))

type Commit =
    { Tree: Map<string, string>
      Parents: string list
      Message: string }

type Identity =
    | AsUser of id: int64 * login: string * kind: string
    | AsInstallation

/// What the next matching request does instead of being answered normally.
type Fault =
    | Answer of status: int * headers: (string * string) list * body: string
    | Lose of UnknownReason
    /// Apply the request, then report its outcome as unknown.
    | ApplyThenLose of UnknownReason
    | FailNetwork

/// The simulated GitHub. Mutable, as a server is; tests inspect it.
type Server(owner: string, name: string) =
    let blobs = Collections.Generic.Dictionary<string, string>()
    let commits = Collections.Generic.Dictionary<string, Commit>()
    let trees = Collections.Generic.Dictionary<string, Map<string, string>>()

    let addTree (tree: Map<string, string>) =
        let id = sha ("tree\n" + String.concat "\n" (tree |> Map.toList |> List.map (fun (p, b) -> $"{p} {b}")))
        trees[id] <- tree
        id
    let refs = Collections.Generic.Dictionary<string, string>()
    let requests = Collections.Generic.List<Authorized>()
    let faults = Collections.Generic.List<(Authorized -> bool) * Fault>()

    let addCommit (commit: Commit) =
        let tree = addTree commit.Tree
        let id = sha ("commit\n" + tree + "|" + String.concat "," commit.Parents + "|" + commit.Message)
        commits[id] <- commit
        id

    do
        let root = addCommit { Tree = Map.empty; Parents = []; Message = "Initial commit" }
        refs["main"] <- root

    member val Token = "ghp_valid0123456789abcdefghij0123456789" with get, set
    member val Identity = AsUser(583231L, "octocat", "User") with get, set
    member val RepositoryId = 9001L with get, set
    member val Visibility = "private" with get, set
    member val Archived = false with get, set
    member val CanPush = true with get, set
    member val Rules: string list = [] with get, set
    /// Paths whose ref updates GitHub refuses as a protected branch.
    member val ProtectedRefUpdate = false with get, set

    member _.Requests = requests |> List.ofSeq
    member _.Owner = owner
    member _.Name = name

    /// Queues a fault for the next request matching the predicate.
    member _.Inject(matches, fault) = faults.Add((matches, fault))

    member _.Head(branch: string) = refs[branch]
    member _.Commit(id: string) = commits[id]
    member _.Branches = refs.Keys |> List.ofSeq

    /// The file contents at the branch head.
    member this.Files(branch: string) =
        commits[refs[branch]].Tree |> Map.map (fun _ blob -> blobs[blob])

    /// Commits a change directly, as a person editing on github.com would.
    member this.CommitDirectly(branch: string, files: (string * string option) list, message: string) =
        let head = refs[branch]

        let tree =
            files
            |> List.fold
                (fun tree (path, content) ->
                    match content with
                    | Some text ->
                        let blob = blobSha text
                        blobs[blob] <- text
                        Map.add path blob tree
                    | None -> Map.remove path tree)
                commits[head].Tree

        let id = addCommit { Tree = tree; Parents = [ head ]; Message = message }
        refs[branch] <- id
        id

    member this.CreateBranch(branch: string) = refs[branch] <- refs["main"]

    member private this.IsAncestor(ancestor: string, descendant: string) =
        let rec walk (id: string) =
            id = ancestor || (commits.ContainsKey id && commits[id].Parents |> List.exists walk)

        walk descendant

    member private this.Respond(request: Authorized) : HttpOutcome =
        let json (status: int) (node: Json) =
            HttpOutcome.Response(status, Map.ofList [ "x-ratelimit-remaining", "4999" ], Json.canonicalText node)

        let obj (pairs: (string * Json) list) = Json.objectOf pairs
        let str (text: string) = Json.String text
        let num (n: int64) = Json.Number(decimal n)
        let boolean (b: bool) = Json.Bool b
        let arr (items: Json list) = Json.Array items

        let status (code: int) message = json code (obj [ "message", str message ])

        let authorized =
            match request.Credential with
            | Some token -> snd (AccessToken.authorization token) = "Bearer " + this.Token
            | None -> false

        let uri = Uri request.Request.Url
        let path = Uri.UnescapeDataString uri.AbsolutePath
        let query = uri.Query
        let repoPrefix = $"/repos/{owner}/{name}"

        let body () =
            use document = JsonDocument.Parse(request.Request.Body |> Option.defaultValue "null")
            document.RootElement.Clone()

        let get (name: string) (element: JsonElement) =
            match element.TryGetProperty name with
            | true, value -> Some value
            | _ -> None

        let textOf (name: string) (element: JsonElement) =
            match get name element with
            | Some value when value.ValueKind = JsonValueKind.String -> value.GetString() |> Option.ofObj |> Option.defaultValue ""
            | _ -> ""

        let commitNode (id: string) =
            let commit = commits[id]

            obj
                [ "sha", str id
                  "tree", obj [ "sha", str (addTree commit.Tree) ]
                  "message", str commit.Message
                  "parents", arr (commit.Parents |> List.map (fun p -> obj [ "sha", str p ])) ]

        let queryValue key =
            query.TrimStart('?').Split('&', StringSplitOptions.RemoveEmptyEntries)
            |> Array.tryPick (fun pair ->
                match pair.Split('=', 2) with
                | [| k; v |] when k = key -> Some(Uri.UnescapeDataString v)
                | _ -> None)

        if not authorized then
            status 401 "Bad credentials"
        else
            match request.Request.Method, path with
            | HttpMethod.Get, "/user" ->
                match this.Identity with
                | AsUser(id, login, kind) -> json 200 (obj [ "id", num id; "login", str login; "type", str kind ])
                | AsInstallation -> status 403 "Resource not accessible by integration"
            | HttpMethod.Get, p when p = repoPrefix ->
                json
                    200
                    (obj
                        [ "id", num this.RepositoryId
                          "name", str name
                          "owner", obj [ "login", str owner ]
                          "visibility", str this.Visibility
                          "private", boolean (this.Visibility <> "public")
                          "archived", boolean this.Archived
                          "permissions", obj [ "pull", boolean true; "push", boolean this.CanPush ] ])
            | HttpMethod.Get, p when p.StartsWith(repoPrefix + "/branches/") ->
                let branch = p.Substring((repoPrefix + "/branches/").Length)
                if refs.ContainsKey branch then json 200 (obj [ "name", str branch; "protected", boolean (not this.Rules.IsEmpty) ]) else status 404 "Branch not found"
            | HttpMethod.Get, p when p.StartsWith(repoPrefix + "/rules/branches/") ->
                json 200 (arr (this.Rules |> List.map (fun r -> obj [ "type", str r ])))
            | HttpMethod.Get, p when p.StartsWith(repoPrefix + "/git/ref/heads/") ->
                let branch = p.Substring((repoPrefix + "/git/ref/heads/").Length)

                if refs.ContainsKey branch then
                    json 200 (obj [ "ref", str $"refs/heads/{branch}"; "object", obj [ "sha", str refs[branch]; "type", str "commit" ] ])
                else
                    status 404 "Not Found"
            | HttpMethod.Get, p when p.StartsWith(repoPrefix + "/git/commits/") ->
                let id = p.Substring((repoPrefix + "/git/commits/").Length)
                if commits.ContainsKey id then json 200 (commitNode id) else status 404 "Not Found"
            | HttpMethod.Get, p when p.StartsWith(repoPrefix + "/contents/") ->
                let file = p.Substring((repoPrefix + "/contents/").Length)
                let reference = queryValue "ref" |> Option.defaultValue "main"
                let commitId = if refs.ContainsKey reference then refs[reference] else reference

                if not (commits.ContainsKey commitId) then
                    status 404 "No commit found for the ref"
                else
                    let tree = commits[commitId].Tree

                    match Map.tryFind file tree with
                    | Some blob ->
                        let content = blobs[blob]

                        json
                            200
                            (obj
                                [ "type", str "file"
                                  "path", str file
                                  "sha", str blob
                                  "size", num (int64 (Encoding.UTF8.GetByteCount content))
                                  "encoding", str "base64"
                                  "content", str (Convert.ToBase64String(Encoding.UTF8.GetBytes content)) ])
                    | None ->
                        let prefix = file + "/"

                        let children =
                            tree
                            |> Map.toList
                            |> List.filter (fun (p, _) -> p.StartsWith prefix)
                            |> List.map (fun (p, blob) ->
                                let rest = p.Substring prefix.Length

                                match rest.IndexOf '/' with
                                | -1 -> rest, "file", blob, int64 (Encoding.UTF8.GetByteCount blobs[blob])
                                | slash -> rest.Substring(0, slash), "dir", sha (prefix + rest.Substring(0, slash)), 0L)
                            |> List.distinctBy (fun (n, _, _, _) -> n)

                        if children.IsEmpty then
                            status 404 "Not Found"
                        else
                            json
                                200
                                (arr (
                                    children
                                    |> List.map (fun (n, kind, s, size) ->
                                        obj [ "name", str n; "path", str (prefix + n); "type", str kind; "sha", str s; "size", num size ])
                                ))
            | HttpMethod.Post, p when p = repoPrefix + "/git/trees" ->
                let node = body ()
                let baseId = textOf "base_tree" node

                let baseTree =
                    match trees.TryGetValue baseId with
                    | true, tree -> Some tree
                    | _ -> None

                match baseTree with
                | None -> status 422 "base_tree is not a tree"
                | Some tree ->
                    let entries =
                        match get "tree" node with
                        | Some items -> items.EnumerateArray() |> List.ofSeq
                        | None -> []

                    let next =
                        entries
                        |> List.fold
                            (fun tree (entry: JsonElement) ->
                                let file = textOf "path" entry

                                match get "content" entry with
                                | Some content when content.ValueKind = JsonValueKind.String ->
                                    let text = content.GetString() |> Option.ofObj |> Option.defaultValue ""
                                    let blob = blobSha text
                                    blobs[blob] <- text
                                    Map.add file blob tree
                                | _ ->
                                    match get "sha" entry with
                                    | Some blob when blob.ValueKind = JsonValueKind.String -> Map.add file (textOf "sha" entry) tree
                                    | _ -> Map.remove file tree)
                            tree

                    json 201 (obj [ "sha", str (addTree next) ])
            | HttpMethod.Post, p when p = repoPrefix + "/git/commits" ->
                let node = body ()
                let treeId = textOf "tree" node

                let tree =
                    match trees.TryGetValue treeId with
                    | true, tree -> Some tree
                    | _ -> None

                match tree with
                | None -> status 422 "tree not found"
                | Some tree ->
                    let parents =
                        match get "parents" node with
                        | Some items -> items.EnumerateArray() |> Seq.map (fun p -> p.GetString() |> Option.ofObj |> Option.defaultValue "") |> List.ofSeq
                        | None -> []

                    let id = addCommit { Tree = tree; Parents = parents; Message = textOf "message" node }
                    json 201 (commitNode id)
            | HttpMethod.Patch, p when p.StartsWith(repoPrefix + "/git/refs/heads/") ->
                let branch = p.Substring((repoPrefix + "/git/refs/heads/").Length)
                let node = body ()
                let target = textOf "sha" node

                if this.ProtectedRefUpdate then
                    status 422 "Protected branch update failed for refs/heads/main."
                elif not (refs.ContainsKey branch) then
                    status 422 "Reference does not exist"
                elif not (commits.ContainsKey target) then
                    status 422 "Object does not exist"
                elif not (this.IsAncestor(refs[branch], target)) then
                    status 422 "Update is not a fast forward"
                else
                    refs[branch] <- target
                    json 200 (obj [ "ref", str $"refs/heads/{branch}"; "object", obj [ "sha", str target ] ])
            | HttpMethod.Get, p when p.StartsWith(repoPrefix + "/compare/") ->
                let spec = p.Substring((repoPrefix + "/compare/").Length)

                match spec.Split("...") with
                | [| baseId; headId |] when commits.ContainsKey baseId && commits.ContainsKey headId ->
                    let result =
                        if baseId = headId then "identical"
                        elif this.IsAncestor(baseId, headId) then "ahead"
                        elif this.IsAncestor(headId, baseId) then "behind"
                        else "diverged"

                    json 200 (obj [ "status", str result ])
                | _ -> status 404 "Not Found"
            | HttpMethod.Get, p when p = repoPrefix + "/commits" ->
                let start = queryValue "sha" |> Option.defaultValue "main"
                let start = if refs.ContainsKey start then refs[start] else start
                let limit = queryValue "per_page" |> Option.map int |> Option.defaultValue 30

                let rec walk (id: string) acc =
                    if List.length acc >= limit || not (commits.ContainsKey id) then
                        List.rev acc
                    else
                        let next = id :: acc

                        match commits[id].Parents with
                        | parent :: _ -> walk parent next
                        | [] -> List.rev next

                json
                    200
                    (arr (
                        walk start []
                        |> List.map (fun id -> obj [ "sha", str id; "commit", obj [ "message", str commits[id].Message ] ])
                    ))
            | _ -> status 404 "Not Found"

    /// Answers one request, applying any queued fault first.
    member this.Send(request: Authorized) : HttpOutcome =
        requests.Add request

        match faults |> Seq.tryFindIndex (fun (matches, _) -> matches request) with
        | Some index ->
            let _, fault = faults[index]
            faults.RemoveAt index

            match fault with
            | Answer(code, headers, text) -> HttpOutcome.Response(code, Http.normalizeHeaders headers, text)
            | Lose reason -> HttpOutcome.OutcomeUnknown reason
            | ApplyThenLose reason ->
                this.Respond request |> ignore
                HttpOutcome.OutcomeUnknown reason
            | FailNetwork -> HttpOutcome.Failed HttpFailure.Network
        | None -> this.Respond request

    /// A token provider answering with the server's valid token.
    member this.ValidToken() =
        AccessToken.create this.Token |> Result.mapError (fun _ -> TokenUnavailable.NoToken)

/// Matches requests by method and a URL fragment.
let request (httpMethod: HttpMethod) (fragment: string) (authorized: Authorized) =
    authorized.Request.Method = httpMethod && authorized.Request.Url.Contains fragment

/// Runs a conversation against the server with its valid token.
let run (server: Server) conversation =
    Conversation.simulate server.Send server.ValidToken [] conversation
