# Connection registry and secret references

A plan for letting an agent act on a named server — a Proxmox host, a NAS, an internal
API — without ever holding the credential for it.

Status: proposed. Phases 1–3 are the agreed first slice.

## The problem

Today an operator who wants an agent to manage a server has nowhere to put the details.
The endpoint could go in a system prompt, and the password could go in `config.json`
next to it, and both of those are bad in ways that are worth naming precisely.

## The rule that shapes the whole design

**An agent that can read a credential can be talked into disclosing it.**

Anything that reaches an agent's context can carry instructions: a fetched web page, a
file in its workspace, an inbound channel message. If a root password is in the context
because the agent read it from configuration, then a prompt injection walks out with it.
No amount of system-prompt instruction fixes this, because the injected text and the
legitimate text arrive through the same channel.

So the design goal is **capability without custody**: the agent learns that
`proxmox-main` exists and what it can ask of it, and a tool performs the work holding a
credential the agent never sees.

This is not a new position for the codebase. `EnvironmentApiKeys` exists so provider keys
live in the environment rather than in config, and `WebhookSecret` is a value type whose
`ToString()` returns a redacted marker so a secret cannot reach a log by accident. This
plan extends both patterns rather than introducing a policy.

## What already exists

| Piece | Where | Notes |
|---|---|---|
| Location registry | `gateway.locations` in `PlatformConfig` | `Dictionary<string, LocationConfig>`; `type` ∈ filesystem, api, mcp-server, database, remote-node |
| REST surface | `LocationsController` (`/api/locations`) | list, add, update, remove |
| CLI surface | `botnexus locations` | |
| Portal client | `LocationsApiClient` | |
| Config pipeline | JSON + SQLite behind `IOptionsMonitor` | `ResilientJsonConfigurationSource` keeps last-known-good; `CrossProcessConfigLock` serialises writes |
| Generated schema | `docs/botnexus-config.schema.json` | Built from `[Display]`/`[ConfigField]` annotations; also drives the portal Configuration page |
| Redacting secret type | `WebhookSecret` | `Reveal()` explicit, `ToString()` redacted, `TryCreate`/`Create`/`Generate` |

Two gaps: **no agent can see locations**, and **nothing in that shape models a secret**.

Note the config pipeline already reloads live rather than at start-up, so "the app reads
the latest settings" is satisfied by construction — with one caveat handled in Phase 2.

## Design

### 1. Locations carry connection metadata and a *reference*

```jsonc
// gateway.locations
"proxmox-main": {
  "type": "proxmox",
  "endpoint": "https://pve.example.lan:8006",
  "username": "automation@pve",
  "credentialRef": "env:PROXMOX_TOKEN",
  "verifyTls": true,
  "description": "Main hypervisor",
  "tags": ["homelab", "hypervisor"]
}
```

New fields on `LocationConfig`: `Username`, `CredentialRef`, `VerifyTls`, `Tags`.
`Endpoint`, `Description` and `Properties` already exist.

`credentialRef` is a **reference, never a value**. A literal in that field must fail
validation — not be discouraged by review. This is the `SkillPath` trick the repo already
uses: make the wrong state unrepresentable rather than merely unwise.

### 2. `SecretRef` and `Secret`

- **`SecretRef`** — a parsed `scheme:identifier`. Construction rejects anything that
  looks like a bare secret (no scheme, or a scheme nobody registered). Validation surfaces
  the failure at config load, next to the offending key.
- **`Secret`** — modelled directly on `WebhookSecret`: `Reveal()` explicit, `ToString()`
  returns `Secret(redacted)`, equality is length-independent where it matters.

### 3. `ISecretResolver` with pluggable providers

```csharp
Task<Secret> ResolveAsync(SecretRef reference, CancellationToken ct);
```

Four providers, registered by scheme. All four were requested; their properties differ
enough to be worth stating plainly rather than treating as interchangeable:

