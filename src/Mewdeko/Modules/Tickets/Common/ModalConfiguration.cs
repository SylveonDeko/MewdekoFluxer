using System.Text.Json;

namespace Mewdeko.Modules.Tickets.Common;

/// <summary>
///     Represents the questions asked in a new ticket channel before the ticket's opening message is sent.
/// </summary>
/// <remarks>
///     The configuration is stored on buttons and select options as JSON, either as this whole object or, for
///     older entries, as just the dictionary of fields.
/// </remarks>
public class ModalConfiguration
{
    private static readonly JsonSerializerOptions Options = new()
    {
        PropertyNameCaseInsensitive = true
    };

    /// <summary>
    ///     Gets or sets the title shown above each question.
    /// </summary>
    public string? Title { get; set; } = "Create Ticket";

    /// <summary>
    ///     Gets or sets the dictionary of questions.
    /// </summary>
    /// <remarks>
    ///     The dictionary keys are the field identifiers used in %modal.key% replacements, and the values are their
    ///     configurations. Questions are asked in the order they were added.
    /// </remarks>
    public Dictionary<string, ModalFieldConfig> Fields { get; set; } = new();

    /// <summary>
    ///     Reads a stored configuration in either of its stored shapes.
    /// </summary>
    /// <param name="json">The stored JSON, or null for none.</param>
    /// <returns>The configuration, empty when the JSON is blank.</returns>
    /// <exception cref="JsonException">Thrown when the JSON is not valid.</exception>
    public static ModalConfiguration Parse(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
            return new ModalConfiguration();

        using var document = JsonDocument.Parse(json);
        var wrapped = document.RootElement.ValueKind == JsonValueKind.Object &&
                      document.RootElement.EnumerateObject().Any(p =>
                          p.Name.Equals(nameof(Fields), StringComparison.OrdinalIgnoreCase) &&
                          p.Value.ValueKind == JsonValueKind.Object);

        if (wrapped)
            return JsonSerializer.Deserialize<ModalConfiguration>(json, Options) ?? new ModalConfiguration();

        return new ModalConfiguration
        {
            Fields = JsonSerializer.Deserialize<Dictionary<string, ModalFieldConfig>>(json, Options) ?? new()
        };
    }
}

/// <summary>
///     Represents configuration for a question asked while opening a ticket.
/// </summary>
public class ModalFieldConfig
{
    /// <summary>
    ///     Gets or sets the question text.
    /// </summary>
    public string Label { get; set; }

    /// <summary>
    ///     Gets or sets whether the question must be answered.
    /// </summary>
    public bool Required { get; set; }

    /// <summary>
    ///     Gets or sets the minimum length for the answer.
    /// </summary>
    public int? MinLength { get; set; }

    /// <summary>
    ///     Gets or sets the maximum length for the answer.
    /// </summary>
    public int? MaxLength { get; set; }

    /// <summary>
    ///     Gets or sets the hint shown under the question.
    /// </summary>
    public string Placeholder { get; set; }

    /// <summary>
    ///     Gets or sets the answer used when an optional question is skipped.
    /// </summary>
    public string Value { get; set; }
}
