using DataModel;
using LinqToDB;
using LinqToDB.Async;
using Mewdeko.Database.Enums;
using Mewdeko.Modules.Giveaways;

namespace Mewdeko.Modules.Forms.Services;

/// <summary>
///     Deciding a response from Discord rather than from the dashboard.
/// </summary>
/// <remarks>
///     Reviewing a queue is moderator work, and handing somebody the whole dashboard so they can
///     press approve on a form is more access than the job needs. The review emotes sit on the
///     submission message itself, where the people doing the reviewing already are.
/// </remarks>
public partial class FormsService
{
    private static readonly TimeSpan RejectReasonTimeout = TimeSpan.FromMinutes(2);

    private readonly ConcurrentDictionary<ulong, byte> reviewMessages = new();

    /// <summary>
    ///     The emote a review reaction falls back to when neither the form nor the guild names one.
    /// </summary>
    private const string DefaultApproveEmote = "✅";

    private const string DefaultRejectEmote = "❌";

    /// <inheritdoc />
    public async Task OnReadyAsync()
    {
        await using var db = await dbFactory.CreateConnectionAsync();

        var pending = await db.FormResponseWorkflows
            .Where(w => w.ReviewMessageId != null && w.Status == (int)ResponseStatus.Pending)
            .Select(w => w.ReviewMessageId!.Value)
            .ToListAsync();

        foreach (var messageId in pending)
            reviewMessages.TryAdd(messageId, 0);
    }

    /// <summary>
    ///     Unloads the service and unsubscribes from events.
    /// </summary>
    public Task Unload()
    {
        eventHandler.ReactionAdded.Unsubscribe("FormsService", OnReviewReactionAddedAsync);
        return Task.CompletedTask;
    }

    /// <summary>
    ///     Adds the approve and reject reactions to a submission message, or nothing when the form
    ///     does not review its responses.
    /// </summary>
    /// <param name="form">The form the response belongs to.</param>
    /// <param name="message">The submission message reviewers react to.</param>
    /// <returns>True when the reactions were added.</returns>
    public async Task<bool> AddReviewReactionsAsync(Form form, Message message)
    {
        if (!ReviewsResponses(form))
            return false;

        var (approve, reject) = await GetReviewEmotesAsync(form);

        await message.AddReactionAsync(approve);
        await message.AddReactionAsync(reject);
        return true;
    }

