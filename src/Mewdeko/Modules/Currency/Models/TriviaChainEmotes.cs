namespace Mewdeko.Modules.Currency.Models;

/// <summary>
///     The reactions a trivia chain game answers with.
/// </summary>
public static class TriviaChainEmotes
{
    /// <summary>
    ///     The number reactions, one per answer option in order.
    /// </summary>
    public static readonly Emoji[] Numbers = [new("1️⃣"), new("2️⃣"), new("3️⃣"), new("4️⃣")];

    /// <summary>
    ///     The reaction that banks the current winnings and ends the game.
    /// </summary>
    public static readonly Emoji CashOut = new("💰");
}
