# Instant replies for the whale-status button (Cloudflare Worker)

## Why

The poller runs as a GitHub Actions job and only reads Telegram button presses (`getUpdates`) at the end of each run, so the button would answer minutes late. A Cloudflare Worker (Workers Free) answers at once through a Telegram webhook.

## How it works

1. After each successful poll the poller renders the whale table and writes it to one Workers KV key (`whale-table`; the same name is `TABLE_KEY` in `cloudflare/whale-bot/src/bot.js` and `CloudflareKvSettings.DefaultKey`) through the Cloudflare REST API. It needs `CLOUDFLARE_API_TOKEN`, `CLOUDFLARE_ACCOUNT_ID` and `CLOUDFLARE_KV_NAMESPACE_ID`. A problem while building or writing the table is logged as `[Cloudflare Warning]` and never fails the poll.
2. Telegram sends each message to `POST /webhook` on the Worker.
3. The Worker checks the secret header, accepts only the owner chat, and for the button text, `/status`, `/whales` or `/start` replies with the stored table. The reply is returned in the webhook response itself (`method: sendMessage`), so the Worker holds no bot token and makes no outgoing request.

While a webhook is set the poller skips `getUpdates` (it asks Telegram with `getWebhookInfo` first); with no webhook it answers button presses itself as before. So deleting the webhook is a complete rollback.

The reply is instant, the data is not: it is the table from the last poll. The table ends with the time it was built (UTC), which is kept when a long table is shortened to fit Telegram's 4096-character limit. If KV cannot be read the Worker answers "temporarily unavailable" with HTTP 200, so Telegram does not redeliver the update.

## Security

- Requests without the `X-Telegram-Bot-Api-Secret-Token` header matching `WEBHOOK_SECRET` get 401 (both values are hashed with SHA-256 first, then compared in constant time). The Worker answers 503 if `WEBHOOK_SECRET` or `ALLOWED_CHAT_ID` is missing.
- Messages from any chat other than `ALLOWED_CHAT_ID` are ignored.
- Use an Account API token limited to this account with Workers Scripts: Edit and Workers KV Storage: Edit, with an expiry, and no IP filter (runner addresses change). It is stored only as a GitHub secret.
- The deploy and webhook workflows run only from `main`. Wrangler is installed from `package-lock.json` (`npm ci`, pinned version). The namespace id is not committed: `wrangler.toml` holds a placeholder and the deploy workflow fills it from the `CLOUDFLARE_KV_NAMESPACE_ID` secret for that run (the poller writes to the same namespace).

## Limits (Cloudflare documentation, checked 2026-11-14)

- Workers Free: 100,000 requests a day, 10 ms CPU per request, 50 subrequests.
- KV Free: 100,000 reads and 1,000 writes a day, one write a second to the same key. A poll every 5 minutes writes about 288 times a day.
- KV is eventually consistent; a new table can take about a minute to reach every location.

## Setup

1. Create a free Cloudflare account and open Workers & Pages once so the `workers.dev` subdomain exists.
2. Storage & Databases, KV: create a namespace and note its id (32 lowercase hex characters); it goes into the `CLOUDFLARE_KV_NAMESPACE_ID` secret.
3. Manage account, Account API tokens, Create Token, Custom token: the two permissions above, this account only, an expiry. **Copy the value shown once on the final page** (an Account API token starts with `cfat_` and is about 53 characters; the 32-character id in the token list is not the token).
4. Add the repository secrets `CLOUDFLARE_API_TOKEN`, `CLOUDFLARE_ACCOUNT_ID`, `CLOUDFLARE_KV_NAMESPACE_ID`, `WHALE_WEBHOOK_SECRET` (a random string of 32 to 64 letters, digits, `_` or `-`).
5. Run the workflow `Deploy Whale Bot Worker` (it runs the Worker tests, deploys, and sets the Worker secrets `WEBHOOK_SECRET` and `ALLOWED_CHAT_ID` from `TELEGRAM_CHAT_ID`).
6. After one poller run, check `https://whale-bot.<subdomain>.workers.dev/health` answers `ok`.
7. Run `Telegram Webhook` with `action = set` and the Worker URL ending in `/webhook`; run it again with `info`.
8. Press the button in Telegram.

## Troubleshooting

| Symptom | Likely cause and fix |
|---|---|
| Deploy fails with error 6111 or 9106 | `CLOUDFLARE_API_TOKEN` is not a valid token. Copy the value from the "Create token" page again. The "Check the Cloudflare token" step prints the stored length and the verify result, never the value. |
| Deploy fails on the namespace check | `CLOUDFLARE_KV_NAMESPACE_ID` is missing or is not 32 lowercase hex characters, or `wrangler.toml` lost its placeholder. |
| Button does nothing | Run `Telegram Webhook` with `info`; `last_error_message` shows what Telegram got. A 401 means `WHALE_WEBHOOK_SECRET` differs from the value the Worker holds: deploy again, then `set` again. |
| "No whale data has been published yet" | KV is empty. Look for `[Cloudflare Warning]` in the latest poller log. |
| `[Cloudflare Warning] KV write failed: HTTP 401` | The Cloudflare token is wrong, expired or lacks Workers KV Storage: Edit. |
| The table looks old | The poller stopped. The Worker keeps serving the last table; its last line shows when it was built. |

## Rollback

Run `Telegram Webhook` with `action = delete`. At its next run the poller sees no webhook and answers button presses itself.
