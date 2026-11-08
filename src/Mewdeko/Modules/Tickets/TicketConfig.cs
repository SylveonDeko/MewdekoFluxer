using System.Text;
using Mewdeko.Common.Attributes.TextCommands;
using Mewdeko.Modules.Tickets.Common;
using Mewdeko.Modules.Tickets.Services;

namespace Mewdeko.Modules.Tickets;

/// <inheritdoc />
public partial class Tickets : MewdekoModuleBase<TicketService>
{
    /// <summary>
    ///     Provides configuration commands for managing ticket panels and ticket types.
    /// </summary>
    /// <remarks>
    ///     This submodule contains commands for server administrators to manage their ticket system,
    ///     including creating and modifying panels and ticket types.
    /// </remarks>
    [Group]
    public class TicketConfig : MewdekoSubmodule<TicketService>
    {
        private readonly InteractiveService interactivity;
        private readonly ILogger<TicketConfig> logger;

        /// <summary>
        ///     Initializes a new instance of the <see cref="TicketConfig" /> class.
        /// </summary>
        /// <param name="interactivity">The service used for interactive commands.</param>
        /// <param name="logger">The logger instance for structured logging.</param>
        public TicketConfig(InteractiveService interactivity, ILogger<TicketConfig> logger)
        {
            this.interactivity = interactivity;
            this.logger = logger;
        }

        /// <summary>
        ///     Lists all ticket panels in the guild.
        /// </summary>
        /// <remarks>
        ///     Displays a paginated list of all ticket panels, showing their locations
        ///     and the number of ticket types they contain.
        /// </remarks>
        [Cmd]
        [Aliases]
        [RequireContext(ContextType.Guild)]
        [UserPerm(GuildPermission.Administrator)]
        public async Task PanelList()
        {
            var panels = await Service.GetPanelsAsync(ctx.Guild.Id);
            if (!panels.Any())
            {
                await ctx.Channel.SendErrorAsync(Strings.NoTicketPanels(ctx.Guild.Id), Config);
                return;
            }

            var paginator = new LazyPaginatorBuilder()
                .AddUser(ctx.User)
                .WithPageFactory(PageFactory)
                .WithFooter(PaginatorFooter.PageNumber)
                .WithMaxPageIndex(panels.Count / 5)
                .Build();

            await interactivity.SendPaginatorAsync(paginator, Context.Channel,
                TimeSpan.FromMinutes(60));

            async Task<PageBuilder> PageFactory(int page)
            {
                var pageBuilder = new PageBuilder()
                    .WithTitle(Strings.TicketPanels(ctx.Guild.Id))
                    .WithOkColor();

                var items = panels.Skip(page * 5).Take(5);
                foreach (var panel in items)
                {
                    var channel = await ctx.Guild.GetTextChannelAsync(panel.ChannelId);
                    var buttonCount = panel.PanelButtons?.Count() ?? 0;

                    pageBuilder.AddField(
                        $"Panel ID: {panel.MessageId}",
                        $"Channel: {(channel == null ? "Deleted" : channel.Mention)}\n" +
                        $"Ticket types: {buttonCount}");
                }

                return pageBuilder;
            }
        }

        /// <summary>
        ///     Deletes a button from a panel.
        /// </summary>
        /// <param name="buttonId">The ID of the button to delete.</param>
        /// <remarks>
        ///     This command removes the specified button from its panel.
        ///     The button ID can be found using the panel info command.
        /// </remarks>
        [Cmd]
        [Aliases]
        [RequireContext(ContextType.Guild)]
        [UserPerm(GuildPermission.Administrator)]
        public async Task DeleteButton(int buttonId)
        {
            var button = await Service.GetButtonAsync(buttonId);
            if (button == null || button.Panel.GuildId != ctx.Guild.Id)
            {
                await ctx.Channel.SendErrorAsync(Strings.ButtonNotFound(ctx.Guild.Id), Config);
                return;
            }

            var success = await Service.DeleteButtonAsync(ctx.Guild, buttonId);
            if (success)
            {
                await ctx.Channel.SendConfirmAsync(Strings.ButtonDeletedSuccess(ctx.Guild.Id, button.Label));
            }
            else
            {
                await ctx.Channel.SendErrorAsync(Strings.FailedDeleteButton(ctx.Guild.Id), Config);
            }
        }

