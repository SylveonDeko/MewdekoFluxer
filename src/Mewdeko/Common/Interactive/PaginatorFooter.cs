namespace Mewdeko.Common.Interactive;

/// <summary>
///     What a paginator writes into the footer of each page.
/// </summary>
public enum PaginatorFooter
{
    /// <summary>
    ///     Keep the footer the page set itself.
    /// </summary>
    None,

    /// <summary>
    ///     Replace the footer with the current page and the page count.
    /// </summary>
    PageNumber
}
