# Plugins & the marketplace

A **plugin** is a git repository you install into BotNexus from the portal. It can carry skills,
and it can carry a prebuilt gateway extension — code and UI that becomes part of the portal.

## Table of contents

- [Plugin, extension or skill?](#plugin-extension-or-skill)
- [Installing a plugin](#installing-a-plugin)
- [Managing installed plugins](#managing-installed-plugins)
- [Building a plugin](#building-a-plugin)
- [Carrying an extension](#carrying-an-extension)
- [Contributing a menu entry](#contributing-a-menu-entry)
- [What a plugin needs to work](#what-a-plugin-needs-to-work)
- [When an install is refused](#when-an-install-is-refused)

## Plugin, extension or skill?

These three words are easy to confuse, and the difference decides how you ship something.

| | What it is | How it arrives | Needs a restart? |
|---|---|---|---|
| **Skill** | Markdown that teaches an agent something | Inside a plugin, or dropped in a skills folder | No |
| **Extension** | A .NET assembly the gateway loads — tools, channels, UI | Built from source, or carried by a plugin | Yes |
| **Plugin** | A git repo bundling skills and optionally one extension | Installed from the portal | Only if it carries an extension |

A plugin is the *delivery mechanism*. What it delivers is skills, an extension, or both.

## Installing a plugin

**Plugins → Install from a repository.** Paste the repository URL, optionally give a branch, tag or
commit, and press **Install**.

```
https://github.com/owner/my-plugin.git      reference: v1.0.0
```

Any git source works, including a local path and a private repository the gateway's git can
already authenticate to.

**Pin to a tag or commit where you can.** The reference is what an update re-resolves later, and
the exact revision installed is recorded separately, so BotNexus can always answer "has the source
moved since I installed this?".

### If the plugin carries code

A plugin that carries an extension is not the same kind of thing as one carrying skills. A skill is
prompt text; a carried extension is an assembly that **runs inside the gateway process at full
trust, with no sandbox**.

So the install stops and asks:

> Plugin 'x' carries a gateway extension, which runs code in the gateway process at full trust.

Choose **Install anyway** to proceed, or **Cancel**. Your entered URL is kept either way.

You are asked *after* the fetch rather than before, because whether a repository carries code is
only knowable once its manifest has been read. Nothing is written to disk until you answer.

### Restart to activate

A carried extension is placed on disk immediately, but the gateway maps extension endpoints once at
startup. The portal says so:

> 'x' deployed a gateway extension. Restart the gateway to activate it.

Skills need no restart — they are picked up the next time an agent builds a prompt.

## Managing installed plugins

Each row on the Plugins page offers:

- **Auto-update** — whether a scheduled update may replace this plugin's content. Turn it off to
  pin the plugin at the revision you installed.
- **In menu** — whether this plugin's contributed menu entries appear in the sidebar. Use it to
  keep a long sidebar readable. This is presentation only: the plugin stays installed, its
  extension stays loaded, and its pages stay reachable by URL. Skills-only plugins show a dash,
  because they have no menu entry to control.
- **Update** — appears when the source has moved. A plugin carrying a deployed extension cannot be
  updated in place; remove it, restart, then install the new version. The gateway holds those
  assemblies open, so replacing them underneath itself would fail.
- **Remove** — takes two clicks. Removal deletes exactly the files the install recorded, plus any
  extension the plugin deployed. Content you added alongside a plugin is never touched.

Removing a plugin that deployed an extension deletes the extension directory at once, but its code
keeps running until the gateway restarts.

## Building a plugin

One plugin per repository, at the repository root. The installer clones the whole repository — there
is no subdirectory or monorepo option — and copies everything except `.git/` into
`~/.botnexus/plugins/<name>/`. Keep the repository lean; nothing is filtered for you.

### The manifest

Required, at this exact path:

```
.botnexus-plugin/plugin.json
```

Only `name` is required, but a marketplace listing with no description is not much of a listing.

```json
{
  "name": "my-plugin",
  "description": "One line describing what this gives an agent.",
  "version": "1.0.0",
  "author": { "name": "Your Name" },
  "homepage": "https://github.com/owner/my-plugin",
  "repository": "https://github.com/owner/my-plugin.git",
  "license": "MIT",
  "keywords": ["example"]
}
```

| Field | Type | Notes |
|---|---|---|
| `name` | string | **Required.** Lowercase kebab-case, 1–64 chars. Becomes the install directory. |
| `description` | string | Shown in the listing. |
| `version` | string | Semantic version. |
| `author` | object | `{ name, email?, url? }` — `name` required. |
| `homepage`, `repository`, `license` | string | Project URL, source URL, SPDX identifier. |
| `keywords` | string[] | Search terms. |
| `skills`, `agents`, `commands` | string[] | Explicit paths. Omit to discover by convention. |
| `hooks`, `mcpServers` | string | A single path, not an array. |
| `extension` | object | A carried gateway extension — see below. |

**The schema rejects unknown fields.** A field you invent fails validation rather than being
ignored, so a typo is caught at install instead of silently doing nothing.

**The manifest's `name` wins.** It becomes the install directory and the record key.

### Skills

```
skills/
  my-skill/
    SKILL.md          <- required
    references/       <- optional supporting folders
    templates/
    scripts/
    assets/
```

- **Exactly one level deep.** `skills/a/b/SKILL.md` is not found.
- A folder without `SKILL.md` is skipped **silently** — the most common "why is nothing showing up".
- `SKILL.md` opens with `---` YAML frontmatter carrying a non-empty `description` (≤ 1024 chars),
  and must be under 512 KB.
- Skill names must not contain a double hyphen.
- Plugin skills lose a name collision with a global, agent or workspace skill, so namespace yours.

See the [Skills guide](skills.md) for the full `SKILL.md` format.

## Carrying an extension

Add one object to the manifest:

```json
"extension": { "manifest": "botnexus-extension.json" }
```

It points at an ordinary extension manifest in the same repository. Recommended layout:

```
.botnexus-plugin/plugin.json      # plugin metadata + the "extension" object
botnexus-extension.json           # the extension manifest
BotNexus.Extensions.MyThing.dll   # the prebuilt entry assembly and its private dependencies
wwwroot/                          # built static assets, if it serves UI
skills/                           # optional; skills work alongside
```

**The extension must be prebuilt and committed.** Plugins are copied verbatim — there is no build
step at install, and no guaranteed SDK on the host.

On install, the extension's folder is copied to `~/.botnexus/extensions/<id>/`, where the gateway's
normal loader finds it. `.botnexus-plugin/`, `skills/` and `.git/` are excluded, so plugin metadata
never leaks into the extensions tree. The plugin record remembers which extension it deployed, so
removal cleans up both.

An extension whose directory already exists is **not** overwritten — the running gateway has those
assemblies loaded. Remove and restart first.

## Contributing a menu entry

An extension that serves a UI can put itself in the portal sidebar, with no change to the portal.
Declare it in `botnexus-extension.json`:

```json
"nav": [
  {
    "id": "my-thing",
    "label": "My Thing",
    "path": "/my-thing",
    "icon": "tools",
    "order": 65,
    "external": true
  }
]
```

| Field | Notes |
|---|---|
| `id` | Lowercase kebab-case, unique across all contributions. |
| `label` | Sidebar text, trimmed to 40 characters. |
| `path` | Must be site-relative, beginning with `/`. Anything else is dropped. |
| `icon` | A portal icon **name**, never markup. An unknown name falls back to a default. |
| `order` | Sorts among the built-ins, which sit 10 apart: Agents 60, Cron 70. So 65 lands between them. |
| `external` | `true` when the path is served by your extension rather than the Blazor router. |
| `fullPage` | Optional. `true` replaces the whole window; omit to embed. |

### Views are embedded by default

A contributed view opens **inside** the portal, framed in the main canvas with the sidebar and
header still around it, at `/extension/<id>`. Taking over the window is opt-in via `fullPage`,
because leaving the portal entirely is the more disruptive behaviour and should be asked for.

**Your view follows the portal's light/dark toggle automatically** if it styles itself from
`prefers-color-scheme`, which is how a standalone web app normally does it. The portal drives the
frame's colour scheme; you need no code and no knowledge that the portal exists.

Note the portal URL does not track navigation *inside* a framed view, so deep links into your
view's internal state are not currently shareable.

### Claiming your path

Extensions register their middleware before the portal's, so a UI extension's route is served by
that extension rather than being swallowed by the portal's catch-all. You do not need to register
your path anywhere. If your extension registers *endpoints* rather than middleware, those are
recognised automatically too.

## What a plugin needs to work

- [ ] `.botnexus-plugin/plugin.json` at the repository root
- [ ] `name` lowercase kebab-case, no fields outside the table above
- [ ] `description` and `keywords` filled in — they are the listing
- [ ] Skills at `skills/<name>/SKILL.md`, exactly one level deep, each with frontmatter and a
      non-empty `description`
- [ ] A carried extension prebuilt and committed, with its `botnexus-extension.json`
- [ ] Shared framework assemblies trimmed from the repository where possible — the gateway supplies
      its own, and a mismatched copy is how a binary plugin breaks on a different gateway
- [ ] No build output or large assets you do not need — everything but `.git/` is copied
- [ ] A tag or release commit to install as a pinned reference

## When an install is refused

Failures name the field at fault rather than failing generically.

| Message | Meaning |
|---|---|
| Plugin manifest not found | No `.botnexus-plugin/plugin.json` at the repository root |
| Field '…' is invalid | A field failed the schema — often an unknown field, or a name that is not kebab-case |
| carries a gateway extension | Consent needed; re-issue with **Install anyway** |
| resolves outside the plugin directory | The `extension.manifest` path tried to escape the plugin |
| must be prebuilt and committed | The manifest names an entry assembly that is not in the repository |
| is already deployed | An extension of that id already exists; remove it and restart first |
| is already installed | A plugin of that name is installed; remove or update it |
| git clone … failed | Bad URL, bad reference, private repo the gateway cannot authenticate to |

Nothing is written unless the whole fetch **and** validation succeed, so a refused install leaves
no partial state behind.

## Next steps

- [Skills](skills.md) — the `SKILL.md` format in full
- [Extensions](extensions.md) — writing the code a plugin can carry
- [Troubleshooting](troubleshooting.md)
