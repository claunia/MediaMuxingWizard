# Media Metadata Wizard

A cross-platform (Linux, macOS, Windows) editor for MP4 and Matroska metadata, inspired by
[Subler](https://github.com/SublerApp/Subler) and built with [Avalonia 12](https://avaloniaui.net) on .NET 10.

- Edit iTunes-style tags (including cast and crew, ratings, TV fields, sort fields and store IDs),
  artwork, chapters and track properties of MP4/M4V/M4A/M4B and MKV/MKA/WebM files.
- Media data is never re-encoded. Edits are written in place when possible, otherwise the file is
  rewritten with only its headers changed.
- Batch actions: organize alternate groups, fix audio fallbacks, complete languages, colour spaces,
  chapter generation, and reusable tag sets.
- Full undo/redo, drag and drop, light and dark themes.

## Building

```sh
dotnet build MediaMetadataWizard.slnx
dotnet run --project src/MMW.App
```

## Metadata search API keys

TheMovieDB and TheTVDB keys are read from `appsettings.json` next to the executable. Copy
`appsettings.example.json` to `appsettings.json` at the repository root and fill in your keys; the build
copies it to the output directory. The file is git-ignored, so keys are never committed. The environment
variables `MMW_TMDB_API_KEY` and `MMW_TVDB_API_KEY` override the file.

## Tests

```sh
dotnet test --solution MediaMetadataWizard.slnx
```

Format tests generate small fixtures with `ffmpeg` and `mkvmerge` (needed only for tests) and skip
when those tools are missing.

Optional environment variables:

| Variable | Effect |
|---|---|
| `MMW_CORPUS` | Directory of real media files; corpus tests read them and edit temporary copies. |
| `MMW_SCREENSHOTS` | Directory where headless UI tests save screenshots. |

## Project layout

| Project | Purpose |
|---|---|
| `src/MMW.Core` | Container-neutral model: tracks, tags, chapters, languages, ratings, undo, batch actions. |
| `src/MMW.Formats.Mp4` | Pure C# ISO-BMFF reader/writer (iTunes metadata, chapters, track properties). |
| `src/MMW.Formats.Matroska` | Pure C# EBML/Matroska reader and in-place editor. |
| `src/MMW.App` | Avalonia user interface. |

## Licence notes

Icons are from [Material Design Icons](https://pictogrammers.com/library/mdi/) (Apache 2.0).
