using System.Diagnostics;
using System.Reflection;
using System.Text.Json;
using DataModel;
using Figgle.Fonts;
using Mewdeko.Common.ModuleBehaviors;
using Mewdeko.Common.TypeReaders;
using Mewdeko.Services.Impl;
using Mewdeko.Services.Settings;
using Microsoft.Extensions.DependencyInjection;
using StackExchange.Redis;

namespace Mewdeko;

/// <summary>
///     The main class for the Mewdeko bot, responsible for initializing services, handling events, and managing the bot's
///     lifecycle.
/// </summary>
public class Mewdeko : IDisposable
{
    // Cached JsonSerializerOptions for performance
    private static readonly JsonSerializerOptions CachedJsonOptions = new()
    {
        PropertyNameCaseInsensitive = true
    };

    private readonly BotConfigService bss;

    private readonly ILogger<Mewdeko> logger;
    private bool disposed;

    /// <summary>
    ///     Initializes a new instance of the Mewdeko class.
    /// </summary>
    /// <param name="services">The service provider for dependency injection.</param>
    /// <param name="logger">The logger instance for structured logging.</param>
    public Mewdeko(IServiceProvider services, ILogger<Mewdeko> logger)
    {
        Services = services;
        this.logger = logger;
        Credentials = Services.GetRequiredService<BotCredentials>();
        Services.GetRequiredService<IDataCache>();
        Client = Services.GetRequiredService<FluxerClient>();
        CommandService = Services.GetRequiredService<CommandService>();
        GuildSettingsService = Services.GetRequiredService<GuildSettingsService>();
        bss = Services.GetRequiredService<BotConfigService>();
    }

    /// <summary>
    ///     Gets the credentials used by the bot.
    /// </summary>
    public BotCredentials Credentials { get; }

    /// <summary>
    ///     Gets the Discord client used by the bot.
    /// </summary>
    public FluxerClient Client { get; }

    private GuildSettingsService GuildSettingsService { get; }

    private CommandService CommandService { get; }

    /// <summary>
    ///     Gets or sets the color used for successful operations.
    /// </summary>
    public static Color OkColor { get; set; }

    /// <summary>
    ///     Gets or sets the color used for error operations.
    /// </summary>
    public static Color ErrorColor { get; set; }

    /// <summary>
    ///     Gets a TaskCompletionSource that completes when the bot is ready.
    /// </summary>
    public TaskCompletionSource<bool> Ready { get; } = new();

    private IServiceProvider Services { get; }

    /// <summary>
    ///     Disposes the Mewdeko instance and unsubscribes from events.
    /// </summary>
    public void Dispose()
    {
        Dispose(true);
        GC.SuppressFinalize(this);
    }

    /// <summary>
    ///     Event that occurs when the bot joins a guild.
    /// </summary>
    public event Func<GuildConfig, Task> JoinedGuild = delegate { return Task.CompletedTask; };

    /// <summary>
    ///     Loads type readers from the specified assembly.
    /// </summary>
    /// <param name="assembly">The assembly to load type readers from.</param>
    private void LoadTypeReaders(Assembly assembly)
    {
        var sw = new Stopwatch();
        sw.Start();
        Type[] allTypes;
        try
        {
            allTypes = assembly.GetTypes();
        }
        catch (ReflectionTypeLoadException ex)
        {
            logger.LogWarning(ex.LoaderExceptions[0], "Error getting types");
            return;
        }

        var filteredTypes = allTypes
            .Where(x => x.IsSubclassOf(typeof(TypeReader))
                        && x.BaseType.GetGenericArguments().Length > 0
                        && !x.IsAbstract);

        foreach (var ft in filteredTypes)
        {
            var x = (TypeReader)ActivatorUtilities.CreateInstance(Services, ft);
            var baseType = ft.BaseType;
            var typeArgs = baseType?.GetGenericArguments();
            if (typeArgs != null)
                CommandService.AddTypeReader(typeArgs[0], x);
        }

        CommandService.AddTypeReaders<Emoji>(
            new TryParseTypeReader<Emoji>(Emoji.TryParse),
            new TryParseTypeReader<Emoji>(Emoji.TryParse));

        sw.Stop();
        logger.LogInformation("TypeReaders loaded in {ElapsedTotalSeconds}s", sw.Elapsed.TotalSeconds);
    }

