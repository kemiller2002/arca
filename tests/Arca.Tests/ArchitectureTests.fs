/// The package layout's rules, checked mechanically (ARCA-ARCH-001, 002, 005,
/// 006, 007; DF-ARCA-2026-0003).
module Arca.Tests.ArchitectureTests

open System
open System.IO
open System.Reflection
open System.Text.RegularExpressions
open System.Xml.Linq
open Xunit

let private root () =
    match RequirementsTraceability.repositoryRoot (DirectoryInfo AppContext.BaseDirectory) with
    | Some root -> root
    | None -> failwith "repository root (Arca.slnx) not found above the test assembly"

let private sources project =
    Directory.GetFiles(Path.Combine(root (), "src", project), "*.fs", SearchOption.AllDirectories)
    |> Array.filter (fun path ->
        let parts = path.Split(Path.DirectorySeparatorChar)
        not (Array.contains "obj" parts || Array.contains "bin" parts))
    |> List.ofArray

/// Removes comments and string literals, so only code is matched. Lexical,
/// like Limen's boundary rules: a banned name inside a doc comment is fine.
let codeOnly (source: string) =
    let tripleQuoted = Regex("\"\"\".*?\"\"\"", RegexOptions.Singleline)
    let verbatim = Regex("@\"(?:[^\"]|\"\")*\"")
    let quoted = Regex("\"(?:[^\"\\\\\\n]|\\\\.)*\"")
    let block = Regex(@"\(\*.*?\*\)", RegexOptions.Singleline)
    let line = Regex(@"//[^\n]*")

    source
    |> fun text -> tripleQuoted.Replace(text, "\"\"")
    |> fun text -> verbatim.Replace(text, "\"\"")
    |> fun text -> quoted.Replace(text, "\"\"")
    |> fun text -> block.Replace(text, " ")
    |> fun text -> line.Replace(text, " ")

/// Authority neither package may acquire: I/O, the network, JavaScript
/// interop, threads, processes, the clock and randomness. Time and entropy
/// are inputs; requests are data the host executes.
let bannedAuthority =
    [ "System.IO", @"(?<![\w.])System\.IO(?![\w])"
      "File.", @"(?<![\w.])File\."
      "Directory.", @"(?<![\w.])Directory\."
      "System.Net", @"(?<![\w.])System\.Net(?![\w])"
      "HttpClient", @"(?<![\w.])HttpClient(?![\w])"
      "JavaScript interop", @"(?<![\w.])(JSImport|JSExport|JSHost|JSObject|IJSRuntime|Microsoft\.JSInterop|System\.Runtime\.InteropServices\.JavaScript)(?![\w])"
      "threads", @"(?<![\w.])(System\.Threading\.Thread|Thread\.|ThreadPool)(?![\w])"
      "timers and delays", @"(?<![\w.])(Task\.Delay|Async\.Sleep|System\.Threading\.Timer|Stopwatch)(?![\w])"
      "processes", @"(?<![\w.])(System\.Diagnostics\.Process|Process\.Start)(?![\w])"
      "the clock", @"(?<![\w.])(DateTime|DateTimeOffset)\.(Now|UtcNow|Today)(?![\w])"
      "randomness", @"(?<![\w.])(Random|RandomNumberGenerator|Guid\.NewGuid)(?![\w])"
      "the process environment", @"(?<![\w.])(System\.Environment|Environment\.(GetEnvironmentVariable|TickCount|ProcessId|Exit))(?![\w])"
      "the console", @"(?<![\w.])(Console|printf|printfn|eprintf|eprintfn)(?![\w])" ]

/// Every banned use in the given F# source text, as (rule, line) pairs.
let violations (source: string) =
    let code = codeOnly source

    bannedAuthority
    |> List.collect (fun (rule, pattern) ->
        Regex.Matches(code, pattern)
        |> Seq.map (fun found -> rule, code.Substring(0, found.Index).Split('\n').Length)
        |> List.ofSeq)

let private projectViolations project =
    sources project
    |> List.collect (fun path ->
        violations (File.ReadAllText path)
        |> List.map (fun (rule, line) -> $"{Path.GetFileName path}:{line} uses {rule}"))

