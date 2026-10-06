using MMW.Metadata.Parsing;
using MMW.Metadata.Search;

namespace MMW.Metadata.Tests;

public class FileNameParserRegressionTests
{
    [Theory]
    [InlineData("/tmp/c69a3e05f8124b4d9e0a1b2c3d4e5f60.mp4")]
    [InlineData("/tmp/7b83fe6d1152452f94ac1280f964fcea.mp4")]
    [InlineData("/tmp/de024f7486194defb495e07fd8ffa38f.mp4")]
    public void Hex_names_are_not_episodes(string path) =>
        Assert.Equal(MediaSearchKind.Movie, FileNameParser.Parse(path).Kind);
}
