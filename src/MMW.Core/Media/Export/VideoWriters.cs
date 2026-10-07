using MMW.Core.Media.Codecs;

namespace MMW.Core.Media.Export;

/// <summary>
/// MPEG-1/2 video (.m1v/.m2v) and MPEG-4 Part 2 (.m4v) elementary streams: the configuration (sequence header / VOS and
/// VOL headers) goes before the first frame when the frame does not start with it.
/// </summary>
internal sealed class MpegVideoWriter(ExportContext context) : TrackWriter(context)
{
    private bool _first = true;

    public override void Write(MediaSample sample)
    {
        var data = sample.GetData().Span;
        if (_first && Config.Extradata is { Length: > 0 } headers && !StartsWithHeaders(data))
            Output.Write(headers);
        _first = false;
        Output.Write(data);
    }

    /// <summary>True when the first start code of the frame is a sequence-level header.</summary>
    private bool StartsWithHeaders(ReadOnlySpan<byte> data)
    {
        var at = data.IndexOf([(byte)0, (byte)0, (byte)1]);
        if (at < 0 || at + 3 >= data.Length)
            return false;
        var code = data[at + 3];
        return Config.Codec == CodecType.Mpeg4Visual
            ? code is 0xB0 or 0xB5 or <= 0x2F // visual object sequence, visual object, video object / VOL
            : code == 0xB3; // sequence header
    }
}

/// <summary>
/// VC-1 elementary streams (SMPTE 421M Annex E, .vc1): the sequence header and entry point of the 'dvc1' box first, then
/// the frames, each starting with a frame start code (00 00 01 0D) when the sample holds a bare frame.
/// </summary>
internal sealed class Vc1Writer(ExportContext context) : TrackWriter(context)
{
    private static ReadOnlySpan<byte> FrameStartCode => [0, 0, 1, 0x0D];

    public override void Start()
    {
        // 'dvc1': profile/level/flags and the frame rate, then the sequence header and entry point units (from their
        // first start code: some writers put a byte more in front of them).
        if (Config.Extradata is { } entry && QuickTime.EntryBox(entry, "dvc1") is { Length: > 7 } dvc1)
        {
            var at = dvc1.AsSpan(6).IndexOf([(byte)0, (byte)0, (byte)1]);
            if (at >= 0)
                Output.Write(dvc1.AsSpan(6 + at));
        }
    }

    public override void Write(MediaSample sample)
    {
        var data = sample.GetData().Span;
        if (!data.StartsWith([(byte)0, (byte)0, (byte)1]))
            Output.Write(FrameStartCode);
        Output.Write(data);
    }
}
