# BotNexus Design System

**Status:** Foundation implemented · **Applies to:** the Blazor WebAssembly portal
(`BotNexus.Extensions.Channels.SignalR.BlazorClient`)

---

## Purpose

The concrete implementation spec for the portal's look and feel. Every token
lives in one block at the top of `wwwroot/css/app.css`; this document explains
what each is *for* and, more usefully, which decisions are already made so they
don't get re-litigated per component.

The goal is not more decoration. It is fewer, more deliberate decisions applied
consistently — which is what actually reads as considered in mature software.

---

## Why this exists

The portal worked but read as a prototype:

| | Before |
|---|---|
| Colour | 104 distinct hardcoded hex values |
| Undeclared tokens | **34 names, 74 references**, each silently resolving to a hex fallback baked into the `var()` call |
| Corner radius | 15 distinct values across 62 hardcoded declarations |
| Elevation | 7 ad-hoc shadows, including two on flat content |
| Typography | no defined scale |
| Themes | dark only |

The undeclared-token problem was the most consequential and the least visible.
A declaration like `var(--surface, #1e1e1e)` looks tokenised but is not: the
name resolves to nothing, the hardcoded fallback wins, and the value can never
respond to a theme. Seventy-four places behaved this way. Any attempt to add a
light theme would have rendered them all in dark-theme colours.

---

## Research basis

Drawn from **Apple's Human Interface Guidelines** and **Microsoft's Fluent 2**,
taking what the two agree on rather than blending their visual languages:

- **Deference (HIG).** Chrome recedes so content leads. Depth comes from the
  surface ladder, not from decorating every card with a shadow.
- **Hit targets.** HIG asks 44px for touch; Fluent's desktop pointer minimum is
  32px. Both are tokenised. The portal's compact density was **28px** — under
  both.
- **Elevation ramp (Fluent).** A small set of named tiers, rather than one
  shadow or a bespoke shadow per component.
- **Materials (both).** Floating chrome is translucent and blurred so the layer
  beneath reads as context — Fluent's acrylic, HIG's vibrancy.
- **Motion (Fluent).** Named durations, and *different easing for entering vs.
  exiting*. Motion explains a change; it is never decorative.
- **Focus (both, emphatically).** One visible keyboard focus indicator, defined
  once and applied globally rather than left to each control.

---

## Tokens

### Colour

A four-step surface ladder on a near-neutral slate — each step a lightness
increment, never a different hue.

```
--color-canvas      page background
--color-surface     cards, panels
--color-surface-2   nested / hovered surfaces
--color-hairline    1px borders

--color-ink / -muted / -faint      text roles
```

The previous palette used a **saturated blue** (`#0f3460`) as its surface. That
is the single biggest reason the shell read as dated: surfaces competed with the
accent instead of receding behind content.

**One accent.** `--color-accent` is BotNexus's cyan, used *only* for primary
actions, focus rings and the active nav item. Never a section heading, never
decoration. ProjectOS's violet was deliberately not adopted — the rule is the
framework, the hex is that product's identity.

`--color-on-accent` exists because text on a filled accent surface must not use
`--color-ink`: ink flips between themes and won't reliably contrast against a
mid-tone fill.

**Status: three tokens each, not one.** `--color-success` / `-bg` / `-text`.
Dot indicators use the solid value; pills use the bg/text pair, so they stay
legible on any surface instead of depending on opacity tinting.

### Category palette

`--cat-live`, `--cat-pinned`, `--cat-agent`, `--cat-subagent`, `--cat-a2a`,
`--cat-webhook`, `--cat-blue`, `--cat-purple`, `--cat-muted`.

These identify a *kind* of thing, not a status and not an action. They are
deliberately separate from the accent: the accent means "act here", and reusing
it for classification dilutes that. Kept desaturated so a screen showing several
at once stays calm. This is the hook for per-plugin colour-coding later.

### Typography

