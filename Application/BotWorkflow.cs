using System.Globalization;
using MyBot.Core;

namespace MyBot.Application;

public sealed class BotWorkflow(BotOptions options, IWazirXClient client,
    IOperationStore store, TextWriter output, Func<string, bool> confirm)
{
    public async Task PreviewAsync(CancellationToken cancellationToken)
    {
        var snapshot = await GetPlanAsync(cancellationToken);
        PrintPlan(snapshot.Plan);
        output.WriteLine("PREVIEW ONLY: no order or withdrawal has been submitted.");
    }

    public async Task BuyAsync(CancellationToken cancellationToken)
    {
        RequireLiveEnabled();
        EnsureNoActiveOperation();
        var preview = await GetPlanAsync(cancellationToken);
        PrintPlan(preview.Plan);
        var shownAt = DateTimeOffset.UtcNow;
        RequireConfirmation($"BUY {Number(preview.Plan.Quantity)} USDT AT {Number(preview.Plan.Price)} INR");
        RequireFreshConfirmation(shownAt);

        var fresh = await GetPlanAsync(cancellationToken);
        if (preview.Plan != fresh.Plan)
            throw new InvalidOperationException("The quote or route changed during confirmation. Preview and buy again.");
        RequireFreshConfirmation(shownAt);

        var operation = new OperationRecord
        {
            Id = Guid.NewGuid().ToString("N"),
            CreatedAt = DateTimeOffset.UtcNow,
            Stage = OperationStage.BuySubmitting,
            DestinationLabel = options.DestinationLabel,
            Network = fresh.Plan.Network.Network,
            AddressBookId = fresh.Plan.Route.AddressBookId,
            Address = fresh.Plan.Route.Address,
            MinimumDepositUsdt = fresh.Plan.Route.MinimumDepositUsdt,
            LimitPrice = fresh.Plan.Price,
            Quantity = fresh.Plan.Quantity,
            InitialUsdtBalance = FreeBalance(fresh.Balances, "usdt")
        };
        store.Insert(operation);
        output.WriteLine($"Operation: {operation.Id}. Journal saved before submitting the order.");
        var order = await client.PlaceBuyAsync(operation.BuyClientId, operation.Quantity,
            operation.LimitPrice, cancellationToken);
        ValidateOrder(operation, order);
        operation.OrderId = order.Id;
        operation.Stage = OperationStage.BuyAcknowledged;
        store.Save(operation);
        output.WriteLine($"Order acknowledged. Run status {operation.Id} to check fills. No withdrawal was submitted.");
    }

    public async Task StatusAsync(string id, CancellationToken cancellationToken)
    {
        var operation = store.Get(id);
        if (operation.Stage == OperationStage.DepositConfirmed)
        {
            PrintOperation(operation);
            return;
        }

        if (operation.Stage is OperationStage.WithdrawalSubmitting or OperationStage.WithdrawalAcknowledged)
        {
            var withdrawals = await client.GetWithdrawalsAsync(operation.WithdrawalClientId, cancellationToken);
            var matches = withdrawals.Where(w => w.ClientId == operation.WithdrawalClientId).ToList();
            if (matches.Count > 1)
                throw new InvalidOperationException("Multiple withdrawals have this client ID. Reconcile in WazirX; no resubmission is allowed.");
            if (matches.Count == 1)
            {
                var withdrawal = matches[0];
                if (!withdrawal.Coin.Equals("usdt", StringComparison.OrdinalIgnoreCase) ||
                    !withdrawal.Network.Equals(operation.Network, StringComparison.OrdinalIgnoreCase) ||
                    withdrawal.Address != operation.Address || withdrawal.Amount <= 0 ||
                    !operation.WithdrawalAmount.HasValue || !operation.WithdrawalFee.HasValue ||
                    (withdrawal.Amount != operation.WithdrawalAmount.Value &&
                        withdrawal.Amount != operation.WithdrawalAmount.Value - operation.WithdrawalFee.Value) ||
                    (operation.WithdrawalId is not null && operation.WithdrawalId != withdrawal.Id))
                    throw new InvalidOperationException("Withdrawal history does not match the saved route. Reconcile in WazirX.");
                operation.WithdrawalId = withdrawal.Id;
                operation.Stage = OperationStage.WithdrawalAcknowledged;
                store.Save(operation);
                output.WriteLine($"Exchange withdrawal status (raw): {withdrawal.RawStatus}");
                output.WriteLine($"Transaction ID: {withdrawal.TransactionId}");
                output.WriteLine("This does not verify a deposit at the receiving exchange. Check its deposit history.");
            }
            else
            {
                output.WriteLine("No matching withdrawal is visible yet. Outcome is unresolved; it will NOT be resubmitted.");
            }
        }
        else
        {
            await ReconcileOrderAsync(operation, cancellationToken);
        }
        PrintOperation(operation);
    }

    public async Task WithdrawAsync(string id, CancellationToken cancellationToken)
    {
        RequireLiveEnabled();
        TransferPlanner.ValidateOptions(options);
        var operation = store.Get(id);
        RequireBuyStage(operation);
        await ReconcileOrderAsync(operation, cancellationToken);
        if (operation.Stage is not (OperationStage.BuyFilled or OperationStage.BuyCanceled) ||
            operation.AcquiredUsdt is not > 0)
            throw new InvalidOperationException("The buy must be fully filled or canceled with confirmed fills before withdrawal. Use status or cancel.");

        var preview = await GetWithdrawalPlanAsync(operation, cancellationToken);
        PrintWithdrawal(operation, preview.Network, preview.Amount, preview.Receive);
        output.WriteLine($"Withdrawal consent supplied by WazirX: {preview.Network.Consent}");
        var shownAt = DateTimeOffset.UtcNow;
        RequireConfirmation($"WITHDRAW {Number(preview.Amount)} USDT ON {operation.Network} TO {operation.Address}");
        RequireFreshConfirmation(shownAt);

        var fresh = await GetWithdrawalPlanAsync(operation, cancellationToken);
        if (preview != fresh)
            throw new InvalidOperationException("Withdrawal conditions changed during confirmation. Run withdraw again to review them.");
        RequireFreshConfirmation(shownAt);

        operation.WithdrawalAmount = fresh.Amount;
        operation.WithdrawalFee = fresh.Network.Fee;
        operation.Stage = OperationStage.WithdrawalSubmitting;
        store.Save(operation);
        output.WriteLine($"Withdrawal intent saved for operation {operation.Id}. It will be submitted only once.");
        var receipt = await client.WithdrawAsync(operation.WithdrawalClientId, operation.AddressBookId,
            fresh.Amount, fresh.Network.Consent, cancellationToken);
        if (receipt.ClientId != operation.WithdrawalClientId || string.IsNullOrWhiteSpace(receipt.Id))
            throw new InvalidOperationException("Unexpected withdrawal receipt. Use status; do not resubmit.");
        operation.WithdrawalId = receipt.Id;
        operation.Stage = OperationStage.WithdrawalAcknowledged;
        store.Save(operation);
        output.WriteLine($"Withdrawal acknowledged: {receipt.Id}. Check status and the receiving exchange's deposit history.");
    }

    public async Task CancelAsync(string id, CancellationToken cancellationToken)
    {
        RequireLiveEnabled();
        var operation = store.Get(id);
        RequireBuyStage(operation);
        await ReconcileOrderAsync(operation, cancellationToken);
        if (operation.Stage is OperationStage.BuyFilled or OperationStage.BuyCanceled)
            throw new InvalidOperationException("The order is already filled or canceled; nothing to cancel.");
        RequireConfirmation($"CANCEL BUY {id}");
        var canceled = await client.CancelOrderAsync(operation.OrderId!.Value, cancellationToken);
        ValidateOrder(operation, canceled);
        await ReconcileOrderAsync(operation, cancellationToken);
        PrintOperation(operation);
    }

    public void ConfirmDeposit(string id)
    {
        RequireLiveEnabled();
        var operation = store.Get(id);
        if (operation.Stage != OperationStage.WithdrawalAcknowledged)
            throw new InvalidOperationException("A withdrawal must be acknowledged before recording your deposit confirmation.");
        output.WriteLine("Only continue after personally verifying the USDT deposit at your receiving exchange.");
        RequireConfirmation($"I VERIFIED DEPOSIT FOR {id}");
        operation.Stage = OperationStage.DepositConfirmed;
        store.Save(operation);
        output.WriteLine("Your manual deposit confirmation was recorded. A new purchase can now be started.");
    }

    public void List()
    {
        foreach (var operation in store.List())
            PrintOperation(operation);
    }

    private async Task<(PurchasePlan Plan, IReadOnlyList<Balance> Balances)> GetPlanAsync(CancellationToken cancellationToken)
    {
        TransferPlanner.ValidateOptions(options);
        var symbol = await client.GetSymbolAsync(cancellationToken);
        var bestAsk = await client.GetBestAskAsync(cancellationToken);
        var networks = await client.GetNetworksAsync(cancellationToken);
        var addresses = await client.GetAddressesAsync(cancellationToken);
        var balances = await client.GetBalancesAsync(cancellationToken);
        var plan = TransferPlanner.CreatePlan(options, symbol, bestAsk, networks, addresses, balances, DateTimeOffset.UtcNow);
        return (plan, balances);
    }

    private async Task<(NetworkInfo Network, decimal Amount, decimal Receive)> GetWithdrawalPlanAsync(
        OperationRecord operation, CancellationToken cancellationToken)
    {
        var route = options.Routes.SingleOrDefault(r =>
            r.Network.Equals(operation.Network, StringComparison.OrdinalIgnoreCase));
        if (route is null || route.AddressBookId != operation.AddressBookId || route.Address != operation.Address ||
            route.MinimumDepositUsdt != operation.MinimumDepositUsdt || options.DestinationLabel != operation.DestinationLabel)
            throw new InvalidOperationException("Destination configuration changed since the buy. Restore the saved route before withdrawing.");
        var networks = await client.GetNetworksAsync(cancellationToken);
        var network = networks.SingleOrDefault(n => n.Network.Equals(operation.Network, StringComparison.OrdinalIgnoreCase))
            ?? throw new InvalidOperationException("The saved network is no longer available.");
        var addresses = await client.GetAddressesAsync(cancellationToken);
        TransferPlanner.ValidateRoute(network, route, addresses, DateTimeOffset.UtcNow);
        var balances = await client.GetBalancesAsync(cancellationToken);
        var availableFromPurchase = Math.Max(0, FreeBalance(balances, "usdt") - operation.InitialUsdtBalance);
        var amount = TransferPlanner.PlanWithdrawal(operation.AcquiredUsdt!.Value, availableFromPurchase,
            network, route, options.MaxWithdrawalFeeUsdt, options.UseExchangeWithdrawalFee);
        return (network, amount.Amount, amount.ConservativeReceive);
    }

    private async Task ReconcileOrderAsync(OperationRecord operation, CancellationToken cancellationToken)
    {
        RequireBuyStage(operation);
        var order = await client.GetOrderAsync(operation.BuyClientId, cancellationToken);
        ValidateOrder(operation, order);
        operation.OrderId = order.Id;
        operation.Stage = OperationStage.BuyAcknowledged;
        operation.AcquiredUsdt = null;
        store.Save(operation);

        var trades = await client.GetTradesAsync(order.Id, cancellationToken);
        if (trades.Any(t => t.OrderId != order.Id || !t.Symbol.Equals("usdtinr", StringComparison.OrdinalIgnoreCase) || t.Side != "buy" ||
            t.Quantity <= 0 || t.Fee < 0 || string.IsNullOrWhiteSpace(t.FeeCurrency)) ||
            trades.Select(t => t.Id).Distinct().Count() != trades.Count ||
            trades.Sum(t => t.Quantity) != order.ExecutedQuantity)
            throw new InvalidOperationException("Trade fills are incomplete or inconsistent with the order. Reconcile in WazirX before proceeding.");
        var usdtFee = trades.Where(t => t.FeeCurrency.Equals("usdt", StringComparison.OrdinalIgnoreCase)).Sum(t => t.Fee);
        var net = order.ExecutedQuantity - usdtFee;
        if (net < 0)
            throw new InvalidOperationException("USDT trading fees exceed the confirmed fills.");
        operation.AcquiredUsdt = net;
        operation.Stage = order.Status switch
        {
            "done" when order.ExecutedQuantity == operation.Quantity => OperationStage.BuyFilled,
            "cancel" => OperationStage.BuyCanceled,
            "wait" or "idle" => OperationStage.BuyAcknowledged,
            _ => throw new InvalidOperationException("Unexpected order state. Reconcile in WazirX before proceeding.")
        };
        store.Save(operation);
        output.WriteLine($"Order {order.Id}: {order.Status}; filled {Number(order.ExecutedQuantity)} USDT; net after reported USDT fees {Number(net)}.");
    }

    private static void ValidateOrder(OperationRecord operation, OrderInfo order)
    {
        if (order.Id <= 0 || order.ClientOrderId != operation.BuyClientId || !order.Symbol.Equals("usdtinr", StringComparison.OrdinalIgnoreCase) ||
            order.Side != "buy" || order.Type != "limit" || order.Price != operation.LimitPrice ||
            order.OriginalQuantity != operation.Quantity || order.ExecutedQuantity < 0 ||
            order.ExecutedQuantity > operation.Quantity || (operation.OrderId.HasValue && operation.OrderId.Value != order.Id))
            throw new InvalidOperationException("The exchange order does not match the saved purchase. No further money request is permitted.");
    }

    private static decimal FreeBalance(IReadOnlyList<Balance> balances, string asset)
    {
        var matching = balances.Where(b => b.Asset.Equals(asset, StringComparison.OrdinalIgnoreCase)).ToList();
        if (matching.Count > 1 || matching.Any(b => b.Free < 0))
            throw new InvalidOperationException("Invalid balance response.");
        return matching.SingleOrDefault()?.Free ?? 0;
    }

    private void EnsureNoActiveOperation()
    {
        var active = store.List().FirstOrDefault(o => o.Strategy is { } strategy
            ? !strategy.Paper && (strategy.OwnedUsdt > 0 || strategy.Pending is not null)
            : o.Stage != OperationStage.DepositConfirmed &&
              !(o.Stage == OperationStage.BuyCanceled && o.AcquiredUsdt == 0));
        if (active is not null)
            throw new InvalidOperationException($"Operation {active.Id} is unresolved. Use status; finish it before starting another purchase.");
    }

    private void RequireLiveEnabled()
    {
        if (!options.EnableLiveTrading)
            throw new InvalidOperationException("Live operations are disabled. EnableLiveTrading must be true as well as supplying --live.");
    }

    private static void RequireBuyStage(OperationRecord operation)
    {
        if (operation.Strategy is not null)
            throw new InvalidOperationException("This is an RSI strategy record. Use strategy-status or strategy-cancel instead.");
        if (operation.Stage is OperationStage.WithdrawalSubmitting or OperationStage.WithdrawalAcknowledged or OperationStage.DepositConfirmed)
            throw new InvalidOperationException("This operation already has a withdrawal intent. Use status; a second withdrawal or buy is not allowed.");
    }

    private void RequireConfirmation(string phrase)
    {
        if (!confirm(phrase))
            throw new InvalidOperationException("Confirmation did not match. No money request was submitted by this command.");
    }

    private static void RequireFreshConfirmation(DateTimeOffset shownAt)
    {
        if (DateTimeOffset.UtcNow - shownAt > TimeSpan.FromMinutes(2))
            throw new InvalidOperationException("The preview expired. Run the command again for current conditions.");
    }

    private void PrintPlan(PurchasePlan plan)
    {
        output.WriteLine($"Destination: {options.DestinationLabel}");
        output.WriteLine($"Limit buy: {Number(plan.Quantity)} USDT at {Number(plan.Price)} INR/USDT.");
        output.WriteLine($"Maximum order cost: {Number(plan.MaximumOrderCostInr)} INR; including configured fee reserve: {Number(plan.BudgetWithReserveInr)} INR.");
        output.WriteLine("The fee allowance also reduces the preview's acquired-USDT estimate. It is not an exchange fee quote; actual fills will be reconciled.");
        output.WriteLine($"Cheapest eligible configured route: {plan.Network.Network}; withdrawal fee: {Number(plan.Network.Fee)} USDT.");
        output.WriteLine($"Saved address-book ID: {plan.Route.AddressBookId}; address: {plan.Route.Address}");
        output.WriteLine($"Estimated withdrawal request after the configured trading fee allowance: {Number(plan.WithdrawalAmount)} USDT; conservative receive estimate: {Number(plan.ConservativeReceiveAmount)} USDT.");
        output.WriteLine("Withdrawal fee is reserved from the balance and also deducted from the receive estimate because the API docs do not specify how it is charged. Some USDT may remain.");
        output.WriteLine("The buy and withdrawal are separate actions. A later withdrawal can still be delayed or disabled by the exchange.");
    }

    private void PrintWithdrawal(OperationRecord operation, NetworkInfo network, decimal amount, decimal receive)
    {
        output.WriteLine($"Destination: {operation.DestinationLabel}; network: {operation.Network}");
        output.WriteLine($"Address-book ID: {operation.AddressBookId}; exact address: {operation.Address}");
        output.WriteLine($"Withdrawal request: {Number(amount)} USDT; API fee: {Number(network.Fee)} USDT; conservative receive estimate: {Number(receive)} USDT.");
        output.WriteLine("Existing USDT balance is excluded. Fee is reserved separately and deducted from this receive estimate; some USDT may remain.");
    }

    private void PrintOperation(OperationRecord operation) => output.WriteLine(
        $"{operation.Id}: {operation.Stage}; order={operation.OrderId?.ToString(CultureInfo.InvariantCulture) ?? "unknown"}; " +
        $"netUSDT={operation.AcquiredUsdt?.ToString(CultureInfo.InvariantCulture) ?? "unknown"}; " +
        $"withdrawal={operation.WithdrawalId ?? "unknown"}; network={operation.Network}.");

    private static string Number(decimal value) => value.ToString("0.############################", CultureInfo.InvariantCulture);
}
