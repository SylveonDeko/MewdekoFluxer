using System.IO;
using Mewdeko.Common.Attributes.TextCommands;
using Mewdeko.Modules.Administration.Services;
using OtpNet;
using QRCoder;

namespace Mewdeko.Modules.Administration;

public partial class Administration
{
    /// <summary>
    ///     Commands for recovering server access if the owner account is lost. Secrets are only ever exchanged in DMs.
    /// </summary>
    public class ServerRecovery : MewdekoSubmodule<ServerRecoveryService>
    {
        /// <summary>
        ///     Clears the stored server recovery setup after asking for confirmation.
        /// </summary>
        [Cmd]
        [Aliases]
        [RequireContext(ContextType.Guild)]
        [UserPerm(GuildPermission.Administrator)]
        public async Task ClearServerRecover()
        {
            var (setup, store) = await Service.RecoveryIsSetup(ctx.Guild.Id);
            if (!setup)
            {
                await ctx.Channel.SendErrorAsync(Strings.Nothingsetup(ctx.Guild.Id), Config);
                return;
            }

            if (!await PromptUserConfirmAsync(Strings.Areyouabsolutelysure(ctx.Guild.Id), ctx.User.Id))
                return;

            await Service.ClearRecoverySetup(store);
            await ctx.Channel.SendConfirmAsync(Strings.Recovrydel(ctx.Guild.Id));
        }

        /// <summary>
        ///     Starts the server recovery setup for the owner, or recovers access with the recovery key and a 2FA code.
        /// </summary>
        [Cmd]
        [Aliases]
        [RequireContext(ContextType.Guild)]
        public async Task ServerRecover()
        {
            var curBotUser = await ctx.Guild.GetCurrentUserAsync();
            if (!curBotUser.GuildPermissions.Has(GuildPermission.Administrator))
            {
                await ctx.Channel.SendErrorAsync(Strings.Recovernoadmin(ctx.Guild.Id), Config);
                return;
            }

            var (setup, store) = await Service.RecoveryIsSetup(ctx.Guild.Id);
            if (!setup && ctx.Guild.OwnerId != ctx.User.Id)
            {
                await ctx.Channel.SendErrorAsync(Strings.Recoverowneronly(ctx.Guild.Id), Config);
                return;
            }

            Channel dm;
            try
            {
                dm = await ctx.User.CreateDMChannelAsync();
                await dm.SendConfirmAsync(Strings.RecoveryStepsPrompt(ctx.Guild.Id));
            }
            catch
            {
                await ctx.Channel.SendErrorAsync(Strings.CantDm(ctx.Guild.Id), Config);
                return;
            }

            if (setup)
                await RecoverAsync(dm, store);
            else
                await SetupAsync(dm);
        }

        private async Task SetupAsync(Channel dm)
        {
            var secretKey = KeyGeneration.GenerateRandomKey();
            var base32Secret = Base32Encoding.ToString(secretKey);
            var secureString = StringExtensions.GenerateSecureString(30);
            var otpauth =
                $"otpauth://totp/{ctx.User.Username}?secret={base32Secret}&issuer={ctx.Client.CurrentUser.Username}";

            using var qrGenerator = new QRCodeGenerator();
            var qrCodeData = qrGenerator.CreateQrCode(otpauth, QRCodeGenerator.ECCLevel.Q);
            var qrCodeImage = new PngByteQRCode(qrCodeData).GetGraphic(20);

            var eb = new EmbedBuilder()
                .WithOkColor()
                .WithTitle(Strings.Keepsecret(ctx.Guild.Id))
                .WithDescription(Strings.RecoveryKeyDetails(ctx.Guild.Id, secureString, base32Secret))
                .WithImageUrl("attachment://qrcode.png");

            await dm.SendFileAsync(new MemoryStream(qrCodeImage), "qrcode.png", embed: eb.Build());
            await dm.SendConfirmAsync(Strings.Pleaseentertwofa(ctx.Guild.Id));

            var code = await NextMessageAsync(dm.Id, ctx.User.Id);
            if (code is null)
            {
                await dm.SendErrorAsync(Strings.Startagain(ctx.Guild.Id), Config);
                return;
            }

            var totp = new Totp(secretKey);
            if (!totp.VerifyTotp(code.Trim(), out _, new VerificationWindow(2, 2)))
            {
                await dm.SendErrorAsync(Strings.Incorrecttwofa(ctx.Guild.Id), Config);
                return;
            }

            await Service.SetupRecovery(ctx.Guild.Id, secureString, base32Secret);
            await dm.SendConfirmAsync(Strings.Recoversetupcomplete(ctx.Guild.Id));
        }

        private async Task RecoverAsync(Channel dm, DataModel.ServerRecoveryStore store)
        {
            await dm.SendConfirmAsync(Strings.ServerRecoverKeyPrompt(ctx.Guild.Id));
            var recoveryKey = await NextMessageAsync(dm.Id, ctx.User.Id);
            if (recoveryKey is null || recoveryKey.Trim() != store.RecoveryKey)
            {
                await dm.SendErrorAsync(Strings.Tryagain(ctx.Guild.Id), Config);
                return;
            }

            await dm.SendConfirmAsync(Strings.Pleaseentertwofa(ctx.Guild.Id));
            var code = await NextMessageAsync(dm.Id, ctx.User.Id);
            var totp = new Totp(Base32Encoding.ToBytes(store.TwoFactorKey));
            if (code is null || !totp.VerifyTotp(code.Trim(), out _, new VerificationWindow(2, 2)))
            {
                await dm.SendErrorAsync(Strings.Incorrecttwofa(ctx.Guild.Id), Config);
                return;
            }

            var currentBotUser = await ctx.Guild.GetCurrentUserAsync();
            var highestRole = currentBotUser.GetRoles().Max(role => role.Position);
            var newRole = await ctx.Guild.CreateRoleAsync(new CreateGuildRoleRequest
            {
                Name = "Recovered Server Owner Role", Permissions = (ulong)GuildPermission.Administrator
            });
            await ctx.Client.Rest.UpdateRolePositionsAsync(ctx.Guild.Id,
                [new RolePositionItemJson { Id = newRole.Id, Position = highestRole - 1 }]);
            await ctx.Member!.AddRoleAsync(newRole);
            await dm.SendConfirmAsync(Strings.Highestpossiblerole(ctx.Guild.Id));
        }
    }
}
