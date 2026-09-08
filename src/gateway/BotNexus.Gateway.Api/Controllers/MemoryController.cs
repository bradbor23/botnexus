using BotNexus.Domain.Primitives;
using BotNexus.Domain.Text;
using BotNexus.Gateway.Abstractions.Agents;
using BotNexus.Memory;
using BotNexus.Memory.Models;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;

namespace BotNexus.Gateway.Api.Controllers;

/// <summary>
/// REST endpoints for inspecting per-agent memory stores and removing individual entries.
/// </summary>
[ApiController]
[Route("api/memory")]
public sealed class MemoryController(
    IAgentRegistry agentRegistry,
    IMemoryStoreFactory memoryStoreFactory,
    ILogger<MemoryController> logger) : ControllerBase
{
    /// <summary>
    /// Lists all agents that have memory enabled along with their store statistics.
    /// </summary>
    [HttpGet]
    public async Task<IActionResult> ListMemoryStores(CancellationToken ct)
    {
        var agents = agentRegistry.GetAll()
            .Where(a => a.Memory is { Enabled: true })
            .OrderBy(a => a.AgentId.ToString(), StringComparer.OrdinalIgnoreCase)
            .ToList();

        var results = new List<MemoryStoreDto>(agents.Count);
        foreach (var agent in agents)
        {
            var dto = await GetStatsForAgentAsync(agent.AgentId.Value, ct).ConfigureAwait(false);
            if (dto is not null)
                results.Add(dto);
        }

        return Ok(results);
    }

    /// <summary>
    /// Gets memory store statistics for a specific agent.
    /// </summary>
    [HttpGet("{agentId}")]
    public async Task<IActionResult> GetMemoryStore(string agentId, CancellationToken ct)
    {
        var descriptor = agentRegistry.Get(AgentId.From(agentId));
        if (descriptor is null)
            return NotFound(new { error = $"Agent '{agentId}' not found." });

        if (descriptor.Memory is not { Enabled: true })
            return NotFound(new { error = $"Agent '{agentId}' does not have memory enabled." });

        var dto = await GetStatsForAgentAsync(agentId, ct).ConfigureAwait(false);
        return dto is not null ? Ok(dto) : NotFound(new { error = $"Memory store for agent '{agentId}' is not available." });
    }

    /// <summary>
    /// Searches memory entries for a specific agent. Requires a query parameter.
    /// </summary>
    [HttpGet("{agentId}/entries")]
    public async Task<IActionResult> SearchEntries(
        string agentId,
        [FromQuery] string? query = null,
        [FromQuery] int limit = 20,
        CancellationToken ct = default)
    {
        var descriptor = agentRegistry.Get(AgentId.From(agentId));
        if (descriptor is null)
            return NotFound(new { error = $"Agent '{agentId}' not found." });

        if (descriptor.Memory is not { Enabled: true })
            return NotFound(new { error = $"Agent '{agentId}' does not have memory enabled." });

        if (string.IsNullOrWhiteSpace(query))
            return BadRequest(new { error = "Query parameter is required for entry search." });

        limit = Math.Clamp(limit, 1, 100);

        try
        {
            var store = memoryStoreFactory.Create(AgentId.From(agentId));
            await store.InitializeAsync(ct).ConfigureAwait(false);

            var result = await store.SearchWithReportAsync(query, limit, ct: ct).ConfigureAwait(false);

            var dtos = result.Entries.Select(scored => new MemoryEntryDto(
                Id: scored.Entry.Id,
                CreatedAt: scored.Entry.CreatedAt,
                SourceType: scored.Entry.SourceType,
                SessionId: scored.Entry.SessionId,
                ContentPreview: TextTruncation.SafeTruncate(scored.Entry.Content, 200, "...")!
            )).ToList();

            // #3244: the scan report travels with the results, so "no older match" and "older rows
            // were never scored" are distinguishable in the UI instead of looking identical.
            return Ok(new
            {
                agentId,
                query,
                entries = dtos,
                count = dtos.Count,
                vectorScan = new
                {
                    status = result.VectorScan.Status.ToString(),
                    possiblyTruncated = result.VectorScan.IsPossiblyTruncated,
                    rowsScanned = result.VectorScan.RowsScanned,
                    scanCeiling = result.VectorScan.ScanCeiling,
                    lexicalUnionRowsScanned = result.VectorScan.LexicalUnionRowsScanned,
                    explanation = result.VectorScan.Explain()
                }
            });
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Failed to search memory entries for agent '{AgentId}'.", agentId);
            return StatusCode(500, new { error = "Failed to access memory store." });
        }
    }

    /// <summary>
    /// Deletes a single memory entry from an agent's own store.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The read side of this controller has always been able to show what an agent remembers; this
    /// is the other half - being able to take one of those entries back out. Deletion is real, not
    /// a hide: the <c>memories_ad</c> trigger mirrors the row out of the FTS index, so the content
    /// stops being searchable rather than merely stopping being listed.
    /// </para>
    /// <para>
    /// <b>Scope is the agent's own store.</b> The store is resolved per agent, so an entry id
    /// belonging to another agent is simply not found here - cross-agent deletion is structurally
    /// impossible rather than merely rejected. Entries promoted into a <i>shared</i> store are not
    /// reachable through this route either: removing one affects every agent reading that store and
    /// needs its own deliberate endpoint, not a side effect of an agent-scoped delete.
    /// </para>
    /// <para>
    /// <b>Why a missing entry is 204 and not 404.</b> Same reasoning as session delete: surfacing
    /// 404 would make the endpoint non-idempotent on retry - a client that retried after a dropped
    /// response would see a failure for a delete that had in fact succeeded - and would turn the
    /// route into an existence oracle for entry ids. The absent case is logged instead, so an
    /// operator can still tell the two apart.
    /// </para>
    /// <para>
    /// Per-agent caller authorization is applied upstream by <c>GatewayAuthMiddleware</c>, which
    /// reads the <c>agentId</c> route value - the same protection the GET routes on this controller
    /// already carry.
    /// </para>
    /// </remarks>
    [HttpDelete("{agentId}/entries/{entryId}")]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> DeleteEntry(string agentId, string entryId, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(entryId))
            return BadRequest(new { error = "Entry id is required." });

        var descriptor = agentRegistry.Get(AgentId.From(agentId));
        if (descriptor is null)
            return NotFound(new { error = $"Agent '{agentId}' not found." });

        if (descriptor.Memory is not { Enabled: true })
            return NotFound(new { error = $"Agent '{agentId}' does not have memory enabled." });

        try
        {
            // #2608: a reaped sub-agent workspace has no store, and opening one is unrecoverable
            // rather than transient. Checking first also keeps a delete from being the thing that
            // creates an empty store file for an agent that never had one.
            if (!memoryStoreFactory.StoreLocationExists(AgentId.From(agentId)))
                return NoContent();

            var store = memoryStoreFactory.Create(AgentId.From(agentId));
            await store.InitializeAsync(ct).ConfigureAwait(false);

            // GetById is the existence probe because IMemoryStore.DeleteAsync returns no row count.
            // It deliberately ignores the archived/expired liveness predicate, which is what makes
            // an expired entry still deletable rather than stranded: invisible to search, and
            // otherwise impossible to remove.
            var existing = await store.GetByIdAsync(entryId, ct).ConfigureAwait(false);
            if (existing is null)
            {
                logger.LogInformation(
                    "Memory entry '{EntryId}' for agent '{AgentId}' was already absent; delete is a no-op.",
                    entryId,
                    agentId);
                return NoContent();
            }

            await store.DeleteAsync(entryId, ct).ConfigureAwait(false);

            logger.LogInformation(
                "Deleted memory entry '{EntryId}' (source '{SourceType}', session '{SessionId}') for agent '{AgentId}'.",
                entryId,
                existing.SourceType,
                existing.SessionId ?? "-",
                agentId);

            return NoContent();
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Failed to delete memory entry '{EntryId}' for agent '{AgentId}'.", entryId, agentId);
            return StatusCode(500, new { error = "Failed to access memory store." });
        }
    }

    private async Task<MemoryStoreDto?> GetStatsForAgentAsync(string agentId, CancellationToken ct)
    {
        try
        {
            var store = memoryStoreFactory.Create(AgentId.From(agentId));
            await store.InitializeAsync(ct).ConfigureAwait(false);
            var stats = await store.GetStatsAsync(ct).ConfigureAwait(false);
            return new MemoryStoreDto(
                AgentId: agentId,
                EntryCount: stats.EntryCount,
                DatabaseSizeBytes: stats.DatabaseSizeBytes,
                LastIndexedAt: stats.LastIndexedAt,
                EmbeddedEntryCount: stats.EmbeddedEntryCount,
                VectorScanCeiling: stats.VectorScanCeiling,
                ExceedsVectorScanCeiling: stats.ExceedsVectorScanCeiling);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Failed to get memory stats for agent '{AgentId}'.", agentId);
            return null;
        }
    }
}

/// <summary>
/// Per-agent memory store row for the Memory tab.
/// </summary>
/// <remarks>
/// The vector-scan trio (#3244) is rendered so an operator can see silent recall truncation as a
/// store property. <c>EmbeddedEntryCount</c> alone would be meaningless without the ceiling it is
/// compared against, and <c>ExceedsVectorScanCeiling</c> is projected server-side so the UI cannot
/// invent a second, drifting definition of the condition.
/// </remarks>
internal sealed record MemoryStoreDto(
    string AgentId,
    int EntryCount,
    long DatabaseSizeBytes,
    DateTimeOffset? LastIndexedAt,
    int EmbeddedEntryCount,
    int? VectorScanCeiling,
    bool ExceedsVectorScanCeiling);

internal sealed record MemoryEntryDto(
    string Id,
    DateTimeOffset CreatedAt,
    string SourceType,
    string? SessionId,
    string ContentPreview);
