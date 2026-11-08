using Mewdeko.Modules.Administration.Services;

namespace Mewdeko.Modules.Server_Management.Services;

/// <summary>
///     Why a channel edit was refused.
/// </summary>
public enum ChannelEditError
{
    /// <summary>
    ///     Nothing is wrong.
    /// </summary>
    None,

    /// <summary>
    ///     A setting does not apply to this kind of channel.
    /// </summary>
    Unsupported,

    /// <summary>
    ///     The name is empty or too long.
    /// </summary>
    BadName,

    /// <summary>
    ///     The bitrate is out of range.
    /// </summary>
    BadBitrate,

    /// <summary>
    ///     The user limit is out of range.
    /// </summary>
    BadUserLimit,

    /// <summary>
    ///     The slowmode is out of range.
    /// </summary>
    BadSlowmode,

    /// <summary>
    ///     The position is out of range.
    /// </summary>
    BadPosition
}

/// <summary>
///     What to change on a channel. Settings left null stay as they are.
/// </summary>
/// <param name="Name">The new name.</param>
/// <param name="Topic">The new topic. An empty topic clears it.</param>
/// <param name="Nsfw">Whether the channel is age restricted.</param>
/// <param name="SlowMode">The slowmode in seconds.</param>
/// <param name="BitrateKbps">The voice bitrate in kbps.</param>
/// <param name="UserLimit">The voice user limit. Zero means no limit.</param>
/// <param name="CategoryId">The category to move the channel to.</param>
/// <param name="RemoveCategory">Whether to take the channel out of its category.</param>
/// <param name="Position">The position, counting from zero.</param>
public record ChannelEditRequest(
    string? Name = null,
    string? Topic = null,
    bool? Nsfw = null,
    int? SlowMode = null,
    int? BitrateKbps = null,
    int? UserLimit = null,
    ulong? CategoryId = null,
    bool RemoveCategory = false,
    int? Position = null);

/// <summary>
///     Edits, syncs and clones channels.
/// </summary>
public class ChannelEditService : INService
{
    /// <summary>
    ///     The longest slowmode allowed, in seconds.
    /// </summary>
    public const int MaxSlowMode = 21600;

    /// <summary>
    ///     The most users a voice channel can be limited to.
    /// </summary>
    public const int MaxUserLimit = 99;

    /// <summary>
    ///     The highest voice bitrate accepted, in kbps.
    /// </summary>
    public const int MaxBitrateKbps = 384;

    /// <summary>
    ///     Checks an edit against the kind of channel and the allowed ranges.
    /// </summary>
    /// <param name="channel">The channel.</param>
    /// <param name="request">The edit.</param>
    /// <returns>The first problem found, or none.</returns>
    public static ChannelEditError Validate(Channel channel, ChannelEditRequest request)
    {
        var voice = channel.Type is ChannelType.GuildVoice or ChannelType.GuildStageVoice;
        var text = channel.Type is ChannelType.GuildText or ChannelType.GuildNews;
        var forum = channel.Type is ChannelType.GuildForum or ChannelType.GuildMedia;
        var category = channel.Type == ChannelType.GuildCategory;

        if (request.Name is { } name && (name.Trim().Length is 0 or > 100))
            return ChannelEditError.BadName;
        if (request.Topic is not null && !(text || forum))
            return ChannelEditError.Unsupported;
        if (request.Nsfw is not null && !(text || forum))
            return ChannelEditError.Unsupported;
        if (request.SlowMode is not null && !text)
            return ChannelEditError.Unsupported;
        if (request.BitrateKbps is not null && !voice)
            return ChannelEditError.Unsupported;
        if (request.UserLimit is not null && !voice)
            return ChannelEditError.Unsupported;
        if ((request.CategoryId is not null || request.RemoveCategory) && category)
            return ChannelEditError.Unsupported;

        if (request.SlowMode is < 0 or > MaxSlowMode)
            return ChannelEditError.BadSlowmode;
        if (request.BitrateKbps is { } kbps && (kbps < 8 || kbps > MaxBitrateKbps))
            return ChannelEditError.BadBitrate;
        if (request.UserLimit is < 0 or > MaxUserLimit)
            return ChannelEditError.BadUserLimit;
        if (request.Position is < 0)
            return ChannelEditError.BadPosition;

        return ChannelEditError.None;
    }

