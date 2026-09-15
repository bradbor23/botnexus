# Integrations: MCP servers from the official registry

A plan for a top-level **Integrations** page in the portal. From it an operator can browse
MCP servers listed in the official MCP Registry (`registry.modelcontextprotocol.io`),
install one, give it credentials, and grant it to specific agents.

Status: **Planned (2026-09-14). Nothing built.**

## Why a separate menu, not part of Plugins

| | Plugins | Integrations |
|---|---|---|
| What it is | A bundle: skills, agents, commands, hooks, MCP servers | One external service connection (an MCP server) |
| Source | Git repos listed in `marketplace.json` | `registry.modelcontextprotocol.io` |
| Install means | Clone a repo, verify trust, load its components | Write an MCP server entry and resolve its credentials |
| The main question the operator has | "What capabilities does this add?" | "Is it connected, and which agents can use it?" |

The two share the **runtime** and must not share the **catalog**:

- **Same runtime.** An installed integration registers with the existing `McpServerManager`,
  just as plugin MCP servers do (see `docs/architecture/plugins.md`, "MCP servers"). A second
  manager would mean a second lifecycle and a second place for a leaked process to hide.
- **Separate catalog and page.** Different source, different install steps, different health
  view. Folding registry servers into Plugins would make that page mix git-repo bundles
  with single remote endpoints.
- **One cross-link.** The Integrations page also lists MCP servers that plugins brought in,
  read-only, labelled "from plugin X". That way it answers "what is connected?" completely.

## What already exists (verified 2026-09-14)

| Piece | Where | Relevance |
|---|---|---|
| MCP client, stdio + SSE + streamable HTTP | `src/extensions/BotNexus.Extensions.Mcp` | Runs installed integrations. No new transport work. |
| `McpServers` config (`Command`, `Args`, `Env`, `Url`, `Headers`, `EnabledTools`) | `docs/configuration.md` "MCP Servers" | The target shape an install writes. |
| `IMcpServerHost` start/stop seam | `Mcp/Plugins/IMcpServerHost.cs` | Starting and stopping without a restart. |
| Scoped server names `plugin:<plugin>:<server>` | `PluginScopedServerName` | Copy the pattern: `integration:<name>`. |
| `McpUrlSecurity` (credentials require https unless loopback) | `Mcp/McpUrlSecurity.cs` | Already guards remote installs. |
| `SecretRef` + `env:`/`sqlite:`/`keyring:` providers | `docs/development/connection-registry-and-secrets.md` | Where integration credentials go. |
| Top menu items keyed by `NavOrderKeys` | `BlazorClient/Layout/MainLayout.razor:319`, `:480` | Where the Integrations item is added. |

**Gap found:** `McpServers` `Env` and `Headers` values are plain strings. Nothing in
`BotNexus.Extensions.Mcp` resolves a `SecretRef`. Phase 2 closes that gap, and nothing that
takes a credential ships before it.

## The registry API (checked live 2026-09-14)

- `GET https://registry.modelcontextprotocol.io/v0.1/servers?search=<text>&version=latest&limit=<n>&cursor=<c>`
  returns `{ servers: [{ server, _meta }], metadata: { nextCursor, count } }`.
  (`/v0/servers` also answers.)
- `server` follows `server.schema.json`: `name` (reverse-DNS, e.g. `io.github.x/y`), `title`,
  `description`, `version`, `repository`, and one or both of:
  - `remotes[]`: `type` (`streamable-http` | `sse`), `url`, `headers[]` with
    `name`, `value` template (`Bearer {api_key}`), `isSecret`, `isRequired`.
  - `packages[]`: `registryType` (`npm` | `pypi` | `oci` | …), `identifier`, `version`,
    `transport`, `environmentVariables[]` with `isSecret` / `isRequired`.
- `_meta["io.modelcontextprotocol.registry/official"]` has `status`, `isLatest`, and dates.
- Several `$schema` dates appear in results (`2025-09-16`, `2025-09-29`, `2025-12-11`), so the
  parser must accept a range and ignore unknown fields.

**What the registry does NOT give us:** any vetting. Namespace ownership is verified, but the
server's code is not reviewed. Many entries are aggregator re-listings (e.g. `ai.smithery/*`)
that require a third-party API key. Trust is therefore our job (Phase 3).

## Design decisions

1. **Remote servers first; local packages later.** A `remotes` entry is just a URL plus
   headers, and nothing runs on the gateway host. A `packages` entry means running
   `npx`/`uvx`/`docker` on the LXC. Node isn't installed there today, and this is the
   bash-inherits-the-keyring class of risk. Packages come in Phase 5, OCI (Docker) first.
