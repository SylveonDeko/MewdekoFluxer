using System.Diagnostics;
using System.Threading;
using LinqToDB.Data;
using Mewdeko.Common.ModuleBehaviors;
using Mewdeko.Database.Impl;
using Microsoft.Extensions.DependencyInjection;

namespace Mewdeko.Services.Analytics;

/// <summary>
///     Wires the collector into the Fluxer client, linq2db and the process, emitting the shard, fleet, database and
///     process metrics from the catalog.
/// </summary>
public sealed class AnalyticsInstrumentation : INService, IReadyExecutor, IDisposable
{
    private const int UserCountEveryTicks = 5;
    private static readonly TimeSpan ProcessInterval = TimeSpan.FromSeconds(15);
    private static readonly TimeSpan FleetInterval = TimeSpan.FromSeconds(60);

    private readonly FluxerClient client;
    private readonly IAnalyticsCollector collector;
    private readonly IDataConnectionFactory dbFactory;
    private readonly ILogger<AnalyticsInstrumentation> logger;
    private readonly Process process = Process.GetCurrentProcess();
    private readonly IServiceProvider services;

    private bool disposed;
    private int fleetTicks;
    private Timer? fleetTimer;
    private TimeSpan lastCpuTime;
    private long lastCpuTimestamp;
    private Timer? processTimer;

    /// <summary>
    ///     Initializes a new instance of the <see cref="AnalyticsInstrumentation" /> class.
    /// </summary>
    /// <param name="collector">The collector to emit into.</param>
    /// <param name="client">The Fluxer client.</param>
    /// <param name="dbFactory">The database connection factory, traced for query timings.</param>
    /// <param name="services">The service provider, used to find the event handler.</param>
    /// <param name="logger">Records hook failures.</param>
    public AnalyticsInstrumentation(IAnalyticsCollector collector, FluxerClient client,
        IDataConnectionFactory dbFactory, IServiceProvider services,
        ILogger<AnalyticsInstrumentation> logger)
    {
        this.collector = collector;
        this.client = client;
        this.dbFactory = dbFactory;
        this.services = services;
        this.logger = logger;
    }

    /// <inheritdoc />
    public void Dispose()
    {
        if (disposed)
            return;
        disposed = true;
        processTimer?.Dispose();
        fleetTimer?.Dispose();
        process.Dispose();
    }

    /// <inheritdoc />
    public Task OnReadyAsync()
    {
        if (!collector.Enabled)
            return Task.CompletedTask;

        var eventHandler = services.GetRequiredService<EventHandler>();
        eventHandler.Connected.Subscribe("AnalyticsInstrumentation", OnShardConnected);
        eventHandler.Disconnected.Subscribe("AnalyticsInstrumentation", OnShardDisconnected);
        eventHandler.Ready.Subscribe("AnalyticsInstrumentation", OnShardReady);
        eventHandler.JoinedGuild.Subscribe("AnalyticsInstrumentation", OnJoinedGuild);
        eventHandler.LeftGuild.Subscribe("AnalyticsInstrumentation", OnLeftGuild);

        AppDomain.CurrentDomain.UnhandledException += OnUnhandledException;
        TaskScheduler.UnobservedTaskException += OnUnobservedTaskException;

        AttachDatabaseTracing();
        collector.Gauge("shard.state", 1, ("shard", "0"));

        lastCpuTime = process.TotalProcessorTime;
        lastCpuTimestamp = Stopwatch.GetTimestamp();
        processTimer = new Timer(_ => EmitProcess(), null, TimeSpan.Zero, ProcessInterval);
        fleetTimer = new Timer(_ => EmitFleet(), null, TimeSpan.Zero, FleetInterval);
        return Task.CompletedTask;
    }

    /// <summary>
    ///     Replaces snowflakes and tokens in a REST endpoint with placeholders so requests group by route.
    /// </summary>
    /// <param name="endpoint">The endpoint as sent, without the base URL.</param>
    /// <returns>The templated route.</returns>
    public static string TemplateRoute(string endpoint)
    {
        var path = endpoint;
        var query = path.IndexOf('?');
        if (query >= 0)
            path = path[..query];

        var segments = path.Split('/');
        for (var i = 0; i < segments.Length; i++)
        {
            var segment = segments[i];
            if (segment.Length is >= 17 and <= 20 && segment.All(char.IsAsciiDigit))
                segments[i] = "{id}";
            else if (segment.Length > 40)
                segments[i] = "{token}";
        }

        return string.Join('/', segments);
    }