    /// <summary>
    ///     Applies an edit to a channel.
    /// </summary>
    /// <param name="channel">The channel.</param>
    /// <param name="request">The edit.</param>
    /// <returns>The problem if the edit was refused, otherwise none.</returns>
    public async Task<ChannelEditError> EditAsync(Channel channel, ChannelEditRequest request)
    {
        var error = Validate(channel, request);
        if (error != ChannelEditError.None)
            return error;

        var update = new UpdateChannelRequest();
        var changed = false;

        if (request.Name?.Trim() is { } name)
        {
            update.Name = name;
            changed = true;
        }

        if (request.Topic is { } topic)
        {
            update.Topic = Optional.Some<string?>(topic.Length == 0 ? null : topic);
            changed = true;
        }

        if (request.Nsfw is { } nsfw)
        {
            update.IsNsfw = Optional.Some<bool?>(nsfw);
            changed = true;
        }

        if (request.SlowMode is { } slow)
        {
            update.RateLimitPerUser = Optional.Some<int?>(slow);
            changed = true;
        }

        if (request.BitrateKbps is { } kbps)
        {
            update.Bitrate = Optional.Some<int?>(kbps * 1000);
            changed = true;
        }

        if (request.UserLimit is { } limit)
        {
            update.UserLimit = Optional.Some<int?>(limit == 0 ? null : limit);
            changed = true;
        }

        if (request.CategoryId is not null || request.RemoveCategory)
        {
            update.ParentId = Optional.Some<ulong?>(request.RemoveCategory ? null : request.CategoryId);
            changed = true;
        }

        if (changed)
            await channel.ModifyAsync(update);

        if (request.Position is { } position && channel.Guild is { } guild)
        {
            await guild.UpdateChannelPositionsAsync([
                new ChannelPositionUpdateRequestItem
                {
                    Id = channel.Id, Position = position
                }
            ]);
        }

        return ChannelEditError.None;
    }

    /// <summary>
    ///     Makes a channel's permissions match its category.
    /// </summary>
    /// <param name="channel">The channel.</param>
    /// <returns>False if the channel has no category to sync with.</returns>
    public async Task<bool> SyncPermissionsAsync(Channel channel)
    {
        if (channel.ParentId is not { } parent || channel.Guild is not { } guild)
            return false;

        await guild.UpdateChannelPositionsAsync([
            new ChannelPositionUpdateRequestItem
            {
                Id = channel.Id, ParentId = parent, LockPermissions = true
            }
        ]);
        return true;
    }

    /// <summary>
    ///     Creates a copy of a channel with the same settings and permissions, right below the original.
    /// </summary>
    /// <param name="channel">The channel to copy.</param>
    /// <param name="name">The name of the copy, or null to keep the name.</param>
    /// <returns>The copy, or null if this kind of channel cannot be copied.</returns>
    public async Task<Channel?> CloneAsync(Channel channel, string? name)
    {
        if (channel.Guild is not { } guild || ServerBackupService.CaptureChannel(channel) is not { } entry)
            return null;

        if (!string.IsNullOrWhiteSpace(name))
            entry.Name = name.Trim();

        var overwrites = (channel.PermissionOverwrites ?? []).Select(OverwritePermissions.ToRequest).ToArray();
        var copy = await ServerBackupService.CreateChannelAsync(guild, entry, entry.ParentId, overwrites);
        try
        {
            await guild.UpdateChannelPositionsAsync([
                new ChannelPositionUpdateRequestItem
                {
                    Id = copy.Id, Position = (channel.Position ?? 0) + 1
                }
            ]);
        }
        catch (Exception)
        {
        }

        return copy;
    }
}
