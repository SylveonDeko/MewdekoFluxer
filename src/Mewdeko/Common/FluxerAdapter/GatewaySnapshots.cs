using Fluxer.Net.Gateway;
using Channel = Fluxer.Net.Channel;

namespace Mewdeko.Common.FluxerAdapter;

/// <summary>
///     A user as it was when last seen.
/// </summary>
public sealed record UserSnapshot(
    ulong Id,
    string Username,
    string Discriminator,
    string? DisplayName,
    string? AvatarHash,
    bool IsBot)
{
    /// <summary>
    ///     Takes a snapshot of a user.
    /// </summary>
    public static UserSnapshot From(User user) =>
        new(user.Id, user.Username, user.Discriminator, user.DisplayName, user.AvatarHash, user.IsBot);

    /// <summary>
    ///     Builds a user object from the snapshot, for code that needs a user when the original is gone.
    /// </summary>
    public User ToUser() => User.Create(FluxerContext.Client, new UserJson
    {
        Id = Id,
        Username = Username,
        Discriminator = Discriminator,
        DisplayName = DisplayName,
        AvatarHash = AvatarHash,
        IsBot = IsBot
    });

    /// <inheritdoc />
    public override string ToString() => ToUser().ToString();
}

/// <summary>
///     A guild member as it was before the latest update.
/// </summary>
public sealed record MemberSnapshot(
    ulong GuildId,
    UserSnapshot User,
    string? Nickname,
    string? AvatarHash,
    IReadOnlySet<ulong> RoleIds,
    DateTimeOffset? TimedOutUntil,
    DateTimeOffset JoinedAt)
{
    /// <summary>The member's user id.</summary>
    public ulong Id => User.Id;

    /// <summary>The name shown in the guild.</summary>
    public string DisplayName => Nickname ?? User.DisplayName ?? User.Username;

    /// <summary>
    ///     Takes a snapshot of a member.
    /// </summary>
    public static MemberSnapshot From(GuildMember member) =>
        new(member.GuildId, UserSnapshot.From(member.User), member.Nickname, member.AvatarHash,
            member.RoleIds.ToHashSet(), member.CommunicationDisabledUntil, member.JoinedAt);
}

/// <summary>
///     A member's voice connection at one moment. A member outside voice has no channel.
/// </summary>
public sealed record VoiceStateSnapshot(
    ulong? ChannelId,
    bool IsMuted,
    bool IsDeafened,
    bool IsSelfMuted,
    bool IsSelfDeafened,
    bool IsStreaming,
    bool IsVideoing,
    bool IsSuppressed)
{
    /// <summary>The state of a member who is not in voice.</summary>
    public static VoiceStateSnapshot None { get; } = new(null, false, false, false, false, false, false, false);

    /// <summary>The voice channel, from the gateway cache.</summary>
    public Channel? VoiceChannel => ChannelId is { } id ? FluxerContext.Client.Gateway.GetChannel(id) : null;

    /// <summary>
    ///     Takes a snapshot of a voice state.
    /// </summary>
    public static VoiceStateSnapshot From(VoiceState state) =>
        new(state.ChannelId, state.IsMute, state.IsDeaf, state.IsSelfMute, state.IsSelfDeaf, state.IsSelfStream,
            state.IsSelfVideo, state.IsSuppressed);
}

/// <summary>
///     A user's status and custom status.
/// </summary>
public sealed record PresenceSnapshot(
    ulong UserId,
    UserStatus Status,
    string? CustomStatusText,
    string? CustomStatusEmojiName,
    ulong? CustomStatusEmojiId)
{
    /// <summary>
    ///     Reads a presence update.
    /// </summary>
    public static PresenceSnapshot From(PresenceGatewayData data) =>
        new(data.User.Id, UserStatusExtensions.ParseUserStatus(data.Status), data.CustomStatus?.Text,
            data.CustomStatus?.EmojiName, data.CustomStatus?.EmojiId);
}

/// <summary>
///     Remembers what members, users, voice states, presences and recent messages looked like, because Fluxer only
///     sends the new state and Mewdeko's events report what changed.
/// </summary>
public sealed class GatewaySnapshotCache
{
    private const int MessagesPerChannel = 15;

    private readonly ConcurrentDictionary<ulong, System.Collections.Concurrent.ConcurrentQueue<ulong>> channelMessages =
        new();
    private readonly ConcurrentDictionary<(ulong GuildId, ulong UserId), MemberSnapshot> members = new();
    private readonly ConcurrentDictionary<ulong, Message> messages = new();
    private readonly ConcurrentDictionary<ulong, PresenceSnapshot> presences = new();
    private readonly ConcurrentDictionary<ulong, UserSnapshot> users = new();
    private readonly ConcurrentDictionary<(ulong GuildId, ulong UserId), VoiceStateSnapshot> voiceStates = new();

    /// <summary>
    ///     Gets a recent message, if it is still cached.
    /// </summary>
    public Message? GetMessage(ulong messageId) => messages.GetValueOrDefault(messageId);

    /// <summary>
    ///     Gets the last known state of a member.
    /// </summary>
    public MemberSnapshot? GetMember(ulong guildId, ulong userId) => members.GetValueOrDefault((guildId, userId));

