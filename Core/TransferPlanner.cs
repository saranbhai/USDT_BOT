namespace MyBot.Core;

public static class TransferPlanner
{
    public static void ValidateOptions(BotOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        if (options.MaxSpendInr <= 0 || options.MaxPriceInr <= 0 ||
            options.VerifiedMinOrderInr <= 0 || options.MaxWithdrawalFeeUsdt < 0 ||
            !options.UseExchangeWithdrawalFee && options.MaxWithdrawalFeeUsdt == 0)
        {
            throw new InvalidOperationException("Set positive INR budget, maximum price, verified minimum order, and either a positive withdrawal fee cap or UseExchangeWithdrawalFee=true.");
        }

        if (options.FeeReservePercent <= 0 || options.FeeReservePercent > 25)
        {
            throw new InvalidOperationException("FeeReservePercent must be greater than zero and at most 25.");
        }

        if (string.IsNullOrWhiteSpace(options.DestinationLabel) || options.DestinationLabel.Any(char.IsControl))
        {
            throw new InvalidOperationException("Set a destination label without control characters.");
        }

        if (options.Routes is null || options.Routes.Count == 0)
        {
            throw new InvalidOperationException("Configure at least one receiving USDT network and saved WazirX address.");
        }

        var networks = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var route in options.Routes)
        {
            ValidateDestination(route);
            if (!networks.Add(route.Network))
            {
                throw new InvalidOperationException("Configure only one destination route for each network.");
            }
        }
    }

    public static PurchasePlan CreatePlan(BotOptions options, SymbolInfo symbol, decimal bestAsk,
        IReadOnlyList<NetworkInfo> networks, IReadOnlyList<AddressEntry> addresses,
        IReadOnlyList<Balance> balances, DateTimeOffset now)
    {
        ValidateOptions(options);
        ArgumentNullException.ThrowIfNull(symbol);
        ArgumentNullException.ThrowIfNull(networks);
        ArgumentNullException.ThrowIfNull(addresses);
        ArgumentNullException.ThrowIfNull(balances);

        if (!string.Equals(symbol.Symbol, "usdtinr", StringComparison.OrdinalIgnoreCase) ||
            !string.Equals(symbol.BaseAsset, "usdt", StringComparison.OrdinalIgnoreCase) ||
            !string.Equals(symbol.QuoteAsset, "inr", StringComparison.OrdinalIgnoreCase) ||
            !string.Equals(symbol.Status, "trading", StringComparison.OrdinalIgnoreCase) ||
            !symbol.SpotAllowed || !symbol.LimitAllowed)
        {
            throw new InvalidOperationException("USDT/INR spot limit buying is not currently available.");
        }

        if (symbol.TickSize <= 0 || symbol.MinimumPrice < 0 ||
            symbol.MaximumPrice is <= 0 ||
            symbol.MaximumPrice.HasValue && symbol.MaximumPrice.Value < symbol.MinimumPrice ||
            symbol.BasePrecision is < 0 or > 28 ||
            symbol.QuantityStep is <= 0 || symbol.MinimumQuantity is < 0 ||
            symbol.MaximumQuantity is <= 0 || symbol.MinimumNotional is < 0)
        {
            throw new InvalidOperationException("USDT/INR price or quantity metadata is missing or inconsistent.");
        }

        var precisionStep = 1m;
        for (var digit = 0; digit < symbol.BasePrecision; digit++)
        {
            precisionStep /= 10m;
        }

        var quantityStep = symbol.QuantityStep ?? precisionStep;
        if (quantityStep % precisionStep != 0 ||
            symbol.MaximumQuantity.HasValue && symbol.MinimumQuantity.HasValue &&
            symbol.MaximumQuantity.Value < symbol.MinimumQuantity.Value)
        {
            throw new InvalidOperationException("USDT/INR quantity step is incompatible with its precision or limits.");
        }

        var price = RoundDown(options.MaxPriceInr, symbol.TickSize);
        if (price <= 0 || price < symbol.MinimumPrice ||
            symbol.MaximumPrice.HasValue && price > symbol.MaximumPrice.Value)
        {
            throw new InvalidOperationException("The configured maximum price falls outside the exchange price limits.");
        }

        if (bestAsk <= 0 || bestAsk > price)
        {
            throw new InvalidOperationException("The current ask exceeds the permitted limit price, or no valid ask is available.");
        }

        var reserveMultiplier = 1m + options.FeeReservePercent / 100m;
        var quantity = RoundDown(options.MaxSpendInr / reserveMultiplier / price, quantityStep);
        var orderCost = quantity * price;
        var budgetWithReserve = orderCost * reserveMultiplier;
        if (budgetWithReserve > options.MaxSpendInr)
        {
            throw new InvalidOperationException("The rounded order and fee reserve exceed the configured INR budget.");
        }

        var minimumNotional = Math.Max(options.VerifiedMinOrderInr, symbol.MinimumNotional ?? 0m);
        if (quantity <= 0 || orderCost < minimumNotional ||
            symbol.MinimumQuantity.HasValue && quantity < symbol.MinimumQuantity.Value ||
            symbol.MaximumQuantity.HasValue && quantity > symbol.MaximumQuantity.Value)
        {
            throw new InvalidOperationException("The configured budget produces an order outside the exchange quantity or minimum order limits.");
        }

        var inrBalances = balances.Where(balance =>
            string.Equals(balance.Asset, "inr", StringComparison.OrdinalIgnoreCase)).ToArray();
        if (inrBalances.Length != 1 || inrBalances[0].Free < 0)
        {
            throw new InvalidOperationException("Exactly one nonnegative INR balance is required to plan a purchase.");
        }

        if (inrBalances[0].Free < budgetWithReserve)
        {
            throw new InvalidOperationException("The available INR balance does not cover the planned order and fee reserve.");
        }

        var estimatedAcquiredUsdt = quantity * (1m - options.FeeReservePercent / 100m);
        var eligible = new List<PurchasePlan>();
        var reasons = new List<string>();
        foreach (var route in options.Routes)
        {
            var matchingNetworks = networks.Where(network =>
                string.Equals(network.Network, route.Network, StringComparison.OrdinalIgnoreCase)).ToArray();
            if (matchingNetworks.Length != 1)
            {
                reasons.Add($"{route.Network}: exchange metadata is missing or ambiguous");
                continue;
            }

            var network = matchingNetworks[0];
            try
            {
                ValidateRoute(network, route, addresses, now);
                var withdrawal = PlanWithdrawal(estimatedAcquiredUsdt, estimatedAcquiredUsdt,
                    network, route, options.MaxWithdrawalFeeUsdt, options.UseExchangeWithdrawalFee);
                eligible.Add(new PurchasePlan(price, quantity, orderCost, budgetWithReserve,
                    network, route, withdrawal.Amount, withdrawal.ConservativeReceive));
            }
            catch (InvalidOperationException exception)
            {
                reasons.Add($"{route.Network}: {exception.Message}");
            }
        }

        return eligible.OrderBy(plan => plan.Network.Fee)
            .ThenBy(plan => plan.Network.Network, StringComparer.OrdinalIgnoreCase)
            .FirstOrDefault() ?? throw new InvalidOperationException(
                "No configured USDT withdrawal route is eligible. " + string.Join("; ", reasons));
    }

    public static (decimal Amount, decimal ConservativeReceive) PlanWithdrawal(decimal acquiredUsdt,
        decimal freeUsdt, NetworkInfo network, DestinationRoute route, decimal maxFee) =>
        PlanWithdrawal(acquiredUsdt, freeUsdt, network, route, maxFee, false);

    public static (decimal Amount, decimal ConservativeReceive) PlanWithdrawal(decimal acquiredUsdt,
        decimal freeUsdt, NetworkInfo network, DestinationRoute route, decimal maxFee, bool useExchangeWithdrawalFee)
    {
        ArgumentNullException.ThrowIfNull(network);
        ValidateDestination(route);
        ValidateNetwork(network);
        if (maxFee < 0 || !useExchangeWithdrawalFee && (maxFee == 0 || network.Fee > maxFee))
        {
            throw new InvalidOperationException("The withdrawal fee exceeds the configured positive fee cap.");
        }

        if (acquiredUsdt <= 0 || freeUsdt <= 0)
        {
            throw new InvalidOperationException("No newly acquired, available USDT can be withdrawn.");
        }

        var available = Math.Min(acquiredUsdt, freeUsdt);
        if (available <= network.Fee)
        {
            throw new InvalidOperationException("The available USDT does not cover the withdrawal fee reserve.");
        }

        var amount = RoundDown(available - network.Fee, network.IntegerMultiple!.Value);
        var conservativeReceive = amount - network.Fee;
        if (amount <= 0 || amount < network.Minimum || amount > network.Maximum)
        {
            throw new InvalidOperationException("The planned withdrawal falls outside the exchange withdrawal limits.");
        }

        if (conservativeReceive < route.MinimumDepositUsdt)
        {
            throw new InvalidOperationException("The conservative received amount is below the receiving exchange minimum deposit.");
        }

        return (amount, conservativeReceive);
    }

    public static void ValidateRoute(NetworkInfo network, DestinationRoute route,
        IReadOnlyList<AddressEntry> addresses, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(network);
        ArgumentNullException.ThrowIfNull(addresses);
        ValidateDestination(route);
        ValidateNetwork(network);
        if (!string.Equals(network.Network, route.Network, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException("The exchange network does not match the configured destination network.");
        }

        if (!route.DepositEnabledConfirmed)
        {
            throw new InvalidOperationException("Confirm that USDT deposits are enabled on the receiving network.");
        }

        var matches = addresses.Where(address => address.Id == route.AddressBookId).ToArray();
        if (matches.Length != 1)
        {
            throw new InvalidOperationException("The saved withdrawal address is missing or ambiguous.");
        }

        var entry = matches[0];
        if (!string.Equals(entry.Currency, "usdt", StringComparison.OrdinalIgnoreCase) ||
            !string.Equals(entry.Network, route.Network, StringComparison.OrdinalIgnoreCase) ||
            !string.Equals(entry.Address, route.Address, StringComparison.Ordinal))
        {
            throw new InvalidOperationException("The saved withdrawal address does not exactly match the configured USDT destination and network.");
        }

        if (entry.IsUniversal || !entry.WithdrawAllowed || entry.AllowedAfter > now)
        {
            throw new InvalidOperationException("The saved address is universal, not withdrawal-enabled, or still in its cooldown period.");
        }
    }

    public static decimal RoundDown(decimal amount, decimal step)
    {
        if (amount < 0 || step <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(amount), "Rounding requires a nonnegative amount and a positive step.");
        }

        return amount - amount % step;
    }

    private static void ValidateDestination(DestinationRoute route)
    {
        if (route is null || string.IsNullOrWhiteSpace(route.Network) ||
            route.Network.Any(char.IsControl) || route.Network != route.Network.Trim() ||
            route.AddressBookId <= 0 || route.MinimumDepositUsdt <= 0 ||
            string.IsNullOrWhiteSpace(route.Address) || route.Address.Any(char.IsWhiteSpace) ||
            route.Address.Any(char.IsControl) || route.Address.Contains('*') ||
            route.Address.Contains('…') || route.Address.Contains("...", StringComparison.Ordinal))
        {
            throw new InvalidOperationException("Each destination needs an exact network, positive address book ID and deposit minimum, and a complete unmasked address.");
        }
    }

    private static void ValidateNetwork(NetworkInfo network)
    {
        if (!network.WithdrawEnabled)
        {
            throw new InvalidOperationException("USDT withdrawals are disabled on this network.");
        }

        if (network.MemoRequired)
        {
            throw new InvalidOperationException("Memo-required networks are unsupported because the address book API does not expose a memo for independent verification.");
        }

        if (network.Fee < 0 || network.Minimum < 0 || network.Maximum <= 0 ||
            network.Maximum < network.Minimum || network.IntegerMultiple is null or <= 0 ||
            string.IsNullOrWhiteSpace(network.Consent))
        {
            throw new InvalidOperationException("Known withdrawal fee, minimum, maximum, integer multiple, and consent are required for this network.");
        }
    }
}
