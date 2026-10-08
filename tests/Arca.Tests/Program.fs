// Dependency-free test runner: every check prints PASS/FAIL and the
// process exits non-zero when any check failed, so CI cannot read a skipped
// or empty run as success.
open System
open System.IO
open Arca.Tests

let private report (name, passed) =
    if passed then Console.WriteLine $"PASS {name}" else Console.Error.WriteLine $"FAIL {name}"
    passed

let private traceabilityChecks () =
    match RequirementsTraceability.repositoryRoot (DirectoryInfo AppContext.BaseDirectory) with
    | None -> [ "repository root (Arca.slnx) is found from the test assembly", false ]
    | Some root ->
        let read relative = File.ReadAllText(Path.Combine(root, relative))
        let document = read "docs/requirements/ARCA-STORAGE-REQUIREMENTS.md"
        let missing = RequirementsTraceability.unplanned document (read ".ros/work/queue.json")
        let declared = RequirementsTraceability.declaredRequirements document

        missing |> Set.iter (fun id -> Console.Error.WriteLine $"  unplanned requirement: {id}")

        [ "the requirements document declares requirement IDs", not declared.IsEmpty
          "every Arca requirement is named by an open work item", missing.IsEmpty ]

let private checks () =
    [ "scaffold builds and links the library", Arca.Library.scaffoldReady ]
    @ traceabilityChecks ()

[<EntryPoint>]
let main _ =
    let failures = checks () |> List.map report |> List.filter not |> List.length

    if failures = 0 then
        0
    else
        Console.Error.WriteLine $"{failures} check(s) failed."
        1
