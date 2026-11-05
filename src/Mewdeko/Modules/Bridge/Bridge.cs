using Mewdeko.Common.Attributes.TextCommands;
using Mewdeko.Modules.Bridge.Common;
using Mewdeko.Modules.Bridge.Services;

namespace Mewdeko.Modules.Bridge;

/// <summary>
///     Commands for bridging a channel to a channel in a community on the other platform.
/// </summary>
/// <param name="guildSettings">The guild settings service, for the prefix.</param>
public class Bridge(GuildSettingsService guildSettings) : MewdekoSubmodule<BridgeService>
{
    /// <summary>
    ///     Creates, links, lists, pauses, resumes, removes or configures a channel bridge.
    /// </summary>
    /// <param name="action">create, link, list, settings, pause, resume, remove, allowbots, direction, allowrole, blockrole, block, unblock, minage, invites, filter, nameformat, pair, pairs, channels, connect, proposals, accept, decline, share, hide, approval or unpair.</param>
    /// <param name="argument">The value for the action, such as the link code or the roles.</param>
    [Cmd]
    [Aliases]
    [RequireContext(ContextType.Guild)]
    [UserPerm(GuildPermission.ManageGuild)]
    public async Task BridgeCommand(string action = "list", [Remainder] string? argument = null)
    {
        var guildId = ctx.Guild.Id;
        var prefix = await guildSettings.GetPrefix(ctx.Guild);
        var bridge = Service.GetBridge(ctx.Channel.Id);

        if (bridge is null && action.ToLowerInvariant() is "settings" or "direction" or "allowrole" or "blockrole"
                or "block" or "unblock" or "minage" or "invites" or "filter" or "nameformat")
        {
            await ReplyErrorAsync(Strings.BridgeNotBridged(guildId));
            return;
        }

        switch (action.ToLowerInvariant())
        {
            case "create":
            {
                var (result, code) = await Service.CreateCodeAsync(guildId, ctx.Channel.Id, ctx.User.Id);
                if (code is null)
                    await ReplyFailureAsync(result, prefix);
                else
                    await ReplyConfirmAsync(Strings.BridgeCodeCreated(guildId, code.Code, prefix));
                break;
            }
            case "link":
            {
                if (string.IsNullOrWhiteSpace(argument))
                {
                    await ReplyErrorAsync(Strings.BridgeMissingArgument(guildId, prefix));
                    return;
                }

                var (result, request) = await Service.AcceptCodeAsync(guildId, ctx.Channel.Id, ctx.User.Id, argument);
                if (request is null)
                    await ReplyFailureAsync(result, prefix);
                else
                    await ReplyConfirmAsync(Strings.BridgeLinked(guildId, request.ChannelName, request.GuildName));
                break;
            }
            case "list":
            {
                var bridges = Service.GetBridges(guildId);
                if (bridges.Count == 0)
                {
                    await ReplyConfirmAsync(Strings.BridgeListEmpty(guildId, prefix));
                    return;
                }

                var lines = bridges.Select(x => Strings.BridgeListEntry(guildId, x.ChannelId, x.RemoteChannelName,
                    x.RemoteGuildName, x.Paused ? Strings.BridgeStatePaused(guildId) : Strings.BridgeStateActive(guildId)));
                await ctx.Channel.SendAsync(embed: new EmbedBuilder()
                    .WithOkColor()
                    .WithTitle(Strings.BridgeListTitle(guildId))
                    .WithDescription(string.Join('\n', lines))
                    .Build());
                break;
            }
            case "pause":
            case "resume":
            {
                if (bridge is null)
                {
                    await ReplyErrorAsync(Strings.BridgeNotBridged(guildId));
                    return;
                }

                var pause = action.Equals("pause", StringComparison.OrdinalIgnoreCase);
                await Service.SetPausedAsync(guildId, bridge.BridgeId, pause);
                await ReplyConfirmAsync(pause ? Strings.BridgePaused(guildId) : Strings.BridgeResumed(guildId));
                break;
            }
            case "remove":
            {
                if (bridge is null)
                {
                    await ReplyErrorAsync(Strings.BridgeNotBridged(guildId));
                    return;
                }

                await Service.RemoveAsync(guildId, bridge.BridgeId);
                await ReplyConfirmAsync(Strings.BridgeRemoved(guildId));
                break;
            }
            case "allowbots":
            {
                if (bridge is null)
                {
                    await ReplyErrorAsync(Strings.BridgeNotBridged(guildId));
                    return;
                }

                if (!bool.TryParse(argument, out var allow))
                {
                    await ReplyErrorAsync(Strings.BridgeMissingBool(guildId, prefix));
                    return;
                }

                await Service.SetAllowBotsAsync(guildId, bridge.BridgeId, allow);
                await ReplyConfirmAsync(allow ? Strings.BridgeAllowbotsOn(guildId) : Strings.BridgeAllowbotsOff(guildId));
                break;
            }
            case "settings":
                await ctx.Channel.SendAsync(embed: new EmbedBuilder()
                    .WithOkColor()
                    .WithTitle(Strings.BridgeSettingsTitle(guildId))
                    .WithDescription(Service.DescribeOptions(guildId, bridge!.Options))
                    .Build());
                break;
            case "direction":
            {
                var direction = argument?.Trim().ToLowerInvariant() switch
                {
                    "both" => BridgeDirection.Both,
                    "send" => BridgeDirection.SendOnly,
                    "receive" => BridgeDirection.ReceiveOnly,
                    _ => (BridgeDirection?)null
                };
                if (direction is null)
                {
                    await ReplyErrorAsync(Strings.BridgeDirectionUsage(guildId, prefix));
                    return;
                }

                await Service.SetOptionsAsync(guildId, bridge!.BridgeId, bridge.Options with
                {
                    Direction = direction.Value
                });
                await ReplyConfirmAsync(Strings.BridgeDirectionSet(guildId,
                    Service.DirectionName(guildId, direction.Value)));
                break;
            }
            case "allowrole":
            case "blockrole":
            {
                var allow = action.Equals("allowrole", StringComparison.OrdinalIgnoreCase);
                if (string.IsNullOrWhiteSpace(argument))
                {
                    await ReplyErrorAsync(Strings.BridgeRolesUsage(guildId, prefix, action.ToLowerInvariant()));
                    return;
                }

                var clear = argument.Trim().Equals("none", StringComparison.OrdinalIgnoreCase);
                var roles = clear
                    ? []
                    : BridgeSettings.ParseRoleIds(argument,
                        ctx.Guild.Roles.Values.Select(x => (x.Id, x.Name)).ToList());
                if (!clear && roles.Count == 0)
                {
                    await ReplyErrorAsync(Strings.BridgeRolesUnknown(guildId));
                    return;
                }

                await Service.SetOptionsAsync(guildId, bridge!.BridgeId, allow
                    ? bridge.Options with { AllowedRoleIds = roles }
                    : bridge.Options with { BlockedRoleIds = roles });
                var list = string.Join(' ', roles.Select(x => $"<@&{x}>"));
                await ReplyConfirmAsync((allow, clear) switch
                {
                    (true, true) => Strings.BridgeAllowroleCleared(guildId),
                    (true, false) => Strings.BridgeAllowroleSet(guildId, list),
                    (false, true) => Strings.BridgeBlockroleCleared(guildId),
                    _ => Strings.BridgeBlockroleSet(guildId, list)
                });
                break;
            }
            case "block":
            case "unblock":
            {
                var block = action.Equals("block", StringComparison.OrdinalIgnoreCase);
                if (BridgeSettings.ParseUserId(argument) is not { } userId)
                {
                    await ReplyErrorAsync(Strings.BridgeBlockUsage(guildId, prefix, action.ToLowerInvariant()));
                    return;
                }

                var blocked = bridge!.Options.BlockedUserIds.ToList();
                if (block == blocked.Contains(userId))
                {
                    await ReplyErrorAsync(block ? Strings.BridgeBlockExists(guildId) : Strings.BridgeBlockMissing(guildId));
                    return;
                }

                if (block)
                    blocked.Add(userId);
                else
                    blocked.Remove(userId);

                await Service.SetOptionsAsync(guildId, bridge.BridgeId, bridge.Options with
                {
                    BlockedUserIds = blocked
                });
                await ReplyConfirmAsync(block
                    ? Strings.BridgeBlockAdded(guildId, userId)
                    : Strings.BridgeBlockRemoved(guildId, userId));
                break;
            }
            case "minage":
            {
                if (!BridgeSettings.TryParseMinAge(argument, out var minutes))
                {
                    await ReplyErrorAsync(Strings.BridgeMinageUsage(guildId, prefix));
                    return;
                }

                await Service.SetOptionsAsync(guildId, bridge!.BridgeId, bridge.Options with
                {
                    MinMemberAgeMinutes = minutes
                });
                await ReplyConfirmAsync(minutes == 0
                    ? Strings.BridgeMinageOff(guildId)
                    : Strings.BridgeMinageSet(guildId, BridgeService.DescribeMinAge(minutes)));
                break;
            }
            case "invites":
            case "filter":
            {
                var invites = action.Equals("invites", StringComparison.OrdinalIgnoreCase);
                if (ParseToggle(argument) is not { } on)
                {
                    await ReplyErrorAsync(Strings.BridgeToggleUsage(guildId, prefix, action.ToLowerInvariant()));
                    return;
                }

                await Service.SetOptionsAsync(guildId, bridge!.BridgeId, invites
                    ? bridge.Options with { BlockInvites = on }
                    : bridge.Options with { UseWordFilter = on });
                await ReplyConfirmAsync((invites, on) switch
                {
                    (true, true) => Strings.BridgeInvitesOn(guildId),
                    (true, false) => Strings.BridgeInvitesOff(guildId),
                    (false, true) => Strings.BridgeFilterOn(guildId),
                    _ => Strings.BridgeFilterOff(guildId)
                });
                break;
            }
            case "nameformat":
            {
                var reset = argument?.Trim().Equals("reset", StringComparison.OrdinalIgnoreCase) == true;
                var format = reset ? BridgeSettings.DefaultNameFormat : argument?.Trim();
                if (!BridgeSettings.IsValidNameFormat(format))
                {
                    await ReplyErrorAsync(Strings.BridgeNameformatUsage(guildId, prefix, "{name}", "{platform}",
                        "{community}"));
                    return;
                }

                await Service.SetOptionsAsync(guildId, bridge!.BridgeId, bridge.Options with
                {
                    NameFormat = format!
                });
                await ReplyConfirmAsync(reset
                    ? Strings.BridgeNameformatReset(guildId)
                    : Strings.BridgeNameformatSet(guildId, format!));
                break;
            }
            case "pair":
            case "pairs":
            case "channels":
            case "connect":
            case "proposals":
            case "accept":
            case "decline":
            case "share":
            case "hide":
            case "approval":
            case "unpair":
            {
                var reply = await Service.Pairs.RunAsync(action, argument, guildId, ctx.User.Id, ctx.Channel.Id,
                    prefix);
                if (!reply.Success)
                    await ReplyErrorAsync(reply.Text);
                else if (reply.Title is null)
                    await ReplyConfirmAsync(reply.Text);
                else
                    await ctx.Channel.SendAsync(embed: new EmbedBuilder()
                        .WithOkColor()
                        .WithTitle(reply.Title)
                        .WithDescription(reply.Text)
                        .Build());
                break;
            }
            default:
                await ReplyErrorAsync(Strings.BridgeUnknownAction(guildId, prefix));
                break;
        }
    }

    private static bool? ParseToggle(string? text) => text?.Trim().ToLowerInvariant() switch
    {
        "on" or "true" or "yes" => true,
        "off" or "false" or "no" => false,
        _ => null
    };

    private Task ReplyFailureAsync(BridgeResult result, string prefix)
    {
        var guildId = ctx.Guild.Id;
        return ReplyErrorAsync(result switch
        {
            BridgeResult.AlreadyBridged => Strings.BridgeAlreadyBridged(guildId, prefix),
            BridgeResult.MissingPermission => Strings.BridgeMissingPermission(guildId),
            BridgeResult.CodeWrongPlatform => Strings.BridgeCodeWrongPlatform(guildId),
            BridgeResult.CodeInvalid => Strings.BridgeCodeInvalid(guildId),
            BridgeResult.NotFound => Strings.BridgeNotBridged(guildId),
            _ => Strings.BridgeChannelInvalid(guildId)
        });
    }
}
