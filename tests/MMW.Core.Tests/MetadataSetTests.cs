using MMW.Core.Chapters;
using MMW.Core.Languages;
using MMW.Core.Metadata;

namespace MMW.Core.Tests;

public class MetadataSetTests
{
    [Fact]
    public void Text_input_is_normalised_to_the_tag_kind()
    {
        var set = new MetadataSet();
        set.Set(TagId.TrackNumber, "3/12");
        set.Set(TagId.TvSeason, "2");
        set.Set(TagId.Compilation, "yes");
        set.Set(TagId.Cast, "Alice, Bob ,  Carol");

        Assert.Equal(new IntPair(3, 12), set.GetPair(TagId.TrackNumber));
        Assert.Equal(2, set.GetInt(TagId.TvSeason));
        Assert.True(set.GetBool(TagId.Compilation));
        Assert.Equal(["Alice", "Bob", "Carol"], set.GetList(TagId.Cast));
    }

    [Fact]
    public void Empty_value_removes_the_tag_and_raises_changed()
    {
        var set = new MetadataSet();
        set.Set(TagId.Name, "Movie");
        var changes = 0;
        set.Changed += (_, _) => changes++;

        set.Set(TagId.Name, "");

        Assert.False(set.Contains(TagId.Name));
        Assert.Equal(1, changes);
    }

    [Fact]
    public void Setting_the_same_value_does_not_raise_changed()
    {
        var set = new MetadataSet();
        set.Set(TagId.Director, new[] { "A" });
        var changes = 0;
        set.Changed += (_, _) => changes++;

        set.Set(TagId.Director, "A");

        Assert.Equal(0, changes);
    }

    [Fact]
    public void Merge_without_overwrite_keeps_existing_values()
    {
        var a = new MetadataSet();
        a.Set(TagId.Name, "Old");
        var b = new MetadataSet();
        b.Set(TagId.Name, "New");
        b.Set(TagId.Genre, "Drama");

        a.Merge(b, overwrite: false, replaceArtworks: false);

        Assert.Equal("Old", a.GetString(TagId.Name));
        Assert.Equal("Drama", a.GetString(TagId.Genre));
    }

    [Fact]
    public void Every_tag_has_a_catalog_entry()
    {
        foreach (var id in Enum.GetValues<TagId>())
            Assert.Equal(id, TagCatalog.Get(id).Id);
    }

    [Theory]
    [InlineData("00:01:02.500", 62_500)]
    [InlineData("1:02:03", 3_723_000)]
    [InlineData("90.25", 90_250)]
    [InlineData("00:00:19,987", 19_987)]
    public void Chapter_times_parse(string text, long ms)
    {
        Assert.True(ChapterTime.TryParse(text, out var t));
        Assert.Equal(ms, (long)t.TotalMilliseconds);
    }

    [Theory]
    [InlineData("ger", "de", "deu", "ger")]
    [InlineData("deu", "de", "deu", "ger")]
    [InlineData("eng", "en", "eng", "eng")]
    [InlineData("chi", "zh", "zho", "chi")]
    public void Iso_codes_map_to_bcp47(string code, string tag, string t, string b)
    {
        Assert.Equal(tag, LanguageTable.ToBcp47(code));
        Assert.Equal(t, LanguageTable.ToIso639_2T(tag));
        Assert.Equal(b, LanguageTable.ToIso639_2B(tag));
    }

    [Fact]
    public void Script_variants_keep_their_iso_code()
    {
        Assert.Equal("zho", LanguageTable.ToIso639_2T("zh-Hans"));
        Assert.Equal("Chinese (Simplified)", LanguageTable.DisplayName("zh-Hans"));
    }
}

public class RatingsTests
{
    [Fact]
    public void Ratings_load_and_round_trip_through_their_encoding()
    {
        Assert.Contains("USA", Ratings.Countries);
        var pg13 = Assert.Single(Ratings.All, r => r.Prefix == "mpaa" && r.Code == "PG-13");
        Assert.Equal("mpaa|PG-13|300|", pg13.Encoded);
        Assert.Same(pg13, Ratings.Find("mpaa|PG-13|300|Some annotation"));
    }
}

public class PresetTests
{
    [Fact]
    public void Preset_round_trips_through_json()
    {
        var set = new MetadataSet();
        set.Set(TagId.TvShow, "Show");
        set.Set(TagId.TvSeason, 3);
        set.Set(TagId.Cast, new[] { "A", "B" });
        set.Set(TagId.HdVideo, 2);
        set.Set(TagId.TrackNumber, new IntPair(1, 8));
        set.Set(TagId.Gapless, true);
        set.Artworks.Add(new Artwork([0xFF, 0xD8, 0xFF, 0x00]));

        var json = System.Text.Json.JsonSerializer.Serialize(MetadataPreset.FromSet("TV", set, true, false));
        var preset = System.Text.Json.JsonSerializer.Deserialize<MetadataPreset>(json)!;
        var back = preset.ToSet();

        Assert.Equal(set.Keys, back.Keys);
        foreach (var id in set.Keys)
            Assert.Equal(MetadataSet.FormatValue(id, set[id]!), MetadataSet.FormatValue(id, back[id]!));
        Assert.Single(back.Artworks);
        Assert.True(preset.ReplaceArtworks);
        Assert.False(preset.ReplaceAnnotations);
    }
}