| Scheme | Backing | Honest assessment |
|---|---|---|
| `env:` | Process environment, from `botnexus.env` | Simplest, matches existing provider-key handling. Visible to anything that can read `/proc/<pid>/environ` as the same user. |
| `file:` | One secret per file, mode `0600` | Easy per-target rotation; works with config management. Protection is filesystem permissions plus whatever disk encryption exists. |
| `sqlite:` | Alongside `config.db` | Convenience and a single backup artifact. **SQLite is not encrypted at rest** — this is not a security improvement over `file:`, and the plan should not present it as one. |
| `keyring:` | libsecret / Keychain | Best at-rest protection. Needs a session bus, so on a headless host it requires deliberate setup and may be unavailable; the provider must degrade with a clear error rather than a stack trace. |

Resolution happens **at call time**, not at start-up. That is what makes credential
rotation take effect without restarting the gateway — the caveat referenced above, since
environment variables are otherwise fixed for a process lifetime.

### 4. The agent-facing `locations` tool

Returns **metadata only**: name, type, endpoint, username, description, tags. Never
`credentialRef`, never a resolved secret. The DTO is a separate type from `LocationConfig`
so a field added to config cannot silently start flowing to agents.

### 5. Fences

The repo enforces architecture rules as tests, and this design depends on two:

- Nothing outside `ISecretResolver` implementations may call `Secret.Reveal()`.
- The locations tool DTO may not expose `credentialRef`, asserted structurally rather
  than by string match.

Pattern to copy: `ConfigurationReadPathFenceTests`, `SkillPathConstructionArchitectureTests`.

## Security properties, and what this is not

**Holds:** credentials stay out of agent context and out of transcripts; a compromised or
injected agent can enumerate targets but cannot read secrets; secrets never enter git; a
secret cannot reach a log through ordinary string interpolation.

**Does not hold:** this is not a secrets vault. Anything running as the gateway user can
read what the gateway can read, `sqlite:` and `file:` are not encrypted at rest, and a
malicious *tool* — as opposed to a malicious prompt — is outside the model. Tools are
trusted code; that is exactly why the credential lives there and not in the context.

## Phases

**Phase 1 — Secret primitives.** `SecretRef`, `Secret`, `ISecretResolver`, `env:` and
`file:` providers. Redaction fence test.
*Done when:* a secret round-trips through the resolver, and a test proves an interpolated
`Secret` renders redacted.

**Phase 2 — Config surface.** Extend `LocationConfig`; validation rejecting inline
secrets; `[Display]`/`[ConfigField]` annotations; regenerate
`docs/botnexus-config.schema.json`; add `config.example.jsonc` with placeholders only;
confirm `.gitignore` covers real config.
*Done when:* a literal in `credentialRef` fails start-up validation naming the key, and
the portal Configuration page renders the new fields with descriptions.

**Phase 3 — Locations tool.** Metadata-only tool plus its exposure fence.
*Done when:* an agent can list targets, and a test proves the DTO cannot carry a ref.

**Phase 4 — `sqlite:` and `keyring:` providers.** Deferred deliberately: they add no
capability the first three phases lack, and `keyring:` needs host setup that should not
block the model landing.

**Phase 5 — Proxmox extension.** A native BotNexus tool extension, following the existing
GitHub/Web tools. Takes a location name, resolves the credential internally, exposes an
explicit verb allow-list, read-only by default (`list_nodes`, `list_vms`, `vm_status`).
State-changing verbs are opt-in per location.
*Done when:* an agent answers a question about a real host without the credential
appearing anywhere in the transcript.

**Phase 6 — Documentation.** A guide page, and refresh the `botnexus-guide` skill so the
Trailguide can explain the model.

## Decisions taken

- All four secret backends will exist; `env:` and `file:` ship first (Phase 1), `sqlite:`
  and `keyring:` follow (Phase 4).
- Proxmox is a native extension rather than an MCP server, for control over the verb
  allow-list and the audit trail.
- First slice is Phases 1–3: the security model and target discovery, before anything can
  consume it.

## Open questions

1. Should `credentialRef` support more than one credential per location (e.g. a token
   *and* a TLS client cert)? A `credentials` map keyed by purpose is more general; a
   single ref is simpler. Recommend starting single and widening if a consumer needs it.
2. Should the locations tool be opt-in per agent via `toolIds`, or available to all?
   Recommend opt-in — an agent with no business touching infrastructure should not be
   able to enumerate it.
