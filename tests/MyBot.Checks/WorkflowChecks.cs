using System.Text.Json;
using MyBot.Application;
using MyBot.Core;

public static class WorkflowChecks
{
    public static async Task RunAsync()
    {
        (string Name, Func<Task> Check)[] checks =
        [
            ("Buy intent survives an ambiguous POST and blocks a second purchase", AmbiguousBuyIsNotRepeated),
            ("A journal failure prevents the buy POST", BuyJournalFailureBlocksSubmission),
            ("Unfilled and inconsistent orders cannot initiate a withdrawal", NonterminalBuyBlocksWithdrawal),
            ("Incomplete trade history blocks a withdrawal", IncompleteFillsBlockWithdrawal),
            ("A canceled order can withdraw only its confirmed partial fills", CanceledBuyUsesConfirmedFills),
            ("Existing USDT is excluded even when the total balance covers the buy", ExistingBalanceIsExcluded),
            ("Reported USDT trade fees reduce the available acquired amount", UsdtTradeFeesAreExcluded),
            ("Withdrawal intent survives an ambiguous POST and blocks resubmission", AmbiguousWithdrawalIsNotRepeated),
            ("A journal failure prevents the withdrawal POST", WithdrawalJournalFailureBlocksSubmission),
            ("A changed address book entry during confirmation blocks withdrawal", ChangedAddressBookBlocksWithdrawal),
            ("Changed withdrawal consent during confirmation blocks submission", ChangedConsentBlocksWithdrawal),
            ("Exchange fee mode uses the current fee and rejects a change during confirmation", ExchangeFeeMode),
            ("A changed buy preflight quote blocks submission", ChangedBuyQuoteBlocksSubmission),
            ("A changed saved destination configuration blocks withdrawal", ChangedDestinationBlocksWithdrawal),
            ("Refusing purchase or withdrawal confirmation submits no money request", RefusedConfirmationBlocksSubmission),
            ("Exchange withdrawal status never automatically confirms the destination deposit", RawWithdrawalStatusDoesNotConfirmDeposit)
        ];

        foreach (var (name, check) in checks)
        {
            try
            {
                await check();
                Console.WriteLine($"PASS {name}");
            }
            catch (Exception exception)
            {
                throw new InvalidOperationException($"Workflow check failed: {name}", exception);
            }
        }
    }

    private static async Task AmbiguousBuyIsNotRepeated()
    {
        using var fixture = new Fixture();
        fixture.Client.BeforeBuy = (clientId, quantity, price) =>
        {
            var saved = fixture.Store.List().Single();
            Equal(OperationStage.BuySubmitting, saved.Stage);
            Equal(clientId, saved.BuyClientId);
            Equal(quantity, saved.Quantity);
            Equal(price, saved.LimitPrice);
            Equal(100m, saved.InitialUsdtBalance);
            throw new TimeoutException("Synthetic timeout after the exchange may have accepted the buy.");
        };

        await ThrowsAsync<TimeoutException>(() => fixture.Workflow().BuyAsync(CancellationToken.None));
        Equal(OperationStage.BuySubmitting, fixture.Store.List().Single().Stage);
        await ThrowsAsync<InvalidOperationException>(() => fixture.Workflow().BuyAsync(CancellationToken.None));
        Equal(1, fixture.Client.BuyCalls);
        Equal(0, fixture.Client.WithdrawalCalls);
    }

    private static async Task BuyJournalFailureBlocksSubmission()
    {
        using var fixture = new Fixture();
        fixture.Store.BeforeInsert = _ => throw new IOException("Synthetic journal write failure.");
        await ThrowsAsync<IOException>(() => fixture.Workflow().BuyAsync(CancellationToken.None));
        Equal(0, fixture.Client.MoneyRequests);
        Equal(0, fixture.Store.List().Count);
    }

    private static async Task NonterminalBuyBlocksWithdrawal()
    {
        foreach (var status in new[] { "wait", "idle", "done" })
        {
            using var fixture = new Fixture();
            var operation = fixture.SeedBuy(status, 3m);
            await ThrowsAsync<InvalidOperationException>(() =>
                fixture.Workflow().WithdrawAsync(operation.Id, CancellationToken.None));
            Equal(0, fixture.Client.MoneyRequests);
            Require(fixture.Store.Get(operation.Id).Stage != OperationStage.WithdrawalSubmitting,
                "A nonterminal or inconsistent order reached withdrawal submission.");
        }
    }

