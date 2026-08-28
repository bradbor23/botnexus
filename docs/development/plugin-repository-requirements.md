# Plugin repository requirements

What a GitHub repository must contain for the BotNexus marketplace to fetch, validate and
install it. Every rule below is taken from the code that will actually read the repo, with the
source file named so it can be re-checked rather than trusted.

Read this before authoring a plugin repo. A repo that does not meet the **Required** section
will be rejected at install time with a message naming the offending field.

## 1. How the installer reads the repo

`GitPluginSourceFetcher` runs, literally:

```
git clone --quiet [--branch <reference>] -- <source> .
git rev-parse HEAD
```

Consequences that constrain the repo layout:

- **The whole repository becomes the plugin.** There is no sparse checkout, no subdirectory
  option, no monorepo path. One plugin per repository, at the repository root.
- **`.git/` is excluded when the staged clone is promoted** to `~/.botnexus/plugins/<name>/`
  (`PluginLifecycleManager.Promote`). Everything else in the repo is copied verbatim — build
  output, `node_modules`, screenshots and all. Keep the repo lean; nothing is filtered for you.
- **The recorded version is the commit SHA, never the branch name.** `reference` may be a
  branch, tag or commit; what gets stored is what `rev-parse HEAD` returned, so "has the source
  moved since install?" stays answerable.
- The source is passed after `--`, so a URL beginning with a dash cannot be reinterpreted as a
  git option. Private repos work via git's own authentication.

## 2. Required: the manifest

**Exact path, at the repository root:**

```
.botnexus-plugin/plugin.json
```

A directory without this file is not a plugin and is rejected — `PluginManifestParser`
returns `Plugin manifest not found` rather than treating it as an empty success.

The manifest is validated against `plugin-manifest.schema.json`, which is
**`"additionalProperties": false`**. Any field not listed below fails validation. There is no
best-effort coercion: an unknown shape is an error, not a warning.

Only **`name`** is required. Everything else is optional but recommended.

| Field | Type | Rules |
|---|---|---|
| `name` | string | **Required.** `^[a-z0-9]+(-[a-z0-9]+)*$`, 1–64 chars. Lowercase kebab-case. |
| `description` | string | Shown in marketplace listings. Write this — it is the card copy. |
| `version` | string | Semver: `^\d+\.\d+\.\d+(-prerelease)?(+build)?$` |
| `author` | object | `{ "name": <required>, "email"?, "url"? }` — no other keys allowed. |
| `homepage` | string | Project or docs URL. |
| `repository` | string | Source repository URL. |
| `license` | string | SPDX identifier, e.g. `MIT`. |
| `keywords` | string[] | Used for marketplace search. |
| `skills` | string[] | Explicit skill paths. **Omit** to use convention discovery. |
| `agents` | string[] | Explicit agent paths. Omit for convention discovery. |
| `commands` | string[] | Explicit command paths. Omit for convention discovery. |
| `hooks` | string | Single path (not an array). |
| `mcpServers` | string | Single path (not an array). |

**The `name` in the manifest is authoritative.** It becomes the install directory
(`~/.botnexus/plugins/<name>/`) and the key in the installed-plugin record. If the marketplace
requested a specific name and the manifest disagrees, the install is rejected:
`declares name 'x', which does not match the requested 'y'`.

Minimal valid manifest:

```json
{
  "name": "my-plugin",
  "description": "One line describing what this plugin gives an agent.",
  "version": "1.0.0",
  "author": { "name": "Your Name" },
  "license": "MIT",
  "keywords": ["example"]
}
```

## 3. Skills — the only part that is actually wired today

Read this section carefully: **skills are the sole plugin content the running gateway
discovers.** See §5 for what is declared but inert.

Layout, relative to the repository root:

```
skills/
  <skill-name>/
    SKILL.md          <- required; a directory without it is silently skipped
    references/       <- optional supporting dirs, surfaced as load-on-demand linked files
    templates/
    scripts/
    assets/
```

- **Exactly one level of nesting.** `SkillDiscovery.ScanDirectory` enumerates the immediate
  subdirectories of `skills/` and looks for `SKILL.md` in each. `skills/a/b/SKILL.md` is not found.
- Only `references/`, `templates/`, `scripts/`, `assets/` are treated as supporting directories.
- `SKILL.md` must be **≤ 512 KB** or the skill is skipped with a warning.

### SKILL.md format

YAML frontmatter delimited by `---`, which must be the **first non-empty line** of the file:

