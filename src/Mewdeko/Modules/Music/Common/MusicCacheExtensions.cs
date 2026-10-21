using System.Text.Json;
using System.Text.Json.Serialization;
using StackExchange.Redis;

namespace Mewdeko.Modules.Music.Common;

/// <summary>
///     Redis backed storage for live music player state, kept next to the music module.
/// </summary>
public static class MusicCacheExtensions
{
    private const string Prefix = "mewdeko:music";

    private static readonly JsonSerializerOptions Options = new()
    {
        ReferenceHandler = ReferenceHandler.IgnoreCycles
    };

    /// <summary>
    ///     Saves the player state for a guild.
    /// </summary>
    /// <param name="cache">The data cache.</param>
    /// <param name="guildId">The ID of the guild.</param>
    /// <param name="state">The player state to save.</param>
    /// <returns>A task representing the asynchronous operation.</returns>
    public static Task SetPlayerState(this IDataCache cache, ulong guildId, MusicPlayerState state)
    {
        return cache.Redis.GetDatabase().StringSetAsync($"{Prefix}:player_state:{guildId}",
            JsonSerializer.Serialize(state), TimeSpan.FromHours(1));
    }

    /// <summary>
    ///     Retrieves the player state for a guild.
    /// </summary>
    /// <param name="cache">The data cache.</param>
    /// <param name="guildId">The ID of the guild.</param>
    /// <returns>The player state if found; otherwise, null.</returns>
    public static async Task<MusicPlayerState?> GetPlayerState(this IDataCache cache, ulong guildId)
    {
        var result = await cache.Redis.GetDatabase().StringGetAsync($"{Prefix}:player_state:{guildId}");
        return result.IsNull ? null : JsonSerializer.Deserialize<MusicPlayerState>((string)result!);
    }

    /// <summary>
    ///     Removes the player state for a guild.
    /// </summary>
    /// <param name="cache">The data cache.</param>
    /// <param name="guildId">The ID of the guild.</param>
    /// <returns>A task representing the asynchronous operation.</returns>
    public static Task RemovePlayerState(this IDataCache cache, ulong guildId)
    {
        return cache.Redis.GetDatabase().KeyDeleteAsync($"{Prefix}:player_state:{guildId}");
    }

    /// <summary>
    ///     Gets the music queue for a guild.
    /// </summary>
    /// <param name="cache">The data cache.</param>
    /// <param name="id">The guild ID.</param>
    /// <returns>A list of tracks.</returns>
    public static async Task<List<MewdekoTrack>> GetMusicQueue(this IDataCache cache, ulong id)
    {
        var result = await cache.Redis.GetDatabase().StringGetAsync($"{Prefix}:queue:{id}");
        return result.HasValue ? JsonSerializer.Deserialize<List<MewdekoTrack>>((string)result!, Options) ?? [] : [];
    }

    /// <summary>
    ///     Sets the music queue for a guild.
    /// </summary>
    /// <param name="cache">The data cache.</param>
    /// <param name="id">The guild ID.</param>
    /// <param name="tracks">The tracks to store.</param>
    /// <returns>A task representing the asynchronous operation.</returns>
    public static Task SetMusicQueue(this IDataCache cache, ulong id, List<MewdekoTrack> tracks)
    {
        return cache.Redis.GetDatabase().StringSetAsync($"{Prefix}:queue:{id}",
            JsonSerializer.Serialize(tracks, Options));
    }

    /// <summary>
    ///     Sets the current track for a guild.
    /// </summary>
    /// <param name="cache">The data cache.</param>
    /// <param name="id">The guild ID.</param>
    /// <param name="track">The track to set, or null to clear it.</param>
    /// <returns>A task representing the asynchronous operation.</returns>
    public static async Task SetCurrentTrack(this IDataCache cache, ulong id, MewdekoTrack? track)
    {
        var db = cache.Redis.GetDatabase();
        if (track is null)
            await db.KeyDeleteAsync($"{Prefix}:current:{id}");
        else
            await db.StringSetAsync($"{Prefix}:current:{id}", JsonSerializer.Serialize(track, Options));
    }

    /// <summary>
    ///     Gets the current track for a guild.
    /// </summary>
    /// <param name="cache">The data cache.</param>
    /// <param name="id">The guild ID.</param>
    /// <returns>The current track, or null.</returns>
    public static async Task<MewdekoTrack?> GetCurrentTrack(this IDataCache cache, ulong id)
    {
        var result = await cache.Redis.GetDatabase().StringGetAsync($"{Prefix}:current:{id}");
        return result.HasValue ? JsonSerializer.Deserialize<MewdekoTrack>((string)result!, Options) : null;
    }
}
