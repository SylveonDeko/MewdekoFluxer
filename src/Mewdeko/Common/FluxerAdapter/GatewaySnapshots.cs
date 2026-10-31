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
///     Gives synchronous access to the latest presence of a user and the current voice state of a member, which the
///     gateway cache only offers asynchronously or per voice session.
/// </summary>
public sealed class GatewaySnapshotCache
{
    private readonly ConcurrentDictionary<ulong, PresenceSnapshot> presences = new();

    /// <summary>
    ///     Gets a user's last known presence.
    /// </summary>
    public PresenceSnapshot? GetPresence(ulong userId) => presences.GetValueOrDefault(userId);

    /// <summary>
    ///     Gets a member's current voice state.
    /// </summary>
    public VoiceStateSnapshot GetVoiceState(ulong guildId, ulong userId) =>
        FluxerContext.Client.Gateway.GetGuild(guildId)?.Members.GetValueOrDefault(userId)?.VoiceStates.Values
            .Where(state => state.ChannelId is not null)
            .Select(VoiceStateSnapshot.From)
            .FirstOrDefault() ?? VoiceStateSnapshot.None;

    /// <summary>
    ///     Records a user's latest presence.
    /// </summary>
    public void SetPresence(PresenceSnapshot presence) => presences[presence.UserId] = presence;
}
