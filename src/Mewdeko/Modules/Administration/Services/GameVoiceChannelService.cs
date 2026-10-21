namespace Mewdeko.Modules.Administration.Services;

/// <summary>
///     Service for storing the game voice channel of a guild.
/// </summary>
public class GameVoiceChannelService : INService
{
    private readonly GuildSettingsService guildSettings;
    private readonly ILogger<GameVoiceChannelService> logger;

    /// <summary>
    ///     Constructs a new instance of the GameVoiceChannelService.
    /// </summary>
    /// <param name="guildSettings">The guild settings service.</param>
    /// <param name="logger">The logger instance for structured logging.</param>
    public GameVoiceChannelService(GuildSettingsService guildSettings, ILogger<GameVoiceChannelService> logger)
    {
        this.guildSettings = guildSettings;
        this.logger = logger;
    }

    /// <summary>
    ///     Toggles the game voice channel for a guild.
    /// </summary>
    /// <param name="guildId">The ID of the guild.</param>
    /// <param name="vchId">The ID of the voice channel.</param>
    /// <returns>
    ///     A task that represents the asynchronous operation. The task result contains the ID of the toggled game voice
    ///     channel.
    /// </returns>
    public async Task<ulong?> ToggleGameVoiceChannel(ulong guildId, ulong vchId)
    {
        ulong? id;
        var gc = await guildSettings.GetGuildConfig(guildId);

        if (gc == null)
        {
            logger.LogWarning("GuildConfig is null for GuildId {GuildId} in ToggleGameVoiceChannel", guildId);
            return null;
        }

        if (gc.GameVoiceChannel == vchId)
        {
            id = gc.GameVoiceChannel = 0;
            await guildSettings.UpdateGuildConfig(guildId, gc);
        }
        else
        {
            id = gc.GameVoiceChannel = vchId;
            await guildSettings.UpdateGuildConfig(guildId, gc);
        }

        return id;
    }
}
