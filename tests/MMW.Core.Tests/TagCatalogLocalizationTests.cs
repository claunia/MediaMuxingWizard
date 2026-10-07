using System.Globalization;
using MMW.Core.Metadata;
using MMW.Core.Resources;

namespace MMW.Core.Tests;

/// <summary>
/// Tag and choice display names come from resources looked up by key (Tag_&lt;TagId&gt;, TagChoice_&lt;name&gt;), so a
/// tag added to the catalog without a resource would silently show its English name: every tag must have one.
/// </summary>
public class TagCatalogLocalizationTests
{
    [Fact]
    public void Every_tag_has_a_display_name_resource()
    {
        foreach (var tag in TagCatalog.All)
        {
            Assert.NotNull(Strings.ResourceManager.GetString("Tag_" + tag.Id, CultureInfo.InvariantCulture));
            Assert.Equal(tag.Name, tag.DisplayName); // English UI: the display name is the stable name
        }
    }

    [Fact]
    public void Display_names_follow_the_ui_language_while_stable_names_do_not()
    {
        var previous = CultureInfo.CurrentUICulture;
        try
        {
            CultureInfo.CurrentUICulture = CultureInfo.GetCultureInfo("es-ES");
            var mediaKind = TagCatalog.Get(TagId.MediaKind);
            Assert.Equal("Media Kind", mediaKind.Name);
            Assert.Equal("Tipo de contenido", mediaKind.DisplayName);
            var movie = TagCatalog.MediaKinds.Single(c => c.Value == TagCatalog.MediaKindMovie);
            Assert.Equal("Movie", movie.Name);
            Assert.Equal("Película", movie.ToString());
            Assert.Equal("Movie", MetadataSet.FormatValue(TagId.MediaKind, TagCatalog.MediaKindMovie));
            Assert.Equal("Vídeo", TagCatalog.GroupDisplayName(TagGroup.Video));
        }
        finally
        {
            CultureInfo.CurrentUICulture = previous;
        }
    }
}
