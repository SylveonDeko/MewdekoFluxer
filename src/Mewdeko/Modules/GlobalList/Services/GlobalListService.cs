using System.Threading;
using DataModel;
using LinqToDB;
using LinqToDB.Async;
using Mewdeko.Database.DbContextStuff;
using Mewdeko.Modules.GlobalList.Common;
using Mewdeko.Modules.MultiGreets.Services;
using Mewdeko.Services.Impl;
using Mewdeko.Services.Strings;
using Npgsql;

namespace Mewdeko.Modules.GlobalList.Services;

/// <summary>
///     Runs the global list: reports, votes, reviews, appeals and the actions communities take on listed users.
/// </summary>
public class GlobalListService : INService
{
    private const int ActionDelayMs = 300;
    private const int ListLimit = 500;

    private readonly BotCredentials creds;
    private readonly FluxerClient client;
    private readonly IDataConnectionFactory dbFactory;
    private readonly ILogger<GlobalListService> logger;
    private readonly ConcurrentDictionary<ulong, SemaphoreSlim> guildGates = new();
    private readonly ConcurrentDictionary<ulong, GlobalListSettingsView> settings = new();
    private readonly GeneratedBotStrings strings;
    private readonly WelcomeCardService images;

    /// <summary>
    ///     Initializes a new instance of the <see cref="GlobalListService" /> class.
    /// </summary>
    /// <param name="client">The Fluxer client.</param>
    /// <param name="dbFactory">The database connection factory.</param>
    /// <param name="eventHandler">The event handler.</param>
    /// <param name="strings">The generated bot strings.</param>
    /// <param name="creds">The bot credentials.</param>
    /// <param name="images">The image service used for screenshots.</param>
    /// <param name="logger">The logger instance.</param>
    public GlobalListService(FluxerClient client, IDataConnectionFactory dbFactory, EventHandler eventHandler,
        GeneratedBotStrings strings, BotCredentials creds, WelcomeCardService images,
        ILogger<GlobalListService> logger)
    {
        this.client = client;
        this.dbFactory = dbFactory;
        this.strings = strings;
        this.creds = creds;
        this.images = images;
        this.logger = logger;
        eventHandler.UserJoined.Subscribe("GlobalListService", OnUserJoined);
    }

    /// <summary>
    ///     Whether a user is a bot owner.
    /// </summary>
    /// <param name="userId">The user ID.</param>
    public bool IsOwner(ulong userId)
    {
        return creds.IsOwner(userId);
    }

    /// <summary>
    ///     Whether a member has moderation access: the community owner, or Administrator, Manage Community, Ban Members
    ///     or Kick Members.
    /// </summary>
    /// <param name="guild">The guild.</param>
    /// <param name="member">The member.</param>
    public static bool IsStaff(SocketGuild guild, SocketGuildMember member)
    {
        if (guild.OwnerId == member.Id)
            return true;

        var permissions = member.GuildPermissions;
        return permissions.Has(GuildPermission.Administrator) || permissions.Has(GuildPermission.ManageGuild) ||
               permissions.Has(GuildPermission.BanMembers) || permissions.Has(GuildPermission.KickMembers);
    }

    /// <summary>
    ///     Whether a user is an owner or an appointed reviewer.
    /// </summary>
    /// <param name="userId">The user ID.</param>
    public async Task<bool> CanReviewAsync(ulong userId)
    {
        var owner = creds.IsOwner(userId);
        if (owner)
            return GlobalListRules.CanReview(true, false);

        await using var db = await dbFactory.CreateConnectionAsync();
        return GlobalListRules.CanReview(false, await db.GlobalListReviewers.AnyAsync(x => x.UserId == userId));
    }

    /// <summary>
    ///     Gets a community's settings, with a value for every category.
    /// </summary>
    /// <param name="guildId">The guild ID.</param>
    public async Task<GlobalListSettingsView> GetSettingsAsync(ulong guildId)
    {
        if (settings.TryGetValue(guildId, out var cached))
            return cached;

        await using var db = await dbFactory.CreateConnectionAsync();
        var row = await db.GlobalListSettings.FirstOrDefaultAsync(x => x.GuildId == guildId);
        var actions = await db.GlobalListCategoryActions.Where(x => x.GuildId == guildId).ToListAsync();

        var view = new GlobalListSettingsView
        {
            Enabled = row?.Enabled ?? false,
            LogChannelId = row?.LogChannelId ?? 0,
            QuarantineRoleId = row?.QuarantineRoleId ?? 0,
            ApplyToExisting = row?.ApplyToExisting ?? false
        };

        foreach (var category in Enum.GetValues<GlobalListCategory>())
            view.Actions[category] = actions.FirstOrDefault(x => x.Category == category)?.Action ??
                                     GlobalListAction.Alert;

        settings[guildId] = view;
        return view;
    }

