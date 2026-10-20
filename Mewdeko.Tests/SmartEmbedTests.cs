using Mewdeko.Common;
using NUnit.Framework;

namespace Mewdeko.Tests;

/// <summary>
///     Tests for parsing embed JSON into messages Discord accepts.
/// </summary>
[TestFixture]
public class SmartEmbedTests
{
    /// <summary>
    ///     An embed with only a color is dropped, since Discord rejects the whole message over it.
    /// </summary>
    [Test]
    public void ColorOnlyEmbedIsDropped()
    {
        const string json = """
            {"content":"Niiiice job!","embeds":[
              {"title":"Verify Via ID","color":"#ab9810","image":{"url":"https://example.com/a.png"}},
              {"title":"Verify Via Server","color":"#ab9810"},
              {"color":"#5865F2"}]}
            """;

        Assert.That(SmartEmbed.TryParse(json, null, out var embeds, out var plainText), Is.True);
        Assert.That(plainText, Is.EqualTo("Niiiice job!"));
        Assert.That(embeds!.Select(e => e.Title), Is.EqualTo(new[] { "Verify Via ID", "Verify Via Server" }));
    }

    /// <summary>
    ///     Link buttons become markdown links under the content, and buttons without a link or select menus are dropped.
    /// </summary>
    [Test]
    public void LinkButtonsBecomeMarkdownLinks()
    {
        const string json = """
            {"content":"Welcome!","components":[
              {"displayName":"Rules","url":"https://example.com/rules"},
              {"displayName":"Run trigger","id":"5"},
              {"displayName":"Pick","isSelect":true,"options":[{"id":"1","name":"One"}]},
              {"displayName":"Site","url":"https://example.com","emoji":"🌐","row":1}]}
            """;

        Assert.That(SmartEmbed.TryParse(json, null, out _, out var plainText), Is.True);
        Assert.That(plainText,
            Is.EqualTo("Welcome!\n[Rules](https://example.com/rules)\n🌐 [Site](https://example.com)"));
    }
}
