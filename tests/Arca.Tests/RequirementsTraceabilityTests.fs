module Arca.Tests.RequirementsTraceabilityTests

open System
open System.IO
open Xunit

let private read relative =
    match RequirementsTraceability.repositoryRoot (DirectoryInfo AppContext.BaseDirectory) with
    | Some root -> File.ReadAllText(Path.Combine(root, relative))
    | None -> failwith "repository root (Arca.slnx) not found above the test assembly"

let private document () =
    read "docs/requirements/ARCA-STORAGE-REQUIREMENTS.md"

[<Fact>]
let ``the requirements document declares requirement IDs`` () =
    Assert.NotEmpty(RequirementsTraceability.declaredRequirements (document ()))

[<Fact>]
let ``every Arca requirement is named by a planned or delivered work item`` () =
    Assert.Empty(RequirementsTraceability.unplanned (document ()) (read ".ros/work/queue.json"))

[<Fact>]
let ``a requirement that no planned or delivered work item names is reported`` () =
    let queue =
        """{"items":[{"status":"captured","title":"covers ARCA-LOC-001","description":null},{"status":"complete","title":"delivered ARCA-LOC-002","description":null},{"status":"abandoned","title":"dropped ARCA-REC-001","description":null}]}"""

    let unplanned =
        RequirementsTraceability.unplanned "**ARCA-LOC-001** **ARCA-LOC-002** **ARCA-REC-001**" queue

    Assert.Equal<Set<string>>(Set.ofList [ "ARCA-REC-001" ], unplanned)
