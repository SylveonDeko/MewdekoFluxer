using Mewdeko.Common.Replacements;
using Mewdeko.Database.Enums;
using Mewdeko.Modules.Automations.Common;
using Mewdeko.Modules.Currency.Services;
using Mewdeko.Modules.Xp.Services;

namespace Mewdeko.Modules.Automations.Services;

/// <summary>
///     Performs the actions of an automation, in order, within the limits of the bot's permissions.
/// </summary>
public class AutomationRunner : INService
{
    private static readonly TimeSpan SuppressFor = TimeSpan.FromSeconds(30);

    private readonly FluxerClient client;
    private readonly ICurrencyService currency;
    private readonly ILogger<AutomationRunner> logger;
    private readonly ConcurrentDictionary<string, DateTime> suppressed = new();
    private readonly XpService xpService;

    /// <summary>
    ///     Initializes a new instance of the <see cref="AutomationRunner" /> class.
    /// </summary>
    /// <param name="client">The Fluxer client.</param>
    /// <param name="currency">The currency service.</param>
    /// <param name="xpService">The XP service.</param>
    /// <param name="logger">The logger instance.</param>
    public AutomationRunner(FluxerClient client, ICurrencyService currency, XpService xpService,
        ILogger<AutomationRunner> logger)
    {
        this.client = client;
        this.currency = currency;
        this.xpService = xpService;
        this.logger = logger;
    }

    /// <summary>
    ///     Remembers something the bot is about to do.
    /// </summary>
    /// <param name="key">The key built by one of the key methods.</param>
    public void Suppress(string key)
    {
        if (suppressed.Count > 2000)
        {
            var now = DateTime.UtcNow;
            foreach (var (stale, expires) in suppressed.Where(x => x.Value < now).ToList())
                suppressed.TryRemove(stale, out _);
        }

        suppressed[key] = DateTime.UtcNow + SuppressFor;
    }

    /// <summary>
    ///     Checks whether an event was caused by the bot's own action, and forgets the mark.
    /// </summary>
    /// <param name="key">The key built by one of the key methods.</param>
    public bool IsSuppressed(string key) =>
        suppressed.TryRemove(key, out var expires) && expires > DateTime.UtcNow;

    /// <summary>
    ///     Builds the key for a role the bot gives or takes.
    /// </summary>
    /// <param name="guildId">The guild ID.</param>
    /// <param name="userId">The member ID.</param>
    /// <param name="roleId">The role ID.</param>
    /// <param name="gained">Whether the role is given.</param>
    public static string RoleKey(ulong guildId, ulong userId, ulong roleId, bool gained) =>
        $"role:{guildId}:{userId}:{roleId}:{gained}";

    /// <summary>
    ///     Builds the key for a message the bot deletes.
    /// </summary>
    /// <param name="messageId">The message ID.</param>
    public static string MessageKey(ulong messageId) => $"message:{messageId}";

    /// <summary>
    ///     Builds the key for a reaction the bot removes.
    /// </summary>
    /// <param name="messageId">The message ID.</param>
    /// <param name="userId">The member ID.</param>
    /// <param name="emojiKey">The normalized emoji.</param>
    public static string ReactionKey(ulong messageId, ulong userId, string emojiKey) =>
        $"reaction:{messageId}:{userId}:{emojiKey}";

    /// <summary>
    ///     Checks whether a channel is a thread.
    /// </summary>
    /// <param name="channel">The channel.</param>
    public static bool IsThread(Channel channel) =>
        channel.Type is ChannelType.PublicThread or ChannelType.PrivateThread or ChannelType.NewsThread;

    /// <summary>
    ///     Performs actions in order and stops at the first one that fails.
    /// </summary>
    /// <param name="context">The event and run state.</param>
    /// <param name="actions">The actions.</param>
    public async Task<AutomationRunResult> RunActionsAsync(AutomationRunContext context,
        IReadOnlyList<AutomationNode> actions)
    {
        return await AutomationPipeline.RunAsync(actions, context.Automation.RetryOnFailure,
            action => ExecuteAsync(context, action), Task.Delay, (ex, action) =>
            {
                if (ex is not AutomationActionException)
                    logger.LogWarning(ex, "{Action} of automation {Id} failed in {GuildId}", action.Type,
                        context.Automation.Id, context.Guild.Id);
            });
    }