    private static async Task IncompleteFillsBlockWithdrawal()
    {
        using var fixture = new Fixture();
        var operation = fixture.SeedBuy();
        fixture.Client.Trades = [new TradeFill(1, 12345, "usdtinr", "buy", 9m, 0m, "inr")];
        await ThrowsAsync<InvalidOperationException>(() =>
            fixture.Workflow().WithdrawAsync(operation.Id, CancellationToken.None));
        Equal(0, fixture.Client.MoneyRequests);
        Equal<decimal?>(null, fixture.Store.Get(operation.Id).AcquiredUsdt);
    }

    private static async Task CanceledBuyUsesConfirmedFills()
    {
        using var fixture = new Fixture();
        var operation = fixture.SeedBuy("cancel", 3m);
        fixture.Client.Balances = [new Balance("inr", 10000m), new Balance("usdt", 103m)];
        await fixture.Workflow().WithdrawAsync(operation.Id, CancellationToken.None);
        Equal(2.8m, fixture.Client.LastWithdrawalAmount);
        Equal(1, fixture.Client.WithdrawalCalls);
        Equal(OperationStage.WithdrawalAcknowledged, fixture.Store.Get(operation.Id).Stage);
    }

    private static async Task ExistingBalanceIsExcluded()
    {
        using (var fixture = new Fixture())
        {
            var operation = fixture.SeedBuy();
            fixture.Client.Balances = [new Balance("inr", 10000m), new Balance("usdt", 100m)];
            await ThrowsAsync<InvalidOperationException>(() =>
                fixture.Workflow().WithdrawAsync(operation.Id, CancellationToken.None));
            Equal(0, fixture.Client.MoneyRequests);
        }

        using (var fixture = new Fixture())
        {
            var operation = fixture.SeedBuy();
            fixture.Client.Balances = [new Balance("inr", 10000m), new Balance("usdt", 104m)];
            await fixture.Workflow().WithdrawAsync(operation.Id, CancellationToken.None);
            Equal(3.8m, fixture.Client.LastWithdrawalAmount);
        }
    }

    private static async Task UsdtTradeFeesAreExcluded()
    {
        using var fixture = new Fixture();
        var operation = fixture.SeedBuy();
        fixture.Client.Balances = [new Balance("inr", 10000m), new Balance("usdt", 120m)];
        fixture.Client.Trades = [new TradeFill(1, 12345, "usdtinr", "buy", 10m, 0.1m, "usdt")];
        await fixture.Workflow().WithdrawAsync(operation.Id, CancellationToken.None);
        Equal(9.7m, fixture.Client.LastWithdrawalAmount);
        Equal<decimal?>(9.9m, fixture.Store.Get(operation.Id).AcquiredUsdt);
    }

    private static async Task AmbiguousWithdrawalIsNotRepeated()
    {
        using var fixture = new Fixture();
        var operation = fixture.SeedBuy();
        fixture.Client.BeforeWithdrawal = (clientId, addressBookId, amount, consent) =>
        {
            var saved = fixture.Store.Get(operation.Id);
            Equal(OperationStage.WithdrawalSubmitting, saved.Stage);
            Equal(clientId, saved.WithdrawalClientId);
            Equal(addressBookId, saved.AddressBookId);
            Equal<decimal?>(amount, saved.WithdrawalAmount);
            Equal("synthetic-consent", consent);
            throw new TimeoutException("Synthetic timeout after the exchange may have accepted withdrawal.");
        };

        await ThrowsAsync<TimeoutException>(() =>
            fixture.Workflow().WithdrawAsync(operation.Id, CancellationToken.None));
        Equal(OperationStage.WithdrawalSubmitting, fixture.Store.Get(operation.Id).Stage);
        await ThrowsAsync<InvalidOperationException>(() =>
            fixture.Workflow().WithdrawAsync(operation.Id, CancellationToken.None));
        await fixture.Workflow().StatusAsync(operation.Id, CancellationToken.None);
        Equal(1, fixture.Client.WithdrawalCalls);
        Equal(OperationStage.WithdrawalSubmitting, fixture.Store.Get(operation.Id).Stage);
        Require(fixture.Output.ToString().Contains("will NOT be resubmitted", StringComparison.Ordinal),
            "An unresolved withdrawal must tell the operator that it will not be resubmitted.");
    }