    private async Task OnReviewReactionAddedAsync(Cacheable<Message> cachedMessage,
        Channel channel, SocketReaction reaction)
    {
        if (!reviewMessages.ContainsKey(cachedMessage.Id) || reaction.UserId == client.CurrentUser.Id ||
            reaction.User?.IsBot == true)
            return;

        try
        {
            await using var db = await dbFactory.CreateConnectionAsync();
            var workflow = await db.FormResponseWorkflows
                .FirstOrDefaultAsync(w => w.ReviewMessageId == cachedMessage.Id);
            if (workflow == null)
                return;

            var response = await GetResponseDetailsAsync(workflow.ResponseId);
            var form = response == null ? null : await GetFormAsync(response.FormId);
            if (form == null)
                return;

            var (approve, reject) = await GetReviewEmotesAsync(form);
            var approving = Equals(reaction.Emoji, approve);
            if (!approving && !Equals(reaction.Emoji, reject))
                return;

            var message = await cachedMessage.GetOrDownloadAsync();
            var reviewer = client.GetGuild(form.GuildId)?.GetUser(reaction.UserId);
            if (reviewer == null)
                return;

            if (!CanReview(form, reviewer))
            {
                await message.RemoveReactionAsync(Emoji.From(reaction.Emoji), reaction.UserId);
                await NotifyReviewerAsync(message.Channel, reviewer,
                    strings.FormsReviewNeedRole(form.GuildId, form.ReviewerRoleId is { } roleId and > 0
                        ? $"<@&{roleId}>"
                        : strings.FormsReviewManageServer(form.GuildId)));
                return;
            }

            if ((ResponseStatus)workflow.Status is ResponseStatus.Approved or ResponseStatus.Rejected)
            {
                await message.RemoveReactionAsync(Emoji.From(reaction.Emoji), reaction.UserId);
                await NotifyReviewerAsync(message.Channel, reviewer,
                    strings.FormsReviewDecided(form.GuildId, response!.Id,
                        ((ResponseStatus)workflow.Status).ToString().ToLowerInvariant()));
                return;
            }

            if (approving)
                await ApproveFromReactionAsync(form, response!.Id, message, reviewer);
            else
                await RejectFromReactionAsync(form, response!.Id, message, reviewer, Emoji.From(reaction.Emoji));
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Could not handle a form review reaction on message {MessageId}",
                cachedMessage.Id);
        }
    }

    private async Task ApproveFromReactionAsync(Form form, int responseId, Message message,
        SocketGuildMember reviewer)
    {
        var (success, inviteCode) = await ApproveResponseAsync(responseId, reviewer.Id, null);
        if (!success)
        {
            await NotifyReviewerAsync(message.Channel, reviewer,
                strings.FormsReviewApproveFailed(form.GuildId));
            return;
        }

        await SettleReviewMessageAsync(form, responseId, true, reviewer.Id, null);
        await NotifyReviewerAsync(message.Channel, reviewer, string.IsNullOrWhiteSpace(inviteCode)
            ? strings.FormsReviewApproved(form.GuildId, responseId)
            : strings.FormsReviewApprovedInvite(form.GuildId, responseId, inviteCode));
    }

    private async Task RejectFromReactionAsync(Form form, int responseId, Message message,
        SocketGuildMember reviewer, Emoji emote)
    {
        var prompt = await message.Channel.SendAsync(
            strings.FormsReviewReasonPrompt(form.GuildId, reviewer.Mention, responseId),
            allowedMentions: new AllowedMentions { UserIds = [reviewer.Id] });

        var reason = (await WaitForReplyAsync(message.Channel.Id, reviewer.Id, RejectReasonTimeout))?.Trim();
        _ = prompt.DeleteAsync();

        if (string.IsNullOrWhiteSpace(reason))
        {
            await message.RemoveReactionAsync(emote, reviewer.Id);
            await NotifyReviewerAsync(message.Channel, reviewer,
                strings.FormsReviewReasonTimeout(form.GuildId, responseId));
            return;
        }

        if (!await RejectResponseAsync(responseId, reviewer.Id, reason))
        {
            await NotifyReviewerAsync(message.Channel, reviewer, strings.FormsReviewRejectFailed(form.GuildId));
            return;
        }

        await SettleReviewMessageAsync(form, responseId, false, reviewer.Id, reason);
        await NotifyReviewerAsync(message.Channel, reviewer, strings.FormsReviewRejected(form.GuildId, responseId));
    }

    private static async Task NotifyReviewerAsync(Channel channel, SocketGuildMember reviewer, string text)
    {
        var line = $"{reviewer.Mention} {text}";
        var notice = await channel.SendAsync(line,
            allowedMentions: new AllowedMentions { UserIds = [reviewer.Id] });
        _ = Task.Delay(TimeSpan.FromSeconds(15)).ContinueWith(_ => notice.DeleteAsync());
    }

    private async Task<string?> WaitForReplyAsync(ulong channelId, ulong userId, TimeSpan timeout)
    {
        var reply = new TaskCompletionSource<string>();
        eventHandler.MessageReceived.Subscribe("FormsService", OnMessage);
        try
        {
            return await Task.WhenAny(reply.Task, Task.Delay(timeout)) == reply.Task ? await reply.Task : null;
        }
        finally
        {
            eventHandler.MessageReceived.Unsubscribe("FormsService", OnMessage);
        }

        Task OnMessage(SocketMessage arg)
        {
            if (arg.Author.Id == userId && arg.Channel.Id == channelId)
                reply.TrySetResult(arg.Content);

            return Task.CompletedTask;
        }
    }

    /// <summary>
    ///     Works out which emotes a form's review reactions carry.
    /// </summary>
    /// <remarks>
    ///     The form's own choice wins, then the guild's default, then a plain tick and cross. An
    ///     emote that no longer parses, usually a custom one from a server the bot has left, falls
    ///     back rather than breaking the reactions on every future submission.
    /// </remarks>
    /// <param name="form">The form whose reactions are being added.</param>
    /// <returns>The approve and reject emotes.</returns>
    public async Task<(Emoji Approve, Emoji Reject)> GetReviewEmotesAsync(Form form)
    {
        var config = await guildSettings.GetGuildConfig(form.GuildId);

        return (
            Resolve(form.ApproveEmote, config?.FormApproveEmote, DefaultApproveEmote),
            Resolve(form.RejectEmote, config?.FormRejectEmote, DefaultRejectEmote));

        Emoji Resolve(string? formChoice, string? guildChoice, string fallback)
        {
            foreach (var candidate in new[]
                     {
                         formChoice, guildChoice, fallback
                     })
            {
                if (string.IsNullOrWhiteSpace(candidate))
                    continue;

                if (candidate.TryToIEmote(out var parsed) && parsed != null)
                    return parsed;
            }

            return Emoji.FromUnicode(fallback);
        }
    }

    /// <summary>
    ///     Reads a guild's default review reaction emotes.
    /// </summary>
    /// <param name="guildId">The guild.</param>
    /// <returns>The stored emotes, null where the guild has not set one.</returns>
    public async Task<(string? Approve, string? Reject)> GetGuildReviewEmotesAsync(ulong guildId)
    {
        var config = await guildSettings.GetGuildConfig(guildId);

        return (config?.FormApproveEmote, config?.FormRejectEmote);
    }

    /// <summary>
    ///     Sets a guild's default review reaction emotes.
    /// </summary>
    /// <param name="guildId">The guild.</param>
    /// <param name="approve">Emoji for the approve reaction, or null or empty to clear it.</param>
    /// <param name="reject">Emoji for the reject reaction, or null or empty to clear it.</param>
    /// <returns>An error message when an emote cannot be used, otherwise null.</returns>
    public async Task<string?> SetGuildReviewEmotesAsync(ulong guildId, string? approve, string? reject)
    {
        // A stored emote that does not parse would leave every future submission with broken
        // reactions, and the failure would surface far from the setting that caused it.
        if (Unusable(approve, out var approveError))
            return approveError;

        if (Unusable(reject, out var rejectError))
            return rejectError;

        var config = await guildSettings.GetGuildConfig(guildId);
        if (config == null)
            return "Guild settings not found";

        config.FormApproveEmote = string.IsNullOrWhiteSpace(approve) ? null : approve.Trim();
        config.FormRejectEmote = string.IsNullOrWhiteSpace(reject) ? null : reject.Trim();

        await guildSettings.UpdateGuildConfig(guildId, config);

        return null;

        static bool Unusable(string? candidate, out string? error)
        {
            error = null;

            if (string.IsNullOrWhiteSpace(candidate))
                return false;

            if (candidate.TryToIEmote(out var parsed) && parsed != null)
                return false;

            error = $"'{candidate}' is not an emote this bot can use";
            return true;
        }
    }

    /// <summary>
    ///     Whether a form's responses go through review at all.
    /// </summary>
    /// <param name="form">The form to inspect.</param>
    /// <returns>True when responses await a decision.</returns>
    public static bool ReviewsResponses(Form form)
    {
        return form.RequireApproval || (FormType)form.FormType != FormType.Regular;
    }

    /// <summary>
    ///     Whether someone may decide a form's responses from Discord.
    /// </summary>
    /// <remarks>
    ///     A form may name the role that reviews it. When it does not, the fallback is Manage
    ///     Server, so a review reaction is never open to whoever happens to see the channel.
    /// </remarks>
    /// <param name="form">The form the response belongs to.</param>
    /// <param name="user">The person who reacted.</param>
    /// <returns>True when they may decide it.</returns>
    public static bool CanReview(Form form, SocketGuildMember user)
    {
        if (user.GuildPermissions.Administrator || user.GuildPermissions.ManageGuild)
            return true;

        if (form.ReviewerRoleId is { } reviewerRoleId and > 0)
            return user.RoleIds.Contains(reviewerRoleId);

        // Whoever the form hands roles out to on approval is also trusted to make that call.
        var approvalRoles = ReadRoleIds(form.ApprovalRoleIds)
            .Concat(ReadRoleIds(form.ApprovalAddRoleIds))
            .ToHashSet();

        return approvalRoles.Count > 0 && user.RoleIds.Any(approvalRoles.Contains);
    }

    /// <summary>
    ///     Posts a form's launch announcement, if it has one configured and it has not gone out yet.
    /// </summary>
    /// <remarks>
    ///     Called both when a scheduled opening time passes and when a form is published by hand,
    ///     because a guild that configured an announcement expects it either way. The stamp is set
    ///     before the message is sent, so a send that fails is not retried on every subsequent
    ///     publish or background pass.
    /// </remarks>
    /// <param name="formId">The form to announce.</param>
    /// <returns>True when an announcement was sent.</returns>
    public async Task<bool> AnnounceLaunchAsync(int formId)
    {
        await using var db = await dbFactory.CreateConnectionAsync();

        var form = await db.Forms.FirstOrDefaultAsync(f => f.Id == formId);

        if (form == null || form.IsDraft || form.AnnouncedAt.HasValue)
            return false;

        if (form.AnnounceChannelId is not { } channelId)
            return false;

        if (form.OpensAt is { } opensAt && opensAt > DateTime.UtcNow)
            return false;

        await db.Forms
            .Where(f => f.Id == formId)
            .Set(f => f.AnnouncedAt, DateTime.UtcNow)
            .UpdateAsync();

        try
        {
            var guild = client.GetGuild(form.GuildId);

            if (guild?.GetTextChannel(channelId) is not { } channel)
            {
                logger.LogWarning("Announcement channel for form {FormId} is not reachable", formId);
                return false;
            }

            var embed = new EmbedBuilder()
                .WithTitle(form.Name)
                .WithDescription(string.IsNullOrWhiteSpace(form.AnnounceMessage)
                    ? form.Description ?? "This form is now open."
                    : form.AnnounceMessage)
                .WithOkColor()
                .WithCurrentTimestamp();

            if (form.ExpiresAt is { } closesAt)
            {
                var closesUnix = new DateTimeOffset(closesAt, TimeSpan.Zero).ToUnixTimeSeconds();
                embed.AddField("Closes", $"<t:{closesUnix}:R> (<t:{closesUnix}:f>)");
            }

            var content = form.AnnounceRoleId is { } roleId and > 0
                ? MentionUtils.MentionRole(roleId)
                : null;

            var mentions = form.AnnounceRoleId is { } pingRole and > 0
                ? new AllowedMentions
                {
                    RoleIds = [pingRole]
                }
                : AllowedMentions.None;

            await channel.SendAsync(content, embed: embed.Build(), allowedMentions: mentions);

            logger.LogInformation("Announced form {FormId} in channel {ChannelId}", formId, channelId);

            return true;
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Could not announce form {FormId}", formId);
            return false;
        }
    }

    /// <summary>
    ///     Records which Discord message carries a response's review reactions.
    /// </summary>
    /// <param name="responseId">The response.</param>
    /// <param name="messageId">The message.</param>
    public async Task SetReviewMessageAsync(int responseId, ulong messageId)
    {
        await using var db = await dbFactory.CreateConnectionAsync();

        await db.FormResponseWorkflows
            .Where(w => w.ResponseId == responseId)
            .Set(w => w.ReviewMessageId, messageId)
            .UpdateAsync();

        reviewMessages.TryAdd(messageId, 0);
    }

    /// <summary>
    ///     Settles the submission message after a decision: notes the outcome on the embed and takes
    ///     the reactions away, so a decided response cannot be decided again from a stale message.
    /// </summary>
    /// <param name="form">The form the response belongs to.</param>
    /// <param name="responseId">The response that was decided.</param>
    /// <param name="approved">Whether it was approved.</param>
    /// <param name="reviewerId">Who decided it.</param>
    /// <param name="notes">The reason they gave, if any.</param>
    public async Task SettleReviewMessageAsync(
        Form form,
        int responseId,
        bool approved,
        ulong reviewerId,
        string? notes)
    {
        try
        {
            await using var db = await dbFactory.CreateConnectionAsync();

            var workflow = await db.FormResponseWorkflows
                .FirstOrDefaultAsync(w => w.ResponseId == responseId);

            if (workflow?.ReviewMessageId is not { } messageId)
                return;

            if (!form.SubmitChannelId.HasValue)
                return;

            var guild = client.GetGuild(form.GuildId);
            if (guild?.GetTextChannel(form.SubmitChannelId.Value) is not { } channel)
                return;

            if (await channel.GetMessageAsync(messageId) is not Message message)
                return;

            var outcome = approved ? "Approved" : "Rejected";
            var existing = message.Embeds.FirstOrDefault();

            var embed = existing == null
                ? new EmbedBuilder().WithTitle(form.Name)
                : existing.ToEmbedBuilder();

            embed.WithColor(approved ? Mewdeko.OkColor : Mewdeko.ErrorColor);

            embed.AddField(outcome, $"by <@{reviewerId}> <t:{DateTimeOffset.UtcNow.ToUnixTimeSeconds()}:R>");

            if (!string.IsNullOrWhiteSpace(notes))
                embed.AddField("Reason", notes.TrimTo(1024));

            if (workflow.DmFailed)
                embed.AddField("Note", "The submitter could not be messaged, so they have not been told.");
            reviewMessages.TryRemove(messageId, out _);
            await message.ModifyAsync(m => m.Embed = embed.Build());
            await message.RemoveAllReactionsAsync();
        }
        catch (Exception ex)
        {
            // The decision is already recorded, so a message that cannot be tidied up is not worth
            // failing the interaction over.
            logger.LogWarning(ex, "Could not settle the review message for response {ResponseId}", responseId);
        }
    }
}