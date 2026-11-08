using System.Globalization;
using MyBot.Core;

namespace MyBot.Application;

public sealed class RsiStrategyWorkflow(BotOptions options, IWazirXClient client,
    IStrategyExchangeClient market, IOperationStore store, TextWriter output,
    Func<string, bool> confirm)
{
    private bool liveSessionConfirmed;
    private decimal confirmedBudget;
    private decimal confirmedPriceCap;
    private decimal confirmedFeeReserve;

    public async Task RunAsync(bool live, bool once, CancellationToken cancellationToken)
    {
        ValidateOptions();
        if (live)
        {
            AuthorizeLive();
        }
        else
        {
            output.WriteLine("Paper strategy: 30-minute Wilder RSI(10), buy below 30, sell above 70. Bid/ask fills are simulated with zero fees; no orders or withdrawals are submitted.");
        }

        do
        {
            await TickAsync(live, cancellationToken);
            if (once)
                return;
            await Task.Delay(TimeSpan.FromSeconds(30), cancellationToken);
        }
        while (!cancellationToken.IsCancellationRequested);
    }

    public async Task StatusAsync(bool live, CancellationToken cancellationToken)
    {
        var operation = FindState(live);
        if (operation is null)
        {
            output.WriteLine($"No {(live ? "live" : "paper")} strategy journal exists.");
            return;
        }

        if (live)
            await ReconcileAsync(operation, cancellationToken);
        PrintState(operation);
    }

    public async Task CancelAsync(bool live, CancellationToken cancellationToken)
    {
        if (!live)
            throw new InvalidOperationException("Paper trading has no exchange order to cancel.");
        RequireLiveEnabled();
        var operation = FindState(true)
            ?? throw new InvalidOperationException("No live strategy journal exists.");
        await ReconcileAsync(operation, cancellationToken);
        var pending = operation.Strategy!.Pending
            ?? throw new InvalidOperationException("There is no pending strategy order to cancel.");
        if (pending.CancelRequested)
            throw new InvalidOperationException("A cancellation was already requested. Use strategy-status --live; cancellation will not be resubmitted.");
        var shownAt = DateTimeOffset.UtcNow;
        RequireConfirmation($"CANCEL STRATEGY {pending.ClientId}");
        RequireFreshConfirmation(shownAt);
        pending.CancelRequested = true;
        Save(operation);
        var receipt = await market.CancelStrategyOrderAsync(pending.ExchangeOrderId!.Value, cancellationToken);
        ValidateOrder(pending, receipt);
        await ReconcileAsync(operation, cancellationToken);
        PrintState(operation);
    }

    public async Task TickAsync(bool live, CancellationToken cancellationToken)
    {
        ValidateOptions();
        if (live)
            AuthorizeLive();
        var operation = await GetOrCreateStateAsync(live, cancellationToken);
        var state = operation.Strategy!;
        if (live)
        {
            await ReconcileAsync(operation, cancellationToken);
            EnsureNoOtherOperation(operation.Id);
        }
        if (state.Pending is not null)
        {
            output.WriteLine($"Order {state.Pending.ClientId} remains pending; no replacement or additional order will be submitted.");
            return;
        }

        var candles = await market.GetCandlesAsync(cancellationToken);
        var serverTime = await market.GetServerTimeAsync(cancellationToken);
        var evaluation = RsiStrategy.Evaluate(candles, serverTime, state.OwnedUsdt > 0);
        if (state.LastCandle > evaluation.CandleOpenTime)
            throw new InvalidOperationException("The strategy candle precedes its saved checkpoint. No new order is allowed.");
        if (state.LastCandle == evaluation.CandleOpenTime)
            return;

        output.WriteLine($"{evaluation.CandleOpenTime:O}: completed 30-minute RSI(10)={Number(evaluation.Rsi)}; signal={evaluation.Signal}.");
        if (evaluation.Signal == RsiSignal.Hold)
        {
            state.LastCandle = evaluation.CandleOpenTime;
            Save(operation);
            return;
        }

        var symbol = await client.GetSymbolAsync(cancellationToken);
        ValidateSymbol(symbol);
        var quote = await market.GetQuoteAsync(cancellationToken);
        if (quote.Bid <= 0 || quote.Ask <= 0 || quote.Bid > quote.Ask)
            throw new InvalidOperationException("The strategy requires a valid bid/ask quote.");
        var side = evaluation.Signal == RsiSignal.Buy ? "buy" : "sell";
        var price = side == "buy" ? RoundUp(quote.Ask, symbol.TickSize) : TransferPlanner.RoundDown(quote.Bid, symbol.TickSize);
        IReadOnlyList<Balance> balances = live ? await client.GetBalancesAsync(cancellationToken) : [];
        var available = side == "buy"
            ? (live ? FreeBalance(balances, "inr", true) : state.PaperCashInr)
            : (live ? Math.Min(state.OwnedUsdt, Math.Max(0, FreeBalance(balances, "usdt", false) - state.InitialUsdtBalance)) : state.OwnedUsdt);
        var step = QuantityStep(symbol);
        var quantity = side == "buy"
            ? TransferPlanner.RoundDown(Math.Min(options.MaxSpendInr, available) / (1m + options.FeeReservePercent / 100m) / price, step)
            : TransferPlanner.RoundDown(available, step);
        state.LastCandle = evaluation.CandleOpenTime;
        if (price < symbol.MinimumPrice || symbol.MaximumPrice.HasValue && price > symbol.MaximumPrice.Value ||
            side == "buy" && price > options.MaxPriceInr || quantity <= 0 ||
            symbol.MinimumQuantity.HasValue && quantity < symbol.MinimumQuantity.Value ||
            symbol.MaximumQuantity.HasValue && quantity > symbol.MaximumQuantity.Value ||
            quantity * price < Math.Max(options.VerifiedMinOrderInr, symbol.MinimumNotional ?? 0m))
        {
            Save(operation);
            output.WriteLine("Signal skipped: the price cap, available strategy balance or exchange order limits do not permit this order. Any owned dust remains part of the position.");
            return;
        }

        if (!live)
        {
            var value = quantity * price;
            state.PaperCashInr += side == "buy" ? -value : value;
            state.OwnedUsdt += side == "buy" ? quantity : -quantity;
            Save(operation);
            output.WriteLine($"PAPER {side}: {Number(quantity)} USDT at {Number(price)} INR, zero simulated fees. Full fills are hypothetical.");
            PrintState(operation);
            return;
        }

        var pending = new PendingStrategyOrder
        {
            ClientId = "r" + Guid.NewGuid().ToString("N"),
            Side = side,
            Quantity = quantity,
            Price = price
        };
        state.Pending = pending;
        Save(operation);
        output.WriteLine($"Strategy order intent {pending.ClientId} saved before submission: {side} {Number(quantity)} USDT at {Number(price)} INR.");
        var order = side == "buy"
            ? await client.PlaceBuyAsync(pending.ClientId, quantity, price, cancellationToken)
            : await market.PlaceSellAsync(pending.ClientId, quantity, price, cancellationToken);
        ValidateOrder(pending, order);
        pending.ExchangeOrderId = order.Id;
        Save(operation);
        output.WriteLine("Order acknowledged. Its fills will be reconciled before any further order. Unknown submissions are never automatically repeated.");
    }

    private async Task<OperationRecord> GetOrCreateStateAsync(bool live, CancellationToken cancellationToken)
    {
        var existing = FindState(live);
        if (existing is not null)
            return existing;
        if (live)
            EnsureNoOtherOperation(null);
        var baseline = live ? FreeBalance(await client.GetBalancesAsync(cancellationToken), "usdt", false) : 0m;
        var operation = new OperationRecord
        {
            Id = Guid.NewGuid().ToString("N"),
            CreatedAt = DateTimeOffset.UtcNow,
            Stage = live ? OperationStage.StrategyFlat : OperationStage.StrategyPaper,
            Strategy = new StrategyState
            {
                Paper = !live,
                InitialUsdtBalance = baseline,
                PaperCashInr = live ? 0m : options.MaxSpendInr
            }
        };
        ValidateState(operation);
        store.Insert(operation);
        output.WriteLine($"{(live ? "Live" : "Paper")} strategy journal: {operation.Id}. Keep the journal across restarts.");
        return operation;
    }

    private OperationRecord? FindState(bool live)
    {
        var records = store.List();
        foreach (var operation in records.Where(operation => operation.Strategy is not null || IsStrategyStage(operation.Stage)))
            ValidateState(operation);
        var matches = records.Where(operation => operation.Strategy?.Paper == !live).ToArray();
        if (matches.Length > 1)
            throw new InvalidOperationException("Multiple strategy journals exist for this mode. Reconcile them before continuing.");
        return matches.SingleOrDefault();
    }

    private async Task ReconcileAsync(OperationRecord operation, CancellationToken cancellationToken)
    {
        var state = operation.Strategy!;
        var pending = state.Pending;
        if (pending is null)
            return;
        var order = await market.GetStrategyOrderAsync(pending.ClientId, cancellationToken);
        ValidateOrder(pending, order);
        var terminal = order.Status switch
        {
            "done" when order.ExecutedQuantity == pending.Quantity => true,
            "cancel" => true,
            "wait" or "idle" => false,
            _ => throw new InvalidOperationException("Unexpected strategy order state. Reconcile at WazirX before continuing.")
        };
        var trades = await market.GetStrategyTradesAsync(order.Id, pending.Side, cancellationToken);
        if (trades.Any(trade => trade.OrderId != order.Id || !trade.Symbol.Equals("usdtinr", StringComparison.OrdinalIgnoreCase) ||
                trade.Side != pending.Side || trade.Quantity <= 0 || trade.Fee < 0 || string.IsNullOrWhiteSpace(trade.FeeCurrency)) ||
            trades.Select(trade => trade.Id).Distinct().Count() != trades.Count ||
            trades.Sum(trade => trade.Quantity) != order.ExecutedQuantity)
            throw new InvalidOperationException("Strategy trade fills are incomplete or inconsistent. No further order is permitted.");
        var fees = trades.Where(trade => trade.FeeCurrency.Equals("usdt", StringComparison.OrdinalIgnoreCase)).Sum(trade => trade.Fee);
        if (order.ExecutedQuantity < pending.AccountedExecuted || fees < pending.AccountedUsdtFees)
            throw new InvalidOperationException("Strategy fills or fees moved backwards. Reconcile before continuing.");
        var delta = order.ExecutedQuantity - pending.AccountedExecuted;
        var feeDelta = fees - pending.AccountedUsdtFees;
        var owned = state.OwnedUsdt + (pending.Side == "buy" ? delta - feeDelta : -delta - feeDelta);
        if (owned < 0)
            throw new InvalidOperationException("Reported strategy USDT fees or sells exceed its acquired position. Reconcile before continuing.");
        state.OwnedUsdt = owned;
        pending.ExchangeOrderId = order.Id;
        pending.AccountedExecuted = order.ExecutedQuantity;
        pending.AccountedUsdtFees = fees;
        if (terminal)
            state.Pending = null;
        Save(operation);
        output.WriteLine($"Strategy order {order.Id}: {order.Status}; confirmed fills={Number(order.ExecutedQuantity)} USDT, reported USDT fees={Number(fees)}; owned={Number(state.OwnedUsdt)} USDT.");
    }

    private void EnsureNoOtherOperation(string? id)
    {
        var active = store.List().FirstOrDefault(operation => operation.Id != id &&
            operation.Stage is not (OperationStage.DepositConfirmed or OperationStage.StrategyPaper or OperationStage.StrategyFlat) &&
            !(operation.Stage == OperationStage.BuyCanceled && operation.AcquiredUsdt == 0));
        if (active is not null)
            throw new InvalidOperationException($"Operation {active.Id} is unresolved. Finish or reconcile it before running the live strategy.");
    }

    private void Save(OperationRecord operation)
    {
        var state = operation.Strategy!;
        operation.Stage = state.Paper ? OperationStage.StrategyPaper :
            state.Pending is not null || state.OwnedUsdt > 0 ? OperationStage.StrategyOpen : OperationStage.StrategyFlat;
        ValidateState(operation);
        store.Save(operation);
    }

    private static void ValidateState(OperationRecord operation)
    {
        var state = operation.Strategy;
        if (state is null || !IsStrategyStage(operation.Stage) ||
            state.InitialUsdtBalance < 0 || state.OwnedUsdt < 0 || state.PaperCashInr < 0 ||
            state.LastCandle.HasValue && state.LastCandle.Value.UtcTicks % RsiStrategy.Interval.Ticks != 0 ||
            state.Paper && (operation.Stage != OperationStage.StrategyPaper || state.Pending is not null) ||
            !state.Paper && (state.PaperCashInr != 0 || operation.Stage !=
                (state.Pending is not null || state.OwnedUsdt > 0 ? OperationStage.StrategyOpen : OperationStage.StrategyFlat)))
            throw new InvalidDataException("The strategy journal contains invalid state. Restore or reconcile it before continuing.");
        var pending = state.Pending;
        if (pending is not null && (string.IsNullOrWhiteSpace(pending.ClientId) || pending.Side is not ("buy" or "sell") ||
            pending.Quantity <= 0 || pending.Price <= 0 || pending.ExchangeOrderId is <= 0 ||
            pending.AccountedExecuted < 0 || pending.AccountedExecuted > pending.Quantity || pending.AccountedUsdtFees < 0 ||
            pending.Side == "buy" && pending.AccountedUsdtFees > pending.AccountedExecuted))
            throw new InvalidDataException("The strategy journal contains an invalid pending order. Reconcile it before continuing.");
    }

    private static bool IsStrategyStage(OperationStage stage) =>
        stage is OperationStage.StrategyPaper or OperationStage.StrategyFlat or OperationStage.StrategyOpen;

    private static void ValidateOrder(PendingStrategyOrder pending, OrderInfo order)
    {
        if (order.Id <= 0 || order.ClientOrderId != pending.ClientId || !order.Symbol.Equals("usdtinr", StringComparison.OrdinalIgnoreCase) ||
            order.Side != pending.Side || order.Type != "limit" || order.Price != pending.Price ||
            order.OriginalQuantity != pending.Quantity || order.ExecutedQuantity < 0 || order.ExecutedQuantity > pending.Quantity ||
            pending.ExchangeOrderId.HasValue && pending.ExchangeOrderId.Value != order.Id)
            throw new InvalidOperationException("The exchange order does not match the journaled strategy intent. No further order is permitted.");
    }

    private void ValidateOptions()
    {
        if (options.MaxSpendInr <= 0 || options.MaxPriceInr <= 0 || options.VerifiedMinOrderInr <= 0 ||
            options.FeeReservePercent < 0 || options.FeeReservePercent > 25)
            throw new InvalidOperationException("The strategy requires positive INR budget, buy price cap and verified minimum order, with FeeReservePercent between zero and 25.");
    }

    private static void ValidateSymbol(SymbolInfo symbol)
    {
        if (!symbol.Symbol.Equals("usdtinr", StringComparison.OrdinalIgnoreCase) ||
            !symbol.BaseAsset.Equals("usdt", StringComparison.OrdinalIgnoreCase) ||
            !symbol.QuoteAsset.Equals("inr", StringComparison.OrdinalIgnoreCase) ||
            !symbol.Status.Equals("trading", StringComparison.OrdinalIgnoreCase) || !symbol.SpotAllowed || !symbol.LimitAllowed ||
            symbol.TickSize <= 0 || symbol.MinimumPrice < 0 || symbol.MaximumPrice is <= 0 ||
            symbol.MaximumPrice.HasValue && symbol.MaximumPrice.Value < symbol.MinimumPrice ||
            symbol.BasePrecision is < 0 or > 28 || symbol.QuantityStep is <= 0 ||
            symbol.MinimumQuantity is < 0 || symbol.MaximumQuantity is <= 0 || symbol.MinimumNotional is < 0 ||
            symbol.MaximumQuantity.HasValue && symbol.MinimumQuantity.HasValue && symbol.MaximumQuantity < symbol.MinimumQuantity)
            throw new InvalidOperationException("USDT/INR spot limit trading metadata is unavailable or inconsistent.");
        _ = QuantityStep(symbol);
    }

    private static decimal QuantityStep(SymbolInfo symbol)
    {
        var precisionStep = 1m;
        for (var digit = 0; digit < symbol.BasePrecision; digit++)
            precisionStep /= 10m;
        var step = symbol.QuantityStep ?? precisionStep;
        if (step <= 0 || step % precisionStep != 0)
            throw new InvalidOperationException("The strategy quantity step is incompatible with asset precision.");
        return step;
    }

    private static decimal FreeBalance(IReadOnlyList<Balance> balances, string asset, bool required)
    {
        var matches = balances.Where(balance => balance.Asset.Equals(asset, StringComparison.OrdinalIgnoreCase)).ToArray();
        if (matches.Length > 1 || matches.Any(balance => balance.Free < 0) || required && matches.Length != 1)
            throw new InvalidOperationException($"The strategy requires one nonnegative {asset.ToUpperInvariant()} balance.");
        return matches.SingleOrDefault()?.Free ?? 0m;
    }

    private static decimal RoundUp(decimal value, decimal step)
    {
        var down = TransferPlanner.RoundDown(value, step);
        return down == value ? value : down + step;
    }

    private void RequireLiveEnabled()
    {
        if (!options.EnableLiveTrading)
            throw new InvalidOperationException("Live strategy trading is disabled. EnableLiveTrading must be true as well as supplying --live.");
    }

    private void AuthorizeLive()
    {
        RequireLiveEnabled();
        if (liveSessionConfirmed)
        {
            if (confirmedBudget != options.MaxSpendInr || confirmedPriceCap != options.MaxPriceInr ||
                confirmedFeeReserve != options.FeeReservePercent)
                throw new InvalidOperationException("The strategy budget, price cap or fee reserve changed after confirmation. Start a new session to review it.");
            return;
        }

        output.WriteLine($"Live strategy: USDT/INR, 30-minute Wilder RSI(10), buy below 30, sell above 70. Maximum purchase budget: {Number(options.MaxSpendInr)} INR; buy price cap: {Number(options.MaxPriceInr)} INR.");
        output.WriteLine("Orders execute automatically while this command runs. Existing USDT is excluded. No withdrawals, stop-loss or daily profit target.");
        var shownAt = DateTimeOffset.UtcNow;
        var budget = options.MaxSpendInr;
        var priceCap = options.MaxPriceInr;
        var feeReserve = options.FeeReservePercent;
        RequireConfirmation($"START LIVE RSI10 30M CAP {Number(budget)} INR");
        RequireFreshConfirmation(shownAt);
        if (budget != options.MaxSpendInr || priceCap != options.MaxPriceInr || feeReserve != options.FeeReservePercent)
            throw new InvalidOperationException("The strategy settings changed during confirmation. Start again to review them.");
        confirmedBudget = budget;
        confirmedPriceCap = priceCap;
        confirmedFeeReserve = feeReserve;
        liveSessionConfirmed = true;
    }

    private void RequireConfirmation(string phrase)
    {
        if (!confirm(phrase))
            throw new InvalidOperationException("Confirmation did not match. No money request was submitted.");
    }

    private static void RequireFreshConfirmation(DateTimeOffset shownAt)
    {
        if (DateTimeOffset.UtcNow - shownAt > TimeSpan.FromMinutes(2))
            throw new InvalidOperationException("The strategy confirmation expired. Start the command again.");
    }

    private void PrintState(OperationRecord operation)
    {
        var state = operation.Strategy!;
        output.WriteLine($"{operation.Id}: {(state.Paper ? "paper" : "live")}; ownedUSDT={Number(state.OwnedUsdt)}; protectedUSDT={Number(state.InitialUsdtBalance)}; paperCashINR={Number(state.PaperCashInr)}; lastCandle={state.LastCandle:O}; pending={state.Pending?.ClientId ?? "none"}; cancelRequested={state.Pending?.CancelRequested ?? false}.");
    }

    private static string Number(decimal value) => value.ToString("0.############################", CultureInfo.InvariantCulture);
}
