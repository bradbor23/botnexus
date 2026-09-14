# Gateway bugs: config shadowing and undeletable API sessions

**Audience:** gateway developers. **Goal:** explain two open gateway defects, why they happen, and how to fix and test them.

1. [Config edits are silently dropped for portal-created agents](#bug-1-config-edits-are-silently-dropped-for-portal-created-agents)
2. [Sessions created by `POST /api/chat` can never be deleted](#bug-2-sessions-created-by-post-api-chat-can-never-be-deleted)

## Bug 1: config edits are silently dropped for portal-created agents

### Symptom

On every config reload, the gateway logs:

> Config-based agent 'X' is already registered with a different descriptor, so config changes for this agent are not being applied.

Every edit to that agent in `~/.botnexus/config.json` is ignored until the gateway restarts.

### Cause

The defect is in the "first-seen" branch of the reload loop in
`src/gateway/BotNexus.Gateway.Configuration/AgentConfigurationHostedService.cs` (around line 226).

1. `POST /api/agents` registers the descriptor and also writes it to config. The reload watcher sees
   the config change about 2 seconds later. The adopt path added for #3561 handles this only when the
   registered descriptor is fingerprint-equal to the config descriptor.
2. `PUT /api/agents/{id}/persona` edits the live descriptor with `with { }`. If that edit, or the
   config write, does not round-trip identically through `PlatformConfigAgentSource`, the two
   fingerprints differ.
3. From then on, the agent id is never added to `_appliedConfigDescriptors`. Every later reload takes
   the first-seen branch again, logs the warning, and continues. The update branch, which calls
   `Unregister` and then `Register`, is never reached. The state is permanent, not a one-reload race.

### Proposed fix

When the id is in the registry but not in `_appliedConfigDescriptors`, and the descriptors differ,
config wins: unregister, register the config descriptor, record it as applied, and log at
Information level (for example, "Reconciled agent 'X' to its config entry"). This matches the update
branch directly above it and the documented contract that config is the owner of record.

Keep the warning only for a registration that really came from a non-config source, if that source
can be identified. Otherwise remove it. As written, it describes a state that the code then refuses
to repair.

Also find which field differs after a persona `PUT`. `AvatarHue` and `Responsibility` are in both the
fingerprint and `PlatformConfigAgentWriter`, so the field that does not round-trip is elsewhere
(`Kind`, `Metadata`, `Order`, or a null versus empty collection). Fix that too. Otherwise config will
keep overwriting persona edits that were never persisted.

### Tests to add

- Register descriptor A, then reload with a different config descriptor B: the registry holds B. A
  second reload with C: the registry holds C. This proves the id is no longer stuck.
- Create an agent through the API, send a persona `PUT`, then reload: no warning is logged, and a
  following config edit is applied.

### Workaround

Restart the gateway. On startup it registers every agent from config.

## Bug 2: sessions created by `POST /api/chat` can never be deleted

### Symptom

`DELETE /api/sessions/{id}` returns `403` with `Caller is not authorized for this session.` for every
session created through `POST /api/chat`. This happens for every caller, including the portal.

### Cause

1. `ChatController.Send` (`src/gateway/BotNexus.Gateway.Api/Controllers/ChatController.cs`) never
   records the caller on the session, so the session is saved with `callerId: null`.
   `GET /api/sessions/{id}` shows the null value.
2. `GatewayAuthMiddleware` always attaches a caller identity. When no API keys are configured,
   `ApiKeyGatewayAuthHandler` (around line 192) authenticates every request as
   `CallerId = "gateway-dev"`.
3. `SessionsController.AuthorizeSessionCaller` (around line 558) denies the request whenever the
   request identity's `CallerId` is non-empty and not ordinal-equal to `session.CallerId`.
   `"gateway-dev"` never equals `null`, so `DELETE` is refused permanently. Other routes that use this
   guard, such as the sub-agent kill route (around line 276), are refused the same way.

`AgentExchangeService` (around line 240) and the cross-world exchange paths set `CallerId = null`
deliberately, so those sessions are probably undeletable too. This needs confirming.

### Proposed fix

- **Record the caller at creation.** In `ChatController.Send`, set `session.CallerId` from
  `HttpContext.Items[GatewayAuthMiddleware.CallerIdentityItemKey]` when a new session is created, to
  match what the SignalR path records.
- **Decide the ownerless case explicitly.** In `AuthorizeSessionCaller`, a session with a null
  `CallerId` currently belongs to nobody. Either treat it as unowned (allow any authenticated caller,
  or only an admin identity), or deny it with a distinct message. The current generic `403` reads as
  a permissions problem, not a missing owner.

### Tests to add

- `POST /api/chat` creates a session that carries the request's caller id, and `DELETE` by the same
  caller returns `204`.
- For a session with a null `CallerId`, `DELETE` follows the chosen ownerless rule instead of
  returning a generic `403`.

### Workaround

Archive the session's conversation with `DELETE /api/conversations/{id}`. This is a soft delete
without a caller check. It hides the sessions from the portal but does not remove them. It is refused
with `409` for an agent's default conversation.
