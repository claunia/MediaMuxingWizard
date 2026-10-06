using System.Runtime.InteropServices;

namespace MMW.Ocr.Interop;

/// <summary>Tesseract page segmentation modes (<c>TessPageSegMode</c>) used by the engine.</summary>
internal enum TessPageSegMode
{
    /// <summary>A single uniform block of text (PSM 6).</summary>
    SingleBlock = 6,

    /// <summary>A single text line (PSM 7).</summary>
    SingleLine = 7,
}

/// <summary>Function pointers of the libtesseract C API (<c>tesseract/capi.h</c>) resolved from a loaded library.</summary>
internal sealed unsafe class TesseractFunctions
{
    private delegate* unmanaged<byte*> _version;
    private delegate* unmanaged<IntPtr> _create;
    private delegate* unmanaged<IntPtr, void> _delete;
    private delegate* unmanaged<IntPtr, byte*, byte*, int, int> _init2;
    private delegate* unmanaged<IntPtr, byte*, byte*, int> _setVariable;
    private delegate* unmanaged<IntPtr, int, void> _setPageSegMode;
    private delegate* unmanaged<IntPtr, byte*, int, int, int, int, void> _setImage;
    private delegate* unmanaged<IntPtr, int, void> _setSourceResolution;
    private delegate* unmanaged<IntPtr, IntPtr, int> _recognize;
    private delegate* unmanaged<IntPtr, byte*> _getUtf8Text;
    private delegate* unmanaged<byte*, void> _deleteText;
    private delegate* unmanaged<IntPtr, int> _meanTextConf;
    private delegate* unmanaged<IntPtr, void> _clear;
    private delegate* unmanaged<IntPtr, void> _end;

    // Optional (progress monitor with a deadline, bounding pathological images).
    private delegate* unmanaged<IntPtr> _monitorCreate;
    private delegate* unmanaged<IntPtr, void> _monitorDelete;
    private delegate* unmanaged<IntPtr, int, void> _monitorSetDeadline;

    private TesseractFunctions()
    {
    }

    /// <summary>Resolves every required export of <paramref name="library"/>; null (with the first missing name) when one is absent.</summary>
    public static TesseractFunctions? Resolve(IntPtr library, out string? missing)
    {
        missing = null;
        var f = new TesseractFunctions();
        string? firstMissing = null;
        IntPtr Get(string name)
        {
            if (NativeLibrary.TryGetExport(library, name, out var address))
                return address;
            firstMissing ??= name;
            return IntPtr.Zero;
        }

        f._version = (delegate* unmanaged<byte*>)Get("TessVersion");
        f._create = (delegate* unmanaged<IntPtr>)Get("TessBaseAPICreate");
        f._delete = (delegate* unmanaged<IntPtr, void>)Get("TessBaseAPIDelete");
        f._init2 = (delegate* unmanaged<IntPtr, byte*, byte*, int, int>)Get("TessBaseAPIInit2");
        f._setVariable = (delegate* unmanaged<IntPtr, byte*, byte*, int>)Get("TessBaseAPISetVariable");
        f._setPageSegMode = (delegate* unmanaged<IntPtr, int, void>)Get("TessBaseAPISetPageSegMode");
        f._setImage = (delegate* unmanaged<IntPtr, byte*, int, int, int, int, void>)Get("TessBaseAPISetImage");
        f._setSourceResolution = (delegate* unmanaged<IntPtr, int, void>)Get("TessBaseAPISetSourceResolution");
        f._recognize = (delegate* unmanaged<IntPtr, IntPtr, int>)Get("TessBaseAPIRecognize");
        f._getUtf8Text = (delegate* unmanaged<IntPtr, byte*>)Get("TessBaseAPIGetUTF8Text");
        f._deleteText = (delegate* unmanaged<byte*, void>)Get("TessDeleteText");
        f._meanTextConf = (delegate* unmanaged<IntPtr, int>)Get("TessBaseAPIMeanTextConf");
        f._clear = (delegate* unmanaged<IntPtr, void>)Get("TessBaseAPIClear");
        f._end = (delegate* unmanaged<IntPtr, void>)Get("TessBaseAPIEnd");
        if (firstMissing is not null)
        {
            missing = firstMissing;
            return null;
        }

        if (NativeLibrary.TryGetExport(library, "TessMonitorCreate", out var mc) && NativeLibrary.TryGetExport(library, "TessMonitorDelete", out var md) &&
            NativeLibrary.TryGetExport(library, "TessMonitorSetDeadlineMSecs", out var ms))
        {
            f._monitorCreate = (delegate* unmanaged<IntPtr>)mc;
            f._monitorDelete = (delegate* unmanaged<IntPtr, void>)md;
            f._monitorSetDeadline = (delegate* unmanaged<IntPtr, int, void>)ms;
        }

        return f;
    }

    public string? GetVersion() => Marshal.PtrToStringUTF8((IntPtr)_version());

    public IntPtr Create() => _create();

    public void Delete(IntPtr api) => _delete(api);

    public int Init(IntPtr api, string dataPath, string language)
    {
        var path = Utf8(dataPath);
        var lang = Utf8(language);
        fixed (byte* p = path)
        fixed (byte* l = lang)
            return _init2(api, p, l, 3); // OEM_DEFAULT: LSTM when the traineddata has it (tessdata_fast is LSTM only)
    }

