

namespace Mewdeko.Common.TypeReaders;

/// <summary>
///     Type reader for parsing permission value inputs into PermValue objects.
///     Used instead of bool for more flexible keywords for true/false only in the permission module.
/// </summary>
public class PermValueTypeReader : MewdekoTypeReader<PermValue>
{
    /// <summary>
    ///     Initializes a new instance of the <see cref="PermValueTypeReader" /> class.
    /// </summary>
    /// <param name="client">The FluxerClient instance.</param>
    /// <param name="cmds">The CommandService instance.</param>
    public PermValueTypeReader(FluxerClient client, CommandService cmds) : base(client, cmds)
    {
    }

    /// <inheritdoc />
    public override Task<TypeReaderResult> ReadAsync(ICommandContext context, string input, IServiceProvider _)
    {
        input = input.ToUpperInvariant(); // Converts input string to uppercase for case-insensitive comparison

        // Switch statement to map input strings to PermValue enum values
        return input switch
        {
            // Allow values
            "1" => Task.FromResult(TypeReaderResult.FromSuccess(PermValue.Allow)),
            "T" => Task.FromResult(TypeReaderResult.FromSuccess(PermValue.Allow)),
            "TRUE" => Task.FromResult(TypeReaderResult.FromSuccess(PermValue.Allow)),
            "ENABLE" => Task.FromResult(TypeReaderResult.FromSuccess(PermValue.Allow)),
            "ENABLED" => Task.FromResult(TypeReaderResult.FromSuccess(PermValue.Allow)),
            "ALLOW" => Task.FromResult(TypeReaderResult.FromSuccess(PermValue.Allow)),
            "PERMIT" => Task.FromResult(TypeReaderResult.FromSuccess(PermValue.Allow)),
            "UNBAN" => Task.FromResult(TypeReaderResult.FromSuccess(PermValue.Allow)),

            // Deny values
            "0" => Task.FromResult(TypeReaderResult.FromSuccess(PermValue.Deny)),
            "F" => Task.FromResult(TypeReaderResult.FromSuccess(PermValue.Deny)),
            "FALSE" => Task.FromResult(TypeReaderResult.FromSuccess(PermValue.Deny)),
            "DENY" => Task.FromResult(TypeReaderResult.FromSuccess(PermValue.Deny)),
            "DISABLE" => Task.FromResult(TypeReaderResult.FromSuccess(PermValue.Deny)),
            "DISABLED" => Task.FromResult(TypeReaderResult.FromSuccess(PermValue.Deny)),
            "DISALLOW" => Task.FromResult(TypeReaderResult.FromSuccess(PermValue.Deny)),
            "BAN" => Task.FromResult(TypeReaderResult.FromSuccess(PermValue.Deny)),

            // Inherit values
            "2" => Task.FromResult(TypeReaderResult.FromSuccess(PermValue.Inherit)),
            "N" => Task.FromResult(TypeReaderResult.FromSuccess(PermValue.Inherit)),
            "NEUTRAL" => Task.FromResult(TypeReaderResult.FromSuccess(PermValue.Inherit)),
            "INHERIT" => Task.FromResult(TypeReaderResult.FromSuccess(PermValue.Inherit)),

            // Error case
            _ => Task.FromResult(TypeReaderResult.FromError(CommandError.ParseFailed,
                "Must be either deny or allow."))
        };
    }
}