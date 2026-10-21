namespace Mewdeko.Common.FluxerAdapter;

/// <summary>
///     Guild, member, user and role members Mewdeko relies on that Fluxer.Net names differently or does not have.
/// </summary>
public static class GuildAdapter
{
    extension(SocketGuild guild)
    {
        /// <summary>
        ///     Every cached member of the guild.
        /// </summary>
        public IReadOnlyCollection<SocketGuildMember> Users => guild.Members.Values.ToList();

        /// <summary>
        ///     The bot's own membership in the guild.
        /// </summary>
        public SocketGuildMember CurrentUser => guild.CurrentMember;

        /// <summary>
        ///     The guild's cached roles.
        /// </summary>
        public IReadOnlyCollection<SocketRole> RoleList => guild.Roles.Values.ToList();

        /// <summary>
        ///     The guild's cached channels that hold messages.
        /// </summary>
        public IReadOnlyCollection<Channel> TextChannels =>
            guild.Channels.Values.Where(c => c.Type is ChannelType.GuildText or ChannelType.GuildNews).ToList();

        /// <summary>
        ///     The guild's cached voice channels.
        /// </summary>
        public IReadOnlyCollection<Channel> VoiceChannels =>
            guild.Channels.Values.Where(c => c.Type == ChannelType.GuildVoice).ToList();

        /// <summary>
        ///     The guild's cached categories.
        /// </summary>
        public IReadOnlyCollection<Channel> CategoryChannels =>
            guild.Channels.Values.Where(c => c.Type == ChannelType.GuildCategory).ToList();

        /// <summary>
        ///     The guild's cached threads.
        /// </summary>
        public IReadOnlyCollection<Channel> ThreadChannels =>
            guild.Channels.Values.Where(c => c.ThreadMetadata != null).ToList();

        /// <summary>
        ///     The member count the gateway reported, or the cached member count when it reported none.
        /// </summary>
        public int TotalMembers => guild.MemberCount ?? guild.Members.Count;

        /// <summary>
        ///     The guild icon, or null when it has none.
        /// </summary>
        public string? IconUrl => guild.GetIconUrl();

        /// <summary>
        ///     The guild banner, or null when it has none.
        /// </summary>
        public string? BannerUrl => guild.GetBannerUrl();

        /// <summary>
        ///     The guild's system channel, or null when it has none.
        /// </summary>
        public Channel? SystemChannel => guild.SystemChannelId is { } id ? guild.GetChannel(id) : null;

        /// <summary>
        ///     Gets a cached member.
        /// </summary>
        public SocketGuildMember? GetUser(ulong userId) => guild.Members.GetValueOrDefault(userId);

        /// <summary>
        ///     Gets a member from the cache or the API.
        /// </summary>
        public Task<SocketGuildMember?> GetUserAsync(ulong userId) => guild.GetMemberAsync(userId);

        /// <summary>
        ///     Bans a user, deleting their recent messages from the given number of days.
        /// </summary>
        public Task AddBanAsync(ulong userId, int pruneDays = 0, string? reason = null) =>
            guild.BanUserAsync(userId, reason, pruneDays > 0 ? pruneDays * 86400 : null);

        /// <summary>
        ///     Bans a user, deleting their recent messages from the given number of days.
        /// </summary>
        public Task AddBanAsync(IUser user, int pruneDays = 0, string? reason = null) =>
            guild.AddBanAsync(user.Id, pruneDays, reason);

        /// <summary>
        ///     Lifts a ban.
        /// </summary>
        public Task RemoveBanAsync(ulong userId) => guild.UnbanUserAsync(userId);

        /// <summary>
        ///     Lifts a ban.
        /// </summary>
        public Task RemoveBanAsync(IUser user) => guild.UnbanUserAsync(user.Id);

        /// <summary>
        ///     The guild's custom emojis.
        /// </summary>
        public IReadOnlyCollection<GuildEmoji> Emotes => guild.Emojis?.Values.ToList() ?? [];

        /// <summary>
        ///     The voice channel idle members are moved to, or null when there is none.
        /// </summary>
        public Channel? AFKChannel => guild.AfkChannelId is { } id ? guild.GetChannel(id) : null;

        /// <summary>
        ///     Gets a cached channel.
        /// </summary>
        public Task<Channel?> GetChannelAsync(ulong channelId) => Task.FromResult(guild.GetChannel(channelId));