    public bool SetVariable(IntPtr api, string name, string value)
    {
        var n = Utf8(name);
        var v = Utf8(value);
        fixed (byte* pn = n)
        fixed (byte* pv = v)
            return _setVariable(api, pn, pv) != 0;
    }

    public void SetPageSegMode(IntPtr api, TessPageSegMode mode) => _setPageSegMode(api, (int)mode);

    public void SetImage(IntPtr api, ReadOnlySpan<byte> gray, int width, int height, int stride)
    {
        if (gray.Length < (long)stride * height)
            throw new ArgumentException("The image buffer is smaller than its dimensions.", nameof(gray));
        fixed (byte* p = gray)
            _setImage(api, p, width, height, 1, stride); // Tesseract copies the pixels into its own Pix
    }

    public void SetSourceResolution(IntPtr api, int ppi) => _setSourceResolution(api, ppi);

    /// <summary>Runs recognition; with <paramref name="deadlineMs"/> &gt; 0 and a monitor-capable library, stops after that time.</summary>
    public int Recognize(IntPtr api, int deadlineMs)
    {
        if (deadlineMs <= 0 || _monitorCreate == null)
            return _recognize(api, IntPtr.Zero);
        var monitor = _monitorCreate();
        try
        {
            _monitorSetDeadline(monitor, deadlineMs);
            return _recognize(api, monitor);
        }
        finally
        {
            _monitorDelete(monitor);
        }
    }

    public string GetUtf8Text(IntPtr api)
    {
        var text = _getUtf8Text(api);
        if (text == null)
            return string.Empty;
        try
        {
            return Marshal.PtrToStringUTF8((IntPtr)text) ?? string.Empty;
        }
        finally
        {
            _deleteText(text);
        }
    }

    public int MeanTextConf(IntPtr api) => _meanTextConf(api);

    public void Clear(IntPtr api) => _clear(api);

    public void End(IntPtr api) => _end(api);

    private static byte[] Utf8(string s)
    {
        var bytes = new byte[System.Text.Encoding.UTF8.GetByteCount(s) + 1];
        System.Text.Encoding.UTF8.GetBytes(s, bytes);
        return bytes;
    }
}

/// <summary>An initialised <c>TessBaseAPI</c> instance (not thread-safe; released by <see cref="Dispose"/>).</summary>
internal sealed class TesseractApi : IDisposable
{
    private readonly TesseractFunctions _f;
    private IntPtr _handle;

    private TesseractApi(TesseractFunctions functions, IntPtr handle)
    {
        _f = functions;
        _handle = handle;
    }

    /// <summary>Creates and initialises an instance for <paramref name="language"/> with the traineddata in <paramref name="dataPath"/>.</summary>
    /// <exception cref="NotSupportedException">Tesseract is unavailable.</exception>
    /// <exception cref="InvalidOperationException">Initialisation failed (missing or invalid traineddata).</exception>
    public static TesseractApi Create(string dataPath, string language)
    {
        TesseractLoader.EnsureAvailable();
        var f = TesseractLoader.Functions!;
        var handle = f.Create();
        if (handle == IntPtr.Zero)
            throw new InvalidOperationException("TessBaseAPICreate failed.");
        var api = new TesseractApi(f, handle);
        try
        {
            // Tesseract wants the directory with a trailing separator in some versions.
            var path = dataPath.EndsWith(Path.DirectorySeparatorChar) ? dataPath : dataPath + Path.DirectorySeparatorChar;
            if (f.Init(handle, path, language) != 0)
                throw new InvalidOperationException($"Tesseract could not load the '{language}' language data from '{dataPath}'.");

            // Silence diagnostics on stderr and keep the engine deterministic for subtitle crops.
            f.SetVariable(handle, "debug_file", OperatingSystem.IsWindows() ? "NUL" : "/dev/null");
            return api;
        }
        catch
        {
            api.Dispose();
            throw;
        }
    }

    public bool SetVariable(string name, string value) => _f.SetVariable(Handle, name, value);

    /// <summary>Recognises an 8-bit grayscale image and returns the UTF-8 text and the mean word confidence (0–100).</summary>
    public (string Text, int Confidence) Recognize(ReadOnlySpan<byte> gray, int width, int height, TessPageSegMode mode, int ppi, int deadlineMs)
    {
        var h = Handle;
        _f.SetPageSegMode(h, mode);
        _f.SetImage(h, gray, width, height, width);
        _f.SetSourceResolution(h, ppi);
        try
        {
            if (_f.Recognize(h, deadlineMs) != 0)
                return (string.Empty, 0);
            var text = _f.GetUtf8Text(h);
            return (text, Math.Clamp(_f.MeanTextConf(h), 0, 100));
        }
        finally
        {
            _f.Clear(h);
        }
    }

    private IntPtr Handle => _handle != IntPtr.Zero ? _handle : throw new ObjectDisposedException(nameof(TesseractApi));

    public void Dispose()
    {
        var h = _handle;
        _handle = IntPtr.Zero;
        if (h == IntPtr.Zero)
            return;
        _f.End(h);
        _f.Delete(h);
    }
}