    private async Task LoginAsync()
    {
        var eventHandler = Services.GetRequiredService<EventHandler>();
        var clientReady = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        eventHandler.Ready.Subscribe("Mewdeko", SetClientReady);

        logger.LogInformation("Logging in...");
        try
        {
            await Client.LoginAsync().ConfigureAwait(false);
            await Client.StartAsync().ConfigureAwait(false);
        }
        catch (FluxerApiException ex)
        {
            LoginErrorHandler.Handle(ex);
            Helpers.ReadErrorAndExit(3);
        }
        catch (Exception ex)
        {
            LoginErrorHandler.Handle(ex);
            Helpers.ReadErrorAndExit(4);
        }

        await clientReady.Task.ConfigureAwait(false);
        eventHandler.Ready.Unsubscribe("Mewdeko", SetClientReady);
        eventHandler.JoinedGuild.Subscribe("Mewdeko", Client_JoinedGuild);
        eventHandler.LeftGuild.Subscribe("Mewdeko", Client_LeftGuild);
        logger.LogInformation("Logged in as:");
        Console.WriteLine(FiggleFonts.Digital.Render(Client.CurrentUser.Username));
        return;

        Task SetClientReady(FluxerClient _)
        {
            clientReady.TrySetResult(true);
            return Task.CompletedTask;
        }
    }

    private Task Client_LeftGuild(SocketGuild arg)
    {
        _ = Task.Run(async () =>
        {
            try
            {
                var chan = await Client.Rest.GetChannelAsync(Credentials.GuildJoinsChannelId).ConfigureAwait(false);
                await ((Channel)chan).SendErrorAsync($"Left server: {arg.Name} [{arg.Id}]", bss.Data,
                [
                    new EmbedFieldBuilder().WithName("Total Guilds")
                        .WithValue(Client.Guilds.Count)
                ]).ConfigureAwait(false);
            }
            catch
            {
                //ignored
            }

            logger.LogInformation("Left server: {0} [{1}]", arg.Name, arg.Id);
        });
        return Task.CompletedTask;
    }

    private Task Client_JoinedGuild(SocketGuild arg)
    {
        _ = Task.Run(async () =>
        {
            logger.LogInformation("Joined server: {0} [{1}]", arg.Name, arg.Id);

            var gc = await GuildSettingsService.GetGuildConfig(arg.Id).ConfigureAwait(false);

            await JoinedGuild.Invoke(gc).ConfigureAwait(false);
            var chan =
                await Client.Rest.GetChannelAsync(Credentials.GuildJoinsChannelId).ConfigureAwait(false) as
                    Channel;
            var eb = new EmbedBuilder();
            eb.WithTitle($"Joined {Format.Bold(arg.Name)} {arg.Id}");
            eb.AddField("Members", arg.TotalMembers);
            eb.AddField("Owner", $"Name: {arg.GetUser(arg.OwnerId)}\nID: {arg.OwnerId}");
            eb.AddField("Text Channels", arg.TextChannels.Count);
            eb.AddField("Voice Channels", arg.VoiceChannels.Count);
            eb.AddField("Total Guilds", Client.Guilds.Count);
            eb.WithThumbnailUrl(arg.IconUrl);
            eb.WithColor(OkColor);
            await chan.SendAsync(embed: eb.Build()).ConfigureAwait(false);
        });
        return Task.CompletedTask;
    }

