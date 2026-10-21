using System.Collections.Immutable;
using System.Threading;

namespace Mewdeko.Services.Impl;

/// <summary>
///     An event modules subscribe to by name, with the arguments packed into <typeparamref name="TArgs" />.
/// </summary>
/// <typeparam name="TArgs">The event's argument, or a tuple of its arguments.</typeparam>
public abstract class BotEventBase<TArgs>
{
    private readonly Lock gate = new();
    private readonly EventHandler owner;
    private ImmutableArray<Subscription> subscriptions = [];

    private protected BotEventBase(EventHandler owner, string name)
    {
        this.owner = owner;
        Name = name;
    }

    /// <summary>
    ///     The event's name, used in metrics and logs.
    /// </summary>
    public string Name { get; }

    /// <summary>
    ///     How many handlers are subscribed.
    /// </summary>
    public int SubscriberCount => subscriptions.Length;

    private protected void Add(string moduleName, Delegate handler, Func<TArgs, Task> invoke)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(moduleName);
        ArgumentNullException.ThrowIfNull(handler);
        lock (gate)
            subscriptions = subscriptions.Add(new Subscription(moduleName, handler, invoke));
    }

    private protected void Remove(string moduleName, Delegate handler)
    {
        lock (gate)
            subscriptions = subscriptions.RemoveAll(s => s.ModuleName == moduleName && s.Handler.Equals(handler));
    }

    /// <summary>
    ///     Runs every subscribed handler, one after another, in the background.
    /// </summary>
    /// <param name="args">The event arguments.</param>
    /// <param name="guild">The guild the event happened in, for metrics and error reports.</param>
    internal void Raise(TArgs args, SocketGuild? guild)
    {
        owner.CountEvent(Name, guild);
        var current = subscriptions;
        if (current.IsEmpty)
            return;

        _ = Task.Run(async () =>
        {
            foreach (var subscription in current)
                await owner.RunHandlerAsync(Name, subscription.ModuleName, guild, () => subscription.Invoke(args))
                    .ConfigureAwait(false);
        });
    }

    private sealed record Subscription(string ModuleName, Delegate Handler, Func<TArgs, Task> Invoke);
}

/// <summary>
///     An event with one argument.
/// </summary>
public sealed class BotEvent<T> : BotEventBase<T>
{
    internal BotEvent(EventHandler owner, string name) : base(owner, name)
    {
    }

    /// <summary>
    ///     Runs <paramref name="handler" /> every time the event fires.
    /// </summary>
    public void Subscribe(string moduleName, Func<T, Task> handler) => Add(moduleName, handler, handler);

    /// <summary>
    ///     Stops running a handler added with <see cref="Subscribe" />.
    /// </summary>
    public void Unsubscribe(string moduleName, Func<T, Task> handler) => Remove(moduleName, handler);
}

/// <summary>
///     An event with two arguments.
/// </summary>
public sealed class BotEvent<T1, T2> : BotEventBase<(T1, T2)>
{
    internal BotEvent(EventHandler owner, string name) : base(owner, name)
    {
    }

    /// <summary>
    ///     Runs <paramref name="handler" /> every time the event fires.
    /// </summary>
    public void Subscribe(string moduleName, Func<T1, T2, Task> handler) =>
        Add(moduleName, handler, args => handler(args.Item1, args.Item2));

    /// <summary>
    ///     Stops running a handler added with <see cref="Subscribe" />.
    /// </summary>
    public void Unsubscribe(string moduleName, Func<T1, T2, Task> handler) => Remove(moduleName, handler);
}

/// <summary>
///     An event with three arguments.
/// </summary>
public sealed class BotEvent<T1, T2, T3> : BotEventBase<(T1, T2, T3)>
{
    internal BotEvent(EventHandler owner, string name) : base(owner, name)
    {
    }

    /// <summary>
    ///     Runs <paramref name="handler" /> every time the event fires.
    /// </summary>
    public void Subscribe(string moduleName, Func<T1, T2, T3, Task> handler) =>
        Add(moduleName, handler, args => handler(args.Item1, args.Item2, args.Item3));

    /// <summary>
    ///     Stops running a handler added with <see cref="Subscribe" />.
    /// </summary>
    public void Unsubscribe(string moduleName, Func<T1, T2, T3, Task> handler) => Remove(moduleName, handler);
}
