using LinqToDB;
using LinqToDB.Async;
using Mewdeko.Modules.Achievements.Services;
using Mewdeko.Modules.Administration.Services;
using Mewdeko.Modules.Afk.Services;
using Mewdeko.Modules.Birthday.Services;
using Mewdeko.Modules.ChannelAccess.Services;
using Mewdeko.Modules.Chat_Triggers.Services;
using Mewdeko.Modules.CoprMonitoring.Services;
using Mewdeko.Modules.Counting.Services;
using Mewdeko.Modules.Currency.Services;
using Mewdeko.Modules.Games.Services;
using Mewdeko.Modules.Highlights.Services;
using Mewdeko.Modules.Minecraft.Services;
using Mewdeko.Modules.Moderation.Services;
using Mewdeko.Modules.OwnerOnly.Services;
using Mewdeko.Modules.Permissions.Services;
using Mewdeko.Modules.Reputation.Services;
using Mewdeko.Modules.RoleMenus.Services;
using Mewdeko.Modules.Searches.Services;
using Mewdeko.Modules.Server_Management.Services;
using Mewdeko.Modules.ServerStats.Services;
using Mewdeko.Modules.Starboard.Services;
using Mewdeko.Modules.StatChannels.Services;
using Mewdeko.Modules.StatusRoles.Services;
using Mewdeko.Modules.Tickets.Services;
using Mewdeko.Modules.Twitch.Services;
using Mewdeko.Modules.Utility.Services;
using Mewdeko.Modules.WordOfTheDay.Services;
using Mewdeko.Modules.Xp.Services;

namespace Mewdeko.Modules.Migration.Services;

