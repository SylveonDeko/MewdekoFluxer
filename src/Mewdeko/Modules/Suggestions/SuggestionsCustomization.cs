
using Mewdeko.Common.Attributes.TextCommands;
using Mewdeko.Modules.Suggestions.Services;

namespace Mewdeko.Modules.Suggestions;

public partial class Suggestions
{
    /// <summary>
    ///     Defines the type of threads used for suggestions.
    /// </summary>
    public enum SuggestThreadType
    {
        /// <summary>
        ///     No thread is used for suggestions.
        /// </summary>
        None = 0,

        /// <summary>
        ///     Public threads are used for suggestions.
        /// </summary>
        Public = 1,

        /// <summary>
        ///     Private threads are used for suggestions.
        /// </summary>
        Private = 2
    }

    /// <summary>
    ///     Customization commands for the Suggestions module.
    /// </summary>
    [Group]
    public class SuggestionsCustomization : MewdekoModuleBase<SuggestionsService>
    {
        /// <summary>
        ///     Sets or updates the suggestion message.
        /// </summary>
        /// <param name="embed">The message or embed code. Use "-" to reset to the default message.</param>
        /// <remarks>
        ///     Allows customization of the suggestion message. Providing "-" will reset the message to its default appearance.
        ///     Requires Administrator permissions.
        /// </remarks>
        [Cmd]
        [Aliases]
        [RequireContext(ContextType.Guild)]
        [UserPerm(GuildPermission.Administrator)]
        public async Task SuggestMessage([Remainder] string embed)
        {
            if (embed == "-")
            {
                await Service.SetSuggestionMessage(ctx.Guild, embed).ConfigureAwait(false);
                await ctx.Channel.SendConfirmAsync(Strings.SuggestionsDefaultLook(ctx.Guild.Id));
                return;
            }

            await Service.SetSuggestionMessage(ctx.Guild, embed).ConfigureAwait(false);
            await ctx.Channel.SendConfirmAsync(Strings.SuggestionMessageUpdated(ctx.Guild.Id));
        }

        /// <summary>
        ///     Sets the minimum length for suggestions.
        /// </summary>
        /// <param name="length">The minimum number of characters a suggestion must have.</param>
        /// <remarks>
        ///     Establishes a minimum character count for suggestions to ensure they are sufficiently detailed.
        ///     Requires Administrator permissions. The length cannot be set to more than 2048 characters.
        /// </remarks>
        [Cmd]
        [Aliases]
        [RequireContext(ContextType.Guild)]
        [UserPerm(GuildPermission.Administrator)]
        public async Task MinSuggestionLength(int length)
        {
            if (length >= 2048)
            {
                await ctx.Channel.SendErrorAsync(Strings.SuggestionLengthInvalid(ctx.Guild.Id), Config)
                    .ConfigureAwait(false);
                return;
            }

            await Service.SetMinLength(ctx.Guild, length).ConfigureAwait(false);
            await ctx.Channel.SendConfirmAsync(Strings.MinLengthSet(ctx.Guild.Id, length)).ConfigureAwait(false);
        }

        /// <summary>
        ///     Sets the maximum length for suggestions.
        /// </summary>
        /// <param name="length">The maximum number of characters a suggestion can have.</param>
        /// <remarks>
        ///     Establishes a maximum character count for suggestions to prevent excessively long submissions.
        ///     Requires Administrator permissions. The length must be greater than 0.
        /// </remarks>
        [Cmd]
        [Aliases]
        [RequireContext(ContextType.Guild)]
        [UserPerm(GuildPermission.Administrator)]
        public async Task MaxSuggestionLength(int length)
        {
            if (length <= 0)
            {
                await ctx.Channel.SendErrorAsync(Strings.SuggestionLengthInvalid(ctx.Guild.Id), Config)
                    .ConfigureAwait(false);
                return;
            }

            await Service.SetMaxLength(ctx.Guild, length).ConfigureAwait(false);
            await ctx.Channel.SendConfirmAsync(Strings.MaxLengthSet(ctx.Guild.Id, length)).ConfigureAwait(false);
        }

