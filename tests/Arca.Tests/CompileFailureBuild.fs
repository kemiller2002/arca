/// Builds tests/Arca.CompileFailure once per test run and reports its
/// compiler errors by file (WI-0021 LCP-085; WI-0036 ARCA-OFF-009). The
/// project must not compile: each forging fixture must fail with exactly the
/// errors its test names, and the control files must compile.
module Arca.Tests.CompileFailureBuild

open System
open System.Diagnostics
open System.IO
open System.Text.RegularExpressions

/// One compiler error: the fixture file, its position and its FS code.
type CompileError =
    { File: string
      Line: int
      Code: string
      Text: string }

/// The build's exit code, its error lines (distinct) and its whole output.
type CompileFailureResult =
    { ExitCode: int
      Errors: CompileError list
      Output: string }

let private errorLine =
    Regex(@"(?<file>[^\\/\s]+\.fs)\((?<line>\d+),\d+\): error (?<code>FS\d+): (?<text>.*?)(\s+\[[^\]]*\])?$", RegexOptions.Compiled)

let private build () =
    let root =
        match RequirementsTraceability.repositoryRoot (DirectoryInfo AppContext.BaseDirectory) with
        | Some root -> root
        | None -> failwith "the repository root (Arca.slnx) was not found"

    let project = Path.Combine(root, "tests", "Arca.CompileFailure", "Arca.CompileFailure.fsproj")
    let start = ProcessStartInfo("dotnet", $"build \"{project}\" -c Debug -nologo -clp:NoSummary")
    start.RedirectStandardOutput <- true
    start.RedirectStandardError <- true
    start.UseShellExecute <- false

    use compiler =
        match Process.Start start with
        | null -> failwith "dotnet did not start"
        | started -> started

    let output = compiler.StandardOutput.ReadToEnd() + compiler.StandardError.ReadToEnd()
    compiler.WaitForExit()

    let errors =
        output.Split('\n')
        |> Array.map (fun line -> line.Trim())
        |> Array.filter (fun line -> line.Contains ": error ")
        |> Array.distinct
        |> Array.toList
        |> List.map (fun line ->
            let found = errorLine.Match line

            if found.Success then
                { File = found.Groups["file"].Value
                  Line = int found.Groups["line"].Value
                  Code = found.Groups["code"].Value
                  Text = found.Groups["text"].Value }
            else
                { File = ""; Line = 0; Code = ""; Text = line })
        |> List.distinctBy (fun error -> error.File, error.Line, error.Code)

    { ExitCode = compiler.ExitCode
      Errors = errors
      Output = output }

/// The one build of the fixtures, shared by every test that reads it.
let result = lazy (build ())

/// The distinct error codes reported in `file`.
let codesIn (file: string) (result: CompileFailureResult) =
    result.Errors |> List.filter (fun error -> error.File = file) |> List.map _.Code |> List.distinct |> List.sort

/// The files that reported any error.
let failingFiles (result: CompileFailureResult) =
    result.Errors |> List.map _.File |> List.distinct |> List.sort
