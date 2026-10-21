using System.Text.RegularExpressions;

namespace Mewdeko.Common.FluxerAdapter;

/// <summary>
///     Helpers for finding custom emotes in text and getting their images.
/// </summary>
public static partial class EmoteAdapter
{
    /// <summary>
    ///     Finds the custom emotes written in a piece of message text.
    /// </summary>
    /// <param name="content">The message text.</param>
    /// <returns>The distinct custom emotes in the text.</returns>
    public static List<Emoji> GetMessageEmotes(string? content)
    {
        var emotes = new List<Emoji>();
        foreach (Match match in EmoteRegex().Matches(content ?? string.Empty))
        {
            if (Emoji.TryParseCustom(match.Value, out var emote) && emotes.All(x => x.Id != emote.Id))
                emotes.Add(emote);
        }

        return emotes;
    }

    /// <summary>
    ///     Gets the image address of a custom emote.
    /// </summary>
    /// <param name="emote">The emote.</param>
    /// <returns>The address of the emote image.</returns>
    public static string GetEmoteUrl(Emoji emote)
    {
        return $"{FluxerContext.Client.Config.MediaUrl}/emojis/{emote.Id}.webp?size=160";
    }

    [GeneratedRegex(@"<a?:\w+:\d+>")]
    private static partial Regex EmoteRegex();
}