        /// <summary>
        ///     Sets or updates the accept message for suggestions.
        /// </summary>
        /// <param name="embed">The message or embed code. Use "-" to reset to the default message.</param>
        /// <remarks>
        ///     Customizes the message displayed when a suggestion is accepted. Providing "-" will reset the message to its default
        ///     appearance.
        ///     Requires Administrator permissions.
        /// </remarks>
        [Cmd]
        [Aliases]
        [RequireContext(ContextType.Guild)]
        [UserPerm(GuildPermission.Administrator)]
        public async Task AcceptMessage([Remainder] string embed)
        {
            if (embed == "-")
            {
                await Service.SetAcceptMessage(ctx.Guild, embed).ConfigureAwait(false);
                await ctx.Channel.SendConfirmAsync(Strings.AcceptedSuggestionsDefaultLook(ctx.Guild.Id))
                    .ConfigureAwait(false);
                return;
            }

            await Service.SetAcceptMessage(ctx.Guild, embed).ConfigureAwait(false);
            await ctx.Channel.SendConfirmAsync(Strings.AcceptedMessageUpdated(ctx.Guild.Id))
                .ConfigureAwait(false);
        }

        /// <summary>
        ///     Sets the channel where accepted suggestions are posted.
        /// </summary>
        /// <param name="channel">The channel for posting accepted suggestions. If null, the feature is disabled.</param>
        /// <remarks>
        ///     Designates a specific channel for showcasing accepted suggestions. Setting the channel to null disables this
        ///     feature.
        ///     Requires Administrator permissions.
        /// </remarks>
        [Cmd]
        [Aliases]
        [RequireContext(ContextType.Guild)]
        [UserPerm(GuildPermission.Administrator)]
        public async Task AcceptChannel(Channel? channel = null)
        {
            await Service.SetAcceptChannel(ctx.Guild, channel?.Id ?? 0).ConfigureAwait(false);
            if (channel is null)
                await ctx.Channel.SendConfirmAsync(Strings.ImplementedSuggestionsDefaultLook(ctx.Guild.Id));
            else
                await ctx.Channel.SendConfirmAsync(Strings.AcceptChannelSet(ctx.Guild.Id, channel.Mention))
                    .ConfigureAwait(false);
        }

        /// <summary>
        ///     Sets the channel where denied suggestions are posted.
        /// </summary>
        /// <param name="channel">The channel for posting denied suggestions. If null, the feature is disabled.</param>
        /// <remarks>
        ///     Designates a specific channel for showcasing denied suggestions. Setting the channel to null disables this feature.
        ///     Requires Administrator permissions.
        /// </remarks>
        [Cmd]
        [Aliases]
        [RequireContext(ContextType.Guild)]
        [UserPerm(GuildPermission.Administrator)]
        public async Task DenyChannel(Channel? channel = null)
        {
            await Service.SetDenyChannel(ctx.Guild, channel?.Id ?? 0).ConfigureAwait(false);
            if (channel is null)
                await ctx.Channel.SendConfirmAsync(Strings.DenyChannelDisabled(ctx.Guild.Id));
            else
                await ctx.Channel.SendConfirmAsync(Strings.DenyChannelSet(ctx.Guild.Id, channel.Mention))
                    .ConfigureAwait(false);
        }