Seven roles. Reference these, never a raw font-size.

| Role | Size / Weight | Use |
|---|---|---|
| `t-display` | 28px / 600 | hero numbers (rare) |
| `t-title` | 20px / 600 | page titles |
| `t-heading` | 16px / 600 | section and card headings |
| `t-body` | 14px / 400 | default |
| `t-label` | 13px / 500 | form labels, small UI text |
| `t-caption` | 12px / 400 | metadata, timestamps |
| `t-mono` | 13px / 400 | IDs, hashes, ports |

Line-heights sit on a 4px grid. Sizes are in `rem` so an OS text-size preference
still scales them — the practical web equivalent of HIG's Dynamic Type.

### Shape

Two radii. `--radius-sm` **6px** for buttons, inputs, badges, small controls;
`--radius-lg` **12px** for cards, dialogs, panels. `--radius-pill` is a shape,
not a third size.

HIG's continuous "squircle" curvature has no portable CSS equivalent yet;
plain `border-radius` is the honest approximation.

### Hit targets

`--hit-pointer` **32px** (Fluent desktop minimum) · `--hit-touch` **44px** (HIG).
Both exist so a control picks the one matching its input modality rather than
splitting the difference and satisfying neither. Compact density now uses the
former, comfortable the latter.

### Elevation

Two tiers, and **flat content gets neither**:

- `--shadow-raised` — popovers, dropdowns, menus, tooltips
- `--shadow-overlay` — dialogs, command palette, toasts

Cards, tables and lists carry **no shadow** and are separated by the surface
ladder alone. Adding shadows to every card is the fastest way to make a redesign
look busier rather than more considered.

### Material

`.material-raised` / `.material-overlay` apply an acrylic-style translucent
blurred background. Confined to floating chrome, because blur is expensive.
Degrades to `--material-fallback` where `backdrop-filter` is unsupported and
under `prefers-reduced-transparency`.

### Motion

`--motion-fast` 100ms (hover/press) · `--motion-base` 160ms (open/close) ·
`--motion-slow` 240ms (drawers).

Entering decelerates (`--ease-enter`), exiting accelerates (`--ease-exit`).
Anything past ~200ms on UI chrome reads as sluggish, not smooth.
`prefers-reduced-motion` is honoured globally.

### Focus

`:focus-visible` (not `:focus`) draws a 2px accent ring with 2px offset, so a
pointer click leaves nothing behind while keyboard traversal always shows one.

---

## Rules

1. No raw hex in a component rule. Add a token instead.
2. Two radii. `--radius-pill` and `50%` are shapes, not extra sizes.
3. Shadows only on floating chrome, only via the two tiers.
4. One accent. Category colours classify; status colours signal; the accent
   invites action.
5. Type roles, never raw font-sizes.
6. Every colour must come from a token, or the light theme will not follow it.

### Deliberate exceptions

Four non-token values remain, each defensible:

- `50%` on circular avatars and status dots — a shape.
- `3px` on scrollbar thumbs — not a control.
- `3px` on inline `<code>` — 6px on a tight inline span reads bubbly.
- `2px` on the burger-menu lines — a line cap, not a corner.

---

## Themes

Dark lives on `:root`; light is opt-in via `[data-theme="light"]` on `<html>`.

This **inverts the usual convention** (light as default) on purpose. Dark was
the only theme this portal ever had, and an existing user must not be repainted
by upgrading. Only tokens are redefined — no component rule is duplicated, and
no `dark:`-style variant classes exist anywhere.

Light is currently **structural only**: the token set is complete and correct,
but no toggle UI ships yet. Adding one is a small, self-contained follow-up.

---

## Deploying a CSS change

Two facts make this less obvious than it looks, and getting either wrong
produces the same symptom: the change is live on the server and invisible in
the browser.

