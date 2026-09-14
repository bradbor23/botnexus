# Cost & credit balance

Anthropic API credits are prepaid. When the balance reaches $0 and auto-reload is off, every agent
that uses the Anthropic provider stops working. This page covers the daily report that tracks that
balance, and every command you need to look after it.

## What you get each morning

At **07:00 Pacific**, the cron job **Daily Cost Monitor Report** runs
`~/daily-cost-report.py` on the gateway host and sends:

| Where | What |
| --- | --- |
| Email | Yesterday's spend with a per-item breakdown, tokens by model, and a **Credit balance** section |
| Telegram | A short message: estimated balance, yesterday's spend, 7-day burn rate and runway |

Both show the **estimated balance**, the **7-day average burn** and the **runway**, meaning days
left at that burn rate and the date the balance would reach zero.

When the estimate falls below **$15**, or the runway drops under **30 days**, the email subject
starts with `LOW BALANCE -` and the Telegram message opens with a ⚠️ warning line.

The job is a `command` cron job, not an agent prompt. It spends no model tokens, and the admin key
never enters an agent's context.

## How the balance is worked out

**No Anthropic API returns your prepaid credit balance.** Only the Console shows it, under
**Settings → Billing**. The report therefore works out an *estimate*:

```
estimated balance = last Console reading − cost of every full UTC day since that reading
```

- The last Console reading is stored in `~/.botnexus/anthropic-balance.json`.
- Daily cost comes from the Anthropic Admin API (`/v1/organizations/cost_report`).
- Costs are counted from the UTC day **after** the reading. A reading taken partway through a day
  therefore does not double-count that day.

The estimate drifts a little over time, because the Console and the cost report update at
different moments. Whenever you look at the Console, re-anchor the estimate to what it shows.

::: warning Cost report amounts are in cents
`cost_report` returns `amount` as a decimal string in **cents**: `"725.54"` means **$7.26**. The
script divides by 100. Any other tool or agent that reads this API must do the same. Reading the
value as dollars inflates spend 100×.
:::

## Commands

Run these from your Mac. Each one connects to the gateway host over SSH.

Set the host once in your own `~/.zshrc`, so every command below works exactly as written:

```bash
export BOTNEXUS_HOST="you@your-gateway-host"
```

Then run `source ~/.zshrc`, or open a new terminal.

### After buying credits: set the new balance

Use the balance the Console shows **after** the top-up, not the amount you added.

```bash
ssh "$BOTNEXUS_HOST" 'python3 ~/daily-cost-report.py --set-balance 121.54'
```

This replaces the stored reading with today's date and takes effect in the next morning's report.
It sends nothing.

### Check the stored reading

```bash
ssh "$BOTNEXUS_HOST" 'cat ~/.botnexus/anthropic-balance.json'
```

### Send the report right now

This sends the email and the Telegram message immediately, so you can test a change:

```bash
ssh "$BOTNEXUS_HOST" 'python3 ~/daily-cost-report.py'
```

Output ends with `sent: <day> total $<amount>` for the email and `telegram: sent` for Telegram.

### Confirm the email was actually delivered

If `sendmail` accepts a message, that does not mean it was delivered. Check the mail log instead:

```bash
ssh "$BOTNEXUS_HOST" 'journalctl -u "postfix*" --since "-10min" --no-pager | grep status='
```

`status=sent` means delivered. `status=deferred` means the message is stuck in the queue; run
`mailq` on the host to see why.

### See the cron job and its recent runs

Open the portal's cron page and find **Daily Cost Monitor Report**. It shows the schedule, whether
the job is enabled, and each run's status and output.

## Files on the gateway host

| Path | What it is |
| --- | --- |
| `~/daily-cost-report.py` | The report script |
| `~/.botnexus/anthropic-balance.json` | The last Console reading: `{"balance": 71.54, "as_of": "2026-09-13"}` |
| `~/.botnexus/anthropic-admin.key` | The Anthropic **admin** key, mode `0600`. Never paste it into a conversation |
| `~/.botnexus/config.json` | Telegram bot token and chat id (`channels.telegram`), read by the script |

The warning thresholds are the `LOW_BALANCE_USD` and `LOW_RUNWAY_DAYS` constants near the top of
the script.

## The admin key

The cost report needs an **admin key** (`sk-ant-admin01-…`), not a normal API key. Create one in
the Console under **Settings → Admin keys**; this requires the admin role. A normal
`sk-ant-api03-…` key gets HTTP 401 from the cost report.

To put a key on the host without typing it, copy it to the clipboard and run:

```bash
pbpaste | ssh "$BOTNEXUS_HOST" 'umask 077; cat > ~/.botnexus/anthropic-admin.key'
```

On your Mac, do not store an admin key as `ANTHROPIC_API_KEY`. The Anthropic SDKs and tools read
that name for ordinary model calls, and an admin key makes them fail. Use `ANTHROPIC_ADMIN_KEY`
instead.

## Troubleshooting

| Symptom | Cause and fix |
| --- | --- |
| Email subject says **FAILED**, HTTP 401 or 403 | The key file holds a normal API key or a revoked key. Replace it with a working admin key (see above) |
| Email arrives but there is no Telegram message | Run the report by hand and read the `telegram:` line. `not configured` means `channels.telegram` in `config.json` has no `botToken` or `allowedChatIds` |
| "No balance anchor" in the report | `anthropic-balance.json` is missing. Run `--set-balance` with the Console balance |
| Estimate differs from the Console | Normal drift. Run `--set-balance` with the Console figure |
| Cost report returns empty `results` | Check the dates you asked for. An empty day is a day with no spend, not a broken API. Data only exists from the organization's first API usage onward |
| An agent says it will "check the Console" for your balance | It cannot. Agents cannot open the Console, and no API exposes the balance. Use this report instead |
