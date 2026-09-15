using BotNexus.Gateway.Api.Integrations;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;

namespace BotNexus.Gateway.Api.Controllers;

/// <summary>
/// REST API behind the portal Integrations page. Phase 1 is read-only: it browses the official MCP
/// Registry through the gateway's cache and installs nothing
/// (see <c>docs/development/integrations-mcp-registry-plan.md</c>).
/// </summary>
[ApiController]
[Route("api/integrations")]
public sealed class IntegrationsController(McpRegistryCatalogCache catalog) : ControllerBase
{
    /// <summary>Page size when the caller does not ask for one.</summary>
    public const int DefaultLimit = 30;

    /// <summary>Largest page size accepted; larger requests are clamped, not refused.</summary>
    public const int MaxLimit = 100;

    /// <summary>Longest search text or server name accepted.</summary>
    public const int MaxTextLength = 200;

    private const string UnreachableMessage =
        "The MCP registry could not be reached, and nothing is cached for this request.";

    /// <summary>
    /// Searches the MCP Registry catalog. <c>stale</c> is <c>true</c> when the registry could not be
    /// reached and the response is the last good copy.
    /// </summary>
    [HttpGet("catalog")]
    public async Task<ActionResult<IntegrationCatalogResponse>> Catalog(
        [FromQuery] string? search,
        [FromQuery] string? cursor,
        [FromQuery] int? limit,
        CancellationToken cancellationToken)
    {
        if (search is { Length: > MaxTextLength })
            return BadRequest(new { error = $"search must be {MaxTextLength} characters or fewer." });

        var snapshot = await catalog.SearchAsync(
            search,
            cursor,
            Math.Clamp(limit ?? DefaultLimit, 1, MaxLimit),
            cancellationToken);

        if (snapshot is null)
            return StatusCode(StatusCodes.Status502BadGateway, new { error = UnreachableMessage });

        return Ok(new IntegrationCatalogResponse(
            snapshot.Value.Entries,
            snapshot.Value.NextCursor,
            snapshot.FetchedAtUtc,
            snapshot.IsStale));
    }

    /// <summary>
    /// Returns the latest version of one server. The name is a query parameter rather than a path
    /// segment because registry names contain <c>/</c>.
    /// </summary>
    [HttpGet("catalog/entry")]
    public async Task<ActionResult<IntegrationCatalogEntryResponse>> Entry(
        [FromQuery] string? name,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(name))
            return BadRequest(new { error = "name is required." });
        if (name.Length > MaxTextLength)
            return BadRequest(new { error = $"name must be {MaxTextLength} characters or fewer." });

        var snapshot = await catalog.GetAsync(name.Trim(), cancellationToken);
        if (snapshot is null)
            return StatusCode(StatusCodes.Status502BadGateway, new { error = UnreachableMessage });
        if (snapshot.Value is null)
            return NotFound(new { error = $"No server named '{name.Trim()}' is listed in the MCP registry." });

        return Ok(new IntegrationCatalogEntryResponse(snapshot.Value, snapshot.FetchedAtUtc, snapshot.IsStale));
    }
}
