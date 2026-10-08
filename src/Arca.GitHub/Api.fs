namespace Arca.GitHub

open System
open System.Text.Json
open Aegis.Integration.GitHub
open Arca

/// How the adapter reaches GitHub for one data location.
type GitHubConfig =
    { /// The REST API root, `https://api.github.com` unless GitHub Enterprise.
      ApiBase: string
      Location: DataLocation
      /// How long the host waits on one request.
      TimeoutMs: int }

/// Configuration defaults.
[<RequireQualifiedAccess>]
module GitHubConfig =

    /// The public GitHub REST API.
    [<Literal>]
    let PublicApi = "https://api.github.com"

    /// A configuration for a location on github.com.
    let create location =
        { ApiBase = PublicApi
          Location = location
          TimeoutMs = 30000 }

/// Why a GitHub call produced no usable response.
[<RequireQualifiedAccess>]
type CallFailure =
    /// An operational failure, classified through Aegis (ARCA-ARCH-007).
    | Failed of GitHubFailure.T
    /// The request may have reached GitHub; its effect is unknown (ARCA-OUT-001).
    | Unknown of UnknownReason
    | Cancelled

/// A response GitHub sent.
type Response =
    { Status: int
      Headers: Map<string, string>
      Body: string }

/// Request construction and response reading for the GitHub REST API.
[<RequireQualifiedAccess>]
module Api =

    /// GitHub's REST API version this adapter is written against.
    [<Literal>]
    let Version = "2022-11-28"

    let private escapeSegments (text: string) =
        text.Split '/' |> Array.map Uri.EscapeDataString |> String.concat "/"

    /// `/repos/{owner}/{repo}` for the configured repository.
    let repositoryPath (config: GitHubConfig) =
        let repository = config.Location.Repository
        $"/repos/{Uri.EscapeDataString repository.Owner}/{Uri.EscapeDataString repository.Name}"

    /// The configured branch, escaped segment by segment for a URL path.
    let branchPath (config: GitHubConfig) =
        escapeSegments (BranchName.value config.Location.Branch)

    /// A repository file path, escaped segment by segment.
    let filePath (path: string) = escapeSegments path

    /// A request to the API with GitHub's standard headers.
    let request (config: GitHubConfig) httpMethod (path: string) (body: string option) =
        { Method = httpMethod
          Url = config.ApiBase.TrimEnd('/') + path
          Headers =
            [ "Accept", "application/vnd.github+json"
              "X-GitHub-Api-Version", Version ]
            @ (body |> Option.map (fun _ -> "Content-Type", "application/json") |> Option.toList)
          Body = body
          TimeoutMs = config.TimeoutMs
          ResponseHeaders = Http.standardResponseHeaders }

    /// The response, or why there is none. A GitHub error status is still a
    /// response; callers decide which statuses they expect.
    let response (outcome: HttpOutcome) =
        match outcome with
        | HttpOutcome.Response(status, headers, body) ->
            Ok
                { Status = status
                  Headers = headers
                  Body = body }
        | HttpOutcome.Failed _ ->
            Error(
                CallFailure.Failed(
                    Faults.ofOutcome "" outcome
                    |> Option.defaultValue (GitHubFailure.InvalidResponse "request failed")
                )
            )
        | HttpOutcome.Cancelled -> Error CallFailure.Cancelled
        | HttpOutcome.OutcomeUnknown reason -> Error(CallFailure.Unknown reason)

    /// The Aegis classification of an unexpected status.
    let unexpected (config: GitHubConfig) (response: Response) =
        let outcome = HttpOutcome.Response(response.Status, response.Headers, response.Body)

        CallFailure.Failed(
            Faults.ofOutcome (string config.Location.Repository) outcome
            |> Option.defaultValue (GitHubFailure.InvalidResponse $"unexpected status {response.Status}")
        )

    /// Reads a JSON body with `read`; a body that is not JSON, or lacks what
    /// `read` needs, is an invalid response, never an exception.
    let json (read: JsonElement -> 'a option) (body: string) =
        try
            use document = JsonDocument.Parse body

            match read document.RootElement with
            | Some value -> Ok value
            | None -> Error(CallFailure.Failed(GitHubFailure.InvalidResponse "response lacks expected fields"))
        with :? JsonException ->
            Error(CallFailure.Failed(GitHubFailure.InvalidResponse "response is not JSON"))

    /// A string property.
    let text (name: string) (element: JsonElement) =
        match element.ValueKind with
        | JsonValueKind.Object ->
            match element.TryGetProperty name with
            | true, value when value.ValueKind = JsonValueKind.String -> Option.ofObj (value.GetString())
            | _ -> None
        | _ -> None

    /// A boolean property.
    let flag (name: string) (element: JsonElement) =
        match element.ValueKind with
        | JsonValueKind.Object ->
            match element.TryGetProperty name with
            | true, value when value.ValueKind = JsonValueKind.True -> Some true
            | true, value when value.ValueKind = JsonValueKind.False -> Some false
            | _ -> None
        | _ -> None

    /// An integer property, as text (GitHub ids exceed 32 bits).
    let integer (name: string) (element: JsonElement) =
        match element.ValueKind with
        | JsonValueKind.Object ->
            match element.TryGetProperty name with
            | true, value when value.ValueKind = JsonValueKind.Number ->
                match value.TryGetInt64() with
                | true, number -> Some number
                | _ -> None
            | _ -> None
        | _ -> None

    /// An object property.
    let child (name: string) (element: JsonElement) =
        match element.ValueKind with
        | JsonValueKind.Object ->
            match element.TryGetProperty name with
            | true, value when value.ValueKind = JsonValueKind.Object -> Some value
            | _ -> None
        | _ -> None

    /// The items of an array, or None when the element is not an array.
    let items (element: JsonElement) =
        match element.ValueKind with
        | JsonValueKind.Array -> Some(element.EnumerateArray() |> List.ofSeq)
        | _ -> None

    /// Sends a request with a credential and returns the response or the failure.
    let call credential request =
        Conversation.send (Some credential) request |> Conversation.map response
