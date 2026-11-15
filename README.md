# whale-watch

Follows the open positions of well-performing Hyperliquid wallets and sends Telegram alerts when they trade. It is an observer: it never places an order and has no trading logic. **It is not investment advice, and nothing here shows that following these wallets makes money.**

## What it does

- Every run (`--smart-money-poll`) picks up to 25 wallets from the public Hyperliquid leaderboard (refreshed every 24 hours; filters in `appsettings.json`), then reads each wallet's recent fills, open positions and stop orders.
- New fills, new positions and positions that disappeared are stored as events in a SQLite file (`smart_money.db`, kept on the `data/smart-money` branch).
- Each run also scores every polled wallet from its recent closed trades (smoothed win rate, profit factor that counts open losses, minimum trades and average win) and stores the score in the `wallet_track_records` table of the same database. It is only recorded; no alert or decision uses it, and nothing shows yet that it predicts anything.
- A Telegram alert is sent when a move is large enough (`MinWhalePositionAlertUsd`, default 500,000 USD). Fills seen in one run are merged per wallet, coin and direction, so many small fills of one position are one alert. Each alert shows the wallet's position now (size, entry, market price, unrealized PnL, leverage, liquidation price, account value, stop orders) and ends with a short analysis block in Persian.
- A Cloudflare Worker answers the "whale status" button of the bot at once from a table the poller stores in Workers KV (see [docs/cloudflare-worker.md](docs/cloudflare-worker.md)).

`--telegram-sample` sends three clearly labelled sample alerts so the format can be checked in the app.

## Setup

1. Create a Telegram bot (BotFather) and note the bot token and your chat id.
2. Repository secrets (Settings, Secrets and variables, Actions):

   | Secret | Needed for |
   |---|---|
   | `TELEGRAM_BOT_TOKEN`, `TELEGRAM_CHAT_ID` | alerts |
   | `TELEGRAM_DISPLAY_TIMEZONE` (optional, for example `Europe/Berlin`) | times in alerts are shown in UTC and in this zone; default UTC |
   | `CLOUDFLARE_API_TOKEN`, `CLOUDFLARE_ACCOUNT_ID`, `CLOUDFLARE_KV_NAMESPACE_ID` | the Worker and the table in KV (optional) |
   | `WHALE_WEBHOOK_SECRET` | link between Telegram and the Worker (optional) |

   Without the Telegram secrets the poller still collects data and sends nothing.
3. The workflow `Smart Money Poller` runs only from `main`. GitHub's own `schedule` trigger is unreliable (runs are delayed or dropped), so use an external timer that calls the API:

   ```
   POST https://api.github.com/repos/<owner>/<repo>/actions/workflows/smart-money-poll.yml/dispatches
   Authorization: Bearer <fine-grained token: this repository only, Actions: Read and write>
   Accept: application/vnd.github+json
   Content-Type: application/json
   {"ref":"main"}
   ```
   A successful call answers HTTP 204. The workflow keeps a fallback cron every 6 hours.
4. Optional: the Cloudflare Worker, see [docs/cloudflare-worker.md](docs/cloudflare-worker.md).

A run takes about one minute. This repository is public, so GitHub-hosted runner minutes are free; in a private repository a run every 5 minutes would use up the free allowance in about a week.

## Workflows

| Workflow | Purpose |
|---|---|
| `CI` | build and test the .NET project and the Worker |
| `Smart Money Poller` | one poll, then saves the database to the `data/smart-money` branch |
| `Telegram Sample Message` | three sample alerts (main only, manual) |
| `Deploy Whale Bot Worker` | tests and deploys the Worker (main only, manual) |
| `Telegram Webhook` | `set`, `info` or `delete` the bot's webhook (main only, manual) |

## Develop

```
dotnet build WhaleWatch.sln
dotnet test WhaleWatch.sln
cd cloudflare/whale-bot && npm ci && node --test
```

## Public by design

Logs of every run, the `data/smart-money` branch and the code are public. The wallet addresses and their trades are public on-chain data. Secrets live only in GitHub Actions secrets; pull requests from forks cannot read them. Do not commit a token, a chat id or a local settings file.
