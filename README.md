<p align="center">
  <img src="packaging/icon/icon-512.png" alt="Media Muxing Wizard" width="256">
</p>

<h1 align="center">Media Muxing Wizard</h1>

<p align="center">
  <strong>The muxer that understands the video it carries.</strong><br>
  MP4 and Matroska, in one app, on Linux, macOS and Windows. Next-generation codecs, HDR done right, and metadata worth looking at.
</p>

<p align="center">
  <a href="LICENSE"><img src="https://img.shields.io/badge/license-GPL--3.0--or--later-blue" alt="License: GPL-3.0-or-later"></a>
  <img src="https://img.shields.io/badge/platforms-Linux%20%7C%20macOS%20%7C%20Windows-informational" alt="Platforms: Linux, macOS, Windows">
  <img src="https://img.shields.io/badge/.NET-10-512BD4" alt=".NET 10">
</p>

---

Most muxers copy bytes from one box to another and hope for the best. **Media Muxing Wizard reads your streams.** It knows when a
Dolby Vision file has lost its configuration, when HDR10 metadata sits in the bitstream but not in the container, and when a
codec the rest of the world hasn't caught up with yet needs to land in a file your player can open. Then it fixes it. Your
video and audio are never re-encoded.

## Why you'll want it

### 🩹 It repairs Dolby Vision. We don't know of any other muxer that does.
A Dolby Vision file whose container lost the Dolby Vision configuration plays as plain HDR10 (or worse) everywhere. Every other
muxer we know of passes that problem straight through. Media Muxing Wizard **finds the Dolby Vision RPU in the bitstream, rebuilds the
configuration from it, and offers to repair the file with one click**. It writes Dolby Vision to MP4 as Dolby's ISO media
format specification requires. For AV1 Dolby Vision it gives you the choice between the spec-mandated `dav1` and the `av01`
form that FFmpeg, mpv, Plex and Jellyfin actually play.

### 🚀 Codecs from the future, today
- **AV2.** Raw IVF and OBU streams in, MP4 and Matroska out, with profile, colour and HDR read from the bitstream. AV2 support
  is still rare in other tools, and you get it in a GUI.
- **VVC / H.266 and MPEG-5 EVC.** Raw Annex B streams, MPEG transport streams, MP4 and Matroska, with profile, level, colour and HDR
  metadata from the bitstream. If there's another GUI muxer that handles both, we haven't met it.
- **China's AVS family: AVS1, AVS2 and AVS3**, with their profiles, colour and HDR, plus **HDR Vivid** (CUVA) dynamic
  metadata. These are national broadcast standards that most Western tools simply ignore.
- **Next-generation audio: Dolby AC-4 and MPEG-H 3D Audio**, from raw streams and DVB transport streams, carried between MP4 and
  Matroska.

### 🌈 HDR that survives the trip
- HDR10 mastering display and light-level metadata is **recovered from the video bitstream** when the container forgot it, and
  written to the output.
- **HDR10+** (SMPTE ST 2094-40) is kept, detected and shown. **ST 2094-10**, **SL-HDR** and **HDR Vivid** are detected too, so you
  know exactly what your file carries.
- Dolby TrueHD and DTS (including **DTS:X**) are described in full and stored in MP4 the way their owners specify.

### 📦 One app, both worlds
**Subler only writes MP4 and only runs on macOS. MKVToolNix only writes Matroska.** Media Muxing Wizard reads and writes both, on every
desktop OS:

| Output | Formats |
|---|---|
| MP4 family | MP4, M4V, M4A, M4B, M4R, MOV |
| Matroska family | MKV, MKA, WebM |

It takes input from MP4, Matroska, **MPEG-TS and Blu-ray M2TS** (with program selection), Ogg, raw elementary streams (H.264, HEVC,
VVC, EVC, AV1, AV2, AVS, AAC, AC-3, E-AC-3, AC-4, DTS, FLAC…), subtitle files and, through FFmpeg, AVI, MPEG-PS/VOB, ASF/WMV,
RealMedia, DV, WAV, AIFF and MP3. VfW, ACM and RealVideo tracks pass through to Matroska exactly as mkvmerge would store them.

### 💬 Subtitles, your way
- Convert freely between **SubRip, ASS, SSA, WebVTT and 3GPP timed text (tx3g)**, or pass them through untouched. You choose per
  track, and the app recommends the best fit for the target.
- tx3g output uses **everything the format can do**: bold, italic, underline, colours, fonts, positioning, vertical text and
  **karaoke**. MKV output recommends ASS, so those styles survive there too.
- **WebVTT stored natively in MP4** (ISO/IEC 14496-30), cue settings included.
- **Duplicate a subtitle track** to ship the same subtitles in several formats.
- **DVB subtitles, Teletext, PGS and VobSub**: Teletext becomes text; bitmap subtitles can be turned into text with OCR.

### 🏷️ Metadata worth looking at
- **Search TheMovieDB, TheTVDB, the iTunes Store and Apple TV**, then apply the result with poster art in one step. Search
  terms are prefilled from your file name.
- Edit every iTunes-style tag: cast and crew, ratings for dozens of countries, TV fields, sort fields and store IDs. Tags carry
  over to Matroska and back without loss.
