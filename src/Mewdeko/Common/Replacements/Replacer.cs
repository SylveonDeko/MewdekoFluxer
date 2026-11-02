using System.Text.RegularExpressions;

namespace Mewdeko.Common.Replacements;

/// <summary>
///     Class that replaces placeholders in text with their corresponding values.
/// </summary>
public class Replacer
{
    /// <summary>
    ///     Matches %choose{first|second|third}% placeholders. Only <c>}%</c> ends one, so options may hold a lone %.
    ///     The older %choose(first|second)% form is read too, for options without a closing parenthesis.
    /// </summary>
    private static readonly Regex ChooseRegex = new(@"%choose(?:\{(?<options>.*?)\}|\((?<options>[^)]*)\))%",
        RegexOptions.Compiled | RegexOptions.IgnoreCase | RegexOptions.Singleline);

    /// <summary>
    ///     Splits the options of a %choose{}% placeholder on unescaped pipes.
    /// </summary>
    private static readonly Regex ChooseSplitRegex = new(@"(?<!\\)\|", RegexOptions.Compiled);

    /// <summary>
    ///     Collection of regular expressions and their corresponding replacement functions.
    /// </summary>
    private readonly IEnumerable<(Regex Regex, Func<Match, string> Replacement)> regex;

    /// <summary>
    ///     Collection of placeholder keys and their corresponding replacement functions.
    /// </summary>
    private readonly IEnumerable<(string Key, Func<string> Text)> replacements;

    /// <summary>
    ///     Initializes a new instance of the Replacer class.
    /// </summary>
    /// <param name="replacements">Collection of placeholder keys and their corresponding replacement functions.</param>
    /// <param name="regex">Collection of regular expressions and their corresponding replacement functions.</param>
    public Replacer(IEnumerable<(string, Func<string>)> replacements,
        IEnumerable<(Regex, Func<Match, string>)> regex)
    {
        this.replacements = replacements;
        this.regex = regex;
    }

    /// <summary>
    ///     Replaces placeholders in the input string with their corresponding values.
    /// </summary>
    /// <param name="input">The input string.</param>
    /// <returns>The input string with placeholders replaced with their corresponding values.</returns>
    public string? Replace(string? input)
    {
        if (string.IsNullOrWhiteSpace(input))
            return input;

        input = ApplyChoices(input);
        foreach (var (key, text) in replacements)
        {
            if (input.Contains(key))
                input = input.Replace(key, text(), StringComparison.InvariantCulture);
        }

        return regex.Aggregate(input, (current, item) => item.Regex.Replace(current, m => item.Replacement(m)));
    }

    /// <summary>
    ///     Replaces each %choose{a|b|c}% placeholder with one of its options at random. It runs before the other
    ///     placeholders, so the chosen option can hold placeholders of its own. A literal pipe is written as \|.
    /// </summary>
    /// <param name="input">The text.</param>
    /// <returns>The text with every choice made.</returns>
    public static string ApplyChoices(string input)
    {
        if (!input.Contains("%choose", StringComparison.OrdinalIgnoreCase))
            return input;

        return ChooseRegex.Replace(input, match =>
        {
            var options = ChooseSplitRegex.Split(match.Groups["options"].Value);
            return options[Random.Shared.Next(options.Length)].Replace("\\|", "|");
        });
    }
}
