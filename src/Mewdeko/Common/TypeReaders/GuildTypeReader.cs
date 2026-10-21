

namespace Mewdeko.Common.TypeReaders;

/// <summary>
///     Type reader for parsing guild inputs into SocketGuild objects.
/// </summary>
public class GuildTypeReader : MewdekoTypeReader<SocketGuild>
{
    private readonly FluxerClient client;

    /// <summary>
    ///     Initializes a new instance of the <see cref="GuildTypeReader" /> class.
    /// </summary>
    /// <param name="client">The FluxerClient instance.</param>
    /// <param name="cmds">The CommandService instance.</param>
    public GuildTypeReader(FluxerClient client, CommandService cmds) : base(client, cmds)
    {
        this.client = client;
    }

    /// <inheritdoc />
    public override Task<TypeReaderResult> ReadAsync(ICommandContext context, string input, IServiceProvider _)
    {
        input = input.Trim()
            .ToUpperInvariant(); // Trims and converts the input string to uppercase for case-insensitive comparison
        var guilds = client.Guilds; // Retrieves the collection of guilds the bot is connected to
        var guild =
            guilds.FirstOrDefault(g =>
                g.Id.ToString().Trim().ToUpperInvariant() == input) ?? // Searches for a guild by ID
            guilds.FirstOrDefault(g => g.Name.Trim().ToUpperInvariant() == input); // Searches for a guild by name

        // Returns TypeReaderResult based on whether a guild is found or not
        return Task.FromResult(guild != null
            ? TypeReaderResult.FromSuccess(guild)
            : TypeReaderResult.FromError(CommandError.ParseFailed, "No guild by that name or Id found"));
    }
}