using System.Threading;

namespace Mewdeko.Common.FluxerAdapter;

/// <summary>
///     Client members Mewdeko relies on that Fluxer.Net names differently or does not have.
/// </summary>
public static class ClientAdapter
{
    private static readonly Lock PresenceLock = new();
    private static UserStatus currentStatus = UserStatus.Online;
    private static string? currentStatusText;

    extension(FluxerClient client)
    {
        /// <summary>
        ///     The bot's own user, once the gateway is ready.
        /// </summary>
        public SocketCurrentUser CurrentUser => client.Gateway.CurrentUser!;

        /// <summary>
        ///     Every guild the bot is in, from the gateway cache.
        /// </summary>
        public IReadOnlyCollection<SocketGuild> Guilds => client.Gateway.Guilds.Values.ToList();

        /// <summary>
        ///     Gets a cached guild.
        /// </summary>
        public SocketGuild? GetGuild(ulong guildId) => client.Gateway.GetGuild(guildId);

        /// <summary>
        ///     Gets a cached channel.
        /// </summary>
        public Channel? GetChannel(ulong channelId) => client.Gateway.GetChannel(channelId);

        /// <summary>
        ///     Gets a user from any guild the bot shares with them.
        /// </summary>
        public User? GetUser(ulong userId) =>
            client.Gateway.Guilds.Values.Select(g => g.Members.GetValueOrDefault(userId)).FirstOrDefault(m => m != null)
                ?.User;

        /// <summary>
        ///     Gets a user from the cache or the API.
        /// </summary>
        public async Task<User?> GetUserAsync(ulong userId) =>
            client.GetUser(userId) ?? await client.Rest.GetUserAsync(userId).ConfigureAwait(false);

        /// <summary>
        ///     Gets a channel from the cache or the API.
        /// </summary>
        public async Task<Channel?> GetChannelAsync(ulong channelId) =>
            client.GetChannel(channelId) ?? await client.Rest.GetChannelAsync(channelId).ConfigureAwait(false);

        /// <summary>
        ///     The shard a guild is on. Fluxer has no sharding, so every guild is on shard 0.
        /// </summary>
        public int GetShardIdFor(SocketGuild? guild) => 0;

        /// <summary>
        ///     Milliseconds between the last gateway heartbeat and its acknowledgement.
        /// </summary>
        public int Latency => client.Gateway.Latency;

        /// <summary>
        ///     The online status the bot last set.
        /// </summary>
        public UserStatus Status
        {
            get
            {
                lock (PresenceLock)
                    return currentStatus;
            }
        }

        /// <summary>
        ///     Sets the bot's online status and keeps its custom status.
        /// </summary>
        public Task SetStatusAsync(UserStatus status)
        {
            lock (PresenceLock)
                currentStatus = status;
            client.SendPresence();
            return Task.CompletedTask;
        }

        /// <summary>
        ///     Sets the bot's custom status text and keeps its online status. Null or empty clears it.
        /// </summary>
        public Task SetCustomStatusAsync(string? text)
        {
            lock (PresenceLock)
                currentStatusText = string.IsNullOrWhiteSpace(text) ? null : text;
            client.SendPresence();
            return Task.CompletedTask;
        }

        private void SendPresence()
        {
            lock (PresenceLock)
            {
                client.Gateway.SetStatus(currentStatus.ToFluxerStatus(),
                    custom: new UserCustomStatusJson { Text = currentStatusText });
            }
        }
    }
}