    private async Task ExecuteAsync(AutomationRunContext ctx, AutomationNode node)
    {
        switch (node.Type.ToLowerInvariant())
        {
            case "sendmessage":
            {
                var (text, embeds) = Build(ctx, node);
                var channel = ResolveChannel(ctx, node.GetId("channelId"));
                RequireSend(ctx, channel);
                await channel.SendAsync(text, embeds: embeds, allowedMentions: Mentions(ctx));
                break;
            }
            case "senddm":
            {
                var user = ResolveTarget(ctx, node);
                var (text, embeds) = Build(ctx, node);
                var dm = await user.CreateDMChannelAsync();
                await dm.SendAsync(text, embeds: embeds, allowedMentions: AllowedMentions.None);
                break;
            }
            case "replytomessage":
            {
                var message = RequireMessage(ctx);
                var (text, embeds) = Build(ctx, node);
                RequireSend(ctx, MessageChannel(ctx, message));
                await message.ReplyAsync(text, embeds?.ToList(),
                    new AllowedMentions(AllowedMentionTypes.Users)
                    {
                        MentionRepliedUser = node.GetBool("pingAuthor")
                    });
                break;
            }
            case "repostmessage":
                await RepostAsync(ctx, node);
                break;
            case "deletemessage":
            {
                var message = RequireMessage(ctx);
                RequireManageMessages(ctx, MessageChannel(ctx, message), message.Author.Id);
                Suppress(MessageKey(message.Id));
                await message.DeleteAsync();
                break;
            }
            case "addreactions":
            {
                var message = RequireMessage(ctx);
                foreach (var emoji in node.GetStrings("emojis").Select(AutomationEmoji.Parse).OfType<Emoji>())
                    await message.AddReactionAsync(emoji);
                break;
            }
            case "removetriggeringreaction":
            {
                var message = RequireMessage(ctx);
                var emote = ctx.Emote ?? throw new AutomationActionException("This run has no reaction.");
                if (ctx.Data.UserId == 0)
                    throw new AutomationActionException("The member is not known.");

                RequireManageMessages(ctx, MessageChannel(ctx, message), ctx.Data.UserId);
                Suppress(ReactionKey(message.Id, ctx.Data.UserId, AutomationEmoji.Normalize(emote)));
                await message.RemoveReactionAsync(emote, ctx.Data.UserId);
                break;
            }
            case "giveroles":
            case "takeroles":
                await ChangeRolesAsync(ctx, node, node.Type.Equals("GiveRoles", StringComparison.OrdinalIgnoreCase));
                break;
            case "createthread":
                await CreateThreadAsync(ctx, node);
                break;
            case "sendinthread":
            {
                var thread = RequireThread(ctx);
                var (text, embeds) = Build(ctx, node);
                RequireSend(ctx, thread);
                await thread.SendAsync(text, embeds: embeds, allowedMentions: Mentions(ctx));
                break;
            }
            case "addthreadmembers":
            {
                var thread = RequireThread(ctx);
                var ids = node.GetIds("userIds");
                if (node.GetBool("addTriggerUser") && ctx.Data.UserId != 0)
                    ids.Add(ctx.Data.UserId);

                foreach (var id in ids.Distinct())
                {
                    var member = await ctx.Guild.TryGetUserAsync(id)
                                 ?? throw new AutomationActionException("A member is no longer in the community.");
                    await thread.AddMemberAsync(member);
                }

                break;
            }
            case "pinmessage":
            {
                var message = RequireMessage(ctx);
                RequirePin(ctx, MessageChannel(ctx, message));
                await message.PinAsync();
                break;
            }
            case "givecoins":
            {
                var user = ResolveTarget(ctx, node);
                await currency.CreditAsync(user.Id, node.GetLong("amount"), $"Automation {ctx.Automation.Id}",
                    CurrencyCategory.AdminAdjust, ctx.Guild.Id, "automation");
                break;
            }
            case "takecoins":
            {
                var user = ResolveTarget(ctx, node);
                var balance = await currency.GetUserBalanceAsync(user.Id, ctx.Guild.Id);
                var amount = Math.Min(node.GetLong("amount"), balance);
                if (amount > 0)
                    await currency.TryDebitAsync(user.Id, amount, $"Automation {ctx.Automation.Id}",
                        CurrencyCategory.AdminAdjust, ctx.Guild.Id, "automation");
                break;
            }
            case "givexp":
            {
                var user = ResolveTarget(ctx, node);
                await xpService.AddXpAsync(ctx.Guild.Id, user.Id, (int)node.GetLong("amount"));
                break;
            }
            case "takexp":
            {
                var user = ResolveTarget(ctx, node);
                var stats = await xpService.GetUserXpStatsAsync(ctx.Guild.Id, user.Id);
                await xpService.SetUserXpAsync(ctx.Guild.Id, user.Id,
                    Math.Max(0, (stats?.TotalXp ?? 0) - node.GetLong("amount")));
                break;
            }
            case "wait":
            {
                var seconds = AutomationPipeline.NextWait(node.GetLong("seconds"), ctx.WaitedSeconds);
                ctx.WaitedSeconds += seconds;
                await Task.Delay(TimeSpan.FromSeconds(seconds));
                break;
            }
            default:
                throw new AutomationActionException($"{node.Type} is not an action.");
        }
    }