        /// <summary>
        ///     Enables or disables transcript saving for a panel.
        /// </summary>
        /// <param name="panelId">The ID of the panel to modify.</param>
        /// <param name="enable">Whether to enable or disable transcript saving.</param>
        /// <remarks>
        ///     When enabled, all tickets created through this panel's buttons
        ///     will automatically save transcripts when closed.
        /// </remarks>
        [Cmd]
        [Aliases]
        [RequireContext(ContextType.Guild)]
        [UserPerm(GuildPermission.Administrator)]
        public async Task SetPanelTranscripts(ulong panelId, bool enable)
        {
            var panel = await Service.GetPanelAsync(panelId);
            if (panel == null || panel.GuildId != ctx.Guild.Id)
            {
                await ctx.Channel.SendErrorAsync(Strings.PanelNotFound(ctx.Guild.Id), Config);
                return;
            }

            if (panel.PanelButtons != null)
            {
                foreach (var button in panel.PanelButtons)
                {
                    await Service.UpdateButtonSettingsAsync(ctx.Guild, button.Id,
                        new Dictionary<string, object>
                        {
                            {
                                "SaveTranscript", enable
                            }
                        });
                }
            }

            await ctx.Channel.SendConfirmAsync(
                Strings.TranscriptsStatus(ctx.Guild.Id, enable ? "enabled" : "disabled") +
                " for all buttons in this panel.");
        }

        /// <summary>
        ///     Sets the required response time for tickets created by a button.
        /// </summary>
        /// <param name="buttonId">The ID of the button to modify.</param>
        /// <param name="minutes">The number of minutes, or null to disable.</param>
        /// <remarks>
        ///     When set, staff will be notified if they don't respond to a ticket
        ///     within the specified time. Set to null to disable the requirement.
        /// </remarks>
        [Cmd]
        [Aliases]
        [RequireContext(ContextType.Guild)]
        [UserPerm(GuildPermission.Administrator)]
        public async Task SetResponseTime(int buttonId, int? minutes)
        {
            var button = await Service.GetButtonAsync(buttonId);
            if (button == null || button.Panel.GuildId != ctx.Guild.Id)
            {
                await ctx.Channel.SendErrorAsync(Strings.ButtonNotFound(ctx.Guild.Id), Config);
                return;
            }

            await Service.UpdateRequiredResponseTimeAsync(ctx.Guild, buttonId,
                minutes.HasValue ? TimeSpan.FromMinutes(minutes.Value) : null);

            if (minutes.HasValue)
                await ctx.Channel.SendConfirmAsync(Strings.ResponseTimeSet(ctx.Guild.Id, minutes));
            else
                await ctx.Channel.SendConfirmAsync(Strings.TicketResponseTimeRemoved(ctx.Guild.Id));
        }

        /// <summary>
        ///     Displays detailed information about a ticket panel.
        /// </summary>
        /// <param name="panelId">The ID of the panel to view.</param>
        /// <remarks>
        ///     Shows information about the panel's location and ticket types.
        /// </remarks>
        [Cmd]
        [Aliases]
        [RequireContext(ContextType.Guild)]
        [UserPerm(GuildPermission.Administrator)]
        public async Task PanelInfo(ulong panelId)
        {
            var panel = await Service.GetPanelAsync(panelId);
            if (panel == null || panel.GuildId != ctx.Guild.Id)
            {
                await ctx.Channel.SendErrorAsync(Strings.PanelNotFound(ctx.Guild.Id), Config);
                return;
            }

            var channel = await ctx.Guild.GetTextChannelAsync(panel.ChannelId);
            var embed = new EmbedBuilder()
                .WithTitle(Strings.TicketPanelInfo(ctx.Guild.Id))
                .WithDescription(Strings.ChannelDeletedOrExists(ctx.Guild.Id,
                    channel == null ? "Deleted" : channel.Mention))
                .AddField("Message ID", panel.MessageId, true)
                .AddField("Ticket types", panel.PanelButtons?.Count() ?? 0, true);

            if (panel.PanelButtons?.Any() == true)
            {
                var buttonInfo = string.Join("\n", panel.PanelButtons.Select(b =>
                    $"• {b.Label} (ID: {b.Id})"));
                embed.AddField("Ticket type details", buttonInfo);
            }

            await ctx.Channel.SendAsync(embed: embed.Build());
        }