    /// <summary>
    ///     Changes a community's settings.
    /// </summary>
    /// <param name="guildId">The guild ID.</param>
    /// <param name="patch">The change.</param>
    /// <returns>The settings as they are now.</returns>
    public async Task<GlobalListSettingsView> UpdateSettingsAsync(ulong guildId, GlobalListSettingsPatch patch)
    {
        await using var db = await dbFactory.CreateConnectionAsync();
        var row = await db.GlobalListSettings.FirstOrDefaultAsync(x => x.GuildId == guildId);
        var exists = row is not null;
        row ??= new GlobalListSetting
        {
            GuildId = guildId, DateAdded = DateTime.UtcNow
        };

        if (patch.Enabled is { } enabled)
            row.Enabled = enabled;
        if (patch.LogChannelId is { } channel)
            row.LogChannelId = channel == 0 ? null : channel;
        if (patch.QuarantineRoleId is { } role)
            row.QuarantineRoleId = role == 0 ? null : role;
        if (patch.ApplyToExisting is { } apply)
            row.ApplyToExisting = apply;

        if (exists)
            await db.UpdateAsync(row);
        else
            await db.InsertAsync(row);

        foreach (var (category, action) in patch.Actions ?? [])
        {
            if (!Enum.IsDefined(category) || !Enum.IsDefined(action))
                continue;

            var updated = await db.GlobalListCategoryActions
                .Where(x => x.GuildId == guildId && x.Category == category)
                .Set(x => x.Action, action)
                .UpdateAsync();
            if (updated == 0)
                await db.InsertAsync(new GlobalListCategoryAction
                {
                    GuildId = guildId, Category = category, Action = action
                });
        }

        settings.TryRemove(guildId, out _);
        return await GetSettingsAsync(guildId);
    }

    /// <summary>
    ///     Gets a community's reporting record and how many reports it made in the last day.
    /// </summary>
    /// <param name="guildId">The guild ID.</param>
    public async Task<GlobalListStandingView> GetStandingAsync(ulong guildId)
    {
        await using var db = await dbFactory.CreateConnectionAsync();
        var row = await db.GlobalListStandings.FirstOrDefaultAsync(x => x.GuildId == guildId);
        var since = DateTime.UtcNow.AddDays(-1);
        var today = await db.GlobalListReports.CountAsync(x => x.GuildId == guildId && x.DateAdded >= since);
        var approved = row?.Approved ?? 0;
        var rejected = row?.Rejected ?? 0;
        return new GlobalListStandingView
        {
            Approved = approved,
            Rejected = rejected,
            Blocked = GlobalListRules.IsBlocked(approved, rejected),
            ReportsToday = today
        };
    }

    /// <summary>
    ///     Lists every community that has a reporting record.
    /// </summary>
    public async Task<List<GlobalListCommunityStanding>> ListStandingsAsync()
    {
        await using var db = await dbFactory.CreateConnectionAsync();
        var rows = await db.GlobalListStandings.OrderByDescending(x => x.Rejected).ToListAsync();
        return rows.Select(x => new GlobalListCommunityStanding
        {
            GuildId = x.GuildId,
            GuildName = client.GetGuild(x.GuildId)?.Name,
            Approved = x.Approved,
            Rejected = x.Rejected,
            Blocked = GlobalListRules.IsBlocked(x.Approved, x.Rejected),
            ResetAt = x.ResetAt
        }).ToList();
    }

    /// <summary>
    ///     Clears a community's approved and rejected counts.
    /// </summary>
    /// <param name="guildId">The guild ID.</param>
    public async Task<bool> ResetStandingAsync(ulong guildId)
    {
        await using var db = await dbFactory.CreateConnectionAsync();
        return await db.GlobalListStandings.Where(x => x.GuildId == guildId)
            .Set(x => x.Approved, 0)
            .Set(x => x.Rejected, 0)
            .Set(x => x.ResetAt, DateTime.UtcNow)
            .UpdateAsync() > 0;
    }

