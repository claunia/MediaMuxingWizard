using System.Runtime.InteropServices;
using FFmpeg.AutoGen;
using MMW.Core.Diagnostics;

namespace MMW.Media.Conversion.Interop;

/// <summary>Routes FFmpeg's log messages (warnings and errors) to <see cref="AppLog"/> at debug level.</summary>
internal static unsafe class FFmpegLog
{
    // Kept alive for the lifetime of the process: native code holds a pointer to it.
    private static av_log_set_callback_callback? s_callback;
    private static int s_installed;

    public static void Install()
    {
        if (Interlocked.Exchange(ref s_installed, 1) != 0)
            return;
        ffmpeg.av_log_set_level(ffmpeg.AV_LOG_WARNING);
        s_callback = Callback;
        ffmpeg.av_log_set_callback(s_callback);
    }

    private static void Callback(void* avcl, int level, string format, byte* vl)
    {
        if (level > ffmpeg.av_log_get_level())
            return;
        const int size = 1024;
        var buffer = stackalloc byte[size];
        var prefix = 1;
        ffmpeg.av_log_format_line2(avcl, level, format, vl, buffer, size, &prefix);
        var line = Marshal.PtrToStringUTF8((IntPtr)buffer)?.TrimEnd();
        if (!string.IsNullOrEmpty(line))
            AppLog.Debug("FFmpeg: " + line);
    }
}
