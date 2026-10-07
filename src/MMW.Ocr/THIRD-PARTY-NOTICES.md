# Third-party notices — MMW.Ocr

## Tesseract OCR

This component uses the Tesseract OCR engine <https://github.com/tesseract-ocr/tesseract> through its C API
(`libtesseract`), licensed under the **Apache License, Version 2.0**
<https://www.apache.org/licenses/LICENSE-2.0>. Copyright © the Tesseract OCR authors (Google and contributors).

The library is **not** statically linked: it is loaded at run time as a shared library (`libtesseract.so.5`,
`libtesseract.5.dylib`, `tesseract55.dll` / `libtesseract-5.dll`) from the application directory
(`runtimes/<rid>/native` or `tesseract/`), `MMW_TESSERACT_PATH` or the system (see `TesseractLoader`). No Tesseract
source code is included in this component; the bindings are hand-written declarations of the public C API.

Tesseract depends on **Leptonica** <http://www.leptonica.org/> (BSD 2-clause style license) and, depending on the
build, on image libraries (libpng, libjpeg, libtiff, libwebp, zlib, giflib, libarchive, libcurl …) under their own
permissive licenses.

### Obligations when distributing Tesseract with the application

1. Include a copy of the Apache License 2.0 and the `NOTICE`/`AUTHORS` information shipped with the Tesseract sources.
2. Include the license texts of Leptonica and of the other libraries bundled with the Tesseract build.
3. State any modifications made to the Tesseract sources (none when an upstream release is used).

## Tesseract language data (tessdata)

Language models (`*.traineddata`) are downloaded on demand from the `tesseract-ocr/tessdata_fast` repository
<https://github.com/tesseract-ocr/tessdata_fast> into the user's application-data folder
(`MediaMuxingWizard/tessdata`); English may be bundled in `<application>/tessdata`. The models are licensed under
the **Apache License, Version 2.0** (see the repository's `LICENSE` file). When bundling a model, include the
Apache-2.0 license text and the repository's attribution.

`tessdata_fast` was chosen over `tessdata_best` because its integer LSTM models are several times smaller and faster
with nearly the same accuracy on clean, rendered subtitle text; `tessdata` (the legacy + LSTM models) also works and
can be used by pointing `TessdataManager` at another base URL.