    private static async Task WithdrawalJournalFailureBlocksSubmission()
    {
        using var fixture = new Fixture();
        var operation = fixture.SeedBuy();
        fixture.Store.BeforeSave = saved =>
        {
            if (saved.Stage == OperationStage.WithdrawalSubmitting)
                throw new IOException("Synthetic journal write failure.");
        };
        await ThrowsAsync<IOException>(() =>
            fixture.Workflow().WithdrawAsync(operation.Id, CancellationToken.None));
        Equal(0, fixture.Client.MoneyRequests);
        Equal(OperationStage.BuyFilled, fixture.Store.Get(operation.Id).Stage);
    }

    private static async Task ChangedAddressBookBlocksWithdrawal()
    {
        using var fixture = new Fixture();
        var operation = fixture.SeedBuy();
        var workflow = fixture.Workflow(_ =>
        {
            fixture.Client.Addresses = [fixture.Client.Addresses.Single() with
            {
                Address = "0x2222222222222222222222222222222222222222"
            }];
            return true;
        });
        await ThrowsAsync<InvalidOperationException>(() => workflow.WithdrawAsync(operation.Id, CancellationToken.None));
        Equal(0, fixture.Client.MoneyRequests);
    }

    private static async Task ExchangeFeeMode()
    {
        using (var fixture = new Fixture())
        {
            fixture.Options.UseExchangeWithdrawalFee = true;
            fixture.Options.MaxWithdrawalFeeUsdt = 0m;
            var operation = fixture.SeedBuy();
            await fixture.Workflow().WithdrawAsync(operation.Id, CancellationToken.None);
            Equal(9.8m, fixture.Client.LastWithdrawalAmount);
            Equal<decimal?>(0.2m, fixture.Store.Get(operation.Id).WithdrawalFee);
        }

        using (var fixture = new Fixture())
        {
            fixture.Options.UseExchangeWithdrawalFee = true;
            fixture.Options.MaxWithdrawalFeeUsdt = 0m;
            var operation = fixture.SeedBuy();
            var workflow = fixture.Workflow(_ =>
            {
                fixture.Client.Networks = [fixture.Client.Networks.Single() with { Fee = 0.3m }];
                return true;
            });
            await ThrowsAsync<InvalidOperationException>(() => workflow.WithdrawAsync(operation.Id, CancellationToken.None));
            Equal(0, fixture.Client.MoneyRequests);
            Equal(OperationStage.BuyFilled, fixture.Store.Get(operation.Id).Stage);
        }
    }

    private static async Task ChangedConsentBlocksWithdrawal()
    {
        using var fixture = new Fixture();
        var operation = fixture.SeedBuy();
        var workflow = fixture.Workflow(_ =>
        {
            fixture.Client.Networks = [fixture.Client.Networks.Single() with { Consent = "changed-consent" }];
            return true;
        });
        await ThrowsAsync<InvalidOperationException>(() => workflow.WithdrawAsync(operation.Id, CancellationToken.None));
        Equal(0, fixture.Client.MoneyRequests);
    }

    private static async Task ChangedBuyQuoteBlocksSubmission()
    {
        using var fixture = new Fixture();
        var workflow = fixture.Workflow(_ =>
        {
            fixture.Client.Networks = [fixture.Client.Networks.Single() with { Fee = 0.3m }];
            return true;
        });
        await ThrowsAsync<InvalidOperationException>(() => workflow.BuyAsync(CancellationToken.None));
        Equal(0, fixture.Client.MoneyRequests);
        Equal(0, fixture.Store.List().Count);
    }

    private static async Task ChangedDestinationBlocksWithdrawal()
    {
        using var fixture = new Fixture();
        var operation = fixture.SeedBuy();
        fixture.Options.Routes.Single().AddressBookId = 999;
        await ThrowsAsync<InvalidOperationException>(() =>
            fixture.Workflow().WithdrawAsync(operation.Id, CancellationToken.None));
        Equal(0, fixture.Client.MoneyRequests);
    }

    private static async Task RefusedConfirmationBlocksSubmission()
    {
        using (var fixture = new Fixture())
        {
            await ThrowsAsync<InvalidOperationException>(() =>
                fixture.Workflow(_ => false).BuyAsync(CancellationToken.None));
            Equal(0, fixture.Client.MoneyRequests);
            Equal(0, fixture.Store.List().Count);
        }

        using (var fixture = new Fixture())
        {
            var operation = fixture.SeedBuy();
            await ThrowsAsync<InvalidOperationException>(() =>
                fixture.Workflow(_ => false).WithdrawAsync(operation.Id, CancellationToken.None));
            Equal(0, fixture.Client.MoneyRequests);
            Equal(OperationStage.BuyFilled, fixture.Store.Get(operation.Id).Stage);
        }
    }