        /// <summary>
        ///     Sets the auto-close time for tickets created by a button.
        /// </summary>
        /// <param name="buttonId">The ID of the button to modify.</param>
        /// <param name="hours">The number of hours of inactivity before auto-close, or null to disable.</param>
        /// <remarks>
        ///     When set, tickets will automatically close after the specified number
        ///     of hours of inactivity. Set to null to disable auto-closing.
        /// </remarks>
        [Cmd]
        [Aliases]
        [RequireContext(ContextType.Guild)]
        [UserPerm(GuildPermission.Administrator)]
        public async Task SetAutoClose(int buttonId, int? hours)
        {
            var button = await Service.GetButtonAsync(buttonId);
            if (button == null || button.Panel.GuildId != ctx.Guild.Id)
            {
                await ctx.Channel.SendErrorAsync(Strings.ButtonNotFound(ctx.Guild.Id), Config);
                return;
            }

            await Service.UpdateButtonSettingsAsync(ctx.Guild, buttonId,
                new Dictionary<string, object>
                {
                    {
                        "autoCloseTime", hours.HasValue ? TimeSpan.FromHours(hours.Value) : null
                    }
                });

            if (hours.HasValue)
                await ctx.Channel.SendConfirmAsync(Strings.AutoCloseTimeSet(ctx.Guild.Id, hours));
            else
                await ctx.Channel.SendConfirmAsync(Strings.TicketAutoCloseDisabled(ctx.Guild.Id));
        }

        /// <summary>
        ///     Displays detailed information about a button.
        /// </summary>
        /// <param name="buttonId">The ID of the button to view.</param>
        /// <remarks>
        ///     Shows all configuration settings for the button including
        ///     transcript settings, categories, and role permissions.
        /// </remarks>
        [Cmd]
        [Aliases]
        [RequireContext(ContextType.Guild)]
        [UserPerm(GuildPermission.Administrator)]
        public async Task ButtonInfo(int buttonId)
        {
            var button = await Service.GetButtonAsync(buttonId);
            if (button == null || button.Panel.GuildId != ctx.Guild.Id)
            {
                await ctx.Channel.SendErrorAsync(Strings.ButtonNotFound(ctx.Guild.Id), Config);
                return;
            }

            var embed = new EmbedBuilder()
                .WithTitle(Strings.ButtonInformation(ctx.Guild.Id))
                .AddField("Label", button.Label, true)
                .AddField("Emoji", string.IsNullOrEmpty(button.Emoji) ? "Auto" : button.Emoji, true)
                .AddField("Save Transcripts", button.SaveTranscript, true)
                .AddField("Max Active Tickets", button.MaxActiveTickets, true)
                .AddField("Auto Close Time",
                    button.AutoCloseTime.HasValue ? $"{button.AutoCloseTime.Value.TotalHours} hours" : "Disabled", true)
                .AddField("Required Response Time",
                    button.RequiredResponseTime.HasValue
                        ? $"{button.RequiredResponseTime.Value.TotalMinutes} minutes"
                        : "None", true);

            if (button.CategoryId.HasValue)
            {
                var category = await ctx.Guild.GetCategoryChannelAsync(button.CategoryId.Value);
                embed.AddField("Ticket Category", category?.Name ?? "Deleted", true);
            }

            if (button.ArchiveCategoryId.HasValue)
            {
                var archiveCategory = await ctx.Guild.GetCategoryChannelAsync(button.ArchiveCategoryId.Value);
                embed.AddField("Archive Category", archiveCategory?.Name ?? "Deleted", true);
            }

            if (button.SupportRoles?.Any() == true)
            {
                var roles = button.SupportRoles
                    .Select(id => ctx.Guild.GetRole(id))
                    .Where(r => r != null)
                    .Select(r => r.Mention);
                embed.AddField("Support Roles", string.Join(", ", roles));
            }

            if (button.ViewerRoles?.Any() == true)
            {
                var roles = button.ViewerRoles
                    .Select(id => ctx.Guild.GetRole(id))
                    .Where(r => r != null)
                    .Select(r => r.Mention);
                embed.AddField("Viewer Roles", string.Join(", ", roles));
            }

            await ctx.Channel.SendAsync(embed: embed.Build());
        }

