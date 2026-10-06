# Third-party notices — MMW.Media.Conversion

## FFmpeg

This component uses the FFmpeg libraries (libavutil, libavcodec, libavformat, libswresample, libswscale)
<https://ffmpeg.org/>, which are licensed under the GNU Lesser General Public License (LGPL) version 2.1 or later
when built without GPL or non-free components. FFmpeg is a trademark of Fabrice Bellard.

The libraries are **not** statically linked: they are loaded at run time as shared libraries (`.so`, `.dylib`,
`.dll`) from the application directory, `MMW_FFMPEG_PATH` or the system (see `FFmpegLoader`). Only LGPL features
are used: FFmpeg's native decoders, `libswresample`, `libswscale`, the native `aac`, `ac3` and `mjpeg` encoders.
No GPL (`--enable-gpl`, e.g. libx264) or non-free (`--enable-nonfree`, e.g. libfdk_aac) encoder is used.

### Obligations when distributing FFmpeg with the application

When the application is distributed together with FFmpeg shared libraries:

1. Ship an **LGPL build** of FFmpeg (configured without `--enable-gpl` and without `--enable-nonfree`). A build
   configured with `--enable-gpl` makes the libraries GPL, which would impose the GPL on the whole distribution.
2. Keep the libraries **dynamically linked and replaceable**: users must be able to substitute their own (modified)
   build of the same major versions (libavcodec 62, libavformat 62, libavutil 60, libswresample 6, libswscale 9).
3. Include the **LGPL license text** (`COPYING.LGPLv2.1` from the FFmpeg sources) and this notice, and state that
   the software uses FFmpeg under the LGPL with a link to <https://ffmpeg.org/>.
4. Provide (or offer in writing) the **exact FFmpeg source code** used for the shipped binaries, including any
   patches and the `configure` line, for at least three years — or link to the corresponding upstream release
   tarball if it was not modified.
5. Do not prohibit reverse engineering of the application to the extent needed to debug modifications of the
   libraries, and do not use the FFmpeg name to endorse the product.

See <https://ffmpeg.org/legal.html> for the FFmpeg project's checklist.

Patent note: some codecs (e.g. AAC, AC-3) may be covered by patents in some jurisdictions; the LGPL does not grant
patent licenses. Distributors are responsible for any licensing required where they distribute.

## FFmpeg.AutoGen

C# bindings for FFmpeg, <https://github.com/Ruslan-B/FFmpeg.AutoGen>, version 8.1.0, Copyright (c) 2025 Ruslan
Balanukhin (Rationale One), licensed under the MIT License (the license text ships in the NuGet package). Used
unmodified as a NuGet package reference.