    /// <summary>
    ///     Gets the last known state of a user.
    /// </summary>
    public UserSnapshot? GetUser(ulong userId) => users.GetValueOrDefault(userId);

    /// <summary>
    ///     Gets a user's last known presence.
    /// </summary>
    public PresenceSnapshot? GetPresence(ulong userId) => presences.GetValueOrDefault(userId);

    /// <summary>
    ///     Gets a member's last known voice state.
    /// </summary>
    public VoiceStateSnapshot GetVoiceState(ulong guildId, ulong userId) =>
        voiceStates.GetValueOrDefault((guildId, userId), VoiceStateSnapshot.None);

    /// <summary>
    ///     Records every member and voice connection of a guild that just became available.
    /// </summary>
    public void SeedGuild(SocketGuild guild)
    {
        foreach (var member in guild.Members.Values)
        {
            SeedMember(member);
            foreach (var state in member.VoiceStates.Values)
                voiceStates[(guild.Id, member.Id)] = VoiceStateSnapshot.From(state);
        }
    }

    /// <summary>
    ///     Forgets everything about a guild the bot left.
    /// </summary>
    public void ForgetGuild(ulong guildId)
    {
        foreach (var key in members.Keys.Where(k => k.GuildId == guildId))
            members.TryRemove(key, out _);
        foreach (var key in voiceStates.Keys.Where(k => k.GuildId == guildId))
            voiceStates.TryRemove(key, out _);
    }

    /// <summary>
    ///     Records a member that has not been seen yet.
    /// </summary>
    public void SeedMember(GuildMember member)
    {
        members.TryAdd((member.GuildId, member.Id), MemberSnapshot.From(member));
        users.TryAdd(member.Id, UserSnapshot.From(member.User));
    }

    /// <summary>
    ///     Records a member's new state and returns the one before it.
    /// </summary>
    public MemberSnapshot? SwapMember(GuildMember member)
    {
        var after = MemberSnapshot.From(member);
        MemberSnapshot? before = null;
        members.AddOrUpdate((member.GuildId, member.Id), after, (_, old) =>
        {
            before = old;
            return after;
        });
        return before;
    }

    /// <summary>
    ///     Forgets a member who left and returns their last state.
    /// </summary>
    public MemberSnapshot? RemoveMember(ulong guildId, ulong userId)
    {
        voiceStates.TryRemove((guildId, userId), out _);
        return members.TryRemove((guildId, userId), out var before) ? before : null;
    }

    /// <summary>
    ///     Records a user's new state and returns the one before it, or null when it did not change.
    /// </summary>
    public UserSnapshot? SwapUser(User user)
    {
        var after = UserSnapshot.From(user);
        UserSnapshot? before = null;
        users.AddOrUpdate(user.Id, after, (_, old) =>
        {
            before = old;
            return after;
        });
        return before == after ? null : before;
    }

    /// <summary>
    ///     Records a member's new voice state and returns the one before it.
    /// </summary>
    public VoiceStateSnapshot SwapVoiceState(ulong guildId, ulong userId, VoiceStateSnapshot after)
    {
        var before = VoiceStateSnapshot.None;
        if (after.ChannelId is null)
        {
            if (voiceStates.TryRemove((guildId, userId), out var removed))
                before = removed;
            return before;
        }

        voiceStates.AddOrUpdate((guildId, userId), after, (_, old) =>
        {
            before = old;
            return after;
        });
        return before;
    }

    /// <summary>
    ///     Records a user's new presence and returns the one before it.
    /// </summary>
    public PresenceSnapshot? SwapPresence(PresenceSnapshot after)
    {
        PresenceSnapshot? before = null;
        presences.AddOrUpdate(after.UserId, after, (_, old) =>
        {
            before = old;
            return after;
        });
        return before;
    }

    /// <summary>
    ///     Caches a new message, dropping the oldest one in its channel once the channel holds too many.
    /// </summary>
    public void AddMessage(Message message)
    {
        messages[message.Id] = message;
        var queue = channelMessages.GetOrAdd(message.ChannelId,
            _ => new System.Collections.Concurrent.ConcurrentQueue<ulong>());
        queue.Enqueue(message.Id);
        while (queue.Count > MessagesPerChannel && queue.TryDequeue(out var oldest))
            messages.TryRemove(oldest, out _);
    }

    /// <summary>
    ///     Caches an edited message and returns the version before the edit, if it was cached.
    /// </summary>
    public Message? SwapMessage(Message message)
    {
        if (!messages.TryGetValue(message.Id, out var before))
            return null;
        messages[message.Id] = message;
        return before;
    }

    /// <summary>
    ///     Forgets a deleted message and returns it, if it was cached.
    /// </summary>
    public Message? RemoveMessage(ulong messageId) => messages.TryRemove(messageId, out var message) ? message : null;

    /// <summary>
    ///     Forgets a deleted channel's messages.
    /// </summary>
    public void ForgetChannel(ulong channelId)
    {
        if (!channelMessages.TryRemove(channelId, out var queue))
            return;
        foreach (var id in queue)
            messages.TryRemove(id, out _);
    }
}
