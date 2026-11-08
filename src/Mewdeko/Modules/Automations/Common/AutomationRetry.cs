using System.Net;
using System.Net.Http;

namespace Mewdeko.Modules.Automations.Common;

/// <summary>
///     Decides which failures are worth trying again.
/// </summary>
public static class AutomationRetry
{
    /// <summary>
    ///     Checks whether a failure is a rate limit or a temporary Fluxer problem.
    /// </summary>
    /// <param name="exception">The failure.</param>
    public static bool IsTransient(Exception exception) => exception switch
    {
        AutomationActionException => false,
        RateLimitedException => true,
        FluxerApiException api => IsTransient(api.HttpCode, api.ErrorCode),
        TimeoutException => true,
        TaskCanceledException => true,
        HttpRequestException => true,
        _ => false
    };

    /// <summary>
    ///     Checks whether an HTTP status is a rate limit or a temporary Fluxer problem.
    /// </summary>
    /// <param name="status">The status code.</param>
    /// <param name="errorCode">The Fluxer error code, if one came back.</param>
    public static bool IsTransient(HttpStatusCode status, string? errorCode)
    {
        if (status is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden or HttpStatusCode.NotFound)
            return false;

        if (errorCode is FluxerErrorCodes.MissingPermissions or FluxerErrorCodes.MissingAccess
            or FluxerErrorCodes.CannotSendMessagesToUser)
            return false;

        return status == HttpStatusCode.TooManyRequests || (int)status >= 500;
    }

    /// <summary>
    ///     Gets how long to wait before the next try.
    /// </summary>
    /// <param name="attempt">The try that just failed, starting at 1.</param>
    public static TimeSpan Backoff(int attempt) => TimeSpan.FromSeconds(Math.Min(8, Math.Pow(2, attempt - 1)));

    /// <summary>
    ///     Writes a failure as a short reason for the run log.
    /// </summary>
    /// <param name="exception">The failure.</param>
    public static string Describe(Exception exception) => exception switch
    {
        AutomationActionException action => action.Message,
        RateLimitedException => "Rate limited by Fluxer.",
        FluxerApiException { ErrorCode: FluxerErrorCodes.CannotSendMessagesToUser } =>
            "The member has direct messages closed.",
        FluxerApiException { HttpCode: HttpStatusCode.Forbidden } => "Fluxer says the bot is missing permissions.",
        FluxerApiException { HttpCode: HttpStatusCode.NotFound } => "The target no longer exists.",
        FluxerApiException api =>
            $"Fluxer error {(int)api.HttpCode}{(api.Reason is { Length: > 0 } ? $": {api.Reason}" : "")}.",
        TimeoutException or TaskCanceledException => "Fluxer took too long to answer.",
        _ => "Something went wrong."
    };
}

/// <summary>
///     A failure caused by an action's settings or the bot's permissions.
/// </summary>
public sealed class AutomationActionException(string reason) : Exception(reason);
