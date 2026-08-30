using System.IO.Abstractions;

namespace BotNexus.Extensions.Plugins.Lifecycle;

/// <summary>
/// Reads a marketplace source and works out what it offers.
/// </summary>
/// <remarks>
/// <para>
/// A source is probed, never trusted. It is fetched, inspected, and the result recorded on the
/// source itself, so the portal can list what is on offer without touching the network on every
/// page load and without anything being installed to find out.
/// </para>
/// <para>
/// Two shapes are accepted, decided by what the repository actually contains rather than by what
/// the operator declared when adding it: a <b>catalog</b> carrying <c>marketplace.json</c> at its
/// root, listing plugins that live elsewhere, or a <b>plugin</b> - a repository that is itself one,
/// carrying <c>.botnexus-plugin/plugin.json</c>. Checking the catalog first matters: a repository
/// may legitimately be both, and the catalog is the broader claim.
/// </para>
/// </remarks>
public sealed class MarketplaceSourceProber(
    IPluginSourceFetcher fetcher,
    PluginManifestParser parser,
    TimeProvider? timeProvider = null,
    IFileSystem? fileSystem = null)
{
    /// <summary>Path of a catalog document within a source repository.</summary>
    public const string CatalogFileName = "marketplace.json";

    /// <summary>Path of a plugin manifest within a plugin repository.</summary>
    public const string PluginManifestPath = ".botnexus-plugin/plugin.json";

    private readonly IPluginSourceFetcher _fetcher = fetcher;
    private readonly PluginManifestParser _parser = parser;
    private readonly TimeProvider _time = timeProvider ?? TimeProvider.System;
    private readonly IFileSystem _fs = fileSystem ?? new FileSystem();

    /// <summary>
    /// Fetches the source and returns it updated with what it offers.
    /// </summary>
    /// <remarks>
    /// Never throws for a source that cannot be read: an unreachable repository, a malformed
    /// catalog and a repository that is neither shape all come back as a record carrying
    /// <see cref="MarketplaceSource.LastError"/>. One bad source must not stop the others being
    /// listed, and the operator needs to see WHY rather than have it vanish.
    /// <para>
    /// A failed probe deliberately keeps the previous offerings. Stale is more useful than empty:
    /// a source that refreshed yesterday and is unreachable today can still be installed from.
    /// </para>
    /// </remarks>
    /// <param name="source">The source to read.</param>
    /// <param name="stagingRoot">Directory to stage fetches under; the caller owns it.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    public async Task<MarketplaceSource> ProbeAsync(
        MarketplaceSource source,
        string stagingRoot,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentException.ThrowIfNullOrWhiteSpace(stagingRoot);

        var staging = _fs.Path.Combine(stagingRoot, $"probe-{Guid.NewGuid():N}");

        try
        {
            _fs.Directory.CreateDirectory(staging);
            await _fetcher.FetchAsync(source.Url, source.Reference, staging, cancellationToken);

            var catalogPath = _fs.Path.Combine(staging, CatalogFileName);

            return _fs.File.Exists(catalogPath)
                ? await ProbeCatalogAsync(source, catalogPath, stagingRoot, cancellationToken)
                : ProbeSinglePlugin(source, staging);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return Failed(source, ex.Message);
        }
        finally
        {
            TryDelete(staging);
        }
    }

    private async Task<MarketplaceSource> ProbeCatalogAsync(
        MarketplaceSource source,
        string catalogPath,
        string stagingRoot,
        CancellationToken cancellationToken)
    {
        var parsed = _parser.ParseMarketplace(_fs.File.ReadAllText(catalogPath));

        if (parsed.Value is not { } catalog)
            return Failed(source, Describe($"{CatalogFileName} is not valid", parsed.Errors));

        var offerings = new List<MarketplaceOffering>();

        foreach (var entry in catalog.Plugins)
        {
            // Each entry's own repository is fetched so its manifest is read from the plugin
            // rather than from the catalog describing it. The catalog states a name, version and
            // description; only the manifest says whether installing runs code in the gateway,
            // and that is the field a person most needs before choosing.
            offerings.Add(await ProbeEntryAsync(entry, stagingRoot, cancellationToken));
        }

        return source with
        {
            Kind = "catalog",
            Offerings = offerings,
            LastRefreshedAtUtc = _time.GetUtcNow(),
            LastError = null,
        };
    }

    private async Task<MarketplaceOffering> ProbeEntryAsync(
        MarketplacePluginEntry entry,
        string stagingRoot,
        CancellationToken cancellationToken)
    {
        // The catalog's own claims are the fallback, used when the plugin cannot be read. Better a
        // listing that says what the catalog claims and admits it could not verify, than an entry
        // that silently disappears from the marketplace.
        var fallback = new MarketplaceOffering
        {
            Name = entry.Name,
            Url = entry.Source,
            Version = entry.Version,
            Description = entry.Description,
        };

        if (string.IsNullOrWhiteSpace(entry.Source))
            return fallback with { Error = "the catalog entry has no source" };

        var staging = _fs.Path.Combine(stagingRoot, $"entry-{Guid.NewGuid():N}");

        try
        {
            _fs.Directory.CreateDirectory(staging);
            await _fetcher.FetchAsync(entry.Source, entry.Version, staging, cancellationToken);

            var manifestPath = _fs.Path.Combine(staging, PluginManifestPath);

            if (!_fs.File.Exists(manifestPath))
                return fallback with { Error = "no .botnexus-plugin/plugin.json in the entry's repository" };

            var manifest = _parser.ParseManifest(_fs.File.ReadAllText(manifestPath));

            if (manifest.Value is not { } value)
                return fallback with { Error = Describe("the plugin manifest is not valid", manifest.Errors) };

            return ToOffering(value, entry.Source, entry.Version);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return fallback with { Error = ex.Message };
        }
        finally
        {
            TryDelete(staging);
        }
    }

    private MarketplaceSource ProbeSinglePlugin(MarketplaceSource source, string staging)
    {
        var manifestPath = _fs.Path.Combine(staging, PluginManifestPath);

        if (!_fs.File.Exists(manifestPath))
        {
            return Failed(
                source,
                $"neither {CatalogFileName} nor {PluginManifestPath} was found - "
                + "this repository is not a plugin and does not list any");
        }

        var manifest = _parser.ParseManifest(_fs.File.ReadAllText(manifestPath));

        if (manifest.Value is not { } value)
            return Failed(source, Describe("the plugin manifest is not valid", manifest.Errors));

        return source with
        {
            Kind = "plugin",
            Offerings = [ToOffering(value, source.Url, source.Reference)],
            LastRefreshedAtUtc = _time.GetUtcNow(),
            LastError = null,
        };
    }

    private static MarketplaceOffering ToOffering(PluginManifest manifest, string url, string? reference) => new()
    {
        Name = manifest.Name,
        Url = url,
        Version = manifest.Version,
        Description = manifest.Description,
        Reference = reference,
        CarriesExtension = manifest.Extension is not null,
    };

    /// <summary>
    /// Records the failure while keeping whatever the source last offered.
    /// </summary>
    private MarketplaceSource Failed(MarketplaceSource source, string error) => source with
    {
        LastError = error,
        // LastRefreshedAtUtc is deliberately NOT advanced: it means "when this content was read",
        // and a failed probe read nothing. Advancing it would make stale offerings look fresh.
    };

    private static string Describe(string summary, IReadOnlyList<PluginValidationError>? errors)
    {
        if (errors is null || errors.Count == 0)
            return summary;

        return $"{summary}: {string.Join("; ", errors.Select(e => e.Message))}";
    }

    private void TryDelete(string directory)
    {
        try
        {
            if (_fs.Directory.Exists(directory))
                _fs.Directory.Delete(directory, recursive: true);
        }
        catch (IOException)
        {
            // A leaked staging directory is not worth failing a probe over.
        }
    }
}