    private async Task RepostAsync(AutomationRunContext ctx, AutomationNode node)
    {
        var message = RequireMessage(ctx);
        var channel = ResolveChannel(ctx, node.GetId("channelId"));
        RequireSend(ctx, channel);

        var embed = new EmbedBuilder()
            .WithOkColor()
            .WithAuthor(message.Author.GlobalName ?? message.Author.Username, message.Author.GetAvatarOrDefaultUrl())
            .WithDescription(message.Content.TrimTo(4000))
            .WithTimestamp(message.Timestamp);

        var attachments = message.Attachments ?? [];
        var image = attachments.FirstOrDefault(x => x.ContentType?.StartsWith("image/") == true && x.Url is not null);
        if (image is not null)
            embed.WithImageUrl(image.Url);

        var others = attachments.Where(x => x != image && x.Url is not null).Select(x => x.Url).ToList();
        if (others.Count > 0)
            embed.AddField("Attachments", string.Join('\n', others).TrimTo(1024));

        if (node.GetBool("includeLink", true))
            embed.AddField("Original", $"[Jump to message]({message.GetJumpUrl()})");

        await channel.SendAsync(embed: embed.Build(), allowedMentions: AllowedMentions.None);
    }

    private async Task ChangeRolesAsync(AutomationRunContext ctx, AutomationNode node, bool give)
    {
        var user = ResolveTarget(ctx, node);
        var member = ctx.Guild.GetUser(user.Id) ?? await ctx.Guild.TryGetUserAsync(user.Id)
            ?? throw new AutomationActionException("The member is no longer in the community.");

        var roleIds = node.GetIds("roleIds").Where(x => give != member.RoleIds.Contains(x)).ToList();
        if (roleIds.Count == 0)
            return;

        var bot = ctx.Guild.CurrentUser;
        if (!bot.GuildPermissions.ManageRoles)
            throw new AutomationActionException("I'm missing the Manage Roles permission.");

        if (member.Id == ctx.Guild.OwnerId || member.Hierarchy >= bot.Hierarchy)
            throw new AutomationActionException("That member's top role is not below mine.");

        foreach (var id in roleIds)
        {
            var role = ctx.Guild.GetRole(id) ?? throw new AutomationActionException("A role no longer exists.");
            if (role.Id == ctx.Guild.Id || role.Position >= bot.Hierarchy)
                throw new AutomationActionException($"{role.Name} is not below my top role.");
        }

        foreach (var id in roleIds)
            Suppress(RoleKey(ctx.Guild.Id, member.Id, id, give));

        if (give)
            await member.AddRolesAsync(roleIds);
        else
            await member.RemoveRolesAsync(roleIds);
    }

    private async Task CreateThreadAsync(AutomationRunContext ctx, AutomationNode node)
    {
        var channel = ResolveChannel(ctx, node.GetId("channelId"));
        var parent = IsThread(channel) ? channel.ParentChannel : channel;
        if (parent is not { Type: ChannelType.GuildText or ChannelType.GuildNews })
            throw new AutomationActionException("Threads cannot be created in that channel.");

        if (!ctx.Guild.CurrentUser.GetPermissions(parent).CreatePublicThreads)
            throw new AutomationActionException($"I'm missing the Create Public Threads permission in #{parent.Name}.");

        var name = (Render(ctx, node.GetString("name")) ?? "Thread").TrimTo(100);
        var archive = (node.GetString("archiveAfter") ?? "OneDay").ToLowerInvariant() switch
        {
            "onehour" => ThreadArchiveDuration.OneHour,
            "threedays" => ThreadArchiveDuration.ThreeDays,
            "oneweek" => ThreadArchiveDuration.OneWeek,
            _ => ThreadArchiveDuration.OneDay
        };

        var request = new CreatePublicThreadRequest
        {
            Name = name, ArchiveDuration = (int)archive
        };

        var source = node.GetBool("fromMessage", true) && ctx.Message is { } message &&
                     message.ChannelId == parent.Id
            ? message
            : null;

        var thread = source is null
            ? await parent.CreatePublicThreadAsync(request)
            : await source.CreateThreadAsync(request);
        ctx.CreatedThread = thread;

        if (node.GetString("firstMessage") is { } first)
        {
            var text = Render(ctx, first);
            if (!ctx.Automation.AllowMassMentions)
                text = text.SanitizeMentions();

            await thread.SendAsync(text.TrimTo(2000), allowedMentions: Mentions(ctx));
        }
    }

    private static Message RequireMessage(AutomationRunContext ctx) =>
        ctx.Message ?? throw new AutomationActionException("The message is not available.");

    private static Channel MessageChannel(AutomationRunContext ctx, Message message) =>
        message.Channel ?? ctx.Channel ?? throw new AutomationActionException("The channel is not available.");

