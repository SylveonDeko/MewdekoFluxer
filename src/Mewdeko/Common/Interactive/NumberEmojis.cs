namespace Mewdeko.Common.Interactive;

/// <summary>
///     Emojis handed out in order to options that have none of their own: the digits one to nine, ten, then letters.
/// </summary>
public static class NumberEmojis
{
    /// <summary>
    ///     Every emoji in hand-out order.
    /// </summary>
    public static readonly IReadOnlyList<string> All = Build();

    /// <summary>
    ///     Gets the emoji for a zero-based position.
    /// </summary>
    /// <param name="index">The position, starting at zero.</param>
    /// <returns>The emoji, or the last letter when the position is past the end.</returns>
    public static string At(int index)
    {
        return All[Math.Clamp(index, 0, All.Count - 1)];
    }

    /// <summary>
    ///     Gets the zero-based position of an emoji in the hand-out order.
    /// </summary>
    /// <param name="emoji">The emoji text.</param>
    /// <returns>The position, or -1 when the emoji is not one of them.</returns>
    public static int IndexOf(string emoji)
    {
        for (var i = 0; i < All.Count; i++)
        {
            if (All[i] == emoji)
                return i;
        }

        return -1;
    }

    private static List<string> Build()
    {
        var list = new List<string>();
        for (var digit = 1; digit <= 9; digit++)
            list.Add(digit + "️⃣");

        list.Add("\U0001F51F");
        for (var letter = 0; letter < 26; letter++)
            list.Add(char.ConvertFromUtf32(0x1F1E6 + letter));

        return list;
    }
}
