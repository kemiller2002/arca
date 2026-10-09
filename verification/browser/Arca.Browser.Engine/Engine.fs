/// A Limen engine that runs Arca's GitHub adapter in the browser
/// (ARCA-TEST-003). The kernel executes every HTTP request through its Http
/// effect (browser fetch), so this proves the adapter, the core, the
/// canonical encoding and Git's blob hashing on the real .NET WebAssembly
/// runtime, behind Limen's boundary, not only on the server runtime.
///
/// The engine holds application authority only: it describes effects and
/// reads their results. It has no browser, network or interop code.
module Arca.Browser.Engine

open System
open System.IO
open System.Text
open System.Text.Json
open Arca
open Arca.GitHub

/// Limen Core's contract identity (`limen.core` v1, Limen 0.7.1).
[<Literal>]
let CoreUnit = "limen.core"

[<Literal>]
let CoreFingerprint = "sha256:2d5e16b7111fc78a319706b9927e4523cfcc519b7a2c9352ca8283ba32d6b71c"

/// Text responses and response headers are Limen protocol 1.3; OutcomeUnknown
/// for a lost connection is 1.4.
[<Literal>]
let MinimumMinor = 3

[<Literal>]
let NewestMinor = 4

type Step = Conversation<Result<CommitReceipt * ReadOutcome, StorageFailure> * GitHubSession>

/// The engine's state.
[<NoEquality; NoComparison>]
type State =
    | Idle
    | Awaiting of correlation: string * next: (HttpOutcome -> Step) * sent: int
    | Finished of summary: (string * string) list
    | Incompatible of reason: string

let private ok result =
    match result with
    | Ok value -> value
    | Error _ -> invalidOp "verification fixture"

let private location = DataLocation.create "verify-owner" "verify-data" "main" "apps" |> ok

let private chrona =
    Namespace.ofApplication
        { Application = AppId.create "chrona" |> ok
          Environment = { Kind = EnvironmentKind.Test; Name = "browser-verification" }
          Location = location }
    |> ok

let private recordText =
    let record =
        { Id = RecordId.create "A-01JBROWSER" |> ok
          Type = RecordType.create "chrona.activity" |> ok
          SchemaVersion = 1
          Mutability = Mutability.Mutable
          Body = Json.objectOf [ "minutes", Json.Number 90m; "note", Json.String "verified in WebAssembly ✓" ] }

    Record.encode Record.DefaultMaxBytes record |> ok

let private recordPath = RelativePath.parse "records/chrona.activity/A-01JBROWSER.json" |> ok

let private operation =
    Operation.create
        chrona
        { Summary = "record time in the browser"
          Actor = { Kind = ActorKind.Agent; Id = ActorId.create "arca/browser-verification" |> ok }
          ProviderIdentity = None
          ExecutionId = None
          CorrelationId = CorrelationId.create "browser-1" |> ok
          IdempotencyKey = IdempotencyKey.create "browser-verification-0001" |> ok }
        [ Change.Create(recordPath, recordText) ]
    |> ok

/// The scenario: commit one record as one atomic commit, then read it back.
let private scenario: Op<CommitReceipt * ReadOutcome> =
    op {
        let! receipt = GitHubStorage.commit operation
        let! read = GitHubStorage.read chrona recordPath
        return receipt, read
    }

/// The test's token. Not a credential: the page's simulated GitHub accepts it.
let private token =
    AccessToken.create "arca-browser-verification-token" |> Result.defaultWith (fun _ -> invalidOp "token")

let private failureText =
    function
    | StorageFailure.Refused _ -> "Refused"
    | StorageFailure.Conflicted _ -> "Conflicted"
    | StorageFailure.OutcomeUnknown _ -> "OutcomeUnknown"
    | StorageFailure.ObjectTooLarge _ -> "ObjectTooLarge"
    | StorageFailure.StaleChangeToken _ -> "StaleChangeToken"
    | StorageFailure.StaleNamespaceToken _ -> "StaleNamespaceToken"
    | StorageFailure.RateLimited _ -> "RateLimited"
    | StorageFailure.WrongLocation _ -> "WrongLocation"
    | StorageFailure.IntegrityRefused(path, _) -> $"IntegrityRefused {path}"
    | StorageFailure.ProviderFailed(code, _, detail) -> $"ProviderFailed {code}: {detail}"

