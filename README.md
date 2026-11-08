# MyBot

A .NET 10 console app that buys USDT with INR on WazirX, then withdraws that purchase to a configured receiving exchange. The two money actions require separate interactive confirmations. Live actions are disabled by default.

The initial implementation used the [official WazirX REST documentation](https://docs.wazirx.com/). A later user-approved read-only diagnosis used local credentials to inspect the server-time and coin-information response structure, revealing a newer coin metadata format. The parser now supports both formats; see [API evidence](docs/API-CONTRACT.md). No trades or withdrawals were submitted during development, and the edited parser has not been built or run.

## What is included

- Read-only discovery of USDT networks, fees, and saved withdrawal address-book IDs.
- Read-only purchase preview using current price, account balances, withdrawal availability, and configured receiving-network support.
- One limit buy, bounded by your limit price and the planned INR allowance.
- Cheapest eligible network among your configured routes, selected before buying and pinned to that operation.
- Actual fill and USDT trading-fee accounting before a separately confirmed withdrawal.
- SQLite intent journal, stable operation IDs, and a process lock to guard against duplicate money requests after interruptions.
- Offline checks using fake exchange responses and temporary local journals. These checks do not send exchange requests.

This version has no dashboard, recurring schedule, unattended withdrawals, automatic retries of money requests, or receiving-exchange API integration. You verify the destination deposit yourself. It supports network-specific saved addresses; memo-required and universal address-book entries are refused because this implementation cannot independently verify their routing details.

## 30-minute RSI(10) trading

The separate `strategy` command trades USDT/INR on WazirX using Wilder RSI(10). It buys while flat when the latest completed 30-minute candle has RSI strictly below 30, and sells its own confirmed holdings when RSI is strictly above 70. RSI equal to either threshold holds. It requires at least 250 contiguous completed candles and excludes the current incomplete candle. There are no additional profit targets, stop-losses, holding deadlines, or withdrawals in this strategy.

Paper mode is the default and requires no API credentials. It polls every 30 seconds, processes each completed candle once, and uses the current ask for simulated buys and current bid for simulated sells. These are estimates of execution, not guaranteed fills or verified profits. Paper calculations assume zero trading fees; live inventory uses the fees actually reported by the exchange. Paper and live state are separate records in the existing durable SQLite journal and survive restarts.

After manually building and running the offline checks, inspect one paper evaluation or leave paper trading running:

```powershell
dotnet run --project .\MyBot.csproj -- strategy --once
dotnet run --project .\MyBot.csproj -- strategy
dotnet run --project .\MyBot.csproj -- strategy-status
```

The existing `Bot:MaxSpendInr` is the maximum budget per purchase, and `Bot:MaxPriceInr` caps buy prices only. `Bot:FeeReservePercent` retains spending headroom; it is not a claim that the exchange charges that fee. The existing minimum-order and exchange precision/quantity restrictions still apply. No settings file has been changed and live trading remains disabled.

For live trading, configure the existing global live flag yourself only after reviewing the implementation and paper behavior. These commands are provided for manual use; they were not executed during development:

```powershell
dotnet run --project .\MyBot.csproj -- strategy --live
dotnet run --project .\MyBot.csproj -- strategy-status --live
dotnet run --project .\MyBot.csproj -- strategy-cancel --live
```

Live startup requires `EnableLiveTrading=true`, `--live`, and an interactive confirmation authorizing the trading loop. `--once` can also be supplied to perform one live evaluation after confirmation. Buy limits use the current ask rounded up to the price tick; sell limits use the current bid rounded down. A limit price does not guarantee a fill. Pending orders are reconciled before another signal can place an order, and remain pending until filled or explicitly canceled; the strategy does not automatically replace them after an RSI change. Stopping the process does not cancel an exchange order.

Each money intent is saved before submission. A timeout or uncertain response leaves the saved client order ID pending; subsequent evaluations query that ID and never resubmit it. An uncertain cancellation is also reconciled without repeating the cancellation request. Use `strategy-status --live` to recover a strategy operation rather than the transfer workflow's `status <id>` command. Unrecovered API, storage, or inconsistent-data errors stop the loop for review.

The strategy sells only quantities confirmed from its own purchases, accounting for reported USDT fees and protecting the starting USDT balance. Do not make other trades, deposits, withdrawals, or use other bots on the account while it runs. Unexpected USDT fees on a sell can require manual reconciliation. Residual quantities below the exchange's step or minimum order remain owned dust and block a fresh purchase. Live strategy holdings or pending orders block the older purchase/transfer money commands. The shared process lock prevents simultaneous local app instances.

This implementation has not been built, run, or tested. Historical candle simulations do not establish executable profit, and RSI can hold a losing position for an extended period.

## Setup

Install the .NET 10 SDK and its ASP.NET Core shared runtime. The application uses the shared framework for configuration and User Secrets, plus the pinned `Microsoft.Data.Sqlite` 10.0.14 package. There is no web server.

Run these commands yourself from `D:\MyBot`; they have not been executed during development:

```powershell
Copy-Item .\botsettings.example.json .\botsettings.local.json
dotnet user-secrets set "WazirX:ApiKey" "YOUR_API_KEY" --project .\MyBot.csproj
dotnet user-secrets set "WazirX:ApiSecret" "YOUR_API_SECRET" --project .\MyBot.csproj
dotnet build .\MyBot.csproj
dotnet run --project .\tests\MyBot.Checks\MyBot.Checks.csproj
```

Replace credential placeholders locally. Do not send keys in chat or put them in the settings JSON. User Secrets keeps them outside the repository; it is not an encrypted vault. Environment variables `MYBOT_WazirX__ApiKey` and `MYBOT_WazirX__ApiSecret` are also supported. Settings load first, User Secrets second, and `MYBOT_` environment variables last.

Your key needs account-read access for preview, spot-trade permission for buying, and Crypto Withdrawal permission for transferring. Restrict the API key to your machine's public IP if your setup permits it.

## Configure your destination and limits

First save your receiving exchange's USDT deposit address in your WazirX withdrawal address book. Complete the exchange's address verification and any cooldown there. Then inspect the read-only API data:

```powershell
dotnet run --project .\MyBot.csproj -- routes
```

In `botsettings.local.json`, fill every placeholder using values you verified:

| Setting | Meaning |
| --- | --- |
| `MaxSpendInr` | Upper bound for the planned order cost plus your configured fee allowance. No automatic budget increase. |
| `MaxPriceInr` | Maximum INR price per USDT. Rounded down to the exchange price tick. |
| `FeeReservePercent` | Your allowance for trading fees and other applicable charges. Used both as INR spending headroom and to reduce estimated acquired USDT for preview minimum checks. Default 2 is a configurable allowance, not a verified fee rate. Check that it covers your account's charges. |
| `VerifiedMinOrderInr` | Current minimum order value verified on WazirX. The docs do not guarantee that the exchange-info response supplies this minimum. |
| `MaxWithdrawalFeeUsdt` | Highest withdrawal fee you are willing to pay; must be positive unless `UseExchangeWithdrawalFee` is true. |
| `UseExchangeWithdrawalFee` | Defaults to false. When true, use the network's current WazirX API fee without a separate fee cap. A zero `MaxWithdrawalFeeUsdt` is then permitted and ignored. Fee reserves, minimum checks, and interactive confirmation still apply; a changed fee during confirmation stops submission. |
| `DestinationLabel` | Name you use to recognize your own receiving exchange account. |
| `Routes[].Network` | Exact WazirX network ID, not a guessed alias such as a token standard. |
| `Routes[].AddressBookId` | Saved WazirX entry ID reported by `routes`. |
| `Routes[].Address` | Complete USDT deposit address from your receiving exchange. Must exactly match the saved entry. |
| `Routes[].MinimumDepositUsdt` | Current receiving exchange's USDT deposit minimum on that network. |
| `Routes[].DepositEnabledConfirmed` | Set true only after checking that the receiving exchange currently accepts USDT on this network. |
| `EnableLiveTrading` | Leave false for setup and preview. Set true only when ready for the live trial. |

Configure multiple routes if appropriate, each with its own network-specific saved entry and minimum. An address format alone does not establish network compatibility. The bot checks the WazirX side, while you supply the receiving exchange's enabled networks and minimums. Do not guess these values.

The limit order's price and quantity cap its purchase notional. The configured fee allowance is not an exchange-enforced spending cap: separately charged fees or taxes can exceed an incorrect allowance. The bot checks reported fills and actual available USDT again before withdrawing.

## First small trial

1. Keep `EnableLiveTrading` false and use `preview`. It performs authenticated read-only calls and sends no order or withdrawal.
2. Set a small budget that passes the buy minimum, withdrawal minimum, withdrawal fee reserve, and receiving deposit minimum. The bot refuses an insufficient amount; it does not silently raise your budget.
3. After inspecting the offline checks and preview, enable live mode locally. Run `buy --live` and review/type its exact confirmation phrase. The program saves the operation ID before sending the order.
4. Use `status <id>` until the order is filled. This command performs no buy, sell, cancellation, or withdrawal. If the order remains open, you may explicitly cancel it with `cancel <id> --live`; a partially filled cancellation may leave a purchase too small to withdraw.
5. Run `withdraw <id> --live`. Review the exact address, pinned network, amount, fee, and WazirX consent text, then type the confirmation phrase. The program refreshes route/balance conditions before submitting.
6. Use `status <id>` to view WazirX's raw withdrawal status and transaction ID. Independently verify receipt in the receiving exchange's deposit history.
7. Only after that verification, run `confirm-deposit <id> --live` to record your manual confirmation and permit another purchase.

Manual command examples (replace `<id>` with the saved operation ID; do not type angle brackets):

```powershell
dotnet run --project .\MyBot.csproj -- preview
dotnet run --project .\MyBot.csproj -- buy --live
dotnet run --project .\MyBot.csproj -- list
dotnet run --project .\MyBot.csproj -- status <id>
dotnet run --project .\MyBot.csproj -- withdraw <id> --live
dotnet run --project .\MyBot.csproj -- confirm-deposit <id> --live
```

Every command accepts `--settings <path>`. The journal location stays the same even when this path changes. Live commands require an interactive terminal and reject redirected confirmation input. Each confirmation expires after two minutes; changed preflight conditions stop the action for another review.

## Fee calculation and transaction recovery

The docs provide a withdrawal fee but do not specify whether it is added to the debit or deducted from the withdrawal amount. This implementation reserves the fee from newly acquired USDT before computing the request, then also subtracts it for the conservative receive estimate. It checks the exchange withdrawal minimum/maximum/step and the receiving minimum. This may leave some USDT on WazirX; the preview is deliberately not a promise of the precise receiving amount.

Only fills belonging to the saved buy order count toward the withdrawal. USDT trading fees are subtracted. Available withdrawal funds are additionally capped at the increase over your initial free USDT balance, so the program does not intentionally sweep pre-existing holdings. Avoid other trades, deposits, and withdrawals on this account during the trial. Such activity can change fee/fund availability and cause the bot to stop.

The SQLite journal lives at `<LocalApplicationData>/MyBot/WazirX/mybot.db` (normally `%LOCALAPPDATA%\MyBot\WazirX\mybot.db` on Windows), independent of build output, working directory, or settings path. It contains operational IDs and destination addresses, not API keys. It is shared by this OS user's app instances. Use one WazirX account, one machine, and this journal for the trial; this is not a distributed account lock. Preserve the database together with any WAL files. Do not delete it, use a fresh profile, or copy the app to another machine to bypass an unresolved operation.

An HTTP error, timeout, cancellation, process crash, or unexpected reply after submitting an intent can leave the outcome unknown. `list` recovers the operation ID; `status` queries the original client ID. **The bot does not resubmit an uncertain buy or withdrawal.** If the original request cannot be found, the operation stays blocked until it is independently reconciled with WazirX. This version intentionally has no command that erases an uncertain intent or declares it not submitted.

Numeric withdrawal statuses are shown as returned, without inventing success/failure mappings. Matching history is not proof of receiving-exchange credit. History must match the saved route and ID, with an amount matching the request or request minus the saved fee; other shapes require manual reconciliation.

Missing withdrawal consent, fee/limit fields, required address metadata, or withdrawal precision stops the operation. A new/unknown trading filter also stops it. At 1,000 returned trade records, the client refuses to assume that fill history is complete. The initial small trial is the intended scope.

The client spaces requests within a command and never automatically retries money requests. A GET rejected with HTTP 429 is retried once after the exchange's `Retry-After` cooldown, or after 30 seconds when no cooldown is supplied. Ctrl+C can cancel that wait. Signed reads synchronize the clock and generate a new timestamp/signature for the retry; the cooldown is excluded from clock round-trip measurement. POST and DELETE requests, including their clock preflight, are never automatically retried. HTTP 418 and a repeated HTTP 429 stop the command. HTTP errors identify the method, endpoint path and supplied cooldown without logging request parameters or response bodies. Rate limits also apply across other programs and CLI invocations; observe any remaining cooldown before restarting and do not poll `status` in a tight loop.

## Development status

Source was reviewed by multiple agents against the documentation. The offline check sources cover planning, storage, request signing, fake exchange errors, and transaction workflow behavior. They have not been compiled or executed. There has been no live-account or small-amount validation, and this is not a production-readiness claim.

Build and tests were not run, according to the global instructions.