    /// <summary>
    ///     Makes a report. A user with a pending entry gets the report attached to it.
    /// </summary>
    /// <param name="guildId">The reporting guild.</param>
    /// <param name="userId">The reported user.</param>
    /// <param name="category">The category.</param>
    /// <param name="reason">The reason.</param>
    /// <param name="links">The message links.</param>
    /// <param name="screenshots">The screenshots as image files.</param>
    /// <returns>The error, or none, and the entry ID.</returns>
    public async Task<(ReportError Error, int EntryId)> ReportAsync(ulong guildId, ulong userId,
        GlobalListCategory category, string? reason, IEnumerable<string>? links, IReadOnlyList<byte[]> screenshots)
    {
        if (!(await GetSettingsAsync(guildId)).Enabled)
            return (ReportError.NotOptedIn, 0);

        if (userId == 0 || userId == client.CurrentUser.Id || creds.IsOwner(userId) || !Enum.IsDefined(category))
            return (ReportError.InvalidTarget, 0);

        var standing = await GetStandingAsync(guildId);
        if (standing.Blocked)
            return (ReportError.Blocked, 0);

        if (GlobalListRules.IsOverDailyCap(standing.ReportsToday))
            return (ReportError.DailyCap, 0);

        var text = GlobalListRules.Clip(reason, GlobalListRules.MaxTextLength);
        if (text is null)
            return (ReportError.NoReason, 0);

        var cleanLinks = GlobalListRules.CleanLinks(links);
        if (screenshots.Count > GlobalListRules.MaxScreenshots)
            return (ReportError.InvalidImage, 0);

        if (!GlobalListRules.HasEvidence(cleanLinks, screenshots.Count))
            return (ReportError.NoEvidence, 0);

        var pictures = new List<byte[]>();
        foreach (var bytes in screenshots)
        {
            if (bytes.Length is 0 or > WelcomeCardService.MaxImageBytes ||
                await Task.Run(() => WelcomeCardService.Normalize(bytes)) is not { } normalized)
                return (ReportError.InvalidImage, 0);

            pictures.Add(normalized.Image);
        }

        for (var attempt = 0; attempt < 2; attempt++)
        {
            try
            {
                return await StoreReportAsync(guildId, userId, category, text, cleanLinks, pictures);
            }
            catch (PostgresException ex) when (ex.SqlState == PostgresErrorCodes.UniqueViolation && attempt == 0)
            {
                logger.LogDebug("Global list report raced another report for {UserId}, retrying", userId);
            }
        }

        return (ReportError.AlreadyReported, 0);
    }

    private async Task<(ReportError Error, int EntryId)> StoreReportAsync(ulong guildId, ulong userId,
        GlobalListCategory category, string reason, List<string> links, List<byte[]> pictures)
    {
        await using var db = await dbFactory.CreateConnectionAsync();
        await using var transaction = await db.BeginTransactionAsync();

        var open = await db.GlobalListEntries.FirstOrDefaultAsync(x =>
            x.UserId == userId && (x.Status == GlobalListStatus.Pending || x.Status == GlobalListStatus.Live));
        var alreadyReported = open is not null &&
                              await db.GlobalListReports.AnyAsync(x => x.EntryId == open.Id && x.GuildId == guildId);

        var route = GlobalListRules.Route(open, alreadyReported);
        switch (route)
        {
            case ReportRoute.AlreadyListed:
                return (ReportError.AlreadyListed, open!.Id);
            case ReportRoute.AlreadyReported:
                return (ReportError.AlreadyReported, open!.Id);
        }

        var entryId = open?.Id ?? await db.InsertWithInt32IdentityAsync(new GlobalListEntry
        {
            UserId = userId, Category = category, Status = GlobalListStatus.Pending, DateAdded = DateTime.UtcNow
        });

        var reportId = await db.InsertWithInt32IdentityAsync(new GlobalListReport
        {
            EntryId = entryId,
            GuildId = guildId,
            Category = category,
            Reason = reason,
            Evidence = string.Join('\n', links),
            DateAdded = DateTime.UtcNow
        });

        foreach (var picture in pictures)
            await db.InsertAsync(new GlobalListEvidenceImage
            {
                ReportId = reportId, Data = picture, DateAdded = DateTime.UtcNow
            });

        await transaction.CommitAsync();
        return (ReportError.None, entryId);
    }

    /// <summary>
    ///     Downloads an image for a report.
    /// </summary>
    /// <param name="url">The link.</param>
    public Task<byte[]?> DownloadImageAsync(string url)
    {
        return images.DownloadImageAsync(url);
    }

    /// <summary>
    ///     Decodes a screenshot sent as base64 or a data URI.
    /// </summary>
    /// <param name="data">The data.</param>
    /// <returns>The bytes, or null when it is not base64.</returns>
    public static byte[]? DecodeImage(string data)
    {
        var payload = data.Trim();
        var comma = payload.IndexOf(',');
        if (payload.StartsWith("data:", StringComparison.OrdinalIgnoreCase) && comma > 0)
            payload = payload[(comma + 1)..];

        try
        {
            return Convert.FromBase64String(payload);
        }
        catch (FormatException)
        {
            return null;
        }
    }

    /// <summary>
    ///     Lists pending entries for a community to look at, with the ones it reported marked.
    /// </summary>
    /// <param name="guildId">The guild ID.</param>
    public async Task<List<GlobalListEntryItem>> GetOpenAsync(ulong guildId)
    {
        await using var db = await dbFactory.CreateConnectionAsync();
        var entries = await db.GlobalListEntries
            .Where(x => x.Status == GlobalListStatus.Pending)
            .OrderBy(x => x.DateAdded)
            .Take(ListLimit)
            .ToListAsync();
        return await BuildItemsAsync(db, entries, guildId);
    }