    /// <summary>
    ///     Runs the bot, initializing all necessary components and services.
    /// </summary>
    /// <returns>A task representing the asynchronous operation.</returns>
    public async Task RunAsync()
    {
        var sw = Stopwatch.StartNew();

        await LoginAsync().ConfigureAwait(false);

        logger.LogInformation("Loading Services...");
        try
        {
            LoadTypeReaders(typeof(Mewdeko).Assembly);

            var dbProvider = Services.GetRequiredService<IDataConnectionFactory>();
            await using var dbContext = await dbProvider.CreateConnectionAsync();
            await dbContext.EnsureUserCreated(Client.CurrentUser.Id, Client.CurrentUser.Username,
                Client.CurrentUser.AvatarHash);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Error adding services");
            Helpers.ReadErrorAndExit(9);
        }


        sw.Stop();
        logger.LogInformation("Connected in {Elapsed:F2}s", sw.Elapsed.TotalSeconds);
        var commandService = Services.GetRequiredService<CommandService>();
        try
        {
            await commandService.AddModulesAsync(Assembly.GetExecutingAssembly(), Services);
        }
        catch (Exception e)
        {
            logger.LogError(e, "Failed to add modules to command service");
            throw;
        }

        _ = Task.Run(HandleStatusChanges);
        _ = Task.Run(ExecuteReadySubscriptions);
        var performanceMonitor = Services.GetRequiredService<PerformanceMonitorService>();
        performanceMonitor.Initialize(typeof(Mewdeko).Assembly, "Mewdeko");
        Ready.TrySetResult(true);
        logger.LogInformation("Ready.");
    }

    private async Task ExecuteReadySubscriptions()
    {
        var readyExecutors = Services.GetServices<IReadyExecutor>();
        var tasks = readyExecutors.Select(async toExec =>
        {
            try
            {
                await toExec.OnReadyAsync().ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Failed running OnReadyAsync method on {Type} type: {Message}",
                    toExec.GetType().Name,
                    ex.Message);
            }
        });
        await Task.WhenAll(tasks);
    }

    private void HandleStatusChanges()
    {
        var sub = Services.GetService<IDataCache>().Redis.GetSubscriber();

        sub.Subscribe(RedisChannel.Literal($"{Client.CurrentUser.Id}_status.game_set"), async void (_, game) =>
        {
            try
            {
                var status = JsonSerializer.Deserialize<GameStatus>((string)game, CachedJsonOptions);
                await Client.SetCustomStatusAsync(status?.Name).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "Error setting game");
            }
        }, CommandFlags.FireAndForget);

        sub.Subscribe(RedisChannel.Literal($"{Client.CurrentUser.Id}_status.stream_set"), async void (_, streamData) =>
        {
            try
            {
                var stream = JsonSerializer.Deserialize<StreamStatus>((string)streamData, CachedJsonOptions);
                await Client.SetCustomStatusAsync(stream?.Name).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "Error setting stream");
            }
        }, CommandFlags.FireAndForget);
    }

    /// <summary>
    ///     Sets the bot's game status.
    /// </summary>
    /// <param name="game">The name of the game to set.</param>
    /// <param name="type">The type of activity.</param>
    /// <returns>A task representing the asynchronous operation.</returns>
    public async Task SetGameAsync(string? game, ActivityType type)
    {
        var obj = new
        {
            Name = game, Activity = type
        };
        var sub = Services.GetService<IDataCache>().Redis.GetSubscriber();
        await sub.PublishAsync(RedisChannel.Literal($"{Client.CurrentUser.Id}_status.game_set"),
                JsonSerializer.Serialize(obj))
            .ConfigureAwait(false);
    }

    /// <summary>
    ///     Protected implementation of Dispose pattern.
    /// </summary>
    protected virtual void Dispose(bool disposing)
    {
        if (disposed)
            return;

        if (disposing)
        {
            var eventHandler = Services.GetService<EventHandler>();
            eventHandler?.JoinedGuild.Unsubscribe("Mewdeko", Client_JoinedGuild);
            eventHandler?.LeftGuild.Unsubscribe("Mewdeko", Client_LeftGuild);
        }

        disposed = true;
    }
}