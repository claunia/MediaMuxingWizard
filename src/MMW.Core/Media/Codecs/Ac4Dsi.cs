// Dolby AC-4 decoder specific information writer (ac4_dsi_v1, ETSI TS 103 190-2 Annex E.6).
//
// Ported from GPAC (https://github.com/gpac/gpac), src/odf/descriptors.c (gf_odf_ac4_cfg_write_bs,
// gf_odf_ac4_cfg_dsi_v1, gf_odf_ac4_cfg_presentation_v1_dsi, gf_odf_ac4_cfg_substream_group_dsi,
// gf_odf_ac4_cfg_substream_dsi, gf_odf_ac4_cfg_content_type, gf_odf_ac4_cfg_bitrate_dsi,
// gf_odf_ac4_cfg_alternative_info), write mode only.
// Copyright (c) Telecom ParisTech / the GPAC authors, licensed under the GNU Lesser General Public License
// version 2.1 or later; relicensed here under the GNU General Public License version 3 or later, as section 3
// of the LGPL permits.

namespace MMW.Core.Media.Codecs;

/// <summary>Writes ac4_dsi_v1 (the payload of the ISO BMFF 'dac4' box) from a parsed TOC, as GPAC does.</summary>
internal static class Ac4Dsi
{
    /// <summary>MSB-first writer that zero-pads on alignment (gf_bs_write_int / gf_bs_align).</summary>
    private sealed class Writer
    {
        private readonly List<byte> _bytes = [];
        private long _bits;

        public void Write(ulong value, int count)
        {
            for (var i = count - 1; i >= 0; i--)
            {
                if ((_bits & 7) == 0)
                    _bytes.Add(0);
                if (((value >> i) & 1) != 0)
                    _bytes[^1] |= (byte)(0x80 >> (int)(_bits & 7));
                _bits++;
            }
        }

        public void Align() => _bits = (_bits + 7) & ~7L;

        public void WriteBytes(byte[] data)
        {
            Align();
            _bytes.AddRange(data);
            _bits += data.Length * 8L;
        }

        public byte[] ToArray() => [.. _bytes];
    }

    public static byte[] Write(Ac4StreamInfo dsi)
    {
        var w = new Writer();
        w.Write(dsi.Ac4DsiVersion, 3);
        w.Write(dsi.BitstreamVersion, 7);
        w.Write(dsi.FsIndex, 1);
        w.Write(dsi.FrameRateIndex, 4);

        // Work on copies: writing adjusts presentation fields, and IMS adds presentations.
        var presentations = dsi.Presentations?.Select(p => p.Clone()).ToList() ?? [];
        int nPresentations = dsi.NPresentations;
        int legacy = 0, ims = 0;
        for (var i = 0; i < nPresentations && i < presentations.Count; i++)
        {
            if (presentations[i].PresentationVersion == 1)
                legacy++;
            else if (presentations[i].PresentationVersion == 2)
                ims++;
        }

        // Immersive stereo only: add a legacy (version 1) copy of each IMS presentation
        // (Dolby AC-4 Online Delivery Kit - Signaling immersive stereo content).
        if (legacy == 0 && ims > 0)
        {
            for (var i = 0; i < nPresentations && i < presentations.Count; i++)
            {
                var p = presentations[i];
                if (p.PresentationVersion != 2)
                    continue;
                var copy = p.Clone();
                copy.PresentationVersion = 1;
                copy.PreVirtualized = 0;
                copy.ImmersiveAudioIndicator = 0;
                presentations.Add(copy);
            }

            nPresentations += ims;
        }

        w.Write((ulong)nPresentations, 9);
        if (dsi.BitstreamVersion > 1)
        {
            w.Write(dsi.ProgramIdPresent, 1);
            if (dsi.ProgramIdPresent != 0)
            {
                w.Write(dsi.ShortProgramId, 16);
                w.Write(dsi.Uuid, 1);
                if (dsi.Uuid != 0)
                {
                    foreach (var b in dsi.ProgramUuid)
                        w.Write(b, 8);
                }
            }
        }

        WriteBitrate(w, dsi.BitrateDsi);
        w.Align();

        for (var i = 0; i < nPresentations; i++)
        {
            if (i >= presentations.Count)
                continue;
            var p = presentations[i];
            var t = new Writer();
            if (p.PresentationVersion is 1 or 2)
                WritePresentationV1(t, p);
            var body = t.ToArray();

            w.Write(p.PresentationVersion, 8);
            if (body.Length < 255)
            {
                w.Write((ulong)body.Length, 8);
            }
            else
            {
                w.Write(255, 8);
                w.Write((ulong)(body.Length - 255), 16);
            }

            w.WriteBytes(body);
        }

        return w.ToArray();
    }

    private static void WriteBitrate(Writer w, Ac4BitrateDsi bitrate)
    {
        w.Write(bitrate.BitRateMode, 2);
        w.Write(bitrate.BitRate, 32);
        w.Write(bitrate.BitRatePrecision, 32);
    }