2. **Catalog is proxied and cached by the gateway.** The portal never calls the registry
   directly. The gateway fetches, caches (e.g. 1 hour), and serves `/api/integrations/catalog`.
   One egress point, works from the iOS app, and still works if the registry is down.
3. **Credentials are always `SecretRef`s.** The install form stores each `isSecret` value via
   `sqlite:` (default) or `keyring:` and writes only the reference into config. `env:` is
   not offered (the leaky scheme).
4. **Agents get nothing by default.** Install ≠ grant. Each integration is granted to named
   agents. Warning in the UI: an agent with a `toolIds` allowlist also needs the new tool
   ids added, and a `toolIds` list drops the default tools.
5. **A curated "Recommended" list sits on top of the full registry.** A small JSON file in the
   repo pins registry `name`s we have tried (e.g. GitHub, Notion, Microsoft Learn). Search of
   the whole registry is allowed but shows an "Unvetted" badge and an extra confirmation.
6. **Pinned versions.** An install records the exact `version`. Updates are shown, never
   applied automatically (same stance as plugins).

## Phases

Each phase is its own PR, verified before the next starts.

### Phase 1: Read-only catalog + Integrations page

1. Add `RegistryClient` (typed `HttpClient`, tolerant JSON parsing, paging via `cursor`).
2. Add `IntegrationsController`: `GET /api/integrations/catalog?search=&cursor=` (cached),
   `GET /api/integrations/catalog/{name}` (detail).
3. Add `NavOrderKeys.Integrations` and a toolbar item in `MainLayout.razor`, plus an icon.
4. Add `Pages/Integrations.razor` with a search box, result cards (title, description, remote/package
   badge, "needs credentials" badge), and a detail panel.
5. Tests: parser against saved registry payloads (all three schema dates), controller with a
   fake client (no network, same fence as #159), bUnit for the page.

**You should see:** an "Integrations" item in the top menu. Typing `github` lists
registry servers with a detail panel. Nothing can be installed yet, and turning off the network
still shows the last cached results.

### Phase 2: Secret references in MCP server config

1. Let `McpServers` `Env` and `Headers` values be a `SecretRef` (e.g. `sqlite:integrations/notion-token`).
2. Resolve at server start through the existing secret resolver. The value never goes into
   logs, config responses, or agent context.
3. `PlatformConfigValidator` rejects a malformed reference.
4. Tests: resolution, redaction in `/api/config`, a missing secret gives a clear startup error.

**You should see:** a hand-written MCP server entry using `sqlite:` for its `Authorization`
header connects. `GET /api/config` shows the reference, not the token.

### Phase 3: Install, grant, and uninstall (remote servers only)

1. `POST /api/integrations` with registry `name` + `version` → builds the MCP server entry from
   `remotes[0]`, prompts for each header/variable, stores secrets, and writes config under
   `integration:<short-name>`.
2. Start it live through `IMcpServerHost`, then list the tools it reports.
3. Grant/revoke per agent. Update the agent's config, and flag agents with a `toolIds` list.
4. `DELETE /api/integrations/{id}` stops the server, removes config, and asks whether to delete
   stored secrets.
5. Refuse to install when `McpUrlSecurity` rejects the URL. Show the "Unvetted"
   confirmation for anything not on the Recommended list.

**You should see:** installing one Recommended remote server from the page shows it as
"Connected, N tools". Granting it to one agent lets that agent call a tool in chat, and
another agent can't. Uninstall removes it without a gateway restart.

### Phase 4: Health and plugin-provided servers

1. Status per integration: connected / failing (last error) / stopped, plus tool count.
2. Read-only rows for `plugin:*` servers, linking to the owning plugin.
3. "Update available" when the registry's `isLatest` version differs from the pinned one.

**You should see:** stopping a remote server's network access turns its row to "failing" with
the error. Plugin MCP servers appear, labelled with their plugin.

### Phase 5 (later, separate decision): local packages

OCI images first (isolated), then `npm`/`pypi` only with an explicit per-install opt-in.
It needs a sandbox decision (container, or a dedicated user without the gateway's keyring)
before any code. Do not start this phase without that decision.

## Open questions

1. OAuth-based remote servers (e.g. Microsoft 365): the registry models headers, not OAuth flows.
   Do we support only API-key servers at first? *(Proposed: yes.)*
2. Where the Recommended list lives: this repo, or `bradbor23/botnexus-plugins`?
3. Should the iOS app get the page in Phase 1, or after Phase 3?
4. Upstream: propose this to the main BotNexus repo, or keep it fork-only first?