```markdown
---
name: my-skill
description: When to use this skill and what it does. This is what the model reads to decide.
license: MIT
compatibility: Requires git on PATH
allowed-tools: read, write, bash
disable-model-invocation: false
metadata:
  category: example
---

# My skill

Body content: trigger conditions, numbered steps, exact commands, pitfalls, verification steps.
```

Recognised frontmatter keys (`SkillParser`) — anything else is ignored, not an error:

| Key | Notes |
|---|---|
| `name` | Must **not** contain a double hyphen (`--`). |
| `description` | Must be non-empty. **≤ 1024 characters.** This is the trigger text. |
| `license` | Free text. |
| `compatibility` | **≤ 500 characters.** |
| `allowed-tools` | Free text list. |
| `disable-model-invocation` | Boolean. |
| `metadata` | Nested map of string values. |

### Name collisions

Plugin skills are scanned **first**, which in this merge means **lowest priority**. A
same-named global, agent or workspace skill wins. Namespace your skill names to avoid being
silently shadowed.

## 4. Optional: a marketplace catalog

Only needed if a repo publishes a *list* of plugins rather than being one. Validated against
`marketplace.schema.json`, also `additionalProperties: false`.

```json
{
  "name": "my-catalog",
  "owner": { "name": "Your Name" },
  "description": "What this catalog offers.",
  "plugins": [
    {
      "name": "my-plugin",
      "source": "https://github.com/you/my-plugin.git",
      "description": "Card copy.",
      "version": "1.0.0",
      "keywords": ["example"]
    }
  ]
}
```

Required: `name`, `owner.name`, `plugins`. Each entry requires `name` and `source`.

## 5. What does NOT work yet — do not build against it

The manifest accepts these fields and the schema documents them, but **nothing in the running
gateway consumes them**. Declaring them is harmless; relying on them will not work.

- `agents`, `commands`, `hooks`, `mcpServers` — parsed, never discovered. Only
  `PluginSkillRootResolver` (skills) has a production consumer.
- **A plugin cannot ship code or UI.** The schema has no field for an assembly, and
  `additionalProperties: false` means one cannot be added by the plugin author. Runnable code
  ships as a gateway *extension* (`botnexus-extension.json` + `IAgentTool`) or an MCP server —
  a different mechanism entirely.

Extending the format to carry code is planned work on this fork; until it lands, assume
skills-only.

## 6. Install-time failure modes

Worth knowing so the repo can avoid them:

| Condition | Result |
|---|---|
| No `.botnexus-plugin/plugin.json` at root | Rejected: manifest not found |
| Unknown field in the manifest | Rejected, naming the field |
| `name` not lowercase kebab-case | Rejected on pattern |
| Plugin of that name already installed | Rejected — remove or update first |
| `~/.botnexus/plugins/<name>/` exists but is unrecorded | Rejected, **not** overwritten |
| `git clone` fails (private, bad ref, network) | Rejected with git's exit code and stderr |

Nothing is written to the plugin directory unless the entire fetch **and** validation
succeeded — installs are staged, then promoted.

## 7. Checklist for the plugin author

- [ ] `.botnexus-plugin/plugin.json` exists at the repository root
- [ ] `name` is lowercase kebab-case and matches the intended install directory
- [ ] `description` and `keywords` are filled in — they are the marketplace card
- [ ] No fields beyond the table in §2
- [ ] Skills live at `skills/<name>/SKILL.md`, exactly one level deep
- [ ] Every `SKILL.md` opens with `---` frontmatter carrying a non-empty `description` ≤ 1024 chars
- [ ] No skill `name` contains `--`
- [ ] Repo contains no build output or large assets — everything except `.git/` is copied on install
- [ ] A tag or release commit exists to install as a pinned `reference`

## Sources

- `src/extensions/BotNexus.Extensions.Plugins/Schemas/plugin-manifest.schema.json`
- `src/extensions/BotNexus.Extensions.Plugins/Schemas/marketplace.schema.json`
- `src/extensions/BotNexus.Extensions.Plugins/PluginManifestParser.cs`
- `src/extensions/BotNexus.Extensions.Plugins/Lifecycle/GitPluginSourceFetcher.cs`
- `src/extensions/BotNexus.Extensions.Plugins/Lifecycle/PluginLifecycleManager.cs`
- `src/extensions/BotNexus.Extensions.Plugins/Lifecycle/PluginSkillRootResolver.cs`
- `src/extensions/BotNexus.Extensions.Skills/SkillDiscovery.cs`
- `src/extensions/BotNexus.Extensions.Skills/SkillParser.cs`
