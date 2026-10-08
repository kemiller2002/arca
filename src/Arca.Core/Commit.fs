namespace Arca

open System

/// The commit and audit format (ARCA-COMMIT-001..005).
///
/// One operation is one commit. Its message is deterministic:
///
/// ```text
/// chrona: start timer for A-1
///
/// Arca-Format: 1
/// Arca-Namespace: chrona
/// Arca-Dataset: org_1
/// Arca-Actor-Kind: agent
/// Arca-Actor: anthropic/claude-code
/// Arca-Provider-Identity: octocat
/// Arca-Execution: EXE-1
/// Arca-Correlation: req-7
/// Arca-Idempotency-Key: op-01J9Z8XK
/// ```
///
/// Optional trailers are omitted when absent; the order is fixed. The actor
/// kind is exactly what the application supplied, so an agent's operation is
/// never recorded as a human's (ARCA-COMMIT-003). The message holds
/// identifiers only: no tokens, and no business content beyond the summary the
/// application wrote (ARCA-COMMIT-004). Git history is supporting evidence;
/// Arca reads it only to reconcile unknown outcomes and to detect edits made
/// outside Arca, never to derive domain state (ARCA-COMMIT-005).
[<RequireQualifiedAccess>]
module Commit =

    /// The trailer format this Arca writes and reads.
    [<Literal>]
    let Format = 1

    /// The parsed trailers of an Arca commit.
    type Trailers =
        { Namespace: string
          Dataset: string option
          Actor: Actor
          ProviderIdentity: string option
          ExecutionId: string option
          CorrelationId: CorrelationId
          IdempotencyKey: IdempotencyKey }

    /// The commit message for an operation.
    let message (operation: Operation) =
        let metadata = operation.Metadata
        let ns = operation.Namespace
        let application = AppId.value ns.Application

        let trailers =
            [ Some("Arca-Format", string Format)
              Some("Arca-Namespace", application)
              ns.Dataset |> Option.map (fun dataset -> "Arca-Dataset", DatasetId.value dataset)
              Some("Arca-Actor-Kind", ActorKind.toWire metadata.Actor.Kind)
              Some("Arca-Actor", ActorId.value metadata.Actor.Id)
              metadata.ProviderIdentity |> Option.map (fun identity -> "Arca-Provider-Identity", identity)
              metadata.ExecutionId |> Option.map (fun execution -> "Arca-Execution", execution)
              Some("Arca-Correlation", CorrelationId.value metadata.CorrelationId)
              Some("Arca-Idempotency-Key", IdempotencyKey.value metadata.IdempotencyKey) ]
            |> List.choose id
            |> List.map (fun (key, value) -> $"{key}: {value}")

        String.concat "\n" ([ $"{application}: {metadata.Summary}"; "" ] @ trailers) + "\n"

    /// The trailers of a commit message, when it is an Arca commit. A commit
    /// without them, or with malformed ones, was not written by Arca: an
    /// external edit (ARCA-INT-002).
    let trailers (message: string) =
        let lines = message.Replace("\r\n", "\n").Split('\n')

        let values =
            lines
            |> Array.choose (fun line ->
                match line.IndexOf ": " with
                | index when index > 0 && line.StartsWith "Arca-" -> Some(line.Substring(0, index), line.Substring(index + 2))
                | _ -> None)
            |> Array.toList

        let single key =
            match values |> List.filter (fun (name, _) -> name = key) with
            | [ _, value ] -> Some value
            | _ -> None

        let optional key =
            match values |> List.filter (fun (name, _) -> name = key) with
            | [] -> Ok None
            | [ _, value ] -> Ok(Some value)
            | _ -> Error()

        match
            single "Arca-Format",
            single "Arca-Namespace",
            single "Arca-Actor-Kind" |> Option.bind ActorKind.ofWire,
            single "Arca-Actor" |> Option.bind (ActorId.create >> Result.toOption),
            single "Arca-Correlation" |> Option.bind (CorrelationId.create >> Result.toOption),
            single "Arca-Idempotency-Key" |> Option.bind (IdempotencyKey.create >> Result.toOption)
        with
        | Some format, Some ns, Some kind, Some actor, Some correlation, Some key when format = string Format ->
            match optional "Arca-Dataset", optional "Arca-Provider-Identity", optional "Arca-Execution" with
            | Ok dataset, Ok identity, Ok execution ->
                Some
                    { Namespace = ns
                      Dataset = dataset
                      Actor = { Kind = kind; Id = actor }
                      ProviderIdentity = identity
                      ExecutionId = execution
                      CorrelationId = correlation
                      IdempotencyKey = key }
            | _ -> None
        | _ -> None

    /// True when a commit message carries this operation's idempotency key:
    /// the operation already landed (ARCA-OUT-002).
    let carriesKey (key: IdempotencyKey) (message: string) =
        match trailers message with
        | Some found -> found.IdempotencyKey = key
        | None -> false
