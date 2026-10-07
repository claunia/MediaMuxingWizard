using MMW.Core.Media.Codecs;

namespace MMW.Core.Tests;

/// <summary>ST 2094-10 and SL-HDR T.35 messages, with headers from the DVB "switched four DMI" HEVC test stream.</summary>
public sealed class DynamicHdrTests
{
    [Fact]
    public void Classifies_the_t35_messages()
    {
        Assert.Equal(DynamicHdrFormats.Smpte2094Part10, DynamicHdr.Classify(Convert.FromHexString("B5003147413934095B300800609B")));
        Assert.Equal(DynamicHdrFormats.SlHdr2, DynamicHdr.Classify(Convert.FromHexString("B5003A00110030090064000033C2")));
        Assert.Equal(DynamicHdrFormats.SlHdr1, DynamicHdr.Classify(Convert.FromHexString("B5003A01010030")));    // SL-HDR1 in AVC (message code 0x01)
        Assert.Equal(DynamicHdrFormats.None, DynamicHdr.Classify(Convert.FromHexString("B5003C0001040040")));   // HDR10+
        Assert.Equal(DynamicHdrFormats.None, DynamicHdr.Classify(Convert.FromHexString("B500314741393403")));   // ATSC closed captions
        Assert.Equal(DynamicHdrFormats.None, DynamicHdr.Classify(Convert.FromHexString("26000400050101")));     // HDR Vivid
    }

    [Fact]
    public void Names_the_formats() =>
        Assert.Equal("ST 2094-10, SL-HDR2", DynamicHdr.Describe(DynamicHdrFormats.Smpte2094Part10 | DynamicHdrFormats.SlHdr2));
}
