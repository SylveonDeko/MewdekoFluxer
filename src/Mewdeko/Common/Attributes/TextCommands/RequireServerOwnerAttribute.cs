
using Mewdeko.Services.Strings;
using Microsoft.Extensions.DependencyInjection;

namespace Mewdeko.Common.Attributes.TextCommands;

/// <summary>
///     Specifies that the command can only be executed by the server owner.
/// </summary>
public class RequireServerOwnerAttribute : PreconditionAttribute
{
    /// <inheritdoc />
    public override Task<PreconditionResult> CheckPermissionsAsync(ICommandContext context, CommandInfo command,
        IServiceProvider services)
    {
        if (context.User is SocketGuildMember guildUser)
            return Task.FromResult(guildUser.Guild.OwnerId == context.User.Id
                ? PreconditionResult.FromSuccess()
                : PreconditionResult.FromError(services.GetRequiredService<GeneratedBotStrings>()
                    .ServerOwnerOnly(guildUser.Guild.Id)));

        return Task.FromResult(PreconditionResult.FromError("This command can only be used in a guild."));
    }
}