        /// <summary>
        ///     Sets the channel where considered suggestions are posted.
        /// </summary>
        /// <param name="channel">The channel for posting considered suggestions. If null, the feature is disabled.</param>
        /// <remarks>
        ///     Designates a specific channel for showcasing suggestions that are currently being considered. Setting the channel
        ///     to null disables this feature.
        ///     Requires Administrator permissions.
        /// </remarks>
        [Cmd]
        [Aliases]
        [RequireContext(ContextType.Guild)]
        [UserPerm(GuildPermission.Administrator)]
        public async Task ConsiderChannel(Channel? channel = null)
        {
            await Service.SetConsiderChannel(ctx.Guild, channel?.Id ?? 0).ConfigureAwait(false);
            if (channel is null)
                await ctx.Channel.SendConfirmAsync(Strings.ConsiderChannelDisabled(ctx.Guild.Id));
            else
                await ctx.Channel.SendConfirmAsync(Strings.ConsiderChannelSet(ctx.Guild.Id, channel.Mention))
                    .ConfigureAwait(false);
        }

        /// <summary>
        ///     Sets the channel where implemented suggestions are posted.
        /// </summary>
        /// <param name="channel">The channel for posting implemented suggestions. If null, the feature is disabled.</param>
        /// <remarks>
        ///     Designates a specific channel for showcasing suggestions that have been implemented. Setting the channel to null
        ///     disables this feature.
        ///     Requires Administrator permissions.
        /// </remarks>
        [Cmd]
        [Aliases]
        [RequireContext(ContextType.Guild)]
        [UserPerm(GuildPermission.Administrator)]
        public async Task ImplementChannel(Channel? channel = null)
        {
            await Service.SetImplementChannel(ctx.Guild, channel?.Id ?? 0).ConfigureAwait(false);
            if (channel is null)
                await ctx.Channel.SendConfirmAsync(Strings.ImplementChannelDisabled(ctx.Guild.Id));
            else
                await ctx.Channel.SendConfirmAsync(Strings.ImplementChannelSet(ctx.Guild.Id, channel.Mention))
                    .ConfigureAwait(false);
        }

        /// <summary>
        ///     Sets or updates the implement message for suggestions.
        /// </summary>
        /// <param name="embed">The message or embed code. Use "-" to reset to the default message.</param>
        /// <remarks>
        ///     Customizes the message displayed when a suggestion is marked as implemented. Providing "-" will reset the message
        ///     to its default appearance.
        ///     Requires Administrator permissions.
        /// </remarks>
        [Cmd]
        [Aliases]
        [RequireContext(ContextType.Guild)]
        [UserPerm(GuildPermission.Administrator)]
        public async Task ImplementMessage([Remainder] string embed)
        {
            if (embed == "-")
            {
                await Service.SetImplementMessage(ctx.Guild, embed).ConfigureAwait(false);
                await ctx.Channel.SendConfirmAsync(Strings.ImplementedSuggestionsDefaultLook(ctx.Guild.Id))
                    .ConfigureAwait(false);
                return;
            }

            await Service.SetImplementMessage(ctx.Guild, embed).ConfigureAwait(false);
            await ctx.Channel.SendConfirmAsync(Strings.ImplementedMessageUpdated(ctx.Guild.Id))
                .ConfigureAwait(false);
        }

        /// <summary>
        ///     Sets the type of threads used in suggestions.
        /// </summary>
        /// <param name="type">The type of thread to use for new suggestions.</param>
        /// <remarks>
        ///     Chooses between no threads, public threads, or private threads. A thread is opened automatically for every new
        ///     suggestion.
        /// </remarks>
        [Cmd]
        [Aliases]
        [RequireContext(ContextType.Guild)]
        [UserPerm(GuildPermission.Administrator)]
        public async Task SuggestThreadsType(SuggestThreadType type)
        {
            await Service.SetSuggestThreadsType(ctx.Guild, (int)type).ConfigureAwait(false);
            await ctx.Channel.SendConfirmAsync(Strings.SuggestionThreadsTypeSet(ctx.Guild.Id, type))
                .ConfigureAwait(false);
        }

