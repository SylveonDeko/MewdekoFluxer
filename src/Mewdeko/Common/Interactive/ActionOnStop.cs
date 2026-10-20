namespace Mewdeko.Common.Interactive;

/// <summary>
///     What happens to a paginator message when it is cancelled or times out.
/// </summary>
public enum ActionOnStop
{
    /// <summary>
    ///     Leave the message and its reactions as they are.
    /// </summary>
    None,

    /// <summary>
    ///     Remove the navigation reactions so the message can no longer be paged.
    /// </summary>
    DisableInput,

    /// <summary>
    ///     Delete the message.
    /// </summary>
    DeleteMessage
}
