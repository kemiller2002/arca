/// A Limen engine for the offline queue's multi-tab verification (WI-0024,
/// Limen LCP-059). Each tab of the page runs Arca's localStorage queue store
/// for one namespace: localStorage through Limen Core's Storage effect, and
/// ownership through the coordination pack's Web Lock `acquire`. Several tabs
/// of one origin share the localStorage, so the browser test drives two
/// tabs and checks that no acknowledged entry is lost.
///
/// The engine describes effects and reads their results; it has no browser
/// or interop code.
module Arca.Browser.QueueEngine

open System
open System.IO
open System.Text
open System.Text.Json
open Arca
open Arca.GitHub

[<Literal>]
let CoordinationUnit = "limen.coordination"

/// The coordination pack's contract identity (`limen.coordination` v1, Limen 0.7.1).
[<Literal>]
let CoordinationFingerprint = "sha256:510dfbcd2f3f7966b842d518d209a511ada3ffb30132853f291e9d5fa8342684"

/// An effect the queue asked of the browser.
type Effect =
    | Storage of LocalStorageRequest
    | Lock of QueueLockRequest

/// What the browser answered.
type Answer =
    | Stored of LocalStorageOutcome
    | Locked of QueueLockOutcome

/// What the page shows.
[<NoEquality; NoComparison>]
type Page =
    { Ownership: string
      Store: QueueStore option
      Queue: OfflineQueue
      Last: string
      /// The tag this tab's entries carry, so the test can tell tabs apart.
      Tag: string
      Coordination: bool }

let private ok result =
    match result with
    | Ok value -> value
    | Error _ -> invalidOp "verification fixture"

let private chrona =
    Namespace.ofApplication
        { Application = AppId.create "chrona" |> ok
          Environment = { Kind = EnvironmentKind.Test; Name = "browser-verification" }
          Location = DataLocation.create "verify-owner" "verify-data" "main" "apps" |> ok }
    |> ok

let private empty = OfflineQueue.create OfflinePolicy.QueueWrites

let initial (tag: string) =
    { Ownership = "none"
      Store = None
      Queue = empty
      Last = ""
      Tag = tag
      Coordination = false }

let private operation (key: string) =
    Operation.create
        chrona
        { Summary = $"note {key}"
          Actor = { Kind = ActorKind.Human; Id = ActorId.create "browser" |> ok }
          ProviderIdentity = None
          ExecutionId = None
          CorrelationId = CorrelationId.create $"c-{key}" |> ok
          IdempotencyKey = IdempotencyKey.create key |> ok }
        [ Change.Create(RelativePath.parse $"notes/{key}.json" |> ok, "{}") ]
    |> ok

let private failureText =
    function
    | QueueStoreFailure.Unavailable -> "unavailable"
    | QueueStoreFailure.QuotaExceeded _ -> "quota-exceeded"
    | QueueStoreFailure.Corrupt _ -> "corrupt"

/// Loads the queue into the page once a store is held.
let private load (page: Page) (store: QueueStore) =
    async {
        match! store.Load() with
        | Ok kept ->
            let queue = kept |> Option.map OfflineQueue.recover |> Option.defaultValue empty
            return { page with Store = Some store; Queue = queue; Last = "loaded" }
        | Error failure -> return { page with Store = None; Last = $"load {failureText failure}" }
    }

/// What one page event does, given the browser's executors.
let onEvent (storage: LocalStorageRequest -> Async<LocalStorageOutcome>) (lock: QueueLockRequest -> Async<QueueLockOutcome>) (page: Page) (name: string) =
    async {
        match name, page.Store with
        | "own", None ->
            match! LocalStorageQueue.own lock storage LocalStorageQueue.DefaultBudget chrona with
            | QueueOwnership.Owned store -> return! load { page with Ownership = "owned" } store
            | QueueOwnership.OwnedElsewhere -> return { page with Ownership = "owned-elsewhere" }
            | QueueOwnership.OwnershipUnsupported -> return { page with Ownership = "unsupported" }
        | "open", None ->
            // The fenced store without a lock, as 0.2.0's callers use it.
            return! load { page with Ownership = "unlocked" } (LocalStorageQueue.store storage LocalStorageQueue.DefaultBudget chrona)
        | "enqueue", _ ->
            let key = $"{page.Tag}-{page.Queue.NextSequence}"

            match OfflineQueue.enqueue DateTimeOffset.UnixEpoch (operation key) page.Queue with
            | Ok(queue, _) -> return { page with Queue = queue; Last = $"enqueued {key}" }
            | Error _ -> return { page with Last = "enqueue refused" }
        | "save", Some store ->
            match! store.Save page.Queue with
            | Ok() -> return { page with Last = "saved" }
            | Error failure -> return { page with Last = $"save {failureText failure}" }
        | _ -> return page
    }

