using FlaUI.Mcp.Core.Interaction;
using Xunit;

namespace FlaUI.Mcp.Tests.Interaction;

// Headless: pure classification logic over a set of present clipboard format ids.
public class ClipboardClassifierTests
{
    const uint CF_TEXT = 1, CF_OEMTEXT = 7, CF_UNICODETEXT = 13, CF_LOCALE = 16, CF_DIB = 8;
    const uint CF_HTML = 49999; // registered-format id stand-in (any non-synonym)

    [Fact] public void No_formats_is_Empty() =>
        Assert.Equal(PriorClipboardKind.Empty, ClipboardClassifier.Classify(new uint[0]));

    [Fact] public void Unicode_plus_synthesized_text_synonyms_is_Text() =>
        Assert.Equal(PriorClipboardKind.Text,
            ClipboardClassifier.Classify(new[] { CF_UNICODETEXT, CF_TEXT, CF_OEMTEXT, CF_LOCALE }));

    [Fact] public void Unicode_plus_html_is_TextWithRichFormats() =>
        Assert.Equal(PriorClipboardKind.TextWithRichFormats,
            ClipboardClassifier.Classify(new[] { CF_UNICODETEXT, CF_HTML }));

    [Fact] public void Image_only_no_text_is_NonText() =>
        Assert.Equal(PriorClipboardKind.NonText, ClipboardClassifier.Classify(new[] { CF_DIB }));
}