[<Fact>]
let ``the ban list catches each kind of authority and ignores comments and strings`` () =
    let offending =
        """
module M
open System.IO
let a = File.ReadAllText "x"
let b = new HttpClient()
let c = DateTimeOffset.UtcNow
let d = Random()
let e = Guid.NewGuid()
let f = Async.Sleep 10
"""

    let found = violations offending |> List.map fst |> Set.ofList

    Assert.Equal<Set<string>>(
        Set.ofList [ "System.IO"; "File."; "HttpClient"; "the clock"; "randomness"; "timers and delays" ],
        found
    )

    let harmless =
        "/// Reads System.IO and HttpClient and DateTime.Now in a comment.\nlet text = \"Random File.Read HttpClient\"\n(* Guid.NewGuid() *)\n"

    Assert.Empty(violations harmless)

[<Fact>]
let ``Arca.Core performs no I/O and reads neither the clock nor randomness (ARCA-ARCH-001)`` () =
    Assert.NotEmpty(sources "Arca.Core")
    Assert.Empty(projectViolations "Arca.Core")

[<Fact>]
let ``Arca.GitHub holds no network, interop, thread or file authority, so it runs in browser WASM (ARCA-ARCH-002)`` () =
    Assert.NotEmpty(sources "Arca.GitHub")
    Assert.Empty(projectViolations "Arca.GitHub")

[<Fact>]
let ``Arca.Limen holds no network, interop, thread or file authority: its only effects are the host's executors (ARCA-ARCH-002)`` () =
    Assert.NotEmpty(sources "Arca.Limen")
    Assert.Empty(projectViolations "Arca.Limen")

let private project name =
    XDocument.Load(Path.Combine(root (), "src", name, $"{name}.fsproj"))

let private items (document: XDocument) (kind: string) =
    document.Descendants(XName.Get kind)
    |> Seq.map (fun element ->
        match element.Attribute(XName.Get "Include") with
        | null -> ""
        | attribute -> attribute.Value)
    |> List.ofSeq

let private property (document: XDocument) (name: string) =
    document.Descendants(XName.Get name) |> Seq.map _.Value |> Seq.tryHead

[<Fact>]
let ``Arca.Core depends on nothing but FSharp.Core and the BCL (ARCA-ARCH-001)`` () =
    let core = project "Arca.Core"
    Assert.Empty(items core "PackageReference")
    Assert.Empty(items core "ProjectReference")

[<Fact>]
let ``Arca.GitHub depends only on Arca.Core and Aegis's GitHub failure model (ARCA-ARCH-007)`` () =
    let adapter = project "Arca.GitHub"
    Assert.Equal<string list>([ "EchelonFoundry.Aegis.Integration.GitHub" ], items adapter "PackageReference")
    Assert.Equal<string list>([ "../Arca.Core/Arca.Core.fsproj" ], items adapter "ProjectReference")

[<Fact>]
let ``Arca.Core and Arca.GitHub take no Limen dependency; Arca.Limen is the opt-in bridge (DF-LIMEN-2026-0005, DF-ARCA-2026-0010)`` () =
    for name in [ "Arca.Core"; "Arca.GitHub" ] do
        let document = project name
        let references = items document "PackageReference" @ items document "ProjectReference"
        Assert.DoesNotContain(references, fun reference -> reference.Contains("Limen", StringComparison.OrdinalIgnoreCase))

    let bridge = project "Arca.Limen"
    Assert.Equal<string list>([ "EchelonFoundry.Limen.Store"; "EchelonFoundry.Aegis.Core" ], items bridge "PackageReference")
    Assert.Equal<string list>([ "../Arca.Core/Arca.Core.fsproj"; "../Arca.GitHub/Arca.GitHub.fsproj" ], items bridge "ProjectReference")