    private static void WritePresentationV1(Writer w, Ac4PresentationV1 p)
    {
        w.Write(p.PresentationConfig, 5);
        if (p.PresentationConfig == 0x06)
        {
            p.AddEmdfSubstreams = 1;
        }
        else
        {
            w.Write(p.MdCompat, 3);
            w.Write(p.PresentationIdPresent, 1);
            if (p.PresentationIdPresent != 0)
                w.Write(p.PresentationId, 5);
            w.Write(p.DsiFrameRateMultiplyInfo, 2);
            w.Write(p.DsiFrameRateFractionInfo, 2);
            w.Write(p.PresentationEmdfVersion, 5);
            w.Write(p.PresentationKeyId, 10);

            w.Write(p.PresentationChannelCoded, 1);
            if (p.PresentationChannelCoded != 0)
            {
                w.Write(p.DsiPresentationChMode, 5);
                if (p.DsiPresentationChMode is >= 11 and <= 14)
                {
                    w.Write(p.PresB4BackChannelsPresent, 1);
                    w.Write(p.PresTopChannelPairs, 2);
                }

                w.Write(0, 6); // reserved
                w.Write(p.PresentationV1ChannelGroups, 18);
            }

            w.Write(p.PresentationCoreDiffers, 1);
            if (p.PresentationCoreDiffers != 0)
            {
                w.Write(p.PresentationCoreChannelCoded, 1);
                if (p.PresentationCoreChannelCoded != 0)
                    w.Write(p.DsiPresentationChannelModeCore, 2);
            }

            w.Write(p.PresentationFilter, 1);
            if (p.PresentationFilter != 0)
            {
                w.Write(p.EnablePresentation, 1);
                w.Write(p.NFilterBytes, 8);
                for (var i = 0; i < p.NFilterBytes; i++)
                    w.Write(0, 8); // filter_data
            }

            if (p.PresentationConfig == 0x1f)
            {
                if (p.SubstreamGroups.Count > 0)
                    WriteSubstreamGroup(w, p.SubstreamGroups[0]);
            }
            else
            {
                w.Write(p.MultiPid, 1);
                if (p.PresentationConfig <= 2)
                    p.NSubstreamGroups = 2;
                if (p.PresentationConfig is 3 or 4)
                    p.NSubstreamGroups = 3;
                if (p.PresentationConfig == 5)
                    w.Write((ulong)(p.NSubstreamGroups - 2), 3);

                for (var i = 0; i < p.NSubstreamGroups; i++)
                {
                    if (i < p.SubstreamGroups.Count)
                        WriteSubstreamGroup(w, p.SubstreamGroups[i]);
                }

                if (p.PresentationConfig > 5)
                {
                    w.Write(p.NSkipBytes, 7);
                    for (var i = 0; i < p.NSkipBytes; i++)
                        w.Write(0, 8); // skip_data
                }
            }

            w.Write(p.PreVirtualized, 1);
            w.Write(p.AddEmdfSubstreams, 1);
        }

        if (p.AddEmdfSubstreams != 0)
        {
            w.Write(p.NAddEmdfSubstreams, 7);
            for (var i = 0; i < p.NAddEmdfSubstreams && i < p.SubstreamEmdfVersion.Length; i++)
            {
                w.Write(p.SubstreamEmdfVersion[i], 5);
                w.Write(p.SubstreamKeyId[i], 10);
            }
        }

        w.Write(p.PresentationBitrateInfo, 1);
        if (p.PresentationBitrateInfo != 0)
            WriteBitrate(w, p.BitrateDsi);
        w.Write(p.Alternative, 1);
        if (p.Alternative != 0)
        {
            w.Align();
            // alternative_info(): GPAC never fills it from the bitstream (name_len 0, n_targets 0)
            w.Write(0, 16);
            w.Write(0, 5);
        }

        w.Align();
        w.Write(p.DeIndicator, 1);
        w.Write(p.ImmersiveAudioIndicator, 1);
        w.Write(0, 4); // reserved

        if (p.PresentationId > 31)
        {
            p.ExtendedPresentationIdPresent = 1;
            p.ExtendedPresentationId = p.PresentationId;
        }

        w.Write(p.ExtendedPresentationIdPresent, 1);
        if (p.ExtendedPresentationIdPresent != 0)
            w.Write(p.ExtendedPresentationId, 9);
        else
            w.Write(0, 1); // reserved
    }

    private static void WriteSubstreamGroup(Writer w, Ac4SubStreamGroup g)
    {
        w.Write(g.SubstreamsPresent, 1);
        w.Write(g.HsfExt, 1);
        w.Write(g.ChannelCoded, 1);
        w.Write(g.NLfSubstreams, 8);
        for (var i = 0; i < g.NLfSubstreams; i++)
        {
            if (i < g.Substreams.Count)
                WriteSubstream(w, g.Substreams[i], g.ChannelCoded);
        }

        w.Write(g.ContentTypePresent, 1);
        if (g.ContentTypePresent == 0)
            return;
        w.Write(g.ContentClassifier, 3);
        w.Write(g.LanguageIndicator, 1);
        if (g.LanguageIndicator != 1)
            return;
        w.Write(g.NLanguageTagBytes, 6);
        for (var i = 0; i < g.NLanguageTagBytes; i++)
            w.Write(g.LanguageTagBytes[i], 8);
    }

    private static void WriteSubstream(Writer w, Ac4SubStream s, byte channelCoded)
    {
        w.Write(s.DsiSfMultiplier, 2);
        w.Write(s.SubstreamBitrateIndicatorPresent, 1);
        if (s.SubstreamBitrateIndicatorPresent == 1)
            w.Write(s.SubstreamBitrateIndicator, 5);
        if (channelCoded == 1)
        {
            w.Write(0, 6); // reserved
            w.Write(s.DsiSubstreamChannelGroups, 18);
        }
        else
        {
            w.Write(s.Ajoc, 1);
            if (s.Ajoc == 1)
            {
                w.Write(s.StaticDmx, 1);
                if (s.StaticDmx == 0)
                    w.Write(s.NDmxObjectsMinus1, 4);
                w.Write(s.NUmxObjectsMinus1, 6);
            }

            w.Write(s.ContainsBedObjects, 1);
            w.Write(s.ContainsDynamicObjects, 1);
            w.Write(s.ContainsIsfObjects, 1);
            w.Write(0, 1); // reserved
        }
    }
}