let private summarize (result: Result<CommitReceipt * ReadOutcome, StorageFailure>) =
    match result with
    | Error failure -> [ "status", "failed"; "error", failureText failure ]
    | Ok(receipt, read) ->
        let (ChangeToken commit) = receipt.ChangeToken

        let written =
            match receipt.Revisions |> Map.tryFind (RelativePath.render recordPath) with
            | Some(Some(Revision revision)) -> revision
            | _ -> ""

        match read with
        | ReadOutcome.Found stored ->
            let (Revision readRevision) = stored.Revision

            [ "status", "done"
              "commit", commit
              "writtenRevision", written
              "readRevision", readRevision
              "revisionsMatch", (if written = readRevision then "yes" else "no")
              "contentMatches", (if stored.Content = recordText then "yes" else "no")
              "recordDecodes",
              (match Record.decode Record.DefaultMaxBytes stored.Content with
               | Ok _ -> "yes"
               | Error _ -> "no") ]
        | ReadOutcome.Absent -> [ "status", "failed"; "error", "the committed record is absent" ]
        | ReadOutcome.Erased _ -> [ "status", "failed"; "error", "the committed record reads as erased" ]

/// Runs the conversation until it needs the kernel: answers token requests
/// with the test token and does not wait (the scenario arranges no back-off).
let rec private advance (sent: int) (step: Step) : State * (string * Authorized) option =
    match step with
    | Done(result, _) -> Finished(summarize result), None
    | RequestToken next -> advance sent (next (Ok token))
    | Wait(_, next) -> advance sent (next ())
    | Send(authorized, next) ->
        let correlation = $"h{sent + 1}"
        Awaiting(correlation, next, sent + 1), Some(correlation, authorized)

// ---------------------------------------------------------------------------
// Limen protocol: kernel messages in, the engine's reply out.
// ---------------------------------------------------------------------------

let private text (name: string) (element: JsonElement) =
    match element.TryGetProperty name with
    | true, value when value.ValueKind = JsonValueKind.String -> value.GetString() |> Option.ofObj
    | _ -> None

let private child (name: string) (element: JsonElement) =
    match element.TryGetProperty name with
    | true, value when value.ValueKind = JsonValueKind.Object -> Some value
    | _ -> None

let private number (name: string) (element: JsonElement) =
    match element.TryGetProperty name with
    | true, value when value.ValueKind = JsonValueKind.Number -> Some(value.GetInt32())
    | _ -> None

/// The kernel's outcome of one Http effect, as Arca's HttpOutcome.
let outcomeOf (outcome: JsonElement) =
    match text "kind" outcome with
    | Some "Success" ->
        let headers =
            match child "headers" outcome with
            | Some values -> values.EnumerateObject() |> Seq.map (fun header -> header.Name, header.Value.GetString() |> Option.ofObj |> Option.defaultValue "") |> Http.normalizeHeaders
            | None -> Map.empty

        let body =
            match outcome.TryGetProperty "body" with
            | true, value when value.ValueKind = JsonValueKind.String -> value.GetString() |> Option.ofObj |> Option.defaultValue ""
            | true, value -> value.GetRawText()
            | _ -> ""

        HttpOutcome.Response(number "status" outcome |> Option.defaultValue 0, headers, body)
    | Some "Failure" ->
        match text "reason" outcome with
        | Some "network" -> HttpOutcome.Failed HttpFailure.Network
        | Some "aborted" -> HttpOutcome.Failed HttpFailure.Aborted
        | Some "too-large" -> HttpOutcome.Failed HttpFailure.TooLarge
        | _ -> HttpOutcome.Failed HttpFailure.InvalidResponse
    | Some "Cancelled" -> HttpOutcome.Cancelled
    | Some "OutcomeUnknown" ->
        match text "reason" outcome with
        | Some "connection-lost" -> HttpOutcome.OutcomeUnknown UnknownReason.ConnectionLost
        | _ -> HttpOutcome.OutcomeUnknown UnknownReason.TimeoutAfterDispatch
    | _ -> HttpOutcome.Failed HttpFailure.InvalidResponse

let private methodName =
    function
    | HttpMethod.Get -> "GET"
    | HttpMethod.Post -> "POST"
    | HttpMethod.Patch -> "PATCH"
    | HttpMethod.Put -> "PUT"
    | HttpMethod.Delete -> "DELETE"

let private writeHttp (writer: Utf8JsonWriter) (correlation: string) (authorized: Authorized) =
    let request = authorized.Request
    writer.WriteStartObject()
    writer.WriteString("kind", "Http")
    writer.WriteString("correlationId", correlation)
    writer.WriteString("method", methodName request.Method)
    writer.WriteString("url", request.Url)
    writer.WritePropertyName "headers"
    writer.WriteStartObject()

    for name, value in request.Headers do
        writer.WriteString(name, value)

    match authorized.Credential with
    | Some credential ->
        let name, value = AccessToken.authorization credential
        writer.WriteString(name, value)
    | None -> ()

    writer.WriteEndObject()

    match request.Body with
    | Some body -> writer.WriteString("body", body)
    | None -> ()

    writer.WriteNumber("timeoutMs", request.TimeoutMs)
    writer.WriteString("response", "text")
    writer.WritePropertyName "responseHeaders"
    writer.WriteStartArray()

    for name in request.ResponseHeaders do
        writer.WriteStringValue name

    writer.WriteEndArray()
    writer.WriteEndObject()

