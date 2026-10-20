using Mewdeko.Modules.Currency.Models;
using Embed = Discord.Embed;

namespace Mewdeko.Modules.Currency.Services;

/// <summary>
///     Service interface for managing trivia chain games.
/// </summary>
public interface ITriviaChainService : INService
{
    /// <summary>
    ///     Gets the trivia chain state for a user.
    /// </summary>
    /// <param name="userId">The user ID.</param>
    /// <returns>The trivia chain state if it exists, otherwise null.</returns>
    public TriviaChainState? GetTriviaChainState(ulong userId);

    /// <summary>
    ///     Creates a new trivia chain game for a user.
    /// </summary>
    /// <param name="userId">The user ID.</param>
    /// <param name="guildId">The guild ID.</param>
    /// <param name="betAmount">The bet amount.</param>
    /// <param name="category">The trivia category.</param>
    /// <returns>The initial trivia chain state with the first question.</returns>
    public Task<TriviaChainState> StartTriviaChainAsync(ulong userId, ulong guildId, long betAmount, string category);

    /// <summary>
    ///     Processes a trivia answer and updates the game state.
    /// </summary>
    /// <param name="userId">The player's user ID.</param>
    /// <param name="guildId">The guild ID the game is played in.</param>
    /// <param name="answerIndex">The selected answer index.</param>
    /// <param name="chainState">The current trivia chain state.</param>
    /// <param name="currencyService">The currency service.</param>
    /// <returns>The result of processing the answer.</returns>
    public Task<TriviaAnswerResult> ProcessTriviaAnswerAsync(ulong userId, ulong guildId, int answerIndex,
        TriviaChainState chainState, ICurrencyService currencyService);

    /// <summary>
    ///     Pays out the winnings a player has built up and ends their game.
    /// </summary>
    /// <param name="chainState">The current trivia chain state.</param>
    /// <param name="currencyService">The currency service.</param>
    /// <returns>The result of cashing out.</returns>
    public Task<TriviaAnswerResult> CashOutAsync(TriviaChainState chainState, ICurrencyService currencyService);

    /// <summary>
    ///     Builds the embed that shows the current question, its numbered options and the stakes.
    /// </summary>
    /// <param name="chainState">The current trivia chain state.</param>
    /// <param name="currencyService">The currency service.</param>
    /// <returns>The question embed.</returns>
    public Task<Embed> BuildQuestionEmbedAsync(TriviaChainState chainState, ICurrencyService currencyService);

    /// <summary>
    ///     Removes the trivia chain state for a user.
    /// </summary>
    /// <param name="userId">The user ID.</param>
    public void RemoveTriviaChainState(ulong userId);
}