**1. The portal is served from the installed extension, not the build tree.**
Rebuilding updates `src/extensions/…SignalR/bin/Release/net10.0/blazor/` but
not `~/.botnexus/extensions/botnexus-signalr/blazor/`.

**2. A service worker sits in front of it.** `service-worker.js` is
network-first for the shell and cache-first for fingerprinted `/_framework/`
assets, and `service-worker-assets.js` carries an integrity hash for every
file it caches — `css/app.css` included.

So **never hand-copy an individual file into the extension directory.** Doing
that leaves the asset manifest holding the hash of the *previous* file and its
`Manifest version` unchanged, which is precisely how the worker decides whether
a new build exists. It concludes nothing changed and keeps serving its cache —
through a hard reload, a fresh tab, `cache: no-store`, and query-string
cache-busting alike, because a service worker intercepts all of them. The
worker's own source comment puts it well: *"The cache was not merely stale, it
was stale FOREVER."*

Rebuild, then deploy the whole output:

```bash
dotnet build src/extensions/BotNexus.Extensions.Channels.SignalR -c Release
rsync -a --delete \
  src/extensions/BotNexus.Extensions.Channels.SignalR/bin/Release/net10.0/blazor/ \
  ~/.botnexus/extensions/botnexus-signalr/blazor/
```

Verify all three moved together — a mismatch between them is the bug:

```bash
curl -s http://<host>:5005/ | grep -o 'app.css[^">]*'                  # versioned href
curl -s http://<host>:5005/service-worker.js | grep -o 'Manifest version: [^ ]*'
curl -s 'http://<host>:5005/css/app.css?v=ds1' | grep -c color-canvas
```

A changed `Manifest version` is the signal that reaches the browser. Expect the
client to need two reloads: one to install the new worker, one for it to take
control. If a client is genuinely wedged, DevTools → Application → Service
Workers → Unregister.

Because `index.html` references the stylesheet at a stable path, the href also
carries a `?v=` token — bump it whenever `app.css` changes materially. Blazor
fingerprints its own `_framework` assets but has no equivalent for
`index.html` in standalone WASM.

---

## Status

**Done**

- Full token layer: colour, category, typography, shape, hit targets,
  elevation, material, motion, focus
- Light theme token set
- All 34 previously-undeclared tokens now declared and theming correctly
- **Every colour in every component rule flows through a token** — 133 literal
  values migrated, 0 remain
- **144 dead `var(--x, #hex)` fallbacks removed.** Now that every token is
  declared, an inline fallback only hides a future typo: it paints a hardcoded
  colour instead of failing visibly. Removing them is what makes the rule "no
  raw hex in a component rule" enforceable rather than aspirational.
- White-on-accent contrast fixed: `#fff` on the cyan accent measured ~2:1,
  under the 4.5:1 minimum. Those 13 call sites now use `--color-on-accent`
  (~8.5:1). White is retained via `--color-on-solid` where the fill is a
  mid-to-dark status or category colour and white is correct.
- Hardcoded radii 62 → 18 (and 15 distinct → 8, of which 4 are deliberate)
- Ad-hoc shadows 7 → 0; two shadows removed from flat content
- Global `:focus-visible`, `prefers-reduced-motion`,
  `prefers-reduced-transparency`

**Verified:** `src/dirs.proj` builds with 0 warnings / 0 errors; the gateway
boots with 0 errors and serves the tokenised stylesheet.

**Next**

1. Theme toggle UI plus a no-flash inline script, to make light reachable.
2. Migrate the 75 remaining `var(--radius)` call sites onto `--radius-sm` /
   `--radius-lg` explicitly, then retire the legacy alias.
3. Apply the seven type roles to markup — currently defined but not yet adopted.
   This is the largest remaining piece and the one that most affects how the
   portal reads.
4. Retire the legacy colour aliases once no rule references them.
5. Real `Dialog` / `Toast` components using `.material-overlay`.
6. Command palette (`Ctrl/Cmd+K`).