- Artwork, chapters (with **preview thumbnails**), Kodi **NFO import and export**, and reusable **tag sets**.

### ⚡ Fast, safe, scriptable
- **Never re-encodes your media.** Edits are written in place whenever they fit; otherwise only the headers are rewritten.
- **Batch queue** for whole seasons: fetch metadata, organise alternate groups, fix audio fallbacks, complete languages, apply
  colour spaces, generate chapters, and change container, all unattended.
- Audio conversion (AAC with every downmix, AC-3, ALAC, LPCM) **only when the target can't hold the original**, with an AAC
  fallback track made automatically.
- **Export any track as a raw stream.**
- Full undo and redo, drag and drop, light and dark themes.
- **Speaks your language:** English, Spanish, German, French, Italian, Brazilian Portuguese and Simplified Chinese.

### 🖥️ Command line included
Everything the app does, `mmw` does in scripts:

```sh
mmw probe movie.ts                               # tracks and the actions each one offers
mmw import movie.m4v extras.mkv:2,3 subs.srt     # pick exactly which tracks to bring in
mmw remux movie.mkv movie.m4v --action 3=aac+ac3 # remux with per-track conversions
mmw search episode.m4v --apply                   # online metadata in one line
mmw extract movie.mkv --all                      # every track as a raw stream
mmw queue add *.mkv && mmw queue start           # batch the lot
```

Run `mmw --help` for the full list.

---

## Getting started

### Build and run

```sh
dotnet build MediaMuxingWizard.slnx
dotnet run --project src/MMW.App
```

The app needs no native libraries for muxing, remuxing and editing. Two optional components add more:

- **Audio conversion and chapter previews** use FFmpeg 9 shared libraries (LGPL build: libavcodec, libavformat, libavutil,
  libswresample, libswscale). They are looked up in `ffmpeg/` or `runtimes/<rid>/native` next to the executable, then
  `MMW_FFMPEG_PATH`, then the system (e.g. Homebrew, `/usr/lib`).
- **OCR of bitmap subtitles** (PGS, VobSub, DVB) uses Tesseract 5. Our built-in OCR is basic.
  [Subtitle Edit](https://www.nikse.dk/subtitleedit) does a much better job, and we recommend converting image subtitles
  with it and importing the resulting SRT file.

`packaging/publish.sh` bundles both when `MMW_BUNDLE_FFMPEG`, `MMW_BUNDLE_TESSERACT` and `MMW_BUNDLE_TESSDATA` point at the
libraries, and copies their licence notices alongside.

### Metadata search API keys

TheMovieDB and TheTVDB keys are read from `appsettings.json` next to the executable. Copy `appsettings.example.json` to
`appsettings.json` at the repository root and fill in your keys; the build copies it to the output directory. The file is
git-ignored, so keys are never committed. The environment variables `MMW_TMDB_API_KEY` and `MMW_TVDB_API_KEY` override the file.

## For developers

### Tests

```sh
dotnet test --solution MediaMuxingWizard.slnx
```

Format tests generate small fixtures with `ffmpeg` and `mkvmerge` (needed only for tests). Tests that need them are skipped
when the tools are missing.

| Variable | Effect |
|---|---|
| `MMW_CORPUS` | Directory of real media files; corpus tests read them and edit temporary copies. |
| `MMW_SCREENSHOTS` | Directory where headless UI tests save screenshots. |

### Project layout

The container engines are pure C#, with no mkvtoolnix, MP4Box or mp4v2 at runtime.

| Project | Purpose |
|---|---|
| `src/MMW.Core` | Container-neutral model: tracks, tags, chapters, languages, ratings, undo, batch actions, codec parsers. |
| `src/MMW.Formats.Mp4` | ISO-BMFF reader and writer (iTunes metadata, chapters, track properties, fragmented MP4). |
| `src/MMW.Formats.Matroska` | EBML/Matroska reader, writer and in-place editor. |
| `src/MMW.Formats.MpegTs` | MPEG transport stream and Blu-ray M2TS demuxer. |
| `src/MMW.Formats.Ogg` | Ogg demuxer (Opus, Vorbis, FLAC). |
| `src/MMW.Formats.Elementary` | Raw elementary streams and subtitle files. |
| `src/MMW.Media.Remux` | Remuxing between containers and track adaptation. |
| `src/MMW.Media.Conversion` | FFmpeg-based audio conversion and foreign-container demuxing. |
| `src/MMW.Ocr` | Tesseract OCR of bitmap subtitles. |
| `src/MMW.Metadata` | Online metadata providers, NFO import and export, file name parsing. |
| `src/MMW.Queue` | Batch queue engine. |
| `src/MMW.App` | Avalonia 12 user interface. |
| `src/MMW.Cli` | The `mmw` command line. |

## License

Media Muxing Wizard is free software: you can redistribute it and/or modify it under the terms of the GNU General Public License
as published by the Free Software Foundation, either version 3 of the License, or (at your option) any later version. See
[LICENSE](LICENSE). Third-party components keep their own licences (FFmpeg: LGPL; Tesseract: Apache-2.0; see the notices next to
each component). Icons are from [Material Design Icons](https://pictogrammers.com/library/mdi/) (Apache 2.0).