        /// <summary>
        ///     Lists all ticket types of a specific ticket panel
        /// </summary>
        /// <param name="panelId">The message ID of the panel to inspect</param>
        /// <remarks>
        ///     Shows detailed information about a panel's ticket types, including:
        ///     - IDs and custom IDs
        ///     - Labels and emoji
        ///     - Custom message configuration
        ///     - Category and role settings
        /// </remarks>
        [Cmd]
        [Aliases]
        [RequireContext(ContextType.Guild)]
        [UserPerm(GuildPermission.ManageChannels)]
        public async Task TicketListPanel(ulong panelId)
        {
            try
            {
                var buttons = await Service.GetPanelButtonsAsync(panelId);

                var embed = new EmbedBuilder()
                    .WithTitle(Strings.PanelComponents(ctx.Guild.Id))
                    .WithOkColor();

                if (buttons.Any())
                {
                    var buttonText = new StringBuilder();
                    foreach (var button in buttons)
                    {
                        buttonText.AppendLine($"**Ticket type ID: {button.Id}**")
                            .AppendLine($"└ Label: {button.Label}")
                            .AppendLine($"└ Emoji: {(string.IsNullOrEmpty(button.Emoji) ? "Auto" : button.Emoji)}")
                            .AppendLine($"└ Custom ID: {button.CustomId}")
                            .AppendLine($"└ Has Custom Open Message: {(button.HasCustomOpenMessage ? "Yes" : "No")}")
                            .AppendLine(
                                $"└ Category: {(button.CategoryId.HasValue ? $"<#{button.CategoryId}>" : "None")}")
                            .AppendLine(
                                $"└ Archive Category: {(button.ArchiveCategoryId.HasValue ? $"<#{button.ArchiveCategoryId}>" : "None")}")
                            .AppendLine(
                                $"└ Support Roles: {string.Join(", ", button.SupportRoles.Select(r => $"<@&{r}>"))}")
                            .AppendLine(
                                $"└ Viewer Roles: {string.Join(", ", button.ViewerRoles.Select(r => $"<@&{r}>"))}")
                            .AppendLine();
                    }

                    embed.AddField("Ticket types", buttonText.ToString());
                }
                else
                {
                    embed.WithDescription(Strings.NoPanelComponents(ctx.Guild.Id));
                }

                await ctx.Channel.SendAsync(embed: embed.Build());
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Error listing panel components for panel {PanelId}", panelId);
                await ctx.Channel.SendErrorAsync(Strings.PanelComponentsError(ctx.Guild.Id), Config);
            }
        }

        /// <summary>
        ///     Lists all ticket panels in the guild
        /// </summary>
        /// <remarks>
        ///     Shows a paginated list of all panels in the server, including:
        ///     - Channel locations
        ///     - Message IDs
        ///     - Ticket type configurations
        ///     - Associated categories and roles
        /// </remarks>
        [Cmd]
        [Aliases]
        [RequireContext(ContextType.Guild)]
        [UserPerm(GuildPermission.ManageChannels)]
        public async Task TicketListPanels()
        {
            try
            {
                var panels = await Service.GetAllPanelsAsync(Context.Guild.Id);

                if (!panels.Any())
                {
                    await ctx.Channel.SendErrorAsync(Strings.NoTicketPanels(ctx.Guild.Id), Config);
                    return;
                }

                var paginator = new LazyPaginatorBuilder()
                    .AddUser(ctx.User)
                    .WithPageFactory(PageFactory)
                    .WithFooter(PaginatorFooter.PageNumber)
                    .WithMaxPageIndex(panels.Count / 5)
                    .WithActionOnCancellation(ActionOnStop.DeleteMessage)
                    .Build();

                await interactivity.SendPaginatorAsync(paginator, Context.Channel, TimeSpan.FromMinutes(60));

                async Task<PageBuilder> PageFactory(int page)
                {
                    await Task.CompletedTask;
                    var pagePanels = panels.Skip(5 * page).Take(5);
                    var pageBuilder = new PageBuilder()
                        .WithTitle(Strings.TicketPanels(ctx.Guild.Id))
                        .WithOkColor();

                    foreach (var panel in pagePanels)
                    {
                        var channel = await Context.Guild.GetChannelAsync(panel.ChannelId) as Channel;
                        var fieldBuilder = new StringBuilder();

                        fieldBuilder.AppendLine($"Channel: #{channel?.Name ?? "deleted-channel"}");

                        if (panel.Buttons.Any())
                        {
                            fieldBuilder.AppendLine("\n**Ticket types:**");
                            foreach (var button in panel.Buttons)
                            {
                                fieldBuilder.AppendLine($"• ID: {button.Id} | Label: {button.Label}")
                                    .AppendLine(
                                        $"  Category: {(button.CategoryId.HasValue ? $"<#{button.CategoryId}>" : "None")}")
                                    .AppendLine(
                                        $"  Support Roles: {string.Join(", ", button.SupportRoles.Select(r => $"<@&{r}>"))}");
                            }
                        }

                        pageBuilder.AddField($"Panel ID: {panel.MessageId}", fieldBuilder.ToString());
                    }

                    return pageBuilder;
                }
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Error listing panels");
                await ctx.Channel.SendErrorAsync(Strings.PanelsListError(ctx.Guild.Id), Config);
            }
        }
    }
}