    /// <summary>
    ///     Lists what waits for a reviewer.
    /// </summary>
    public async Task<GlobalListQueue> GetQueueAsync()
    {
        await using var db = await dbFactory.CreateConnectionAsync();
        var entries = await db.GlobalListEntries
            .Where(x => x.Status == GlobalListStatus.Pending)
            .OrderBy(x => x.DateAdded)
            .Take(ListLimit)
            .ToListAsync();

        var appeals = await (from a in db.GlobalListAppeals
            join e in db.GlobalListEntries on a.EntryId equals e.Id
            where a.Status == GlobalListAppealStatus.Open
            orderby a.DateAdded
            select new GlobalListAppealView
            {
                Id = a.Id,
                EntryId = a.EntryId,
                UserId = a.UserId,
                Category = e.Category,
                Text = a.Text,
                Status = a.Status,
                DecisionNote = a.DecisionNote,
                DateAdded = a.DateAdded
            }).Take(ListLimit).ToListAsync();

        return new GlobalListQueue
        {
            Entries = await BuildItemsAsync(db, entries, null), Appeals = appeals
        };
    }

    /// <summary>
    ///     Lists the live entries, newest first.
    /// </summary>
    /// <param name="userId">Only this user, or null for everyone.</param>
    public async Task<List<GlobalListEntryItem>> GetLiveAsync(ulong? userId = null)
    {
        await using var db = await dbFactory.CreateConnectionAsync();
        var query = db.GlobalListEntries.Where(x => x.Status == GlobalListStatus.Live);
        if (userId is { } id)
            query = query.Where(x => x.UserId == id);

        var entries = await query.OrderByDescending(x => x.DecidedAt).Take(ListLimit).ToListAsync();
        return await BuildItemsAsync(db, entries, null);
    }

    /// <summary>
    ///     Gets the live entry for a user.
    /// </summary>
    /// <param name="userId">The user ID.</param>
    public async Task<GlobalListEntry?> GetLiveEntryAsync(ulong userId)
    {
        await using var db = await dbFactory.CreateConnectionAsync();
        return await db.GlobalListEntries.FirstOrDefaultAsync(x =>
            x.UserId == userId && x.Status == GlobalListStatus.Live);
    }

    /// <summary>
    ///     Gets an entry with its reports, votes and appeals.
    /// </summary>
    /// <param name="entryId">The entry ID.</param>
    /// <param name="guildId">The asking community, or null for a reviewer.</param>
    public async Task<GlobalListEntryDetail?> GetDetailAsync(int entryId, ulong? guildId)
    {
        await using var db = await dbFactory.CreateConnectionAsync();
        var entry = await db.GlobalListEntries.FirstOrDefaultAsync(x => x.Id == entryId);
        if (entry is null)
            return null;

        var item = (await BuildItemsAsync(db, [entry], guildId))[0];
        var reports = await db.GlobalListReports.Where(x => x.EntryId == entryId).OrderBy(x => x.Id).ToListAsync();
        var reportIds = reports.Select(x => x.Id).ToList();
        var pictures = await db.GlobalListEvidenceImages.Where(x => reportIds.Contains(x.ReportId))
            .Select(x => new
            {
                x.Id, x.ReportId
            }).ToListAsync();
        var votes = await db.GlobalListVotes.Where(x => x.EntryId == entryId).OrderBy(x => x.Id).ToListAsync();
        var appeals = await (from a in db.GlobalListAppeals
            where a.EntryId == entryId
            orderby a.Id descending
            select new GlobalListAppealView
            {
                Id = a.Id,
                EntryId = a.EntryId,
                UserId = a.UserId,
                Category = entry.Category,
                Text = a.Text,
                Status = a.Status,
                DecisionNote = a.DecisionNote,
                DateAdded = a.DateAdded
            }).ToListAsync();

        return new GlobalListEntryDetail
        {
            Entry = item,
            Reports = reports.Select(x => new GlobalListReportView
            {
                Id = x.Id,
                GuildId = x.GuildId,
                GuildName = client.GetGuild(x.GuildId)?.Name,
                Category = x.Category,
                Reason = x.Reason,
                Links = x.Evidence.Split('\n', StringSplitOptions.RemoveEmptyEntries).ToList(),
                ImageIds = pictures.Where(p => p.ReportId == x.Id).Select(p => p.Id).ToList(),
                DateAdded = x.DateAdded
            }).ToList(),
            Votes = votes.Select(x => new GlobalListVoteView
            {
                GuildId = x.GuildId,
                GuildName = client.GetGuild(x.GuildId)?.Name,
                Confirm = x.Confirm,
                Note = x.Note,
                DateAdded = x.DateAdded
            }).ToList(),
            Appeals = appeals
        };
    }

    /// <summary>
    ///     Gets a screenshot of an entry as PNG.
    /// </summary>
    /// <param name="entryId">The entry the screenshot belongs to.</param>
    /// <param name="imageId">The screenshot ID.</param>
    public async Task<byte[]?> GetImageAsync(int entryId, int imageId)
    {
        await using var db = await dbFactory.CreateConnectionAsync();
        return await (from i in db.GlobalListEvidenceImages
            join r in db.GlobalListReports on i.ReportId equals r.Id
            where i.Id == imageId && r.EntryId == entryId
            select i.Data).FirstOrDefaultAsync();
    }

