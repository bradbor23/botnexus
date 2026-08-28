# Notifications

An agent that fails at 2am, or one that stops halfway through a job waiting for an answer, is only
useful information if it reaches you. The notification manager is the part of BotNexus that carries
that news out of the gateway and to wherever you actually are.

It has three layers, and you can stop at whichever one suits you:

| Layer | What it does | Needs |
| --- | --- | --- |
| The store | Records what happened, server-side, whether or not anyone is connected | Nothing |
| The bell | Shows it in the portal, live, with an unread badge | The portal open |
| Desktop alerts | Raises an OS notification when the portal is *not* what you are looking at | A one-time permission grant |

The important design choice is that notifications are stored **on the gateway**, not in your
browser. Read state travels with them. Dismissing something on your laptop leaves it dismissed on
every other device, and a notification raised while every browser was closed is waiting for you when
one opens.

## The bell

The bell sits in the top bar. A red badge carries the unread count; it caps at `99+` rather than
growing without limit.

Opening it lists the fifty most recent notifications, newest first, read and unread together.

| Action | Effect |
| --- | --- |
| Click a notification | Marks it read and, when it has somewhere to go, takes you there |
| **Mark all read** | Clears the badge without dismissing anything. Only offered when something is unread |
| **&times;** on a row | Deletes that notification permanently |
| Click anywhere outside, or press <kbd>Esc</kbd> | Closes the panel |

An unread row carries a coloured bar down its left edge rather than a background wash, so a long
list stays legible and an unread item never looks disabled.

An empty list is the ordinary state of a healthy gateway. It says so rather than showing you an
empty table.

## What raises a notification

Four things do. Each one was chosen because it is something you would want to know *without*
watching for it.

| Raised when | Kind | Severity | Takes you to |
| --- | --- | --- | --- |
| An agent run ends in an error | `AgentRunFailed` | Error | The conversation, when there is one |
| An agent asks a question and blocks | `AgentWaitingForInput` | Warning | The paused conversation |
| A scheduled job fails, times out, or cannot deliver its result | `CronRunOutcome` | Error | The cron page |
| The gateway stops responding, and again when it recovers | `GatewayHealth` | Error, then Info | — |

Three deliberate silences are worth knowing about, because their absence is a design decision rather
than a gap:

- **Successful runs are not notified.** They are visible in run history, and a notification for
  every success is how people learn to ignore notifications.
- **Successful scheduled runs are not notified** either, for the same reason — and neither is a job
  *you* aborted, since being told about something you just did is noise.
- **A failing run notifies once**, not once per error event. A single run can emit several errors;
  you get one report of the failure.

The gateway health notification is also latched: it fires when the watchdog first sees the gateway
stop responding, and does not fire again until after it has recovered. A gateway that is still down
is not news a second time.

## Desktop alerts

The bell answers "what happened?" for someone looking at the portal. Desktop alerts answer it for
someone who is not.

**To turn them on:** open the bell and click **Enable desktop alerts** in the panel footer. Your
browser will ask for permission; granting it also opts you in.

That button is the only way in, and that is a browser rule rather than a choice — a permission
prompt raised any other way is ignored by Chrome and refused outright by Safari.

### What you will and will not be interrupted by

An alert is **suppressed while the portal is visible and focused**. You are already looking at the
bell; the badge has moved, and a duplicate toast on top of it is exactly how people end up switching
notifications off.

| Where you are | What happens |
| --- | --- |
| Portal open and in front of you | No toast — the badge already said it |
| Portal open behind another window | Toast |
| Portal tab in the background | Toast |
| Portal closed entirely | Nothing now; it is waiting in the bell when you return |

A toast carries the notification's own identifier, so a repeat of the same notification **replaces**
its toast rather than stacking a second copy. Clicking one focuses the portal and navigates to
whatever it is about, without reloading the app.

### Scope, and turning them off

The permission and the opt-in both belong to **one browser on one machine**. Enabling alerts in
Chrome on your desktop does nothing for Firefox, for your laptop, or for your phone; each is a
separate grant. This mirrors how the browser itself treats the permission, and it means a shared or
public machine cannot inherit your choice.

Click **Desktop alerts on** to turn them off again. That only clears your opt-in — the browser
permission is left alone, so turning them back on later does not re-prompt.

### If the panel says alerts are blocked

