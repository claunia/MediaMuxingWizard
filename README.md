# Media Muxing Wizard

A cross-platform (Linux, macOS, Windows) multiplexer and metadata editor for MP4 and Matroska, inspired by
[Subler](https://github.com/SublerApp/Subler) and built with [Avalonia 12](https://avaloniaui.net) on .NET 10.

- Mux, remux and convert tracks into MP4/M4V/M4A/M4B/MOV and MKV/MKA/WebM: from MP4, Matroska, MPEG-TS/M2TS,
  Ogg, raw elementary streams (H.264, HEVC, VVC, AV1, AV2, AAC, AC-3, DTS, FLAC, …), subtitle files, and through
  FFmpeg AVI, MPEG-PS/VOB, ASF/WMV, RealMedia, DV, WAV/AIFF, MP3 and VobSub. Audio is converted (AAC, AC-3, ALAC,
  LPCM) and bitmap subtitles are turned into text by OCR when the target cannot hold them.
- Edit iTunes-style tags (including cast and crew, ratings, TV fields, sort fields and store IDs),
  artwork, chapters and track properties of MP4/M4V/M4A/M4B and MKV/MKA/WebM files.
- Media data is never re-encoded. Edits are written in place when possible, otherwise the file is
  rewritten with only its headers changed.
- Batch actions: organize alternate groups, fix audio fallbacks, complete languages, colour spaces,
  chapter generation, and reusable tag sets.
- Full undo/redo, drag and drop, light and dark themes.

## Building

```sh
dotnet build MediaMuxingWizard.slnx
dotnet run --project src/MMW.App
```

## Optional native libraries

Everything works without native dependencies except:

- **Audio conversion and chapter previews** need FFmpeg 9 shared libraries (LGPL build: libavcodec,
  libavformat, libavutil, libswresample, libswscale). They are looked up in `ffmpeg/` or `runtimes/<rid>/native`
  next to the executable, `MMW_FFMPEG_PATH`, then the system (e.g. Homebrew, `/usr/lib`).
- **OCR of bitmap subtitles** (PGS, VobSub) needs Tesseract 5. The built-in OCR is basic:
  [Subtitle Edit](https://www.nikse.dk/subtitleedit) does a much better job, and we recommend converting image
  subtitles with it and importing the resulting SRT file.

`packaging/publish.sh` bundles them when `MMW_BUNDLE_FFMPEG`, `MMW_BUNDLE_TESSERACT` and `MMW_BUNDLE_TESSDATA`
point at the libraries; their licence notices are copied alongside.

## Command line

`mmw` (project `src/MMW.Cli`) scripts the same operations: `mmw info file.m4v --json`,
`mmw set file.m4v "Name=Pilot" "Media Kind=TV Show"`, `mmw artwork file.mkv --add cover.jpg`,
`mmw chapters file.mp4 --import chapters.txt`, `mmw tracks file.m4v --organize-groups --fix-fallbacks`,
`mmw search file.m4v --apply` (online metadata), `mmw nfo file.mkv --import`, `mmw import file.m4v subs.srt`,
`mmw remux file.mkv file.m4v`, and `mmw queue add|start|status` for the editor's batch queue. Run `mmw --help` for everything.

## Metadata search API keys

TheMovieDB and TheTVDB keys are read from `appsettings.json` next to the executable. Copy
`appsettings.example.json` to `appsettings.json` at the repository root and fill in your keys; the build
copies it to the output directory. The file is git-ignored, so keys are never committed. The environment
variables `MMW_TMDB_API_KEY` and `MMW_TVDB_API_KEY` override the file.

## Tests

```sh
dotnet test --solution MediaMuxingWizard.slnx
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

## License

Media Muxing Wizard is free software: you can redistribute it and/or modify it under the terms of the GNU General
Public License as published by the Free Software Foundation, either version 3 of the License, or (at your option) any
later version. See [LICENSE](LICENSE). Third-party components keep their own licenses (FFmpeg: LGPL; Tesseract:
Apache-2.0; see the notices next to each component).
