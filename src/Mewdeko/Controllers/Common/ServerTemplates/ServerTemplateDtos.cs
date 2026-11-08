namespace Mewdeko.Controllers.Common.ServerTemplates;

/// <summary>
///     A saved template as listed in the dashboard.
/// </summary>
public class ServerTemplateDto
{
    /// <summary>
    ///     Gets or sets the template name.
    /// </summary>
    public string Name { get; set; } = "";

    /// <summary>
    ///     Gets or sets the compressed size in bytes.
    /// </summary>
    public int SizeBytes { get; set; }

    /// <summary>
    ///     Gets or sets how many roles the template holds.
    /// </summary>
    public int RoleCount { get; set; }

    /// <summary>
    ///     Gets or sets how many channels and categories the template holds.
    /// </summary>
    public int ChannelCount { get; set; }

    /// <summary>
    ///     Gets or sets when the template was saved.
    /// </summary>
    public DateTime CreatedAt { get; set; }
}

/// <summary>
///     The starter templates and a user's saved templates.
/// </summary>
public class ServerTemplateListResponse
{
    /// <summary>
    ///     Gets or sets the names of the starter templates.
    /// </summary>
    public List<string> BuiltIn { get; set; } = [];

    /// <summary>
    ///     Gets or sets the user's saved templates.
    /// </summary>
    public List<ServerTemplateDto> Templates { get; set; } = [];
}

/// <summary>
///     Request to save a community as a template.
/// </summary>
public class SaveServerTemplateRequest
{
    /// <summary>
    ///     Gets or sets the template name.
    /// </summary>
    public string Name { get; set; } = "";
}