    /// <summary>
    ///     Casts or changes a community's advisory vote on a pending entry.
    /// </summary>
    /// <param name="guildId">The voting guild.</param>
    /// <param name="entryId">The entry ID.</param>
    /// <param name="confirm">True to confirm the report, false to dispute it.</param>
    /// <param name="note">An optional short note.</param>
    public async Task<VoteError> VoteAsync(ulong guildId, int entryId, bool confirm, string? note)
    {
        if (!(await GetSettingsAsync(guildId)).Enabled)
            return VoteError.NotOptedIn;

        await using var db = await dbFactory.CreateConnectionAsync();
        var entry = await db.GlobalListEntries.FirstOrDefaultAsync(x => x.Id == entryId);
        if (entry is not { Status: GlobalListStatus.Pending })
            return VoteError.NotOpen;

        var reporters = await db.GlobalListReports.Where(x => x.EntryId == entryId).Select(x => x.GuildId)
            .ToListAsync();
        var voters = await db.GlobalListVotes.Where(x => x.EntryId == entryId).Select(x => x.GuildId).ToListAsync();
        if (!GlobalListRules.CanVote(reporters, voters, guildId))
            return VoteError.NotAllowed;

        try
        {
            await db.InsertAsync(new GlobalListVote
            {
                EntryId = entryId,
                GuildId = guildId,
                Confirm = confirm,
                Note = GlobalListRules.Clip(note, GlobalListRules.MaxNoteLength),
                DateAdded = DateTime.UtcNow
            });
        }
        catch (PostgresException ex) when (ex.SqlState == PostgresErrorCodes.UniqueViolation)
        {
            return VoteError.NotAllowed;
        }

        return VoteError.None;
    }

    /// <summary>
    ///     Approves or rejects an entry. Owners can also overturn an earlier decision.
    /// </summary>
    /// <param name="entryId">The entry ID.</param>
    /// <param name="approve">True to approve, false to reject.</param>
    /// <param name="note">The reviewer's note.</param>
    /// <param name="actorId">The reviewer's user ID.</param>
    /// <param name="isOwner">Whether the reviewer is a bot owner.</param>
    /// <param name="category">A category to list the user under instead of the reported one.</param>
    public async Task<(ReviewError Error, GlobalListEntry? Entry)> ReviewAsync(int entryId, bool approve,
        string? note, ulong actorId, bool isOwner, GlobalListCategory? category = null)
    {
        await using var db = await dbFactory.CreateConnectionAsync();
        await using var transaction = await db.BeginTransactionAsync();

        var entry = await db.GlobalListEntries.FirstOrDefaultAsync(x => x.Id == entryId);
        if (entry is null)
            return (ReviewError.NotFound, null);

        var target = approve ? GlobalListStatus.Live : GlobalListStatus.Rejected;
        if (entry.Status == GlobalListStatus.Removed)
            return (ReviewError.WrongState, entry);

        if (entry.Status == target)
            return (ReviewError.AlreadyDecided, entry);

        if (entry.Status != GlobalListStatus.Pending && !isOwner)
            return (ReviewError.AlreadyDecided, entry);

        if (approve && entry.Status != GlobalListStatus.Rejected && entry.Status != GlobalListStatus.Pending)
            return (ReviewError.WrongState, entry);

        var previous = entry.Status;
        entry.Status = target;
        entry.DecisionNote = GlobalListRules.Clip(note, GlobalListRules.MaxTextLength);
        entry.DecidedBy = actorId;
        entry.DecidedAt = DateTime.UtcNow;
        if (approve && category is { } chosen && Enum.IsDefined(chosen))
            entry.Category = chosen;

        await db.UpdateAsync(entry);

        var reporters = await db.GlobalListReports.Where(x => x.EntryId == entryId).Select(x => x.GuildId)
            .Distinct().ToListAsync();
        foreach (var guildId in reporters)
        {
            var approvedDelta = (target == GlobalListStatus.Live ? 1 : 0) - (previous == GlobalListStatus.Live ? 1 : 0);
            var rejectedDelta = (target == GlobalListStatus.Rejected ? 1 : 0) -
                                (previous == GlobalListStatus.Rejected ? 1 : 0);
            await AdjustStandingAsync(db, guildId, approvedDelta, rejectedDelta);
        }

        if (!approve)
            await CloseAppealsAsync(db, entryId, actorId, GlobalListAppealStatus.Approved);

        await transaction.CommitAsync();

        if (approve)
            _ = Task.Run(() => ApplyToExistingAsync(entry));

        return (ReviewError.None, entry);
    }

