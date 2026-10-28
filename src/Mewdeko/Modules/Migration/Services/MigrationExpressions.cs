using System.Net.Http;
using System.Text.RegularExpressions;
using System.Threading;
using Mewdeko.Modules.Migration.Common;

namespace Mewdeko.Modules.Migration.Services;

/// <summary>
///     What copying emojis and stickers did.
/// </summary>
/// <param name="EmojisCopied">Emojis uploaded to the Fluxer server.</param>
/// <param name="EmojisFailed">Emojis that could not be downloaded or were refused, for example once the server was full.</param>
/// <param name="StickersCopied">Stickers uploaded to the Fluxer server.</param>
/// <param name="StickersFailed">Stickers that could not be copied, including Lottie stickers Fluxer cannot show.</param>
/// <param name="MissingPermission">Whether the bot lacks the permission to create emojis and stickers.</param>
public sealed record ExpressionOutcome(
    int EmojisCopied,
    int EmojisFailed,
    int StickersCopied,
    int StickersFailed,
    bool MissingPermission);

/// <summary>
///     Copies a Discord server's custom emojis and stickers into its Fluxer server, downloading them from Discord's
///     public CDN and uploading in bulk. Emojis already present under the same name are left alone. Every uploaded
///     emoji is added to the migration's emoji pairs, so emoji ids in the copied settings point at the new ones.
/// </summary>
public sealed partial class MigrationExpressions(
    FluxerClient client,
    IHttpClientFactory httpClientFactory,
    ILogger<MigrationExpressions> logger) : INService
{
    private const int BatchSize = 50;
    private const int ParallelDownloads = 8;
    private const int MaxImageBytes = 524288;
    private const int LottieFormat = 3;
    private const int GifFormat = 4;

    /// <summary>
    ///     Counts how many of the export's emojis and stickers would be uploaded.
    /// </summary>
    /// <param name="plan">The prepared migration.</param>
    /// <returns>The emojis without a Fluxer counterpart, and the stickers Fluxer can show.</returns>
    public static (int Emojis, int Stickers) CountToCopy(MigrationPlan plan)
    {
        var emojis = plan.Meta.Emojis.Count(x => !plan.Match.Emojis.ContainsKey(ulong.Parse(x.Id)));
        var stickers = plan.Meta.Stickers.Count(x => x.Format != LottieFormat);
        return (emojis, stickers);
    }

    /// <summary>
    ///     Uploads the export's emojis and stickers that the Fluxer server does not have yet.
    /// </summary>
    /// <param name="plan">The prepared migration, whose emoji pairs grow with every upload.</param>
    /// <returns>What was copied.</returns>
    public async Task<ExpressionOutcome> CopyAsync(MigrationPlan plan, MigrationProgress? progress = null)
    {
        var guild = client.GetGuild(plan.GuildId);
        var permissions = guild?.CurrentMember?.GuildPermissions;
        if (guild is null || permissions is null ||
            !(permissions.Has(GuildPermission.Administrator) ||
              permissions.Has(GuildPermission.CreateExpressions) ||
              permissions.Has(GuildPermission.ManageExpressions)))
            return new ExpressionOutcome(0, 0, 0, 0, true);

        using var http = httpClientFactory.CreateClient();
        var (emojisCopied, emojisFailed) = await CopyEmojisAsync(http, guild, plan, progress);
        var (stickersCopied, stickersFailed) = await CopyStickersAsync(http, guild, plan, progress);
        return new ExpressionOutcome(emojisCopied, emojisFailed, stickersCopied, stickersFailed, false);
    }

    private async Task<(int Copied, int Failed)> CopyEmojisAsync(HttpClient http, SocketGuild guild, MigrationPlan plan,
        MigrationProgress? progress)
    {
        var taken = (guild.Emojis?.Values.Select(x => x.Name) ?? []).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var pending = new List<(DiscordEmojiInfo Emoji, CreateGuildEmojiRequest Request)>();
        var failed = 0;

        var wanted = plan.Meta.Emojis.Where(x => !plan.Match.Emojis.ContainsKey(ulong.Parse(x.Id))).ToList();
        progress?.Begin("emoji_download", wanted.Count);
        var images = await DownloadAllAsync(http, wanted.Select(EmojiUrls).ToList(), progress);
        for (var i = 0; i < wanted.Count; i++)
        {
            if (images[i] is not { } image)
            {
                failed++;
                continue;
            }

            var name = UniqueName(EmojiName(wanted[i].Name), taken, 32);
            pending.Add((wanted[i], new CreateGuildEmojiRequest { Name = name, ImageBase64 = image }));
        }

        var copied = 0;
        progress?.Begin("emoji_upload", pending.Count);
        foreach (var batch in pending.Chunk(BatchSize))
        {
            try
            {
                var result = await client.Rest.CreateEmojiBulkAsync(guild.Id,
                    new BulkCreateGuildEmojisRequest { Emojis = batch.Select(x => x.Request).ToArray() });
                var byName = batch.ToDictionary(x => x.Request.Name, x => x.Emoji, StringComparer.OrdinalIgnoreCase);
                foreach (var created in result.Success)
                {
                    if (!byName.TryGetValue(created.Name, out var source) || created.Id is not { } createdId)
                        continue;

                    plan.Match.Emojis[ulong.Parse(source.Id)] = (createdId, created.Name, created.IsAnimated);
                    copied++;
                }

                failed += result.Failed.Length;
                foreach (var refused in result.Failed)
                    logger.LogDebug("Emoji {Name} was not copied into {GuildId}: {Error}", refused.Name, guild.Id,
                        refused.Error);
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "Uploading a batch of emojis into {GuildId} failed", guild.Id);
                failed += batch.Length;
            }

            progress?.Advance(batch.Length);
        }

        return (copied, failed);
    }

    private async Task<(int Copied, int Failed)> CopyStickersAsync(HttpClient http, SocketGuild guild, MigrationPlan plan,
        MigrationProgress? progress)
    {
        var taken = (guild.Stickers?.Values.Select(x => x.Name) ?? []).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var requests = new List<CreateGuildStickerRequest>();
        var failed = 0;

        progress?.Begin("stickers", plan.Meta.Stickers.Count);
        foreach (var sticker in plan.Meta.Stickers)
        {
            progress?.Advance();
            if (sticker.Format == LottieFormat || taken.Contains(sticker.Name))
            {
                failed += sticker.Format == LottieFormat ? 1 : 0;
                continue;
            }

            var image = await DownloadAsync(http, StickerUrls(sticker));
            if (image is null)
            {
                failed++;
                continue;
            }

            var tags = (sticker.Tags ?? [])
                .Select(x => x.Trim())
                .Where(x => x.Length is >= 1 and <= 30)
                .Take(10)
                .ToArray();
            requests.Add(new CreateGuildStickerRequest
            {
                Name = UniqueName(Truncate(sticker.Name.Trim(), 30), taken, 30),
                Description = string.IsNullOrWhiteSpace(sticker.Description) ? null : Truncate(sticker.Description, 500),
                Tags = tags,
                ImageBase64 = image
            });
        }

        var copied = 0;
        foreach (var batch in requests.Chunk(BatchSize))
        {
            try
            {
                var result = await client.Rest.CreateStickerBulkAsync(guild.Id,
                    new BulkCreateGuildStickersRequest { Stickers = batch });
                copied += result.Success?.Length ?? 0;
                failed += result.Failed.Length;
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "Uploading a batch of stickers into {GuildId} failed", guild.Id);
                failed += batch.Length;
            }
        }

        return (copied, failed);
    }

    private async Task<string?[]> DownloadAllAsync(HttpClient http, List<IEnumerable<string>> sources,
        MigrationProgress? progress)
    {
        using var gate = new SemaphoreSlim(ParallelDownloads);
        var tasks = sources.Select(async urls =>
        {
            await gate.WaitAsync();
            try
            {
                var image = await DownloadAsync(http, urls);
                progress?.Advance();
                return image;
            }
            finally
            {
                gate.Release();
            }
        });
        return await Task.WhenAll(tasks);
    }

    private async Task<string?> DownloadAsync(HttpClient http, IEnumerable<string> urls)
    {
        foreach (var url in urls)
        {
            try
            {
                using var response = await http.GetAsync(url);
                if (!response.IsSuccessStatusCode)
                    continue;

                var bytes = await response.Content.ReadAsByteArrayAsync();
                if (bytes.Length == 0 || bytes.Length > MaxImageBytes)
                    continue;

                var type = response.Content.Headers.ContentType?.MediaType ?? "image/png";
                return $"data:{type};base64,{Convert.ToBase64String(bytes)}";
            }
            catch (Exception ex)
            {
                logger.LogDebug(ex, "Downloading {Url} failed", url);
            }
        }

        return null;
    }

    private static IEnumerable<string> EmojiUrls(DiscordEmojiInfo emoji)
    {
        var extension = emoji.Animated ? "gif" : "png";
        yield return $"https://cdn.discordapp.com/emojis/{emoji.Id}.{extension}?quality=lossless";
        yield return $"https://cdn.discordapp.com/emojis/{emoji.Id}.webp?size=128&quality=lossless&animated={emoji.Animated.ToString().ToLowerInvariant()}";
        yield return $"https://cdn.discordapp.com/emojis/{emoji.Id}.webp?size=64&animated={emoji.Animated.ToString().ToLowerInvariant()}";
    }

    private static IEnumerable<string> StickerUrls(DiscordStickerInfo sticker)
    {
        var extension = sticker.Format == GifFormat ? "gif" : "png";
        yield return $"https://media.discordapp.net/stickers/{sticker.Id}.{extension}";
        yield return $"https://media.discordapp.net/stickers/{sticker.Id}.{extension}?size=160";
    }

    private static string EmojiName(string name)
    {
        var cleaned = InvalidEmojiCharacters().Replace(name, "_").Trim('_');
        if (cleaned.Length < 2)
            cleaned = (cleaned + "_emoji").TrimStart('_');

        return Truncate(cleaned, 32);
    }

    private static string UniqueName(string name, HashSet<string> taken, int maxLength)
    {
        var candidate = name;
        for (var suffix = 2; !taken.Add(candidate); suffix++)
        {
            var tail = $"_{suffix}";
            candidate = Truncate(name, maxLength - tail.Length) + tail;
        }

        return candidate;
    }

    private static string Truncate(string value, int length)
    {
        return value.Length <= length ? value : value[..length];
    }

    [GeneratedRegex("[^A-Za-z0-9_]")]
    private static partial Regex InvalidEmojiCharacters();
}