        /// <summary>
        ///     Toggles the archive setting for suggestions upon denial.
        /// </summary>
        /// <remarks>
        ///     Enables or disables automatic archiving of suggestion threads when a suggestion is denied.
        ///     Requires Administrator permissions.
        /// </remarks>
        [Cmd]
        [Aliases]
        [RequireContext(ContextType.Guild)]
        [UserPerm(GuildPermission.Administrator)]
        public async Task ArchiveOnDeny()
        {
            var current = await Service.GetArchiveOnDeny(ctx.Guild);
            await Service.SetArchiveOnDeny(ctx.Guild, !current).ConfigureAwait(false);
            await ctx.Channel.SendConfirmAsync(Strings.ArchiveOnDenySet(ctx.Guild.Id, !current)).ConfigureAwait(false);
        }

        /// <summary>
        ///     Toggles the archive setting for suggestions upon acceptance.
        /// </summary>
        /// <remarks>
        ///     Enables or disables automatic archiving of suggestion threads when a suggestion is accepted.
        ///     Requires Administrator permissions.
        /// </remarks>
        [Cmd]
        [Aliases]
        [RequireContext(ContextType.Guild)]
        [UserPerm(GuildPermission.Administrator)]
        public async Task ArchiveOnAccept()
        {
            var current = await Service.GetArchiveOnAccept(ctx.Guild);
            await Service.SetArchiveOnAccept(ctx.Guild, !current).ConfigureAwait(false);
            await ctx.Channel.SendConfirmAsync(Strings.ArchiveOnAcceptSet(ctx.Guild.Id, !current))
                .ConfigureAwait(false);
        }

        /// <summary>
        ///     Toggles the archive setting for suggestions upon consideration.
        /// </summary>
        /// <remarks>
        ///     Enables or disables automatic archiving of suggestion threads when a suggestion is considered.
        ///     Requires Administrator permissions.
        /// </remarks>
        [Cmd]
        [Aliases]
        [RequireContext(ContextType.Guild)]
        [UserPerm(GuildPermission.Administrator)]
        public async Task ArchiveOnConsider()
        {
            var current = await Service.GetArchiveOnConsider(ctx.Guild);
            await Service.SetArchiveOnConsider(ctx.Guild, !current).ConfigureAwait(false);
            await ctx.Channel.SendConfirmAsync(Strings.ArchiveOnConsiderSet(ctx.Guild.Id, !current))
                .ConfigureAwait(false);
        }

        /// <summary>
        ///     Toggles the archive setting for suggestions upon implementation.
        /// </summary>
        /// <remarks>
        ///     Enables or disables automatic archiving of suggestion threads when a suggestion is implemented.
        ///     Requires Administrator permissions.
        /// </remarks>
        [Cmd]
        [Aliases]
        [RequireContext(ContextType.Guild)]
        [UserPerm(GuildPermission.Administrator)]
        public async Task ArchiveOnImplement()
        {
            var current = await Service.GetArchiveOnImplement(ctx.Guild);
            await Service.SetArchiveOnImplement(ctx.Guild, !current).ConfigureAwait(false);
            await ctx.Channel.SendConfirmAsync(Strings.ArchiveOnImplementSet(ctx.Guild.Id, !current))
                .ConfigureAwait(false);
        }

        /// <summary>
        ///     Sets or updates the deny message for suggestions.
        /// </summary>
        /// <param name="embed">The message or embed code. Use "-" to reset to the default message.</param>
        /// <remarks>
        ///     Customizes the message displayed when a suggestion is denied. Providing "-" will reset the message to its default
        ///     appearance.
        ///     Requires Administrator permissions.
        /// </remarks>
        [Cmd]
        [Aliases]
        [RequireContext(ContextType.Guild)]
        [UserPerm(GuildPermission.Administrator)]
        public async Task DenyMessage([Remainder] string embed)
        {
            if (embed == "-")
            {
                await Service.SetDenyMessage(ctx.Guild, embed).ConfigureAwait(false);
                await ctx.Channel.SendConfirmAsync(Strings.DeniedSuggestionsDefaultLook(ctx.Guild.Id))
                    .ConfigureAwait(false);
                return;
            }

            await Service.SetDenyMessage(ctx.Guild, embed).ConfigureAwait(false);
            await ctx.Channel.SendConfirmAsync(Strings.DeniedMessageUpdated(ctx.Guild.Id));
        }