        /// <summary>
        ///     Gets a cached channel that holds messages, or null when the id is not one.
        /// </summary>
        public Task<Channel?> GetTextChannelAsync(ulong channelId) =>
            Task.FromResult(guild.GetChannel(channelId) is { IsTextable: true } channel ? channel : null);

        /// <summary>
        ///     Gets a cached voice channel, or null when the id is not one.
        /// </summary>
        public Task<Channel?> GetVoiceChannelAsync(ulong channelId) =>
            Task.FromResult(guild.GetChannel(channelId) is { Type: ChannelType.GuildVoice } channel ? channel : null);

        /// <summary>
        ///     Gets the guild's channels that hold messages.
        /// </summary>
        public Task<IReadOnlyCollection<Channel>> GetTextChannelsAsync() =>
            Task.FromResult<IReadOnlyCollection<Channel>>(guild.TextChannels.ToList());

        /// <summary>
        ///     Gets the guild's voice channels.
        /// </summary>
        public Task<IReadOnlyCollection<Channel>> GetVoiceChannelsAsync() =>
            Task.FromResult<IReadOnlyCollection<Channel>>(guild.VoiceChannels.ToList());

        /// <summary>
        ///     Gets the guild's categories.
        /// </summary>
        public Task<IReadOnlyCollection<Channel>> GetCategoriesAsync() =>
            Task.FromResult<IReadOnlyCollection<Channel>>(guild.CategoryChannels.ToList());

        /// <summary>
        ///     Gets every member, downloading them first when the cache is incomplete.
        /// </summary>
        public async Task<IReadOnlyCollection<SocketGuildMember>> GetUsersAsync()
        {
            if (!guild.HasAllMembers)
                await guild.DownloadMembersAsync().ConfigureAwait(false);
            return guild.Members.Values.ToList();
        }

        /// <summary>
        ///     Downloads every member into the cache.
        /// </summary>
        public Task DownloadUsersAsync() => guild.DownloadMembersAsync();

        /// <summary>
        ///     Gets the bot's own membership in the guild.
        /// </summary>
        public Task<SocketGuildMember> GetCurrentUserAsync() => Task.FromResult(guild.CurrentMember);
    }

    extension(GuildMember member)
    {
        /// <summary>
        ///     The member's username.
        /// </summary>
        public string Username => member.User.Username;

        /// <summary>
        ///     The member's discriminator.
        /// </summary>
        public string Discriminator => member.User.Discriminator;

        /// <summary>
        ///     The member's nickname, display name or username, whichever is set first.
        /// </summary>
        public string DisplayName => member.GetCurrentName();

        /// <summary>
        ///     The member's account wide display name, if set.
        /// </summary>
        public string? GlobalName => member.User.DisplayName;

        /// <summary>
        ///     Whether the member is a bot.
        /// </summary>
        public bool IsBot => member.User.IsBot;

        /// <summary>
        ///     Adds several roles one after another.
        /// </summary>
        public async Task AddRolesAsync(IEnumerable<ulong> roleIds, RequestOptions? options = null)
        {
            foreach (var roleId in roleIds)
                await member.AddRoleAsync(roleId, options).ConfigureAwait(false);
        }

        /// <summary>
        ///     Adds several roles one after another.
        /// </summary>
        public Task AddRolesAsync(IEnumerable<Role> roles, RequestOptions? options = null) =>
            member.AddRolesAsync(roles.Select(r => r.Id), options);

        /// <summary>
        ///     Removes several roles one after another.
        /// </summary>
        public async Task RemoveRolesAsync(IEnumerable<ulong> roleIds)
        {
            foreach (var roleId in roleIds)
                await member.RemoveRoleAsync(roleId).ConfigureAwait(false);
        }

        /// <summary>
        ///     Removes several roles one after another.
        /// </summary>
        public Task RemoveRolesAsync(IEnumerable<Role> roles) => member.RemoveRolesAsync(roles.Select(r => r.Id));

        /// <summary>
        ///     Times the member out for the given length of time.
        /// </summary>
        public Task SetTimeOutAsync(TimeSpan span) => member.SetTimeoutAsync(DateTimeOffset.UtcNow + span);