// ---------------------------------------------------------------------------
// Limen protocol.
// ---------------------------------------------------------------------------

let private text (name: string) (element: JsonElement) =
    match element.TryGetProperty name with
    | true, value when value.ValueKind = JsonValueKind.String -> value.GetString() |> Option.ofObj
    | _ -> None

let private child (name: string) (element: JsonElement) =
    match element.TryGetProperty name with
    | true, value when value.ValueKind = JsonValueKind.Object -> Some value
    | _ -> None

/// A Storage result as Arca's LocalStorageOutcome.
let storageOutcome (outcome: JsonElement) =
    match text "kind" outcome, text "reason" outcome with
    | Some "Success", _ -> LocalStorageOutcome.Success(text "value" outcome)
    | Some "Failure", Some "quota-exceeded" -> LocalStorageOutcome.Failure LocalStorageFailure.QuotaExceeded
    | _ -> LocalStorageOutcome.Failure LocalStorageFailure.Unavailable

/// A coordination `acquire` result as Arca's QueueLockOutcome. A request the
/// kernel did not execute (no pack) is Unsupported.
let lockOutcome (outcome: JsonElement) =
    match text "kind" outcome with
    | Some "Completed" ->
        match child "result" outcome |> Option.bind (text "kind") with
        | Some "Acquired" -> QueueLockOutcome.Acquired
        | Some "Busy" -> QueueLockOutcome.Busy
        | _ -> QueueLockOutcome.Unsupported
    | _ -> QueueLockOutcome.Unsupported

let private writeEffect (writer: Utf8JsonWriter) (correlation: string) (effect: Effect) =
    writer.WriteStartObject()

    match effect with
    | Storage request ->
        writer.WriteString("kind", "Storage")
        writer.WriteString("correlationId", correlation)

        match request with
        | LocalStorageRequest.Get key ->
            writer.WriteString("operation", "get")
            writer.WriteString("key", key)
        | LocalStorageRequest.Set(key, value) ->
            writer.WriteString("operation", "set")
            writer.WriteString("key", key)
            writer.WriteString("value", value)
        | LocalStorageRequest.Remove key ->
            writer.WriteString("operation", "remove")
            writer.WriteString("key", key)
    | Lock(QueueLockRequest.Acquire name) ->
        writer.WriteString("kind", "Capability")
        writer.WriteString("correlationId", correlation)
        writer.WriteString("capability", CoordinationUnit)
        writer.WriteNumber("version", 1)
        writer.WritePropertyName "request"
        writer.WriteStartObject()
        writer.WriteString("operation", "acquire")
        writer.WriteString("name", name)
        writer.WriteString("mode", "exclusive")
        writer.WriteBoolean("wait", false)
        writer.WriteBoolean("steal", false)
        writer.WriteEndObject()

    writer.WriteEndObject()

let private view (page: Page) =
    [ "ownership", page.Ownership
      "entries", string page.Queue.Entries.Length
      "keys", (page.Queue.Entries |> List.map _.Operation.IdempotencyKey |> String.concat ",")
      "last", page.Last ]

/// The engine's reply: its view, the effects it asks for, and an optional handshake.
let reply (page: Page) (effects: (string * Effect) list) (handshake: (Utf8JsonWriter -> unit) option) =
    use stream = new MemoryStream()

    do
        use writer = new Utf8JsonWriter(stream)
        writer.WriteStartObject()
        writer.WritePropertyName "view"
        writer.WriteStartObject()

        for name, value in view page do
            writer.WriteString(name, value)

        writer.WriteEndObject()
        writer.WritePropertyName "effects"
        writer.WriteStartArray()

        for correlation, effect in effects do
            writeEffect writer correlation effect

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