    /// <summary>
    ///     Takes a live entry off the list.
    /// </summary>
    /// <param name="entryId">The entry ID.</param>
    /// <param name="note">The note.</param>
    /// <param name="actorId">The owner's user ID.</param>
    public async Task<(ReviewError Error, GlobalListEntry? Entry)> RemoveAsync(int entryId, string? note,
        ulong actorId)
    {
        await using var db = await dbFactory.CreateConnectionAsync();
        await using var transaction = await db.BeginTransactionAsync();

        var entry = await db.GlobalListEntries.FirstOrDefaultAsync(x => x.Id == entryId);
        if (entry is null)
            return (ReviewError.NotFound, null);

        if (entry.Status != GlobalListStatus.Live)
            return (ReviewError.WrongState, entry);

        entry.Status = GlobalListStatus.Removed;
        entry.RemovedBy = actorId;
        entry.RemovedAt = DateTime.UtcNow;
        entry.RemovedNote = GlobalListRules.Clip(note, GlobalListRules.MaxTextLength);
        await db.UpdateAsync(entry);
        await CloseAppealsAsync(db, entryId, actorId, GlobalListAppealStatus.Approved);
        await transaction.CommitAsync();
        return (ReviewError.None, entry);
    }

    /// <summary>
    ///     Files an appeal for a listed user.
    /// </summary>
    /// <param name="userId">The listed user's ID.</param>
    /// <param name="text">The appeal text.</param>
    public async Task<(AppealError Error, int AppealId)> AppealAsync(ulong userId, string? text)
    {
        var clipped = GlobalListRules.Clip(text, GlobalListRules.MaxTextLength);
        if (clipped is null)
            return (AppealError.NoText, 0);

        await using var db = await dbFactory.CreateConnectionAsync();
        var entry = await db.GlobalListEntries.FirstOrDefaultAsync(x =>
            x.UserId == userId && x.Status == GlobalListStatus.Live);
        if (entry is null)
            return (AppealError.NotListed, 0);

        if (await db.GlobalListAppeals.AnyAsync(x => x.EntryId == entry.Id && x.Status == GlobalListAppealStatus.Open))
            return (AppealError.AlreadyOpen, 0);

        try
        {
            var id = await db.InsertWithInt32IdentityAsync(new GlobalListAppeal
            {
                EntryId = entry.Id, UserId = userId, Text = clipped, DateAdded = DateTime.UtcNow
            });
            return (AppealError.None, id);
        }
        catch (PostgresException ex) when (ex.SqlState == PostgresErrorCodes.UniqueViolation)
        {
            return (AppealError.AlreadyOpen, 0);
        }
    }

    /// <summary>
    ///     Approves or rejects an appeal. Approving removes the entry.
    /// </summary>
    /// <param name="appealId">The appeal ID.</param>
    /// <param name="approve">True to approve.</param>
    /// <param name="note">The reviewer's note.</param>
    /// <param name="actorId">The reviewer's user ID.</param>
    public async Task<ReviewError> ReviewAppealAsync(int appealId, bool approve, string? note, ulong actorId)
    {
        await using var db = await dbFactory.CreateConnectionAsync();
        await using var transaction = await db.BeginTransactionAsync();

        var appeal = await db.GlobalListAppeals.FirstOrDefaultAsync(x => x.Id == appealId);
        if (appeal is null)
            return ReviewError.NotFound;

        if (appeal.Status != GlobalListAppealStatus.Open)
            return ReviewError.AlreadyDecided;

        var clipped = GlobalListRules.Clip(note, GlobalListRules.MaxTextLength);
        appeal.Status = approve ? GlobalListAppealStatus.Approved : GlobalListAppealStatus.Rejected;
        appeal.DecisionNote = clipped;
        appeal.DecidedBy = actorId;
        appeal.DecidedAt = DateTime.UtcNow;
        await db.UpdateAsync(appeal);

        if (approve)
            await db.GlobalListEntries.Where(x => x.Id == appeal.EntryId && x.Status == GlobalListStatus.Live)
                .Set(x => x.Status, GlobalListStatus.Removed)
                .Set(x => x.RemovedBy, actorId)
                .Set(x => x.RemovedAt, DateTime.UtcNow)
                .Set(x => x.RemovedNote, clipped)
                .Set(x => x.RemovedByAppeal, true)
                .UpdateAsync();

        await transaction.CommitAsync();
        await NotifyAppealAsync(appeal.UserId, approve, clipped);
        return ReviewError.None;
    }

    private async Task NotifyAppealAsync(ulong userId, bool approved, string? note)
    {
        try
        {
            IUser? user = client.GetUser(userId);
            user ??= await client.Rest.GetUserAsync(userId);
            if (user is null)
                return;

            var text = approved
                ? strings.GloballistAppealDmApproved(null)
                : strings.GloballistAppealDmRejected(null);
            if (!string.IsNullOrWhiteSpace(note))
                text += "\n" + strings.GloballistAppealDmNote(null, note);

            var dm = await user.CreateDMChannelAsync();
            await dm.SendAsync(text);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Could not send the appeal result to {UserId}", userId);
        }
    }