[<Fact>]
let ``no Arca package depends on Fides or any identity package (ARCA-ARCH-005)`` () =
    let references =
        [ "Arca.Core"; "Arca.GitHub"; "Arca.Limen" ]
        |> List.collect (fun name ->
            let document = project name
            items document "PackageReference" @ items document "ProjectReference")

    Assert.DoesNotContain(references, fun reference -> reference.Contains("Fides", StringComparison.OrdinalIgnoreCase))

    let packages = File.ReadAllText(Path.Combine(root (), "Directory.Packages.props"))
    Assert.DoesNotContain("Fides", packages, StringComparison.OrdinalIgnoreCase)

[<Fact>]
let ``every package is packable under its Echelon package id (ARCA-ARCH-006)`` () =
    for name, id in
        [ "Arca.Core", "EchelonFoundry.Arca.Core"
          "Arca.GitHub", "EchelonFoundry.Arca.GitHub"
          "Arca.Limen", "EchelonFoundry.Arca.Limen" ] do
        let document = project name
        Assert.Equal(Some "true", property document "IsPackable")
        Assert.Equal(Some id, property document "PackageId")
        Assert.Equal(Some name, property document "AssemblyName")

let private forbiddenAssembly (name: string) =
    [ "System.Net"
      "System.IO.FileSystem"
      "System.Diagnostics.Process"
      "System.Threading.Thread"
      "System.Runtime.InteropServices.JavaScript"
      "Microsoft.JSInterop"
      "Fides"
      "EchelonFoundry.Fides" ]
    |> List.exists (fun prefix -> name.StartsWith(prefix, StringComparison.Ordinal))

let private referencedAssemblies (assembly: Assembly) =
    assembly.GetReferencedAssemblies() |> Array.map _.Name |> Array.choose Option.ofObj |> List.ofArray

[<Fact>]
let ``the built Arca.Core assembly references no network, file, process or interop assembly, and not Aegis`` () =
    let references = referencedAssemblies typeof<Arca.ProviderCapabilities>.Assembly
    Assert.Empty(references |> List.filter forbiddenAssembly)
    Assert.DoesNotContain("Aegis.Core", references)
    Assert.DoesNotContain("Aegis.Integration.GitHub", references)

[<Fact>]
let ``the built Arca.GitHub assembly references no network, file, process or interop assembly`` () =
    let references = referencedAssemblies typeof<Arca.GitHub.HttpRequest>.Assembly
    Assert.Empty(references |> List.filter forbiddenAssembly)
    Assert.Contains("Arca.Core", references)

[<Fact>]
let ``the built Arca.Limen assembly references no network, file, process or interop assembly`` () =
    let references = referencedAssemblies typeof<Arca.Limen.QueueRecord>.Assembly
    Assert.Empty(references |> List.filter forbiddenAssembly)
    Assert.Contains("Limen.Store", references)

[<Fact>]
let ``the types consumers persist and handle carry no GitHub concepts (ARCA-ARCH-004)`` () =
    let consumerTypes =
        [ typeof<Arca.Record>
          typeof<Arca.Change>
          typeof<Arca.OperationMetadata>
          typeof<Arca.Conflict>
          typeof<Arca.MergeResult>
          typeof<Arca.Entry>
          typeof<Arca.RecordKey> ]

    let githubTypes =
        [ typeof<Arca.RepositoryRef>; typeof<Arca.BranchName>; typeof<Arca.ObjectAddress>; typeof<Arca.DataLocation> ]

    let githubWords = [ "Commit"; "Branch"; "Sha"; "Repository"; "Blob"; "Tree" ]

    for consumerType in consumerTypes do
        let members =
            Array.append
                (consumerType.GetProperties(BindingFlags.Public ||| BindingFlags.Instance))
                (consumerType.GetNestedTypes() |> Array.collect _.GetProperties(BindingFlags.Public ||| BindingFlags.Instance))

        for property in members do
            Assert.False(List.contains property.PropertyType githubTypes, $"{consumerType.Name}.{property.Name} is a GitHub type")

            Assert.False(
                githubWords |> List.exists (fun word -> property.Name.Contains(word, StringComparison.Ordinal)),
                $"{consumerType.Name}.{property.Name} names a GitHub concept"
            )