/// Accepts Limen Core (protocol 1.3 or newer, as the other verification
/// engine) and selects the coordination pack when the kernel offers it.
let answer (offer: JsonElement option) : Result<(Utf8JsonWriter -> unit) * bool, Utf8JsonWriter -> unit> =
    let number (name: string) (element: JsonElement) =
        match element.TryGetProperty name with
        | true, value when value.ValueKind = JsonValueKind.Number -> Some(value.GetInt32())
        | _ -> None

    let protocol = offer |> Option.bind (child "protocol")
    let contract = offer |> Option.bind (child "contract")

    let coordination =
        match offer |> Option.map (fun o -> o.TryGetProperty "capabilities") with
        | Some(true, list) when list.ValueKind = JsonValueKind.Array ->
            list.EnumerateArray()
            |> Seq.exists (fun item -> text "id" item = Some CoordinationUnit && number "version" item = Some 1 && text "fingerprint" item = Some CoordinationFingerprint)
        | _ -> false

    match protocol |> Option.bind (number "major"), protocol |> Option.bind (number "minor"), contract with
    | Some 1, Some minor, Some offeredContract when minor >= Engine.MinimumMinor && text "unit" offeredContract = Some Engine.CoreUnit && text "fingerprint" offeredContract = Some Engine.CoreFingerprint ->
        let raw = offeredContract.GetRawText()

        Ok(
            (fun (writer: Utf8JsonWriter) ->
                writer.WriteStartObject()
                writer.WriteString("kind", "Accepted")
                writer.WritePropertyName "protocol"
                writer.WriteStartObject()
                writer.WriteNumber("major", 1)
                writer.WriteNumber("minor", min minor Engine.NewestMinor)
                writer.WriteEndObject()
                writer.WritePropertyName "contract"
                use parsed = JsonDocument.Parse raw
                parsed.RootElement.WriteTo writer
                writer.WritePropertyName "capabilities"
                writer.WriteStartArray()

                if coordination then
                    writer.WriteStartObject()
                    writer.WriteString("id", CoordinationUnit)
                    writer.WriteNumber("version", 1)
                    writer.WriteString("fingerprint", CoordinationFingerprint)
                    writer.WriteEndObject()

                writer.WriteEndArray()
                writer.WriteEndObject()),
            coordination
        )
    | _ ->
        Error(fun (writer: Utf8JsonWriter) ->
            writer.WriteStartObject()
            writer.WriteString("kind", "Rejected")
            writer.WritePropertyName "reason"
            writer.WriteStartObject()
            writer.WriteString("kind", "ProtocolUnsupported")
            writer.WriteEndObject()
            writer.WriteEndObject())

/// The one stateful edge: the page and the work waiting on the browser,
/// between WASM calls. The browser runtime has one thread, so an event's
/// work runs synchronously until it needs the browser, parks under a fresh
/// correlation id, and resumes when the kernel answers.
module Dispatch =
    let mutable private page = initial (Guid.NewGuid().ToString("N").Substring(0, 8))
    let mutable private waiting: Map<string, Answer -> unit> = Map.empty
    let mutable private outbox: (string * Effect) list = []
    let mutable private sequence = 0

    let private call (effect: Effect) =
        Async.FromContinuations(fun (resume, _, _) ->
            sequence <- sequence + 1
            let correlation = $"q{sequence}"
            waiting <- waiting |> Map.add correlation resume
            outbox <- outbox @ [ correlation, effect ])

    let private storage request =
        async {
            match! call (Storage request) with
            | Stored outcome -> return outcome
            | Locked _ -> return LocalStorageOutcome.Failure LocalStorageFailure.Unavailable
        }

    let private lock request =
        async {
            if not page.Coordination then
                return QueueLockOutcome.Unsupported
            else
                match! call (Lock request) with
                | Locked outcome -> return outcome
                | Stored _ -> return QueueLockOutcome.Unsupported
        }

    let private start (work: Async<Page>) =
        Async.StartImmediate(
            async {
                let! next = work
                page <- next
            }
        )

    let private drain handshake =
        let effects = outbox
        outbox <- []
        reply page effects handshake

    let private resume (correlation: string) (answer: Answer) =
        match waiting |> Map.tryFind correlation with
        | Some continuation ->
            waiting <- waiting |> Map.remove correlation
            continuation answer
        | None -> ()

    /// One kernel message in, the engine's reply out.
    let handle (messageJson: string) =
        use document = JsonDocument.Parse messageJson
        let message = document.RootElement

        match text "kind" message with
        | Some "Initialize" ->
            match answer (child "handshake" message) with
            | Ok(accepted, coordination) ->
                page <- { page with Coordination = coordination }
                drain (Some accepted)
            | Error rejected -> drain (Some rejected)
        | Some "Event" ->
            match child "event" message |> Option.bind (text "name") with
            | Some name when waiting.IsEmpty -> start (onEvent storage lock page name)
            | _ -> ()

            drain None
        | Some "EffectResult" ->
            match child "result" message with
            | Some result ->
                match text "kind" result, text "correlationId" result, child "outcome" result with
                | Some "StorageResult", Some correlation, Some outcome -> resume correlation (Stored(storageOutcome outcome))
                | Some "CapabilityResult", Some correlation, Some outcome -> resume correlation (Locked(lockOutcome outcome))
                | _ -> ()
            | None -> ()

            drain None
        | _ -> drain None
