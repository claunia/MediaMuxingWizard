namespace MMW.Core.Model;

/// <summary>Colour description (ITU-T H.273 code points).</summary>
public readonly record struct ColorInfo(int Primaries, int Transfer, int Matrix, bool? FullRange = null)
{
    public static ColorInfo Unspecified => new(0, 0, 0);

    public bool IsSpecified => Primaries != 0 || Transfer != 0 || Matrix != 0;

    public override string ToString() => IsSpecified ? $"{Primaries}-{Transfer}-{Matrix}" : "Implicit";
}

/// <summary>Named colour-space presets offered by the video inspector and the queue.</summary>
public sealed record ColorPreset(string Name, ColorInfo Color)
{
    public static readonly IReadOnlyList<ColorPreset> All =
    [
        new("Implicit", ColorInfo.Unspecified),
        new("Rec. 601 (5-1-6)", new(5, 1, 6)),
        new("Rec. 601 (6-1-6)", new(6, 1, 6)),
        new("Rec. 709 (1-1-1)", new(1, 1, 1)),
        new("Rec. 2020 (9-1-9)", new(9, 1, 9)),
        new("Rec. 2100 PQ (9-16-9)", new(9, 16, 9)),
        new("Rec. 2100 HLG (9-18-9)", new(9, 18, 9)),
        new("P3-DCI (11-17-6)", new(11, 17, 6)),
        new("P3-D65 (12-17-6)", new(12, 17, 6)),
        new("sRGB (1-13-1)", new(1, 13, 1)),
        new("IPT-C2 (9-16-15)", new(9, 16, 15)),
        new("Undefined (2-2-2)", new(2, 2, 2)),
    ];

    public override string ToString() => Name;
}

/// <summary>Static HDR metadata (SMPTE ST 2086 mastering display + CTA-861.3 content light level).</summary>
public sealed record HdrInfo
{
    /// <summary>Display primaries R,G,B as (x,y) chromaticity pairs.</summary>
    public (double X, double Y)[]? DisplayPrimaries { get; init; }

    public (double X, double Y)? WhitePoint { get; init; }

    public double? MaxLuminance { get; init; }

    public double? MinLuminance { get; init; }

    public int? MaxCll { get; init; }

    public int? MaxFall { get; init; }

    /// <summary>Ambient viewing environment illuminance in lux, if present.</summary>
    public double? AmbientIlluminance { get; init; }
}

/// <summary>Dolby Vision decoder configuration record.</summary>
public sealed record DolbyVisionInfo(int VersionMajor, int VersionMinor, int Profile, int Level, bool RpuPresent, bool ElPresent, bool BlPresent, int BlSignalCompatibilityId)
{
    public override string ToString() => $"Profile {Profile}.{BlSignalCompatibilityId:00}, level {Level}";
}
