namespace Mewdeko.Common.Interactive;

/// <summary>
///     A built paginator, ready to be sent with <see cref="InteractiveService.SendPaginatorAsync" />.
/// </summary>
public sealed class Paginator
{
    internal Paginator(Func<int, Task<PageBuilder>> pageFactory, int maxPageIndex, IReadOnlyCollection<ulong> users,
        PaginatorFooter footer, ActionOnStop actionOnCancellation, ActionOnStop actionOnTimeout)
    {
        PageFactory = pageFactory;
        MaxPageIndex = maxPageIndex;
        Users = users;
        Footer = footer;
        ActionOnCancellation = actionOnCancellation;
        ActionOnTimeout = actionOnTimeout;
    }

    /// <summary>
    ///     Produces the page at a zero based index.
    /// </summary>
    public Func<int, Task<PageBuilder>> PageFactory { get; }

    /// <summary>
    ///     The zero based index of the last page.
    /// </summary>
    public int MaxPageIndex { get; }

    /// <summary>
    ///     The users allowed to turn pages. Empty means anyone.
    /// </summary>
    public IReadOnlyCollection<ulong> Users { get; }

    /// <summary>
    ///     What gets written into each page footer.
    /// </summary>
    public PaginatorFooter Footer { get; }

    /// <summary>
    ///     What happens to the message when a user stops the paginator.
    /// </summary>
    public ActionOnStop ActionOnCancellation { get; }

    /// <summary>
    ///     What happens to the message when the paginator times out.
    /// </summary>
    public ActionOnStop ActionOnTimeout { get; }
}

/// <summary>
///     Shared settings for the paginator builders.
/// </summary>
/// <typeparam name="TBuilder">The concrete builder, returned from each setter for chaining.</typeparam>
public abstract class PaginatorBuilder<TBuilder> where TBuilder : PaginatorBuilder<TBuilder>
{
    private readonly List<ulong> users = [];
    private ActionOnStop actionOnCancellation = ActionOnStop.DeleteMessage;
    private ActionOnStop actionOnTimeout = ActionOnStop.DisableInput;
    private PaginatorFooter footer = PaginatorFooter.PageNumber;

    /// <summary>
    ///     Allows a user to turn pages.
    /// </summary>
    public TBuilder AddUser(IUser user)
    {
        users.Add(user.Id);
        return (TBuilder)this;
    }

    /// <summary>
    ///     Replaces the users allowed to turn pages.
    /// </summary>
    public TBuilder WithUsers(params IUser[] allowed)
    {
        users.Clear();
        users.AddRange(allowed.Select(u => u.Id));
        return (TBuilder)this;
    }

    /// <summary>
    ///     Sets what gets written into each page footer.
    /// </summary>
    public TBuilder WithFooter(PaginatorFooter value)
    {
        footer = value;
        return (TBuilder)this;
    }

    /// <summary>
    ///     Sets what happens to the message when a user stops the paginator.
    /// </summary>
    public TBuilder WithActionOnCancellation(ActionOnStop action)
    {
        actionOnCancellation = action;
        return (TBuilder)this;
    }

    /// <summary>
    ///     Sets what happens to the message when the paginator times out.
    /// </summary>
    public TBuilder WithActionOnTimeout(ActionOnStop action)
    {
        actionOnTimeout = action;
        return (TBuilder)this;
    }

    /// <summary>
    ///     Builds the paginator.
    /// </summary>
    public abstract Paginator Build();

    private protected Paginator Build(Func<int, Task<PageBuilder>> pageFactory, int maxPageIndex)
    {
        return new Paginator(pageFactory, Math.Max(0, maxPageIndex), users.ToArray(), footer, actionOnCancellation,
            actionOnTimeout);
    }
}

/// <summary>
///     Builds a paginator whose pages are produced on demand.
/// </summary>
public sealed class LazyPaginatorBuilder : PaginatorBuilder<LazyPaginatorBuilder>
{
    private Func<int, Task<PageBuilder>>? pageFactory;
    private int maxPageIndex;

    /// <summary>
    ///     Sets the function that produces the page at a zero based index.
    /// </summary>
    public LazyPaginatorBuilder WithPageFactory(Func<int, Task<PageBuilder>> factory)
    {
        pageFactory = factory;
        return this;
    }

    /// <summary>
    ///     Sets the function that produces the page at a zero based index.
    /// </summary>
    public LazyPaginatorBuilder WithPageFactory(Func<int, PageBuilder> factory)
    {
        pageFactory = index => Task.FromResult(factory(index));
        return this;
    }

    /// <summary>
    ///     Sets the zero based index of the last page.
    /// </summary>
    public LazyPaginatorBuilder WithMaxPageIndex(int index)
    {
        maxPageIndex = index;
        return this;
    }

    /// <inheritdoc />
    public override Paginator Build()
    {
        if (pageFactory is null)
            throw new InvalidOperationException("A page factory is required.");
        return Build(pageFactory, maxPageIndex);
    }
}

/// <summary>
///     Builds a paginator from a fixed list of pages.
/// </summary>
public sealed class StaticPaginatorBuilder : PaginatorBuilder<StaticPaginatorBuilder>
{
    private IReadOnlyList<PageBuilder> pages = [];

    /// <summary>
    ///     Sets the pages.
    /// </summary>
    public StaticPaginatorBuilder WithPages(IEnumerable<PageBuilder> value)
    {
        pages = value.ToList();
        return this;
    }

    /// <inheritdoc />
    public override Paginator Build()
    {
        if (pages.Count == 0)
            throw new InvalidOperationException("At least one page is required.");
        var snapshot = pages;
        return Build(index => Task.FromResult(snapshot[Math.Clamp(index, 0, snapshot.Count - 1)]),
            snapshot.Count - 1);
    }
}