Once a browser permission has been **denied**, no site can ever ask again from script. There is
nothing the portal can offer you at that point, so it says so instead of showing a button that
cannot work. Clear it in your browser's own site settings for the gateway's address, then reload.

### Browsers that cannot do this

If the footer control is missing entirely, the browser has no Notification API at all and there is
nothing to enable.

**Android Chrome is the notable case.** It permits notifications only through a service worker, so
this layer does not work there — the alert fails silently and the notification still arrives in the
bell as normal. Reaching an Android phone properly needs web push, which is not built yet.

## Where notifications live

`~/.botnexus/notifications.sqlite`, alongside the other gateway stores.

Nothing about a notification is kept in the browser except your desktop-alert opt-in. That is what
lets read state be shared, and it is the reason a phone or desktop app can later read exactly the
same records — which is the point of storing them server-side in the first place.

## Live updates

A connected portal is pushed each notification over SignalR as it is raised, so the badge moves
without polling.

The push is broadcast to every connected client, because a notification is about the gateway and its
agents rather than about one conversation. It is also allowed to be **lossy**: the store is
authoritative, and a client that was disconnected when something was raised picks it up on its next
read. Losing a push costs you immediacy, never the notification.

## The API

Every endpoint is under `/api/notifications`. Non-loopback callers need a gateway token like any
other endpoint.

| Method | Path | Purpose |
| --- | --- | --- |
| `GET` | `/api/notifications` | List, newest first |
| `GET` | `/api/notifications/unread-count` | Just the number, for a badge |
| `POST` | `/api/notifications/{id}/read` | Mark one read |
| `POST` | `/api/notifications/read-all` | Mark everything read |
| `DELETE` | `/api/notifications/{id}` | Delete one permanently |

`GET /api/notifications` takes two query parameters:

| Parameter | Default | Notes |
| --- | --- | --- |
| `includeRead` | `true` | Set `false` for unread only |
| `limit` | `100` | Clamped to 500, so a client cannot ask for the whole history |

```bash
curl -s localhost:5005/api/notifications?includeRead=false | jq
curl -s localhost:5005/api/notifications/unread-count
```

`kind` and `severity` come back as **names**, not numbers, and the wire shape is identical whether a
notification arrived over REST or over SignalR — so a client parses one shape either way.

```json
{
  "id": "a1b2c3",
  "kind": "AgentRunFailed",
  "severity": "Error",
  "title": "Agent 'assistant' run failed",
  "body": "The provider returned 529.",
  "agentId": "assistant",
  "conversationId": "c47f...",
  "link": "agent/assistant/conversation/c47f...",
  "createdAtUtc": "2026-08-28T02:14:33Z",
  "readAtUtc": null
}
```

`link` is site-relative and has no leading slash. It is `null` when there is nowhere useful to go —
a gateway health notification, for instance, is not about any one page.

## Limits worth knowing

- **Nothing is deleted automatically.** The store can prune read notifications older than a given
  age, but no scheduled task calls it yet. In practice the volume is low — these are failures, not
  events — but the file grows without bound until you delete rows yourself.
- **There is no per-kind muting.** Alerts are all-or-nothing per browser; you cannot ask for agent
  failures but not scheduled-job failures.
- **`AgentRunCompleted` is defined but never raised.** It exists in the API surface for clients to
  handle, and is reserved for an opt-in "tell me when it finishes" setting that does not exist yet.
- **Alerts reach browsers only.** Web push — and with it iOS, Android and Windows clients — is the
  next layer, and is not built.

## Troubleshooting

**No bell in the top bar.** The notifications client is not registered, which means the portal was
served by a gateway without the notifications API. Check `GET /api/notifications` responds.

**The badge never moves, but notifications appear when I open the panel.** The live push is not
arriving. The badge is still correct on open, so nothing is lost. Check the gateway log for
`NotificationSignalRBridge started.` — and note that the console only shows warnings and above, so
look in `~/.botnexus/logs/` rather than at the terminal.

**Alerts are on, but I get no toast.** Check the four cases in the table above first — a portal that
is visible and focused is *supposed* to stay quiet. Then check your OS: macOS Focus, Windows Focus
Assist and Do Not Disturb all swallow browser notifications without telling the page.

**The bell is empty and I expected something.** Notifications are raised by failures. A quiet bell
usually means a healthy gateway rather than a broken one. To confirm the pipeline end to end, let a
scheduled job fail — or stop one mid-run — and watch for the entry.