    private static async Task RawWithdrawalStatusDoesNotConfirmDeposit()
    {
        foreach (var rawStatus in new[] { "0", "1", "2", "3", "6", "completed" })
        {
            using var fixture = new Fixture();
            var operation = fixture.SeedBuy();
            operation.Stage = OperationStage.WithdrawalSubmitting;
            operation.AcquiredUsdt = 10m;
            operation.WithdrawalAmount = 9.8m;
            operation.WithdrawalFee = 0.2m;
            fixture.Store.Save(operation);
            fixture.Client.Withdrawals =
            [
                new WithdrawalInfo("synthetic-withdrawal", operation.WithdrawalClientId, "usdt",
                    operation.Network, operation.Address, 9.8m, rawStatus, "synthetic-transaction")
            ];

            await fixture.Workflow().StatusAsync(operation.Id, CancellationToken.None);
            Equal(OperationStage.WithdrawalAcknowledged, fixture.Store.Get(operation.Id).Stage);
            Equal(0, fixture.Client.MoneyRequests);
            Require(fixture.Output.ToString().Contains($"status (raw): {rawStatus}", StringComparison.Ordinal),
                "Withdrawal status must remain explicitly raw in the output.");
            await ThrowsAsync<InvalidOperationException>(() => fixture.Workflow().BuyAsync(CancellationToken.None));
            Equal(0, fixture.Client.MoneyRequests);
        }
    }

    private static async Task ThrowsAsync<TException>(Func<Task> action) where TException : Exception
    {
        try
        {
            await action();
        }
        catch (TException)
        {
            return;
        }
        throw new InvalidOperationException($"Expected {typeof(TException).Name}, but the operation succeeded.");
    }

    private static void Equal<T>(T expected, T actual)
    {
        if (!EqualityComparer<T>.Default.Equals(expected, actual))
            throw new InvalidOperationException($"Expected {expected}; received {actual}.");
    }

    private static void Require(bool condition, string message)
    {
        if (!condition)
            throw new InvalidOperationException(message);
    }

    private sealed class Fixture : IDisposable
    {
        public BotOptions Options { get; } = new()
        {
            MaxSpendInr = 1020m,
            MaxPriceInr = 100m,
            FeeReservePercent = 2m,
            VerifiedMinOrderInr = 100m,
            MaxWithdrawalFeeUsdt = 1m,
            EnableLiveTrading = true,
            DestinationLabel = "Synthetic offline destination",
            Routes =
            [
                new DestinationRoute
                {
                    Network = "networkA",
                    AddressBookId = 10,
                    Address = "0x1111111111111111111111111111111111111111",
                    MinimumDepositUsdt = 1m,
                    DepositEnabledConfirmed = true
                }
            ]
        };
        public MemoryStore Store { get; } = new();
        public FakeClient Client { get; } = new();
        public StringWriter Output { get; } = new();

        public BotWorkflow Workflow(Func<string, bool>? confirmation = null) =>
            new(Options, Client, Store, Output, confirmation ?? (_ => true));

        public OperationRecord SeedBuy(string status = "done", decimal executed = 10m)
        {
            var route = Options.Routes.Single();
            var operation = new OperationRecord
            {
                Id = "11111111111111111111111111111111",
                CreatedAt = new DateTimeOffset(2026, 11, 7, 12, 0, 0, TimeSpan.Zero),
                Stage = OperationStage.BuyAcknowledged,
                DestinationLabel = Options.DestinationLabel,
                Network = route.Network,
                AddressBookId = route.AddressBookId,
                Address = route.Address,
                MinimumDepositUsdt = route.MinimumDepositUsdt,
                LimitPrice = 100m,
                Quantity = 10m,
                InitialUsdtBalance = 100m,
                OrderId = 12345
            };
            Store.Insert(operation);
            Client.Order = new OrderInfo(12345, operation.BuyClientId, "usdtinr", "buy", "limit",
                100m, 10m, executed, status);
            Client.Trades = executed == 0 ? [] : [new TradeFill(1, 12345, "usdtinr", "buy", executed, 0m, "inr")];
            Client.Balances = [new Balance("inr", 10000m), new Balance("usdt", 100m + executed)];
            return operation;
        }

        public void Dispose()
        {
            Store.Dispose();
            Output.Dispose();
        }
    }

    private sealed class MemoryStore : IOperationStore
    {
        private readonly Dictionary<string, string> records = [];
        public Action<OperationRecord>? BeforeInsert { get; set; }
        public Action<OperationRecord>? BeforeSave { get; set; }

