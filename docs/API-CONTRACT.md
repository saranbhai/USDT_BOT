# API evidence and implementation boundaries

Sources inspected on 2026-11-07: [official WazirX REST documentation](https://docs.wazirx.com/) and the official web client's withdrawal source. The initial implementation used documentation only. A later user-approved read-only diagnosis called the server-time and coin-information endpoints using local credentials and inspected response structure. No order, withdrawal, balance, or address-book request was made by that diagnosis, and no keys or raw responses were printed or saved.

| Operation | Documented endpoint | Implementation |
| --- | --- | --- |
| Server clock | `GET /sapi/v1/time` | Synchronize timestamps for signing. |
| Spot restrictions | `GET /sapi/v1/exchangeInfo` | USDT/INR spot limit support, precision, and supported filters. |
| Ask price | `GET /sapi/v1/depth` | Reject a purchase if the ask exceeds the configured limit. |
| Balance | `GET /sapi/v1/funds` | Read free INR/USDT; reject duplicate asset entries. |
| Network information | `GET /sapi/v1/coins` | Read USDT withdrawal availability, fees, limits, precision, and exact consent. |
| Saved destinations | `GET /sapi/v1/crypto/withdraw/address-book` | Match configured ID, currency, network, complete address, allowed flag, and cooldown. |
| Buy | `POST /sapi/v1/order` | Explicit `buy` / `limit`, capped quantity/price, saved client ID. |
| Order status | `GET /sapi/v1/order` | Query the original client ID after interruptions. |
| Cancel | `DELETE /sapi/v1/order` | Explicit cancellation only; query order and fills afterward. |
| Fills | `GET /sapi/v1/myTrades` | Filter by order ID/symbol and reconcile quantity and reported USDT fees. |
| Withdraw | `POST /sapi/v1/crypto/withdraw` | Saved address-book ID, amount, API-supplied consent, remarks, saved client withdrawal ID. |
| Withdrawal history | `GET /sapi/v1/crypto/withdraws` | Query original withdrawal client ID; preserve raw status. |

The RSI strategy additionally uses the public `GET /sapi/v1/klines` endpoint with `symbol=usdtinr`, `interval=30m`, and `limit=1000`. Spot candle rows contain six fields: opening timestamp in seconds, open, high, low, close, and base volume. Public clock and bid/ask depth requests are unsigned. Only completed, current, contiguous candle history can generate a strategy signal.

Strategy sell orders use the existing signed limit-order endpoint with explicit `side=sell`. `IStrategyExchangeClient` adds buy/sell reconciliation without widening the legacy purchase-only order and fill contracts. The strategy journals its pending client ID and fill-accounting progress in optional JSON payload fields of the existing operations table; no database schema migration or new dependency is required. Paper mode has its own journal record and never calls order endpoints.

Authentication follows the documented `X-API-KEY` header, HMAC-SHA256 signature, millisecond timestamp, and 5,000 ms receive window. The signature covers the exact encoded payload sent. GET uses query parameters; POST and DELETE use form bodies. The production HTTP factory fixes the WazirX host and disables redirects.

The [official rate-limit documentation](https://docs.wazirx.com/#limits) identifies HTTP 429 as rate limiting and defines `Retry-After` in seconds. Error code 2136 means too many API requests. The client permits one automatic retry of an outer GET request after the cooldown (30-second fallback), rebuilding signed requests with a fresh synchronized clock. Clock-preflight failures for POST/DELETE never trigger a retry or a money request. HTTP 418 and persistent rate limits remain terminal. Synthetic regression checks cover these boundaries, cancellation and sanitized endpoint/cooldown diagnostics; those checks have not been executed during this change.

The withdrawal documentation requires `networkList.withdrawConsent.message` exactly as returned. Its illustrative coins response omits that field; the implementation refuses a missing value instead of copying sample consent into code. Withdrawal increments come from network metadata, never from the trading pair's precision.

## Observed coin-information format

The approved diagnosis found an array of entries identified by `currency`, with USDT network limits named `minWithdrawAmount` and `maxWithdrawAmount`, plus an integer `precision`. The parser supports these fields alongside the documented `coin`, `withdrawMin`, `withdrawMax`, and `withdrawIntegerMultiple` fields. If both naming variants are supplied, conflicting identifiers, limits, or increments are rejected.

The [official funds/withdrawal UI](https://wazirx.com/static/index-JsnB3YU_.js) passes the network's `precision` into its amount input and derives memo handling from the optional `memo.regex` property. Its [shared step helper](https://wazirx.com/static/app-DvbqZt8E.js) calculates the amount step as `1 / 10^precision`. The parser therefore converts integer decimal precision in the range 0 through 28 into an exact decimal increment. It reads the optional memo object only for the observed current network layout; the legacy layout still requires its explicit `memoRequired` flag. Memo/tag networks remain unsupported for automated withdrawal.

This diagnosis established schema compatibility, not successful execution of the edited bot. The updated parser and its synthetic regression cases were not built or run. Live address-book compatibility and end-to-end delivery remain unverified.

Account availability, fee charging semantics, receiving-exchange compatibility, actual trade charges, numeric withdrawal status meanings, and end-to-end delivery are not established by the static docs. See the README for conservative fee handling and manual destination verification.

Dependency pinned to the verified package listing: [Microsoft.Data.Sqlite 10.0.14](https://www.nuget.org/packages/Microsoft.Data.Sqlite/10.0.14), which declares SQLitePCLRaw 2.1.13 or newer dependencies. Package resolution and native SQLite runtime verification remain pending a manual restore/build.
