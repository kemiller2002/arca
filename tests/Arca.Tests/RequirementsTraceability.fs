/// Every Arca requirement is planned or delivered: a work item in the Praxis
/// queue that is not abandoned (captured, ready, active, blocked or complete)
/// names it, either directly (`ARCA-LOC-004`) or inside a range of the same
/// family (`ARCA-LOC-001..010`). A requirement that no such work item names
/// would silently fall out of the backlog. Completed items count because a
/// delivered slice still accounts for the requirements it delivered.
module Arca.Tests.RequirementsTraceability

open System.IO
open System.Text.Json
open System.Text.RegularExpressions

/// The repository root: the nearest directory above `start` that holds Arca.slnx.
let rec repositoryRoot (start: DirectoryInfo | null) =
    match start with
    | Null -> None
    | NonNull directory when File.Exists(Path.Combine(directory.FullName, "Arca.slnx")) -> Some directory.FullName
    | NonNull directory -> repositoryRoot directory.Parent

/// Requirement IDs declared in the requirements document (`**ARCA-XXX-NNN**`).
let declaredRequirements (document: string) =
    Regex.Matches(document, @"\*\*(ARCA-[A-Z]+-\d{3})\*\*")
    |> Seq.map _.Groups[1].Value
    |> Set.ofSeq

let private isAccountedFor (item: JsonElement) =
    item.GetProperty("status").GetString() <> "abandoned"

let private text (item: JsonElement) (name: string) =
    match item.TryGetProperty name with
    | true, value when value.ValueKind = JsonValueKind.String -> string (value.GetString())
    | _ -> ""

/// Title and description of every work item in a Praxis queue.json that is not abandoned.
let accountedWorkText (queueJson: string) =
    use queue = JsonDocument.Parse queueJson

    queue.RootElement.GetProperty("items").EnumerateArray()
    |> Seq.filter isAccountedFor
    |> Seq.map (fun item -> text item "title" + "\n" + text item "description")
    |> String.concat "\n"

/// Requirement IDs named in `work`, directly or through `FAMILY-NNN..MMM` ranges.
let namedRequirements (work: string) =
    let direct = Regex.Matches(work, @"ARCA-[A-Z]+-\d{3}") |> Seq.map _.Value

    let ranges =
        Regex.Matches(work, @"(ARCA-[A-Z]+)-(\d{3})\.\.(\d{3})")
        |> Seq.collect (fun m ->
            let family, low, high = m.Groups[1].Value, int m.Groups[2].Value, int m.Groups[3].Value
            seq { for n in low..high -> sprintf "%s-%03d" family n })

    Seq.append direct ranges |> Set.ofSeq

/// Declared requirements that no planned or delivered work item names.
let unplanned (document: string) (queueJson: string) =
    Set.difference (declaredRequirements document) (namedRequirements (accountedWorkText queueJson))
