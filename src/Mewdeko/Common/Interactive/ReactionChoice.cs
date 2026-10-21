namespace Mewdeko.Common.Interactive;

/// <summary>
///     One option on a reaction prompt.
/// </summary>
/// <param name="Emoji">The reaction that picks this option.</param>
/// <param name="Label">The text shown beside the reaction in the prompt.</param>
/// <param name="Value">The value returned when this option is picked.</param>
public record ReactionChoice(Emoji Emoji, string Label, string Value);