    /// <summary>
    ///     Derives the operation label for a SQL statement from its first keyword.
    /// </summary>
    /// <param name="sql">The statement text.</param>
    /// <returns>select, insert, update, delete or other.</returns>
    public static string OperationOf(string? sql)
    {
        if (string.IsNullOrEmpty(sql))
            return "other";

        var index = 0;
        while (index < sql.Length)
        {
            while (index < sql.Length && char.IsWhiteSpace(sql[index]))
                index++;
            if (index + 1 < sql.Length && sql[index] == '-' && sql[index + 1] == '-')
            {
                while (index < sql.Length && sql[index] != '\n')
                    index++;
                continue;
            }

            break;
        }

        var end = index;
        while (end < sql.Length && char.IsLetter(sql[end]))
            end++;

        var keyword = sql.AsSpan(index, end - index);
        if (keyword.Equals("select", StringComparison.OrdinalIgnoreCase) ||
            keyword.Equals("with", StringComparison.OrdinalIgnoreCase))
            return "select";
        if (keyword.Equals("insert", StringComparison.OrdinalIgnoreCase))
            return "insert";
        if (keyword.Equals("update", StringComparison.OrdinalIgnoreCase))
            return "update";
        if (keyword.Equals("delete", StringComparison.OrdinalIgnoreCase))
            return "delete";
        return "other";
    }

    /// <summary>
    ///     Buckets a guild by member count.
    /// </summary>
    /// <param name="memberCount">The member count.</param>
    /// <returns>tiny, small, medium, large or huge.</returns>
    public static string SizeBucket(int memberCount)
    {
        return memberCount switch
        {
            < 50 => "tiny",
            < 250 => "small",
            < 1000 => "medium",
            < 10000 => "large",
            _ => "huge"
        };
    }

    private Task OnShardConnected(FluxerClient shard)
    {
        collector.Gauge("shard.state", 1, ("shard", "0"));
        return Task.CompletedTask;
    }

    private Task OnShardDisconnected(FluxerClient shard)
    {
        collector.Gauge("shard.state", 0, ("shard", "0"));
        collector.Counter("shard.reconnects", 1, ("shard", "0"));
        return Task.CompletedTask;
    }

    private Task OnShardReady(FluxerClient shard)
    {
        collector.Gauge("shard.state", 1, ("shard", "0"));
        return Task.CompletedTask;
    }

    private Task OnJoinedGuild(SocketGuild guild)
    {
        collector.Counter("guild.join", 1, ("shard", "0"), ("size", SizeBucket(guild.TotalMembers)));
        return Task.CompletedTask;
    }

    private Task OnLeftGuild(SocketGuild guild)
    {
        collector.Counter("guild.leave", 1, ("shard", "0"), ("size", SizeBucket(guild.TotalMembers)));
        return Task.CompletedTask;
    }

    private void OnUnhandledException(object sender, UnhandledExceptionEventArgs args)
    {
        if (args.ExceptionObject is Exception exception)
            collector.Error(exception, "AppDomain.UnhandledException");
    }

    private void OnUnobservedTaskException(object? sender, UnobservedTaskExceptionEventArgs args)
    {
        collector.Error(args.Exception, "TaskScheduler.UnobservedTaskException");
    }

    private void EmitProcess()
    {
        try
        {
            process.Refresh();
            var now = Stopwatch.GetTimestamp();
            var cpu = process.TotalProcessorTime;
            var wall = Stopwatch.GetElapsedTime(lastCpuTimestamp, now).TotalMilliseconds;
            if (wall > 0)
            {
                var percent = (cpu - lastCpuTime).TotalMilliseconds / (wall * Environment.ProcessorCount) * 100;
                collector.Gauge("proc.cpu", Math.Clamp(percent, 0, 100));
            }

            lastCpuTime = cpu;
            lastCpuTimestamp = now;

            collector.Gauge("proc.rss", process.WorkingSet64);
            collector.Gauge("proc.threads", process.Threads.Count);
            collector.Gauge("proc.gc0", GC.CollectionCount(0));
            collector.Gauge("proc.gc1", GC.CollectionCount(1));
            collector.Gauge("proc.gc2", GC.CollectionCount(2));
            collector.Gauge("proc.heap", GC.GetTotalMemory(false));
        }
        catch (Exception ex)
        {
            logger.LogDebug(ex, "Process metrics failed");
        }
    }

    private void EmitFleet()
    {
        try
        {
            var emitUsers = Interlocked.Increment(ref fleetTicks) % UserCountEveryTicks == 1;
            var guilds = client.Guilds.ToList();
            collector.Gauge("guild.count", guilds.Count, ("shard", "0"));
            if (emitUsers)
                collector.Gauge("user.count", guilds.Sum(guild => (long)guild.TotalMembers));
        }
        catch (Exception ex)
        {
            logger.LogDebug(ex, "Fleet metrics failed");
        }
    }

    private void AttachDatabaseTracing()
    {
        if (dbFactory is PostgreSqlConnectionFactory factory)
            factory.UseTracing(OnDatabaseTrace);
        else
            logger.LogWarning("Database analytics not attached: {Factory} does not support tracing",
                dbFactory.GetType().Name);
    }

    private void OnDatabaseTrace(TraceInfo info)
    {
        switch (info.TraceInfoStep)
        {
            case TraceInfoStep.AfterExecute when info.ExecutionTime is { } elapsed:
                collector.Duration("db.duration", elapsed.TotalMilliseconds, ("op", OperationOf(info.CommandText)));
                break;
            case TraceInfoStep.Error:
                collector.Counter("db.errors", 1, ("op", OperationOf(info.CommandText)));
                break;
        }
    }

}