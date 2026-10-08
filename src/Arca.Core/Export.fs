namespace Arca

open System
open System.Security.Cryptography
open System.Text

/// One object in an export, exactly as stored.
type ExportedObject =
    { /// Relative to the namespace.
      Path: string
      Content: string }

/// Everything a namespace stores, in its canonical form, for backup and as an
/// escape hatch (ARCA-MIG-003): records, derived data and the manifest,
/// byte for byte, as of one change token.
type ExportArchive =
    { Application: string
      Dataset: string option
      Owner: string
      Repository: string
      Branch: string
      BasePath: string
      ChangeToken: string
      /// Ordered by path.
      Objects: ExportedObject list }

/// Why an export could not be taken or read back.
[<RequireQualifiedAccess>]
type ExportError =
    | Snapshot of SnapshotError
    | Corrupt of reason: string
    /// An object's content does not have the hash the archive recorded.
    | HashMismatch of path: string

/// Canonical export (ARCA-MIG-003). The archive is canonical JSON and records
/// each object's SHA-256, so a backup can be verified before it is trusted.
/// Corrupt records are exported as stored: an export never repairs or drops
/// anything.
[<RequireQualifiedAccess>]
module Export =

    /// The export format this Arca writes and reads.
    [<Literal>]
    let Format = 1

    /// `sha256:<hex>` of the UTF-8 bytes of a stored object.
    let hash (content: string) =
        "sha256:" + Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes content))

    /// The archive of a snapshot.
    let ofSnapshot (snapshot: Snapshot) =
        let ns = snapshot.Namespace
        let (ChangeToken token) = snapshot.ChangeToken

        { Application = AppId.value ns.Application
          Dataset = ns.Dataset |> Option.map DatasetId.value
          Owner = ns.Location.Repository.Owner
          Repository = ns.Location.Repository.Name
          Branch = BranchName.value ns.Location.Branch
          BasePath = RelativePath.render ns.Location.BasePath
          ChangeToken = token
          Objects =
            snapshot.Objects
            |> List.map (fun item ->
                { Path = RelativePath.render item.Path
                  Content = item.Content }) }

    /// Exports a namespace: a consistent snapshot of everything it stores.
    let take (provider: StorageProvider) (ns: Namespace) (attempts: int) =
        async {
            match! Snapshot.take provider ns attempts with
            | Error error -> return Error(ExportError.Snapshot error)
            | Ok snapshot -> return Ok(ofSnapshot snapshot)
        }

    /// The archive as canonical text.
    let encode (archive: ExportArchive) =
        Json.canonicalText (
            Json.objectOf
                [ "arcaExport", Json.Number(decimal Format)
                  "application", Json.String archive.Application
                  "dataset",
                  (match archive.Dataset with
                   | Some dataset -> Json.String dataset
                   | None -> Json.Null)
                  "location",
                  Json.objectOf
                      [ "owner", Json.String archive.Owner
                        "repository", Json.String archive.Repository
                        "branch", Json.String archive.Branch
                        "basePath", Json.String archive.BasePath ]
                  "changeToken", Json.String archive.ChangeToken
                  "objects",
                  Json.Array(
                      archive.Objects
                      |> List.map (fun item ->
                          Json.objectOf [ "path", Json.String item.Path; "content", Json.String item.Content; "sha256", Json.String(hash item.Content) ])
                  ) ]
        )

    /// An archive from its text, with every object's hash verified.
    let decode (text: string) =
        let corrupt reason = Error(ExportError.Corrupt reason)

        let str name value =
            match Json.field name value with
            | Some(Json.String found) -> Ok found
            | _ -> corrupt $"{name} is not text"

        match Json.parse text with
        | Error error -> corrupt (JsonError.describe error)
        | Ok value ->
            match Json.field "arcaExport" value, Json.field "location" value, Json.field "objects" value with
            | Some(Json.Number format), _, _ when format <> decimal Format -> corrupt $"export format {format}, not {Format}"
            | Some(Json.Number _), Some location, Some(Json.Array objects) ->
                let dataset =
                    match Json.field "dataset" value with
                    | Some(Json.String dataset) -> Ok(Some dataset)
                    | Some Json.Null
                    | None -> Ok None
                    | Some _ -> corrupt "dataset is not text"

                let rec items (pending: Json list) acc =
                    match pending with
                    | [] -> Ok(List.rev acc)
                    | item :: rest ->
                        match str "path" item, str "content" item, str "sha256" item with
                        | Ok path, Ok content, Ok recorded ->
                            if hash content <> recorded then
                                Error(ExportError.HashMismatch path)
                            else
                                items rest ({ Path = path; Content = content } :: acc)
                        | _ -> corrupt "an object is malformed"

                match
                    str "application" value,
                    dataset,
                    str "owner" location,
                    str "repository" location,
                    str "branch" location,
                    str "basePath" location,
                    str "changeToken" value
                with
                | Ok application, Ok dataset, Ok owner, Ok repository, Ok branch, Ok basePath, Ok token ->
                    items objects []
                    |> Result.map (fun objects ->
                        { Application = application
                          Dataset = dataset
                          Owner = owner
                          Repository = repository
                          Branch = branch
                          BasePath = basePath
                          ChangeToken = token
                          Objects = objects })
                | _ -> corrupt "the archive's namespace is malformed"
            | _ -> corrupt "the archive is malformed"
