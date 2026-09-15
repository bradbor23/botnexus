# Integrations API Reference

Reference for the **integrations** endpoints behind the portal Integrations page. They browse the
official [MCP Registry](https://registry.modelcontextprotocol.io) through the gateway.

> **Read-only.** This is Phase 1 of the
> [integrations plan](../development/integrations-mcp-registry-plan.md). Nothing here installs,
> starts or configures an MCP server. Install, grant and uninstall arrive in a later phase.

Routes come from `IntegrationsController` in `src/gateway/BotNexus.Gateway.Api/Controllers/` and
authenticate like every other `/api` route.

## How the gateway reaches the registry

- The portal never calls the registry. The gateway does, through the named `HttpClient`
  `McpRegistry` (15-second timeout), so there is one egress point.
- Answers are cached in memory for **1 hour**, up to 256 distinct requests. The cache does not
  survive a gateway restart.
- If the registry cannot be reached, the gateway serves the last good copy with `stale: true`. If
  there is no copy, the route answers `502`.
- The registry verifies who published a server, not what the server does. Treat every entry as
  third-party content.

---

## Data types

### CatalogEntry

| Field | Type | Notes |
|-------|------|-------|
| `name` | string | Reverse-DNS registry name, e.g. `io.github.github/github-mcp-server`. Contains `/`. |
| `title` | string? | Display title, when the publisher set one. |
| `description` | string? | One-line description. |
| `version` | string? | Latest published version. |
| `repositoryUrl` | string? | Source repository. Third-party text: not guaranteed to be `http(s)`. |
| `websiteUrl` | string? | Publisher website. |
| `remotes` | Remote[] | Hosted endpoints. Nothing runs on the gateway host. |
| `packages` | Package[] | Local packages the gateway host would have to run. |
| `status` | string? | Registry status, e.g. `active` or `deprecated`. |
| `isLatest` | bool | Whether this is the latest version. |
| `publishedAt` | timestamp? | When this version was published. |
| `needsCredentials` | bool | `true` when any remote header or package input is marked secret. |

### Remote

| Field | Type | Notes |
|-------|------|-------|
| `type` | string | `streamable-http` or `sse`. |
| `url` | string | Endpoint URL. |
| `headers` | Input[] | Headers the endpoint expects. |

### Package

| Field | Type | Notes |
|-------|------|-------|
| `registryType` | string | `npm`, `pypi`, `oci`, ... |
| `identifier` | string | Package identifier. |
| `version` | string? | Package version, when listed separately from the identifier. |
| `transport` | string? | e.g. `stdio`. |
| `inputs` | Input[] | Environment variables, plus placeholders declared inside runtime or package arguments. |

### Input

| Field | Type | Notes |
|-------|------|-------|
| `name` | string | Header, variable or placeholder name. |
| `description` | string? | What it is for. |
| `isRequired` | bool | Whether the server needs it. |
| `isSecret` | bool | Whether it is a credential. |

---

## GET /api/integrations/catalog

Searches the latest version of each server in the registry.

| Query | Type | Notes |
|-------|------|-------|
| `search` | string? | Free text, at most 200 characters. Omit to list from the start. |
| `cursor` | string? | `nextCursor` from the previous page. |
| `limit` | int? | Page size. Default `30`; clamped to `1`-`100`. |

**200**

```json
{
  "entries": [ { "name": "io.github.github/github-mcp-server", "title": "GitHub", "needsCredentials": true, "...": "..." } ],
  "nextCursor": "io.github.github/github-mcp-server:1.12.1",
  "fetchedAtUtc": "2026-09-14T17:00:00+00:00",
  "stale": false
}
```

| Status | When |
|--------|------|
| `200` | Results, fresh or `stale`. |
| `400` | `search` is longer than 200 characters. |
| `502` | The registry could not be reached and nothing is cached for this query. Body: `{ "error": "..." }`. |

## GET /api/integrations/catalog/entry

Returns the latest version of one server. The name is a query parameter, not a path segment, because
registry names contain `/`.

| Query | Type | Notes |
|-------|------|-------|
| `name` | string | Registry name. Required; at most 200 characters. |

**200**: `{ "entry": CatalogEntry, "fetchedAtUtc": "...", "stale": false }`

| Status | When |
|--------|------|
| `200` | The server, fresh or `stale`. |
| `400` | `name` is missing, blank or too long. |
| `404` | The registry lists no server by that name. |
| `502` | The registry could not be reached and nothing is cached for this name. |