    private static ThreadChannel RequireThread(AutomationRunContext ctx) =>
        ctx.CreatedThread ?? ctx.TriggerThread ?? throw new AutomationActionException("There is no thread yet.");

    private static Channel ResolveChannel(AutomationRunContext ctx, ulong channelId)
    {
        if (channelId == 0)
            return ctx.Channel ?? throw new AutomationActionException("This event has no channel.");

        return ctx.Guild.GetChannel(channelId) is { IsTextable: true } channel
            ? channel
            : throw new AutomationActionException("The channel no longer exists or cannot hold messages.");
    }

    private static User ResolveTarget(AutomationRunContext ctx, AutomationNode node)
    {
        var author = node.GetString("target")?.Equals("MessageAuthor", StringComparison.OrdinalIgnoreCase) == true;
        var user = author ? ctx.Message?.Author : ctx.User;
        if (user is null)
            throw new AutomationActionException("The member is not known.");

        if (user.IsBot && node.Type is "GiveCoins" or "TakeCoins" or "GiveXp" or "TakeXp")
            throw new AutomationActionException("Bots cannot be given or lose coins or XP.");

        return user;
    }

    private static AllowedMentions Mentions(AutomationRunContext ctx) => ctx.Automation.AllowMassMentions
        ? new AllowedMentions(AllowedMentionTypes.Users | AllowedMentionTypes.Roles | AllowedMentionTypes.Everyone)
        : new AllowedMentions(AllowedMentionTypes.Users);

    private static void RequireSend(AutomationRunContext ctx, Channel channel)
    {
        if (channel.GuildId is null)
            return;

        var permissions = ctx.Guild.CurrentUser.GetPermissions(channel);
        var canSend = IsThread(channel) ? permissions.SendMessagesInThreads : permissions.SendMessages;
        if (!permissions.ViewChannel || !canSend)
            throw new AutomationActionException($"I can't send messages in #{channel.Name}.");
    }

    private static void RequireManageMessages(AutomationRunContext ctx, Channel channel, ulong authorId)
    {
        if (authorId == ctx.Guild.CurrentUser.Id || channel.GuildId is null)
            return;

        if (!ctx.Guild.CurrentUser.GetPermissions(channel).ManageMessages)
            throw new AutomationActionException($"I'm missing the Manage Messages permission in #{channel.Name}.");
    }

    private static void RequirePin(AutomationRunContext ctx, Channel channel)
    {
        if (channel.GuildId is null)
            return;

        var permissions = ctx.Guild.CurrentUser.GetPermissions(channel);
        if (!permissions.PinMessages && !permissions.ManageMessages)
            throw new AutomationActionException($"I'm missing the Pin Messages permission in #{channel.Name}.");
    }

    private (string? Text, EmbedRequest[]? Embeds) Build(AutomationRunContext ctx, AutomationNode node) =>
        AutomationPipeline.Compose(node, BuildReplacer(ctx), ctx.Automation.AllowMassMentions, ctx.Guild.Id);

    private string? Render(AutomationRunContext ctx, string? input) => BuildReplacer(ctx).Replace(input);

    private Replacer BuildReplacer(AutomationRunContext ctx)
    {
        var builder = new ReplacementBuilder(client).WithServer(client, ctx.Guild);
        if (ctx.User is not null)
        {
            if (ctx.Guild.GetUser(ctx.User.Id) is { } member)
                builder.WithUser(member);
            else
                builder.WithUser(ctx.User);
        }

        if (ctx.Channel is not null)
            builder.WithChannel(ctx.Channel);

        var message = ctx.Message;
        var thread = ctx.CreatedThread ?? ctx.TriggerThread;
        builder.WithOverride("%message.content%", () => message?.Content ?? ctx.Data.Content ?? "");
        builder.WithOverride("%message.id%", () => (message?.Id ?? ctx.Data.MessageId).ToString());
        builder.WithOverride("%message.link%", () => message?.GetJumpUrl() ??
                                                      $"{FluxerContext.WebUrl}/channels/{ctx.Guild.Id}/{ctx.Data.ChannelId}/{ctx.Data.MessageId}");
        builder.WithOverride("%message.author%", () => message?.Author.Mention ?? ctx.User?.Mention ?? "");
        builder.WithOverride("%reaction.emoji%", () => ctx.Emote?.ToString() ?? "");
        builder.WithOverride("%reaction.count%", () => ctx.Data.ReactionCount.ToString());
        builder.WithOverride("%role.name%", () => ctx.Role?.Name ?? "");
        builder.WithOverride("%role.mention%", () => ctx.Role?.Mention ?? "");
        builder.WithOverride("%thread.name%", () => thread?.Name ?? "");
        builder.WithOverride("%thread.mention%", () => thread?.Mention ?? "");
        builder.WithOverride("%thread.id%", () => thread?.Id.ToString() ?? "");
        return builder.Build();
    }
}