        /// <summary>
        ///     Ends the member's time out.
        /// </summary>
        public Task RemoveTimeOutAsync() => member.SetTimeoutAsync(null);

        /// <summary>
        ///     Opens a direct message with the member.
        /// </summary>
        public Task<DMChannel> CreateDMChannelAsync() => member.User.GetOrCreateDMChannelAsync();

        /// <summary>
        ///     Sends the member a direct message.
        /// </summary>
        public Task<Message> SendAsync(string? text = null, EmbedRequest? embed = null,
            IEnumerable<EmbedRequest>? embeds = null) => member.User.SendAsync(text, embed, embeds);

        /// <summary>
        ///     Changes the member. Only the properties set by <paramref name="change" /> are sent.
        /// </summary>
        public Task<GuildMember> ModifyAsync(Action<UpdateGuildMemberRequest> change, RequestOptions? options = null)
        {
            var request = new UpdateGuildMemberRequest();
            change(request);
            return member.ModifyAsync(request, options);
        }

        /// <summary>
        ///     The member's guild avatar, or their account avatar when they have none.
        /// </summary>
        public string GetDisplayAvatarUrl(int size = 160) =>
            member.GetAvatarUrl(size) ?? member.User.GetAvatarOrDefaultUrl(size);

        /// <summary>
        ///     The member's guild avatar, or their account avatar when they have none.
        /// </summary>
        public Uri RealAvatarUrl(int size = 2048) => new(member.GetDisplayAvatarUrl(size));

        /// <summary>
        ///     The member's online status, from the last presence the gateway sent.
        /// </summary>
        public UserStatus Status => FluxerContext.Snapshots.GetPresence(member.Id)?.Status ?? UserStatus.Offline;
    }

    extension(SocketGuildMember member)
    {
        /// <summary>
        ///     The voice channel the member is connected to, from the gateway cache.
        /// </summary>
        public Channel? VoiceChannel =>
            member.VoiceStates.Values.FirstOrDefault(v => v.ChannelId != null) is { ChannelId: { } channelId }
                ? member.Guild.GetChannel(channelId)
                : null;
    }

    extension(User user)
    {
        /// <summary>
        ///     The account wide display name, if set.
        /// </summary>
        public string? GlobalName => user.DisplayName;

        /// <summary>
        ///     Opens a direct message with the user.
        /// </summary>
        public Task<DMChannel> CreateDMChannelAsync() => user.GetOrCreateDMChannelAsync();

        /// <summary>
        ///     The user's online status, from the last presence the gateway sent.
        /// </summary>
        public UserStatus Status => FluxerContext.Snapshots.GetPresence(user.Id)?.Status ?? UserStatus.Offline;
    }

    extension(IUser user)
    {
        /// <summary>
        ///     Opens a direct message with the user.
        /// </summary>
        public Task<DMChannel> CreateDMChannelAsync() =>
            FluxerContext.Client.Rest.CreateDMChannelAsync(new CreatePrivateChannelRequest { RecipientId = user.Id });

        /// <summary>
        ///     Sends the user a direct message.
        /// </summary>
        public async Task<Message> SendAsync(string? text = null, EmbedRequest? embed = null,
            IEnumerable<EmbedRequest>? embeds = null)
        {
            var channel = await user.CreateDMChannelAsync().ConfigureAwait(false);
            return await channel.SendAsync(text, embed, embeds).ConfigureAwait(false);
        }
    }

    extension(SocketVoiceState state)
    {
        /// <summary>
        ///     The voice channel of the connection, or null when it has left voice.
        /// </summary>
        public Channel? VoiceChannel => state.Channel;
    }

    extension(SocketReaction reaction)
    {
        /// <summary>
        ///     The user who reacted, from the gateway cache.
        /// </summary>
        public User? User => reaction.Member?.User ?? FluxerContext.Client.GetUser(reaction.UserId);
    }

    extension(SocketAuditLog entry)
    {
        /// <summary>
        ///     The member who made the change, from the gateway cache.
        /// </summary>
        public User? User => entry.UserId is { } userId ? entry.Guild.GetUser(userId)?.User : null;
    }

    extension(Role role)
    {
        /// <summary>
        ///     The role color.
        /// </summary>
        public Color Colour => role.Color == 0 ? Color.Default : new Color((uint)role.Color);
    }
}