/// Every key the page binds; the kernel requires each in every view.
let private viewKeys =
    [ "status"; "requests"; "commit"; "writtenRevision"; "readRevision"; "revisionsMatch"; "contentMatches"; "recordDecodes"; "error" ]

let private view (state: State) =
    let values =
        match state with
        | Idle -> [ "status", "idle" ]
        | Awaiting(_, _, sent) -> [ "status", "running"; "requests", string sent ]
        | Finished summary -> summary
        | Incompatible reason -> [ "status", "incompatible"; "error", reason ]
        |> Map.ofList

    viewKeys |> List.map (fun key -> key, values |> Map.tryFind key |> Option.defaultValue "")

let private reply (state: State) (effect: (string * Authorized) option) (handshake: (Utf8JsonWriter -> unit) option) =
    use stream = new MemoryStream()

    do
        use writer = new Utf8JsonWriter(stream)
        writer.WriteStartObject()
        writer.WritePropertyName "view"
        writer.WriteStartObject()

        for name, value in view state do
            writer.WriteString(name, value)

        writer.WriteEndObject()
        writer.WritePropertyName "effects"
        writer.WriteStartArray()

        match effect with
        | Some(correlation, authorized) -> writeHttp writer correlation authorized
        | None -> ()

        writer.WriteEndArray()
        writer.WritePropertyName "cancellations"
        writer.WriteStartArray()
        writer.WriteEndArray()

        match handshake with
        | Some write ->
            writer.WritePropertyName "handshake"
            write writer
        | None -> ()

        writer.WriteEndObject()

    Encoding.UTF8.GetString(stream.ToArray())

/// The engine's answer to the kernel's handshake offer: Core only, protocol 1.3 or newer.
let private answer (offer: JsonElement option) =
    let protocol = offer |> Option.bind (child "protocol")
    let contract = offer |> Option.bind (child "contract")
    let major = protocol |> Option.bind (number "major")
    let minor = protocol |> Option.bind (number "minor")

    let coreOffered =
        contract |> Option.bind (text "unit") = Some CoreUnit
        && contract |> Option.bind (number "version") = Some 1
        && contract |> Option.bind (text "fingerprint") = Some CoreFingerprint

    match major, minor, contract with
    | Some 1, Some offered, Some offeredContract when offered >= MinimumMinor && coreOffered ->
        let raw = offeredContract.GetRawText()

        Ok(fun (writer: Utf8JsonWriter) ->
            writer.WriteStartObject()
            writer.WriteString("kind", "Accepted")
            writer.WritePropertyName "protocol"
            writer.WriteStartObject()
            writer.WriteNumber("major", 1)
            writer.WriteNumber("minor", min offered NewestMinor)
            writer.WriteEndObject()
            writer.WritePropertyName "contract"
            use parsed = JsonDocument.Parse raw
            parsed.RootElement.WriteTo writer
            writer.WritePropertyName "capabilities"
            writer.WriteStartArray()
            writer.WriteEndArray()
            writer.WriteEndObject())
    | _ ->
        Error(fun (writer: Utf8JsonWriter) ->
            writer.WriteStartObject()
            writer.WriteString("kind", "Rejected")
            writer.WritePropertyName "reason"
            writer.WriteStartObject()
            writer.WriteString("kind", "ProtocolUnsupported")
            writer.WriteEndObject()
            writer.WriteEndObject())

/// One transition: the next state and the reply to the kernel.
let handle (state: State) (messageJson: string) : State * string =
    use document = JsonDocument.Parse messageJson
    let message = document.RootElement

    match text "kind" message, state with
    | Some "Initialize", _ ->
        match answer (child "handshake" message) with
        | Ok accepted -> Idle, reply Idle None (Some accepted)
        | Error rejected ->
            let next = Incompatible "the kernel does not offer Limen Core with protocol 1.3 or newer"
            next, reply next None (Some rejected)
    | Some "Event", (Idle | Finished _) when (child "event" message |> Option.bind (text "name")) = Some "run" ->
        let conversation = scenario (Session.create (GitHubConfig.create location))
        let next, effect = advance 0 conversation
        next, reply next effect None
    | Some "EffectResult", Awaiting(correlation, continue', sent) ->
        match child "result" message with
        | Some result when text "correlationId" result = Some correlation ->
            let outcome = child "outcome" result |> Option.map outcomeOf |> Option.defaultValue (HttpOutcome.Failed HttpFailure.InvalidResponse)
            let next, effect = advance sent (continue' outcome)
            next, reply next effect None
        | _ -> state, reply state None None
    | _ -> state, reply state None None

/// The one stateful edge: the engine's state between WASM calls.
module Dispatch =
    let mutable private state = Idle

    /// One kernel message in, the engine's reply out.
    let handle (messageJson: string) =
        let next, response = handle state messageJson
        state <- next
        response