    /// <summary>
    ///     Lists the appointed reviewers.
    /// </summary>
    public async Task<List<GlobalListReviewerView>> GetReviewersAsync()
    {
        await using var db = await dbFactory.CreateConnectionAsync();
        return await db.GlobalListReviewers.OrderBy(x => x.Id).Select(x => new GlobalListReviewerView
        {
            UserId = x.UserId, AddedBy = x.AddedBy, DateAdded = x.DateAdded
        }).ToListAsync();
    }

    /// <summary>
    ///     Appoints a reviewer.
    /// </summary>
    /// <param name="userId">The user ID.</param>
    /// <param name="actorId">The owner appointing them.</param>
    /// <returns>False when they already are one.</returns>
    public async Task<bool> AddReviewerAsync(ulong userId, ulong actorId)
    {
        await using var db = await dbFactory.CreateConnectionAsync();
        if (await db.GlobalListReviewers.AnyAsync(x => x.UserId == userId))
            return false;

        await db.InsertAsync(new GlobalListReviewer
        {
            UserId = userId, AddedBy = actorId, DateAdded = DateTime.UtcNow
        });
        return true;
    }

    /// <summary>
    ///     Removes a reviewer.
    /// </summary>
    /// <param name="userId">The user ID.</param>
    public async Task<bool> RemoveReviewerAsync(ulong userId)
    {
        await using var db = await dbFactory.CreateConnectionAsync();
        return await db.GlobalListReviewers.Where(x => x.UserId == userId).DeleteAsync() > 0;
    }

    private static async Task AdjustStandingAsync(MewdekoDb db, ulong guildId, int approvedDelta, int rejectedDelta)
    {
        if (approvedDelta == 0 && rejectedDelta == 0)
            return;

        var row = await db.GlobalListStandings.FirstOrDefaultAsync(x => x.GuildId == guildId);
        if (row is null)
        {
            await db.InsertAsync(new GlobalListStanding
            {
                GuildId = guildId, Approved = Math.Max(0, approvedDelta), Rejected = Math.Max(0, rejectedDelta)
            });
            return;
        }

        row.Approved = Math.Max(0, row.Approved + approvedDelta);
        row.Rejected = Math.Max(0, row.Rejected + rejectedDelta);
        await db.UpdateAsync(row);
    }

    private static async Task CloseAppealsAsync(MewdekoDb db, int entryId, ulong actorId,
        GlobalListAppealStatus outcome)
    {
        await db.GlobalListAppeals.Where(x => x.EntryId == entryId && x.Status == GlobalListAppealStatus.Open)
            .Set(x => x.Status, outcome)
            .Set(x => x.DecidedBy, actorId)
            .Set(x => x.DecidedAt, DateTime.UtcNow)
            .UpdateAsync();
    }

    private static async Task<List<GlobalListEntryItem>> BuildItemsAsync(MewdekoDb db, List<GlobalListEntry> entries,
        ulong? guildId)
    {
        var ids = entries.Select(x => x.Id).ToList();
        var reports = await db.GlobalListReports.Where(x => ids.Contains(x.EntryId))
            .Select(x => new
            {
                x.EntryId, x.GuildId
            }).ToListAsync();
        var votes = await db.GlobalListVotes.Where(x => ids.Contains(x.EntryId))
            .Select(x => new
            {
                x.EntryId, x.GuildId, x.Confirm
            }).ToListAsync();

        return entries.Select(x => new GlobalListEntryItem
        {
            Id = x.Id,
            UserId = x.UserId,
            Category = x.Category,
            Status = x.Status,
            ReportCount = reports.Count(r => r.EntryId == x.Id),
            ConfirmVotes = votes.Count(v => v.EntryId == x.Id && v.Confirm),
            DisputeVotes = votes.Count(v => v.EntryId == x.Id && !v.Confirm),
            ReportedByMe = guildId is { } g && reports.Any(r => r.EntryId == x.Id && r.GuildId == g),
            MyVote = guildId is { } v ? votes.FirstOrDefault(r => r.EntryId == x.Id && r.GuildId == v)?.Confirm : null,
            DecisionNote = x.DecisionNote,
            DecidedAt = x.DecidedAt,
            DateAdded = x.DateAdded
        }).ToList();
    }

    private async Task OnUserJoined(SocketGuildMember member)
    {
        try
        {
            var guild = member.Guild;
            var view = await GetSettingsAsync(guild.Id);
            if (!view.Enabled)
                return;

            var entry = await GetLiveEntryAsync(member.Id);
            if (entry is null)
                return;

            await ActAsync(guild, member, entry, view);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Global list check failed for {UserId} in {GuildId}", member.Id, member.Guild.Id);
        }
    }

