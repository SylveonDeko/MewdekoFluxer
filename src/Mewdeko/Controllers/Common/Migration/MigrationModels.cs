namespace Mewdeko.Controllers.Common.Migration;

/// <summary>
///     Asks which Discord servers a signed in Discord user can bring over.
/// </summary>
/// <param name="DiscordUserId">The Discord user, from Discord sign in.</param>
/// <param name="GuildIds">The Discord servers the user is in, from Discord sign in.</param>
public sealed record DiscordServersRequest(ulong DiscordUserId, List<ulong> GuildIds);

/// <summary>
///     Asks Mewdeko on Discord to export a server.
/// </summary>
/// <param name="DiscordUserId">The Discord user, from Discord sign in.</param>
/// <param name="DiscordGuildId">The Discord server to export.</param>
/// <param name="BotId">The Mewdeko instance on Discord whose data to export, as listed with the server.</param>
public sealed record MigrationExportRequest(ulong DiscordUserId, ulong DiscordGuildId, ulong BotId);

/// <summary>
///     Identifies an export and any channel or role pairs chosen by hand.
/// </summary>
/// <param name="Code">The export's code.</param>
/// <param name="ChannelOverrides">Discord channel ids to Fluxer channel ids.</param>
/// <param name="RoleOverrides">Discord role ids to Fluxer role ids.</param>
/// <param name="UserId">The Fluxer user confirming the migration, for applying.</param>
public sealed record MigrationPlanRequest(
    string Code,
    Dictionary<ulong, ulong>? ChannelOverrides,
    Dictionary<ulong, ulong>? RoleOverrides,
    ulong UserId = 0);

/// <summary>
///     Links a Fluxer account to a Discord account proved by Discord sign in.
/// </summary>
/// <param name="UserId">The Fluxer user.</param>
/// <param name="DiscordUserId">The Discord user.</param>
public sealed record MigrationLinkRequest(ulong UserId, ulong DiscordUserId);

/// <summary>
///     A Discord channel in a migration preview, with the Fluxer channel it was paired with.
/// </summary>
public sealed record MigrationChannelPreview(string Id, string Name, int Type, string? ParentName, string? FluxerId);

/// <summary>
///     A Discord role in a migration preview, with the Fluxer role it was paired with.
/// </summary>
public sealed record MigrationRolePreview(string Id, string Name, uint Color, string? FluxerId);

/// <summary>
///     What a migration would do.
/// </summary>
public sealed record MigrationPreviewResponse(
    string Code,
    string DiscordGuildId,
    string DiscordGuildName,
    List<MigrationChannelPreview> Channels,
    List<MigrationRolePreview> Roles,
    int EmojisMatched,
    int EmojisTotal,
    int Tables,
    int SettingRows,
    int MemberRows,
    int Members,
    int LinkedMembers);
