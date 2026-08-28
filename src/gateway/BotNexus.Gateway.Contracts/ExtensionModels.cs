namespace BotNexus.Gateway.Abstractions.Extensions;
/// <summary>
/// Manifest format stored in botnexus-extension.json.
/// </summary>
public sealed record ExtensionManifest
{
    /// <summary>
    /// Gets or sets the id.
    /// </summary>
    public string Id { get; init; } = string.Empty;
    /// <summary>
    /// Gets or sets the name.
    /// </summary>
    public string Name { get; init; } = string.Empty;
    /// <summary>
    /// Gets or sets the version.
    /// </summary>
    public string Version { get; init; } = string.Empty;
    /// <summary>
    /// Gets or sets the entry assembly.
    /// </summary>
    public string EntryAssembly { get; init; } = string.Empty;
    /// <summary>
    /// Gets or sets the extension types.
    /// </summary>
    public IReadOnlyList<string> ExtensionTypes { get; init; } = [];
    /// <summary>
    /// Gets or sets the dependencies.
    /// </summary>
    public IReadOnlyList<string> Dependencies { get; init; } = [];
    /// <summary>
    /// Whether this extension is enabled. When false, the extension is discovered but not loaded.
    /// Defaults to true.
    /// </summary>
    public bool Enabled { get; init; } = true;
    /// <summary>
    /// Configuration field schema declared by this extension.
    /// Used to validate operator config and apply defaults at startup.
    /// </summary>
    public IReadOnlyList<ExtensionConfigFieldSchema> ConfigSchema { get; init; } = [];

    /// <summary>
    /// Left-nav entries this extension contributes to the portal. Empty for an extension with no
    /// UI, which is most of them.
    /// </summary>
    /// <remarks>
    /// Declared on the EXTENSION manifest rather than a plugin manifest so that an extension built
    /// from source gets contributed nav on the same terms as one delivered by a marketplace
    /// plugin. Nav is a property of the thing that serves the path, not of the thing that
    /// delivered it.
    /// </remarks>
    public IReadOnlyList<ExtensionNavEntry> Nav { get; init; } = [];
}
/// <summary>
/// One left-nav entry contributed by an extension.
/// </summary>
public sealed record ExtensionNavEntry
{
    /// <summary>Stable key for ordering and de-duplication, e.g. <c>agent-builder</c>.</summary>
    public string Id { get; init; } = string.Empty;

    /// <summary>Text shown in the sidebar.</summary>
    public string Label { get; init; } = string.Empty;

    /// <summary>
    /// Path the entry navigates to. Must be a site-relative path beginning with <c>/</c>; anything
    /// else is dropped rather than rendered.
    /// </summary>
    public string Path { get; init; } = string.Empty;

    /// <summary>
    /// Name of a portal icon. Unknown names fall back to a default rather than failing, and
    /// arbitrary markup is never accepted - an extension cannot inject SVG into the portal DOM.
    /// </summary>
    public string? Icon { get; init; }

    /// <summary>Sort position among nav entries; lower sorts earlier.</summary>
    public int Order { get; init; }

    /// <summary>
    /// Whether the path is served outside the Blazor router and therefore cannot be reached by
    /// client-side routing.
    /// </summary>
    public bool External { get; init; }

    /// <summary>
    /// Whether the view should replace the whole window instead of being hosted inside the portal.
    /// </summary>
    /// <remarks>
    /// Defaults to <c>false</c>, so a contributed view is embedded in the portal frame and keeps
    /// the sidebar, header and theme around it. Navigating away from the portal entirely is the
    /// disjointed option and therefore the one that has to be asked for, not the default a plugin
    /// gets by saying nothing.
    /// </remarks>
    public bool FullPage { get; init; }
}
/// <summary>
/// Schema declaration for a single extension configuration field.
/// Extensions declare these in their botnexus-extension.json manifest so the
/// gateway can validate operator config and apply defaults at startup.
/// </summary>
public sealed record ExtensionConfigFieldSchema
{
    /// <summary>Field identifier (key in the extension config object).</summary>
    public string Id { get; init; } = string.Empty;
    /// <summary>Expected value type: string, integer, bool, object, array.</summary>
    public string Type { get; init; } = "string";
    /// <summary>Default value as a string. Applied when the field is absent and not required.</summary>
    public string? Default { get; init; }
    /// <summary>Whether this field must be present in operator config. Missing required fields produce a warning.</summary>
    public bool Required { get; init; }
    /// <summary>Whether this field contains a secret/credential (masked in logs and UI).</summary>
    public bool Sensitive { get; init; }
    /// <summary>Human-readable description of the field's purpose.</summary>
    public string? Description { get; init; }
}
/// <summary>
/// Metadata for a discovered extension on disk.
/// </summary>
public sealed record ExtensionInfo
{
    /// <summary>
    /// Gets or sets the directory path.
    /// </summary>
    public required string DirectoryPath { get; init; }
    /// <summary>
    /// Gets or sets the manifest path.
    /// </summary>
    public required string ManifestPath { get; init; }
    /// <summary>
    /// Gets or sets the entry assembly path.
    /// </summary>
    public required string EntryAssemblyPath { get; init; }
    /// <summary>
    /// Gets or sets the manifest.
    /// </summary>
    public required ExtensionManifest Manifest { get; init; }
}
/// <summary>
/// Result of attempting to load an extension.
/// </summary>
public sealed record ExtensionLoadResult
{
    /// <summary>
    /// Gets or sets the extension id.
    /// </summary>
    public required string ExtensionId { get; init; }
    /// <summary>
    /// Gets or sets the success.
    /// </summary>
    public required bool Success { get; init; }
    /// <summary>
    /// Gets or sets the error.
    /// </summary>
    public string? Error { get; init; }
    /// <summary>
    /// Gets or sets the registered services.
    /// </summary>
    public IReadOnlyList<string> RegisteredServices { get; init; } = [];
}
/// <summary>
/// Runtime information about an extension that is currently loaded.
/// </summary>
public sealed record LoadedExtension
{
    /// <summary>
    /// Gets or sets the extension id.
    /// </summary>
    public required string ExtensionId { get; init; }
    /// <summary>
    /// Gets or sets the name.
    /// </summary>
    public required string Name { get; init; }
    /// <summary>
    /// Gets or sets the version.
    /// </summary>
    public required string Version { get; init; }
    /// <summary>
    /// Gets or sets the directory path.
    /// </summary>
    public required string DirectoryPath { get; init; }
    /// <summary>
    /// Gets or sets the entry assembly path.
    /// </summary>
    public required string EntryAssemblyPath { get; init; }
    /// <summary>
    /// Gets or sets the extension types.
    /// </summary>
    public IReadOnlyList<string> ExtensionTypes { get; init; } = [];
    /// <summary>
    /// Gets or sets the loaded at utc.
    /// </summary>
    public required DateTimeOffset LoadedAtUtc { get; init; }
    /// <summary>
    /// Gets or sets the registered services.
    /// </summary>
    public IReadOnlyList<string> RegisteredServices { get; init; } = [];
    /// <summary>
    /// Whether this extension is enabled. Sourced from the manifest.
    /// </summary>
    public bool Enabled { get; init; } = true;
    /// <summary>
    /// Configuration field schema declared by this extension in the manifest.
    /// </summary>
    public IReadOnlyList<ExtensionConfigFieldSchema> ConfigSchema { get; init; } = [];

    /// <summary>Left-nav entries this extension contributes to the portal.</summary>
    public IReadOnlyList<ExtensionNavEntry> Nav { get; init; } = [];
}