    private async Task ApplyToExistingAsync(GlobalListEntry entry)
    {
        try
        {
            List<ulong> guildIds;
            await using (var db = await dbFactory.CreateConnectionAsync())
                guildIds = await db.GlobalListSettings.Where(x => x.Enabled && x.ApplyToExisting)
                    .Select(x => x.GuildId).ToListAsync();

            foreach (var guildId in guildIds)
            {
                var guild = client.GetGuild(guildId);
                var member = guild?.GetUser(entry.UserId);
                if (guild is null || member is null)
                    continue;

                try
                {
                    await ActAsync(guild, member, entry, await GetSettingsAsync(guildId));
                }
                catch (Exception ex)
                {
                    logger.LogWarning(ex, "Global list could not act on {UserId} in {GuildId}", entry.UserId, guildId);
                }

                await Task.Delay(ActionDelayMs);
            }
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Global list could not apply entry {EntryId} to existing members", entry.Id);
        }
    }

    private async Task ActAsync(SocketGuild guild, SocketGuildMember member, GlobalListEntry entry,
        GlobalListSettingsView view)
    {
        if (member.Id == client.CurrentUser.Id)
            return;

        var action = view.Actions.GetValueOrDefault(entry.Category, GlobalListAction.Alert);
        if (action == GlobalListAction.Ignore)
            return;

        var gate = guildGates.GetOrAdd(guild.Id, _ => new SemaphoreSlim(1, 1));
        await gate.WaitAsync();
        try
        {
            var outcome = action == GlobalListAction.Alert
                ? strings.GloballistOutcomeAlert(guild.Id)
                : await RunActionAsync(guild, member, entry, view, action);

            var date = (entry.DecidedAt ?? entry.DateAdded).ToString("yyyy-MM-dd");
            await LogAsync(guild, view,
                strings.GloballistLogLine(guild.Id, member.Mention, member.Id, entry.Category, date, outcome));
            await Task.Delay(ActionDelayMs);
        }
        finally
        {
            gate.Release();
        }
    }

    private async Task<string> RunActionAsync(SocketGuild guild, SocketGuildMember member, GlobalListEntry entry,
        GlobalListSettingsView view, GlobalListAction action)
    {
        var failure = CheckAction(guild, member, view, action);
        if (failure is not null)
            return strings.GloballistOutcomeFailed(guild.Id, failure);

        var reason = strings.GloballistAuditReason(guild.Id, entry.Category);
        var options = new RequestOptions
        {
            AuditLogReason = reason
        };

        try
        {
            switch (action)
            {
                case GlobalListAction.QuarantineRole:
                    await member.AddRoleAsync(view.QuarantineRoleId, options);
                    return strings.GloballistOutcomeQuarantine(guild.Id);
                case GlobalListAction.Kick:
                    await member.KickAsync(reason, options);
                    return strings.GloballistOutcomeKick(guild.Id);
                default:
                    await guild.AddBanAsync(member.Id, 0, reason);
                    return strings.GloballistOutcomeBan(guild.Id);
            }
        }
        catch (FluxerApiException ex)
        {
            logger.LogWarning("Global list could not act on {UserId} in {GuildId}: {Reason}", member.Id, guild.Id,
                ex.Reason ?? ex.Message);
            return strings.GloballistOutcomeFailed(guild.Id, strings.GloballistFailHttp(guild.Id, ex.Reason ?? ex.Message));
        }
    }

    private string? CheckAction(SocketGuild guild, SocketGuildMember member, GlobalListSettingsView view,
        GlobalListAction action)
    {
        if (member.Id == guild.OwnerId)
            return strings.GloballistFailOwner(guild.Id);

        var me = guild.CurrentUser;
        if (member.Hierarchy >= me.Hierarchy)
            return strings.GloballistFailHierarchy(guild.Id);

        var permissions = me.GuildPermissions;
        switch (action)
        {
            case GlobalListAction.Kick when !permissions.KickMembers:
                return strings.GloballistFailPermission(guild.Id, "Kick Members");
            case GlobalListAction.Ban when !permissions.BanMembers:
                return strings.GloballistFailPermission(guild.Id, "Ban Members");
            case GlobalListAction.QuarantineRole when !permissions.ManageRoles:
                return strings.GloballistFailPermission(guild.Id, "Manage Roles");
        }

        if (action == GlobalListAction.QuarantineRole &&
            (view.QuarantineRoleId == 0 || guild.GetRole(view.QuarantineRoleId) is not { } role ||
             role.Position >= me.Hierarchy))
            return strings.GloballistFailRole(guild.Id);

        return null;
    }

    private async Task LogAsync(SocketGuild guild, GlobalListSettingsView view, string description)
    {
        if (view.LogChannelId == 0 || guild.GetTextChannel(view.LogChannelId) is not { } channel)
            return;

        try
        {
            await channel.SendAsync(embed: new EmbedBuilder()
                .WithOkColor(guild.Id)
                .WithTitle(strings.GloballistLogTitle(guild.Id))
                .WithDescription(description)
                .WithTimestamp(DateTimeOffset.UtcNow)
                .Build());
        }
        catch (FluxerApiException ex)
        {
            logger.LogDebug(ex, "Could not write to the global list log channel in {GuildId}", guild.Id);
        }
    }
}