        public void Insert(OperationRecord operation)
        {
            BeforeInsert?.Invoke(operation);
            records.Add(operation.Id, JsonSerializer.Serialize(operation));
        }

        public void Save(OperationRecord operation)
        {
            BeforeSave?.Invoke(operation);
            if (!records.ContainsKey(operation.Id))
                throw new KeyNotFoundException(operation.Id);
            records[operation.Id] = JsonSerializer.Serialize(operation);
        }

        public OperationRecord Get(string id) =>
            JsonSerializer.Deserialize<OperationRecord>(records[id]) ?? throw new InvalidDataException();

        public IReadOnlyList<OperationRecord> List() => records.Keys.Select(Get).ToArray();
        public void Dispose() { }
    }

    private sealed class FakeClient : IWazirXClient
    {
        public IReadOnlyList<NetworkInfo> Networks { get; set; } =
        [new NetworkInfo("networkA", "Synthetic network", true, 0.2m, 1m, 10000m, 0.001m, false, "synthetic-consent")];
        public IReadOnlyList<AddressEntry> Addresses { get; set; } =
        [new AddressEntry(10, "usdt", "networkA", "0x1111111111111111111111111111111111111111", true, false, null)];
        public IReadOnlyList<Balance> Balances { get; set; } = [new Balance("inr", 10000m), new Balance("usdt", 100m)];
        public OrderInfo? Order { get; set; }
        public IReadOnlyList<TradeFill> Trades { get; set; } = [];
        public IReadOnlyList<WithdrawalInfo> Withdrawals { get; set; } = [];
        public Action<string, decimal, decimal>? BeforeBuy { get; set; }
        public Action<string, long, decimal, string>? BeforeWithdrawal { get; set; }
        public int BuyCalls { get; private set; }
        public int CancellationCalls { get; private set; }
        public int WithdrawalCalls { get; private set; }
        public int MoneyRequests => BuyCalls + CancellationCalls + WithdrawalCalls;
        public decimal LastWithdrawalAmount { get; private set; }

        public Task<SymbolInfo> GetSymbolAsync(CancellationToken cancellationToken) => Task.FromResult(
            new SymbolInfo("usdtinr", "usdt", "inr", "trading", true, true, 6,
                0.01m, 0.01m, 100000m, 0.001m, 0.001m, 100000m, 100m));

        public Task<decimal> GetBestAskAsync(CancellationToken cancellationToken) => Task.FromResult(99m);
        public Task<IReadOnlyList<NetworkInfo>> GetNetworksAsync(CancellationToken cancellationToken) => Task.FromResult(Networks);
        public Task<IReadOnlyList<AddressEntry>> GetAddressesAsync(CancellationToken cancellationToken) => Task.FromResult(Addresses);
        public Task<IReadOnlyList<Balance>> GetBalancesAsync(CancellationToken cancellationToken) => Task.FromResult(Balances);

        public Task<OrderInfo> PlaceBuyAsync(string clientId, decimal quantity, decimal price, CancellationToken cancellationToken)
        {
            BuyCalls++;
            BeforeBuy?.Invoke(clientId, quantity, price);
            var order = new OrderInfo(12345, clientId, "usdtinr", "buy", "limit", price, quantity, 0m, "wait");
            Order = order;
            return Task.FromResult(order);
        }

        public Task<OrderInfo> GetOrderAsync(string clientId, CancellationToken cancellationToken) =>
            Task.FromResult(Order ?? throw new InvalidOperationException("Synthetic order was not configured."));

        public Task<OrderInfo> CancelOrderAsync(long orderId, CancellationToken cancellationToken)
        {
            CancellationCalls++;
            var order = (Order ?? throw new InvalidOperationException("Synthetic order was not configured.")) with { Status = "cancel" };
            Order = order;
            return Task.FromResult(order);
        }

        public Task<IReadOnlyList<TradeFill>> GetTradesAsync(long orderId, CancellationToken cancellationToken) => Task.FromResult(Trades);

        public Task<WithdrawalReceipt> WithdrawAsync(string clientId, long addressBookId, decimal amount,
            string consent, CancellationToken cancellationToken)
        {
            WithdrawalCalls++;
            LastWithdrawalAmount = amount;
            BeforeWithdrawal?.Invoke(clientId, addressBookId, amount, consent);
            return Task.FromResult(new WithdrawalReceipt("synthetic-withdrawal", clientId));
        }

        public Task<IReadOnlyList<WithdrawalInfo>> GetWithdrawalsAsync(string clientId, CancellationToken cancellationToken) =>
            Task.FromResult(Withdrawals);
    }
}