        /// <summary>
        ///     Sets or updates the consider message for suggestions.
        /// </summary>
        /// <param name="embed">The message or embed code. Use "-" to reset to the default message.</param>
        /// <remarks>
        ///     Customizes the message displayed when a suggestion is considered. Providing "-" will reset the message to its
        ///     default appearance.
        ///     Requires Administrator permissions.
        /// </remarks>
        [Cmd]
        [Aliases]
        [RequireContext(ContextType.Guild)]
        [UserPerm(GuildPermission.Administrator)]
        public async Task ConsiderMessage([Remainder] string embed)
        {
            if (embed == "-")
            {
                await Service.SetConsiderMessage(ctx.Guild, embed).ConfigureAwait(false);
                await ctx.Channel.SendConfirmAsync(Strings.ConsideredSuggestionsDefaultLook(ctx.Guild.Id));
                return;
            }

            await Service.SetConsiderMessage(ctx.Guild, embed).ConfigureAwait(false);
            await ctx.Channel.SendConfirmAsync(Strings.ConsideredMessageUpdated(ctx.Guild.Id));
        }

        /// <summary>
        ///     Configures the custom emotes used for suggestions.
        /// </summary>
        /// <param name="_">A comma-separated list of emote strings, or "disable" to use default reactions.</param>
        /// <remarks>
        ///     Allows the server to specify custom emotes for reacting to suggestions. Set to "disable" to revert to default
        ///     thumbs up/down reactions.
        ///     Requires Administrator permissions.
        /// </remarks>
        [Cmd]
        [Aliases]
        [RequireContext(ContextType.Guild)]
        [UserPerm(GuildPermission.Administrator)]
        public async Task SuggestMotes([Remainder] string? _ = null)
        {
            if (_ == null)
            {
                await ctx.Channel.SendErrorAsync(Strings.EmotesRequired(ctx.Guild.Id), Config);
                return;
            }

            if (_ != null && _.Contains("disable"))
            {
                await Service.SetSuggestionEmotes(ctx.Guild, "disable").ConfigureAwait(false);
                await ctx.Channel.SendConfirmAsync(Strings.CustomEmotesDisabled(ctx.Guild.Id));
                return;
            }

            if (_ != null && !_.Contains("disable") &&
                ctx.Message.Tags.Where(t => t.Type == TagType.Emoji).Select(x => (Emoji)x.Value).Count() > 5)
            {
                await ctx.Channel.SendErrorAsync(Strings.MaxEmotesLimit(ctx.Guild.Id), Config)
                    .ConfigureAwait(false);
                return;
            }

            if (!_.Contains("disable") &&
                !ctx.Message.Tags.Any(t => t.Type == TagType.Emoji))
            {
                await ctx.Channel.SendErrorAsync(Strings.EmotesRequiredSpecific(ctx.Guild.Id), Config)
                    .ConfigureAwait(false);
                return;
            }

            var emotes = ctx.Message.Tags.Where(t => t.Type == TagType.Emoji).Select(x => (Emoji)x.Value);
            foreach (var emoji in emotes)
            {
                try
                {
                    await ctx.Message.AddReactionAsync(emoji).ConfigureAwait(false);
                }
                catch
                {
                    await ctx.Channel
                        .SendErrorAsync(
                            Strings.UnableAccessEmote(ctx.Guild.Id, emoji.Name),
                            Config)
                        .ConfigureAwait(false);
                    return;
                }
            }

            var list = emotes.Select(emote => emote.ToString()).ToList();
            await Service.SetSuggestionEmotes(ctx.Guild, string.Join(",", list)).ConfigureAwait(false);
            await ctx.Channel.SendConfirmAsync(Strings.SuggestionEmotesSet(ctx.Guild.Id, string.Join(",", list)))
                .ConfigureAwait(false);
        }
    }
}
