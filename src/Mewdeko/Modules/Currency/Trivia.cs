using Mewdeko.Common.Attributes.TextCommands;
using Mewdeko.Modules.Currency.Models;
using Mewdeko.Modules.Currency.Services;

namespace Mewdeko.Modules.Currency;

/// <summary>
///     Trivia chain game commands for the Currency module.
/// </summary>
public partial class Currency
{
    /// <summary>
    ///     The trivia chain service for managing trivia chain games.
    /// </summary>
    public ITriviaChainService TriviaChainService { get; set; }

    private static readonly TimeSpan TriviaChainExpiry = TimeSpan.FromMinutes(10);

    /// <summary>
    ///     Play a trivia chain where you answer questions in succession for increasing rewards.
    /// </summary>
    /// <param name="betAmount">The amount to bet.</param>
    /// <param name="category">The trivia category.</param>
    /// <example>.triviachain 100 science</example>
    [Cmd]
    [Aliases]
    public async Task TriviaChain(long betAmount, string category = "general")
    {
        var validCategories = new[]
        {
            "general", "science", "history", "sports", "entertainment"
        };
        if (!validCategories.Contains(category.ToLower()))
        {
            await ReplyAsync(Strings.TriviaChainInvalidCategory(ctx.Guild.Id));
            return;
        }

        if (TriviaChainService.GetTriviaChainState(ctx.User.Id) != null)
        {
            await ReplyAsync(Strings.TriviaChainActiveGame(ctx.Guild.Id));
            return;
        }

        if (!await TryTakeBetAsync(betAmount, "triviachain"))
            return;

        // Start the trivia chain game
        var chainState =
            await TriviaChainService.StartTriviaChainAsync(ctx.User.Id, ctx.Guild.Id, betAmount, category.ToLower());

        var message = await ctx.Channel.SendAsync(
            embed: await TriviaChainService.BuildQuestionEmbedAsync(chainState, Service));

        while (true)
        {
            var choices = TriviaChainEmotes.Numbers.Take(chainState.CurrentOptions.Length).Cast<Emoji>().ToList();
            if (chainState.ChainLength > 0)
                choices.Add(TriviaChainEmotes.CashOut);

            var remaining = TriviaChainExpiry - (DateTime.UtcNow - chainState.CreatedAt);
            var picked = remaining > TimeSpan.Zero
                ? await Interactive.WaitForReactionAsync(message, ctx.User.Id, choices, remaining)
                : null;

            _ = message.RemoveAllReactionsAsync();

            if (picked is null)
            {
                TriviaChainService.RemoveTriviaChainState(ctx.User.Id);
                await ReplyAsync(Strings.TriviaChainExpired(ctx.Guild.Id));
                return;
            }

            if (picked.Value == chainState.CurrentOptions.Length)
            {
                var cashOut = await TriviaChainService.CashOutAsync(chainState, Service);
                await ctx.Channel.SendAsync(embed: cashOut.ResultEmbed);
                return;
            }

            var result = await TriviaChainService.ProcessTriviaAnswerAsync(ctx.User.Id, ctx.Guild.Id, picked.Value,
                chainState, Service);

            if (result.UpdatedState is null)
            {
                await ctx.Channel.SendAsync(embed: result.ResultEmbed);
                return;
            }

            chainState = result.UpdatedState;
            message = await ctx.Channel.SendAsync(embed: result.ResultEmbed);
        }
    }
}
