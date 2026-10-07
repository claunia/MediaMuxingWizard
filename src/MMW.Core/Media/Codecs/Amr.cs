namespace MMW.Core.Media.Codecs;

/// <summary>AMR narrowband and wideband (3GPP TS 26.101 / 26.201) frames as 3GPP files store them.</summary>
public static class Amr
{
    private static readonly double[] s_narrowband = [4.75, 5.15, 5.9, 6.7, 7.4, 7.95, 10.2, 12.2];
    private static readonly double[] s_wideband = [6.6, 8.85, 12.65, 14.25, 15.85, 18.25, 19.85, 23.05, 23.85];

    /// <summary>"12.2 kbps" from the frame type of the first speech frame among <paramref name="samples"/>; empty when none is.</summary>
    public static string Describe(IEnumerable<ReadOnlyMemory<byte>> samples, bool wideband)
    {
        ArgumentNullException.ThrowIfNull(samples);
        var rates = wideband ? s_wideband : s_narrowband;
        var seen = new SortedSet<double>();
        foreach (var sample in samples)
        {
            if (sample.Length == 0)
                continue;
            var type = (sample.Span[0] >> 3) & 0x0F;
            if (type < rates.Length)
                seen.Add(rates[type]);
        }

        if (seen.Count == 0)
            return string.Empty;
        var text = string.Join("/", seen.Select(r => r.ToString(System.Globalization.CultureInfo.InvariantCulture)));
        return $"{text} kbps";
    }
}