/// <summary>
///     Makes every running service pick up a guild's data after it was written straight into the database, so the
///     guild behaves as if the data had been set through the bot's commands.
/// </summary>
public class MigrationRefresher(
    GuildSettingsService guildSettings,
    DashboardAccessService dashboardAccess,
    PermissionService permissions,
    DiscordPermOverrideService permOverrides,
    ProtectionService protection,
    LogCommandService logCommands,
    MuteService mute,
    BanPruneService banPrune,
    ChatTriggersService chatTriggers,
    VcRoleService vcRoles,
    HighlightsService highlights,
    CurrencyConfigService currencyConfig,
    BirthdayService birthdays,
    WordOfTheDayService wordOfTheDay,
    StatChannelService statChannels,
    ServerStatsSettingsService serverStats,
    MessageCountService messageCounts,
    InviteCountService inviteCounts,
    StarboardService starboards,
    StatusRolesService statusRoles,
    RoleMenuService roleMenus,
    RoleMonitorService roleMonitor,
    RepService reputation,
    RepCommandRequirementsService repRequirements,
    CountingService counting,
    CountingModerationService countingModeration,
    CountingStatsService countingStats,
    StreamNotificationService streamNotifications,
    FeedsService feeds,
    TwitchService twitch,
    MinecraftService minecraft,
    CoprMonitoringService coprMonitoring,
    KaladontChannelService kaladont,
    ChannelAccessService channelAccess,
    AchievementService achievements,
    XpCacheManager xpCache,
    XpRewardManager xpRewards,
    XpCompetitionManager xpCompetitions,
    AfkService afk,
    MessageRepeaterService repeaters,
    RemindService reminders,
    OwnerOnlyService ownerOnly,
    TicketService tickets,
    IDataCache dataCache,
    IDataConnectionFactory dbFactory,
    ILogger<MigrationRefresher> logger) : INService
{
    /// <summary>
    ///     Posts the messages that ticket panels, role menus and channel access gates work through, for those that
    ///     came over without one because their old messages are on Discord.
    /// </summary>
    /// <param name="guildId">The guild whose data was just written.</param>
    /// <returns>How many messages were posted.</returns>
    public async Task<int> RepostPanelsAsync(ulong guildId)
    {
        await using var db = await dbFactory.CreateConnectionAsync();
        var panels = await db.TicketPanels.Where(x => x.GuildId == guildId && x.MessageId == 0).ToListAsync();
        var menus = await db.RoleMenus.LoadWith(x => x.Options)
            .Where(x => x.GuildId == guildId && x.MessageId == null).ToListAsync();
        var gates = await db.ChannelAccessConfigs
            .Where(x => x.GuildId == guildId && x.PanelMessageId == null && x.PanelChannelId != null)
            .ToListAsync();

        var posted = 0;
        foreach (var panel in panels)
            await Run(nameof(TicketService), guildId, async () => posted += await tickets.RepostPanelAsync(panel) ? 1 : 0);

        foreach (var menu in menus)
            await Run(nameof(RoleMenuService), guildId, async () => posted += await roleMenus.RepostAsync(menu) ? 1 : 0);

        foreach (var gate in gates)
            await Run(nameof(ChannelAccessService), guildId,
                async () => posted += await channelAccess.PostPanelAsync(gate, gate.PanelChannelId!.Value) is null ? 0 : 1);

        return posted;
    }

    /// <summary>
    ///     Reloads the cached state and rebuilds the startup-only timers of every service for one guild.
    ///     A failure in one step is logged and does not stop the others.
    /// </summary>
    /// <param name="guildId">The guild whose data was just written.</param>
    /// <returns>A task that completes when every step has run.</returns>
    public async Task RefreshGuildAsync(ulong guildId)
    {
        guildSettings.ClearCacheForGuild(guildId);

        await Run(nameof(DashboardAccessService), guildId, () => dashboardAccess.RefreshCache(guildId));
        await Run(nameof(PermissionService), guildId, () => permissions.ReloadGuildAsync(guildId));
        await Run(nameof(DiscordPermOverrideService), guildId, () => permOverrides.ReloadGuildAsync(guildId));
        await Run(nameof(ProtectionService), guildId, () => protection.Initialize(guildId));
        await Run(nameof(LogCommandService), guildId, () => logCommands.ReloadGuildAsync(guildId));
        await Run(nameof(MuteService), guildId, () => mute.ReloadGuildAsync(guildId));
        await Run(nameof(BanPruneService), guildId, () => banPrune.ReloadGuildAsync(guildId));
        await Run(nameof(ChatTriggersService), guildId, () => chatTriggers.ReloadGuildAsync(guildId));
        await Run(nameof(VcRoleService), guildId, () => vcRoles.ReloadGuildAsync(guildId));
        await Run(nameof(HighlightsService), guildId, () => highlights.ReloadGuildAsync(guildId));
        await Run(nameof(CurrencyConfigService), guildId, () => currencyConfig.ReloadGuildAsync(guildId));
        await Run(nameof(BirthdayService), guildId, () => birthdays.ReloadGuildAsync(guildId));
        await Run(nameof(WordOfTheDayService), guildId, () => wordOfTheDay.ReloadGuildAsync(guildId));
        await Run(nameof(StatChannelService), guildId, () => statChannels.ReloadGuildAsync(guildId));
        await Run(nameof(ServerStatsSettingsService), guildId, () => serverStats.ReloadGuildAsync(guildId));
        await Run(nameof(MessageCountService), guildId, () => messageCounts.ReloadGuildAsync(guildId));
        await Run(nameof(InviteCountService), guildId, () => inviteCounts.ReloadGuildAsync(guildId));
        await Run(nameof(StarboardService), guildId, () => starboards.ReloadGuildAsync(guildId));
        await Run(nameof(StatusRolesService), guildId, () => statusRoles.ReloadGuildAsync(guildId));
        await Run(nameof(RoleMenuService), guildId, () => roleMenus.ReloadGuildAsync(guildId));
        await Run(nameof(RoleMonitorService), guildId, () => roleMonitor.ReloadGuildAsync(guildId));
        await Run(nameof(RepService), guildId, () => reputation.ReloadGuildAsync(guildId));
        await Run(nameof(RepCommandRequirementsService), guildId, () => repRequirements.ReloadGuildAsync(guildId));
        await Run(nameof(CountingService), guildId, () => counting.ReloadGuildAsync(guildId));
        await Run(nameof(CountingModerationService), guildId, () => countingModeration.ReloadGuildAsync(guildId));
        await Run(nameof(CountingStatsService), guildId, () => countingStats.ReloadGuildAsync(guildId));
        await Run(nameof(StreamNotificationService), guildId, () => streamNotifications.ReloadGuildAsync(guildId));
        await Run(nameof(FeedsService), guildId, () => feeds.ReloadGuildAsync(guildId));
        await Run(nameof(TwitchService), guildId, () => twitch.ReloadGuildAsync(guildId));
        await Run(nameof(MinecraftService), guildId, () => minecraft.ReloadGuildAsync(guildId));
        await Run(nameof(CoprMonitoringService), guildId, () => coprMonitoring.ReloadGuildAsync(guildId));
        await Run(nameof(KaladontChannelService), guildId, () => kaladont.ReloadGuildAsync(guildId));
        await Run(nameof(ChannelAccessService), guildId, () => channelAccess.ReloadGuildAsync(guildId));
        await Run(nameof(AchievementService), guildId, async () =>
        {
            await achievements.ReloadGuildAsync(guildId);
            achievements.QueueSweep(guildId);
        });
        await Run(nameof(XpCacheManager), guildId, () => xpCache.ClearGuildXpCachesAsync(guildId));
        await Run(nameof(XpRewardManager), guildId, () => xpRewards.ReloadGuildAsync(guildId));
        await Run(nameof(XpCompetitionManager), guildId, () => xpCompetitions.LoadActiveCompetitionsAsync(guildId));
        await Run(nameof(AfkService), guildId, () => afk.ReloadGuildAsync(guildId));
        await Run(nameof(MessageRepeaterService), guildId, () => repeaters.ReloadGuildAsync(guildId));
        await Run(nameof(RemindService), guildId, () => reminders.ReloadGuildAsync(guildId));
        await Run(nameof(OwnerOnlyService), guildId, () => ownerOnly.ReloadGuildAsync(guildId));
        await Run(nameof(IDataCache), guildId, () => RefreshMusicSettingsAsync(guildId));
    }

    private async Task RefreshMusicSettingsAsync(ulong guildId)
    {
        await using var db = await dbFactory.CreateConnectionAsync();
        var settings = await db.MusicPlayerSettings.FirstOrDefaultAsync(x => x.GuildId == guildId);
        if (settings is not null)
            await dataCache.SetMusicPlayerSettings(guildId, settings);
    }

    private async Task Run(string step, ulong guildId, Func<Task> action)
    {
        try
        {
            await action();
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Migration refresh step {Step} failed for guild {GuildId}", step, guildId);
        }
    }
}
