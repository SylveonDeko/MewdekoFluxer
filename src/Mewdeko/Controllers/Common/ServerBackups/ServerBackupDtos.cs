using Mewdeko.Modules.Administration.Common;

namespace Mewdeko.Controllers.Common.ServerBackups;

/// <summary>
///     A backup as listed in the dashboard.
/// </summary>
public class ServerBackupDto
{
    /// <summary>
    ///     Gets or sets the backup ID.
    /// </summary>
    public int Id { get; set; }

    /// <summary>
    ///     Gets or sets the community the backup was taken from.
    /// </summary>
    public ulong SourceGuildId { get; set; }

    /// <summary>
    ///     Gets or sets the name the community had at the time.
    /// </summary>
    public string SourceGuildName { get; set; } = "";

    /// <summary>
    ///     Gets or sets the compressed size in bytes.
    /// </summary>
    public int SizeBytes { get; set; }

    /// <summary>
    ///     Gets or sets how many roles the backup holds.
    /// </summary>
    public int RoleCount { get; set; }

    /// <summary>
    ///     Gets or sets how many channels and categories the backup holds.
    /// </summary>
    public int ChannelCount { get; set; }

    /// <summary>
    ///     Gets or sets how many emojis the backup holds.
    /// </summary>
    public int EmojiCount { get; set; }

    /// <summary>
    ///     Gets or sets how many messages the backup holds.
    /// </summary>
    public int MessageCount { get; set; }

    /// <summary>
    ///     Gets or sets when the backup was taken.
    /// </summary>
    public DateTime CreatedAt { get; set; }
}

/// <summary>
///     A user's backups and the limits that apply to them.
/// </summary>
public class ServerBackupListResponse
{
    /// <summary>
    ///     Gets or sets the most backups one user can keep.
    /// </summary>
    public int MaxBackups { get; set; }

    /// <summary>
    ///     Gets or sets the minutes a user has to wait between backups.
    /// </summary>
    public int CooldownMinutes { get; set; }

    /// <summary>
    ///     Gets or sets the backups.
    /// </summary>
    public List<ServerBackupDto> Backups { get; set; } = [];
}

/// <summary>
///     Request to back up a community.
/// </summary>
public class CreateServerBackupRequest
{
    /// <summary>
    ///     Gets or sets how many recent messages to save from each chosen channel.
    /// </summary>
    public int Messages { get; set; }

    /// <summary>
    ///     Gets or sets the channels to save messages from.
    /// </summary>
    public List<ulong> ChannelIds { get; set; } = [];
}

/// <summary>
///     Request to restore a backup.
/// </summary>
public class RestoreServerBackupRequest
{
    /// <summary>
    ///     Gets or sets the restore mode: Replace or AddMissing.
    /// </summary>
    public string Mode { get; set; } = "Replace";
}

/// <summary>
///     A role shown in a backup preview.
/// </summary>
public class BackupPreviewRoleDto
{
    /// <summary>
    ///     Gets or sets the role name.
    /// </summary>
    public string Name { get; set; } = "";

    /// <summary>
    ///     Gets or sets the raw colour value.
    /// </summary>
    public uint Color { get; set; }
}

/// <summary>
///     A channel shown in a backup preview.
/// </summary>
public class BackupPreviewChannelDto
{
    /// <summary>
    ///     Gets or sets the channel name.
    /// </summary>
    public string Name { get; set; } = "";

    /// <summary>
    ///     Gets or sets the kind: category, text, voice or forum.
    /// </summary>
    public string Type { get; set; } = "";

    /// <summary>
    ///     Gets or sets the name of the category the channel is in.
    /// </summary>
    public string? Category { get; set; }
}

/// <summary>
///     What a backup or template holds.
/// </summary>
public class BackupPreviewDto
{
    /// <summary>
    ///     Gets or sets the community name.
    /// </summary>
    public string Name { get; set; } = "";

    /// <summary>
    ///     Gets or sets the roles, without @everyone.
    /// </summary>
    public List<BackupPreviewRoleDto> Roles { get; set; } = [];

    /// <summary>
    ///     Gets or sets the categories and channels.
    /// </summary>
    public List<BackupPreviewChannelDto> Channels { get; set; } = [];

    /// <summary>
    ///     Gets or sets how many emojis it holds.
    /// </summary>
    public int EmojiCount { get; set; }

    /// <summary>
    ///     Gets or sets how many messages it holds.
    /// </summary>
    public int MessageCount { get; set; }

    /// <summary>
    ///     Builds a preview from backup data.
    /// </summary>
    /// <param name="data">The backup data.</param>
    /// <returns>The preview.</returns>
    public static BackupPreviewDto From(ServerBackupData data)
    {
        var names = data.Channels.ToDictionary(x => x.Id, x => x.Name);
        return new BackupPreviewDto
        {
            Name = data.Settings.Name,
            Roles = data.Roles.Where(x => !x.IsEveryone).OrderBy(x => x.Position)
                .Select(x => new BackupPreviewRoleDto
                {
                    Name = x.Name, Color = x.Color
                }).ToList(),
            Channels = data.Channels.OrderBy(x => x.Position).Select(x => new BackupPreviewChannelDto
            {
                Name = x.Name,
                Type = x.Type,
                Category = x.ParentId is { } parent && names.TryGetValue(parent, out var name) ? name : null
            }).ToList(),
            EmojiCount = data.Emojis.Count,
            MessageCount = data.Messages.Sum(x => x.Messages.Count)
        };
    }
}

/// <summary>
///     How far the latest restore into a community has got.
/// </summary>
public class RestoreStatusDto
{
    /// <summary>
    ///     Gets or sets whether a restore was started.
    /// </summary>
    public bool Started { get; set; }

    /// <summary>
    ///     Gets or sets whether the restore is over.
    /// </summary>
    public bool Finished { get; set; }

    /// <summary>
    ///     Gets or sets what the restore is doing now.
    /// </summary>
    public string Step { get; set; } = "";

    /// <summary>
    ///     Gets or sets how many roles were created.
    /// </summary>
    public int RolesCreated { get; set; }

    /// <summary>
    ///     Gets or sets how many channels and categories were created.
    /// </summary>
    public int ChannelsCreated { get; set; }

    /// <summary>
    ///     Gets or sets how many emojis were created.
    /// </summary>
    public int EmojisCreated { get; set; }

    /// <summary>
    ///     Gets or sets how many messages were posted.
    /// </summary>
    public int MessagesRestored { get; set; }

    /// <summary>
    ///     Gets or sets the things that failed or were skipped.
    /// </summary>
    public List<string> Failures { get; set; } = [];
}
