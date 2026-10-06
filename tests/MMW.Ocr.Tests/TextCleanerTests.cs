namespace MMW.Ocr.Tests;

/// <summary>Post-processing of OCR output.</summary>
public sealed class TextCleanerTests
{
    [Theory]
    [InlineData(null, "")]
    [InlineData("", "")]
    [InlineData("  \n \n", "")]
    [InlineData("Hello world\n", "Hello world")]
    [InlineData("  Hello    world  ", "Hello world")]
    [InlineData("First line\r\n\r\nSecond line\n\n", "First line\nSecond line")]
    [InlineData("- Where?\n- Here.", "- Where?\n- Here.")]
    [InlineData("Line\f", "Line")]
    public void Whitespace_and_lines_are_normalised(string? raw, string expected) => Assert.Equal(expected, OcrTextCleaner.Clean(raw));

    [Theory]
    [InlineData("Hello\n.\nworld", "Hello\nworld")]
    [InlineData("‘\nText", "Text")]
    [InlineData("-\nText", "Text")]
    [InlineData("...", "...")]
    [InlineData("OK", "OK")]
    [InlineData("1", "1")]
    public void Noise_lines_are_dropped(string raw, string expected) => Assert.Equal(expected, OcrTextCleaner.Clean(raw));

    [Theory]
    [InlineData("|t's me", "It's me")]
    [InlineData("| know", "I know")]
    [InlineData("don’t", "don't")]
    [InlineData("‘quoted’", "'quoted'")]
    [InlineData("“Hi”", "\"Hi\"")]
    [InlineData("„Hallo“", "„Hallo\"")]
    [InlineData("l know", "I know")]
    [InlineData("And l said", "And I said")]
    [InlineData("l'm here", "I'm here")]
    [InlineData("l'll go, l've seen, l'd say", "I'll go, I've seen, I'd say")]
    [InlineData("lT'S OK", "IT'S OK")]
    [InlineData("lF", "IF")]
    [InlineData("heIlo", "hello")]
    [InlineData("J Take on me I", "\u266A Take on me \u266A")]
    [InlineData("I Today's another day J", "\u266A Today's another day \u266A")]
    [InlineData("J Say after me [", "\u266A Say after me \u266A")]
    [InlineData("I Today's another\nday to find you J", "\u266A Today's another\nday to find you \u266A")]
    public void Common_confusions_are_fixed(string raw, string expected) => Assert.Equal(expected, OcrTextCleaner.Clean(raw));

    [Theory]
    [InlineData("l'homme et l'ami")]
    [InlineData("MacIntyre and McIntosh")]
    [InlineData("I like it")]
    [InlineData("Il est là")]
    [InlineData("lol, little")]
    [InlineData("iPhone")]
    [InlineData("IKEA")]
    [InlineData("level l2")]
    [InlineData("I said I")]
    [InlineData("J is a letter")]
    [InlineData("A J")]
    [InlineData("I\nJ")]
    public void Correct_text_is_untouched(string text) => Assert.Equal(text, OcrTextCleaner.Clean(text));
}
