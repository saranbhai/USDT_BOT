using System.Text.Json;
using MyBot.Application;
using MyBot.Core;
using MyBot.Persistence;

public static class StrategyWorkflowChecks
{
    public static async Task RunAsync()
    {
        (string Name, Func<Task> Check)[] checks =
        [
            ("Paper RSI ticks never submit exchange mutations", PaperTickDoesNotTrade),
            ("The global live flag blocks an RSI live tick", DisabledLiveTradingBlocksOrders),
            ("Refusing live RSI confirmation blocks every exchange mutation", LiveConfirmationRejectedBlocksTick),
            ("Live RSI confirms once and rejects changed settings", LiveConfirmationIsBoundToSettings),
            ("An ambiguous RSI buy is journaled and reconciled without resubmission", AmbiguousBuyIsNotRepeated),
            ("An RSI journal failure prevents submitting a buy", JournalFailureBlocksSubmission),
            ("An already consumed RSI candle cannot buy again after cancellation", CompletedCandleIsConsumedOnce),
            ("A partially filled nonterminal order blocks additional RSI orders", PartialNonterminalBlocksNewOrders),
            ("RSI sells exclude the preexisting USDT balance", ExistingBalanceIsExcluded),
            ("RSI sells cannot exceed the strategy-owned USDT", OwnedBalanceCapsSell),
            ("USDT fill fees reduce RSI ownership exactly once", UsdtFeesAreAccountedOnce),
            ("A canceled RSI buy retains and manages confirmed partial fills", CanceledPartialBuyIsManaged),
            ("An active legacy transfer blocks RSI live trading", ActiveLegacyOperationBlocksLiveTrading),
            ("The SQLite journal preserves RSI pending order recovery state", ActualSqliteJournalRoundTrip)
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
                throw new InvalidOperationException($"Strategy workflow check failed: {name}", exception);
            }
        }
    }

    private static async Task PaperTickDoesNotTrade()
    {
        using var fixture = new Fixture();
        fixture.Options.EnableLiveTrading = false;
        await fixture.Workflow().TickAsync(false, CancellationToken.None);
        var bought = fixture.Store.List().Single().Strategy!;
        Require(bought.Paper && bought.OwnedUsdt > 0m && bought.PaperCashInr < fixture.Options.MaxSpendInr,
            "The paper buy did not remain in its simulated wallet.");

        fixture.Client.SetMarket(fixture.Client.ServerTime.AddMinutes(30), rising: true);
        await fixture.Workflow().TickAsync(false, CancellationToken.None);
        Equal(0m, fixture.Store.List().Single().Strategy!.OwnedUsdt);
        Equal(0, fixture.Client.Mutations);
    }

    private static async Task DisabledLiveTradingBlocksOrders()
    {
        using var fixture = new Fixture();
        fixture.Options.EnableLiveTrading = false;
        await ThrowsAsync<InvalidOperationException>(() => fixture.Workflow().TickAsync(true, CancellationToken.None));
        Equal(0, fixture.Client.Mutations);
    }

    private static async Task LiveConfirmationRejectedBlocksTick()
    {
        using var fixture = new Fixture();
        await ThrowsAsync<InvalidOperationException>(() =>
            fixture.Workflow(_ => false).TickAsync(true, CancellationToken.None));
        Equal(0, fixture.Client.Mutations);
        Equal(0, fixture.Store.List().Count);
    }

    private static async Task LiveConfirmationIsBoundToSettings()
    {
        using var fixture = new Fixture();
        var confirmations = 0;
        var workflow = fixture.Workflow(_ =>
        {
            confirmations++;
            return true;
        });
        await workflow.TickAsync(true, CancellationToken.None);
        await workflow.TickAsync(true, CancellationToken.None);
        Equal(1, confirmations);
        Equal(1, fixture.Client.BuyCalls);

        fixture.Options.MaxSpendInr += 1000m;
        await ThrowsAsync<InvalidOperationException>(() => workflow.TickAsync(true, CancellationToken.None));
        Equal(1, confirmations);
        Equal(1, fixture.Client.Mutations);
    }

    private static async Task AmbiguousBuyIsNotRepeated()
    {
        using var fixture = new Fixture();
        fixture.Client.BeforeBuy = (clientId, quantity, price) =>
        {
            var state = fixture.Store.List().Single().Strategy!;
            Equal(100m, state.InitialUsdtBalance);
            var pending = state.Pending ?? throw new InvalidOperationException("Buy intent was not journaled.");
            Equal(clientId, pending.ClientId);
            Equal("buy", pending.Side);
            Equal(quantity, pending.Quantity);
            Equal(price, pending.Price);
            throw new TimeoutException("Synthetic timeout after the exchange may have accepted the buy.");
        };

        await ThrowsAsync<TimeoutException>(() => fixture.Workflow().TickAsync(true, CancellationToken.None));
        Require(fixture.Store.List().Single().Strategy!.Pending is not null,
            "An ambiguous buy lost its durable intent.");

        await fixture.Workflow().TickAsync(true, CancellationToken.None);
        Equal(1, fixture.Client.BuyCalls);
        Require(fixture.Client.OrderReads > 0, "Restart did not reconcile the saved client order ID.");
        Equal(0, fixture.Client.SellCalls);
    }

    private static async Task JournalFailureBlocksSubmission()
    {
        using var fixture = new Fixture();
        static void FailPendingWrite(OperationRecord operation)
        {
            if (operation.Strategy?.Pending is not null)
                throw new IOException("Synthetic journal write failure.");
        }
        fixture.Store.BeforeInsert = FailPendingWrite;
        fixture.Store.BeforeSave = FailPendingWrite;

        await ThrowsAsync<IOException>(() => fixture.Workflow().TickAsync(true, CancellationToken.None));
        Equal(0, fixture.Client.Mutations);
        Require(fixture.Store.List().All(operation => operation.Strategy?.Pending is null),
            "The failed journal write changed the durable order intent.");
    }

    private static async Task CompletedCandleIsConsumedOnce()
    {
        using var fixture = new Fixture();
        await fixture.Workflow().TickAsync(true, CancellationToken.None);
        var submitted = fixture.Store.List().Single().Strategy!;
        Require(submitted.LastCandle.HasValue, "The evaluated candle was not recorded before submission.");
        fixture.Client.Order = fixture.Client.Order! with { Status = "cancel" };

        await fixture.Workflow().TickAsync(true, CancellationToken.None);
        await fixture.Workflow().TickAsync(true, CancellationToken.None);
        Equal(1, fixture.Client.BuyCalls);
        Equal(0m, fixture.Store.List().Single().Strategy!.OwnedUsdt);
        Equal(submitted.LastCandle, fixture.Store.List().Single().Strategy!.LastCandle);
    }

    private static async Task PartialNonterminalBlocksNewOrders()
    {
        using var fixture = new Fixture();
        await fixture.Workflow().TickAsync(true, CancellationToken.None);
        fixture.SetBuyFill("wait", 3m);
        fixture.Client.SetMarket(fixture.Client.ServerTime.AddMinutes(30), rising: true);

        await fixture.Workflow().TickAsync(true, CancellationToken.None);
        await fixture.Workflow().TickAsync(true, CancellationToken.None);
        var state = fixture.Store.List().Single().Strategy!;
        Require(state.Pending is not null, "A nonterminal partial order was prematurely cleared.");
        Equal(3m, state.OwnedUsdt);
        Equal(1, fixture.Client.BuyCalls);
        Equal(0, fixture.Client.SellCalls);
    }

    private static async Task ExistingBalanceIsExcluded()
    {
        using (var fixture = new Fixture())
        {
            fixture.SeedOwned(10m, 104m);
            await fixture.Workflow().TickAsync(true, CancellationToken.None);
            Equal(1, fixture.Client.SellCalls);
            Equal(4m, fixture.Client.LastSellQuantity);
        }

        using (var fixture = new Fixture())
        {
            fixture.SeedOwned(10m, 100m);
            try
            {
                await fixture.Workflow().TickAsync(true, CancellationToken.None);
            }
            catch (InvalidOperationException)
            {
            }
            Equal(0, fixture.Client.Mutations);
        }
    }

    private static async Task OwnedBalanceCapsSell()
    {
        using var fixture = new Fixture();
        fixture.SeedOwned(3m, 120m);
        await fixture.Workflow().TickAsync(true, CancellationToken.None);
        Equal(1, fixture.Client.SellCalls);
        Equal(3m, fixture.Client.LastSellQuantity);
        Equal(0, fixture.Client.BuyCalls);
    }

    private static async Task UsdtFeesAreAccountedOnce()
    {
        using var fixture = new Fixture();
        await fixture.Workflow().TickAsync(true, CancellationToken.None);
        fixture.SetBuyFill("cancel", 3m, 0.1m);

        await fixture.Workflow().StatusAsync(true, CancellationToken.None);
        Equal(2.9m, fixture.Store.List().Single().Strategy!.OwnedUsdt);
        await fixture.Workflow().StatusAsync(true, CancellationToken.None);
        Equal(2.9m, fixture.Store.List().Single().Strategy!.OwnedUsdt);

        fixture.Client.SetMarket(fixture.Client.ServerTime.AddMinutes(30), rising: true);
        await fixture.Workflow().TickAsync(true, CancellationToken.None);
        Equal(2.9m, fixture.Client.LastSellQuantity);
    }

    private static async Task CanceledPartialBuyIsManaged()
    {
        using var fixture = new Fixture();
        await fixture.Workflow().TickAsync(true, CancellationToken.None);
        fixture.SetBuyFill("cancel", 3m);
        await fixture.Workflow().StatusAsync(true, CancellationToken.None);
        var state = fixture.Store.List().Single().Strategy!;
        Equal(3m, state.OwnedUsdt);
        Require(state.Pending is null, "A terminal canceled buy remained pending after its fills were reconciled.");

        fixture.Client.SetMarket(fixture.Client.ServerTime.AddMinutes(30), rising: true);
        await fixture.Workflow().TickAsync(true, CancellationToken.None);
        Equal(1, fixture.Client.SellCalls);
        Equal(3m, fixture.Client.LastSellQuantity);
    }

    private static async Task ActiveLegacyOperationBlocksLiveTrading()
    {
        using var fixture = new Fixture();
        fixture.Store.Insert(new OperationRecord
        {
            Id = "11111111111111111111111111111111",
            CreatedAt = fixture.Client.ServerTime,
            Stage = OperationStage.BuyFilled,
            InitialUsdtBalance = 100m,
            AcquiredUsdt = 3m
        });

        await ThrowsAsync<InvalidOperationException>(() => fixture.Workflow().TickAsync(true, CancellationToken.None));
        Equal(0, fixture.Client.Mutations);
    }

    private static Task ActualSqliteJournalRoundTrip()
    {
        using var directory = new TemporaryDirectory();
        var databasePath = Path.Combine(directory.Path, "operations.db");
        var expected = new OperationRecord
        {
            Id = "33333333333333333333333333333333",
            CreatedAt = new DateTimeOffset(2026, 11, 8, 12, 0, 0, TimeSpan.Zero),
            Stage = OperationStage.StrategyOpen,
            Strategy = new StrategyState
            {
                Paper = false,
                InitialUsdtBalance = 100m,
                LastCandle = new DateTimeOffset(2026, 11, 8, 11, 30, 0, TimeSpan.Zero),
                Pending = new PendingStrategyOrder
                {
                    ClientId = "r44444444444444444444444444444444",
                    Side = "buy",
                    Quantity = 9.803m,
                    Price = 100m
                }
            }
        };

        using (var store = new SqliteOperationStore(databasePath))
        {
            store.Insert(expected);
            var state = expected.Strategy!;
            var pending = state.Pending!;
            state.OwnedUsdt = 2.9m;
            pending.ExchangeOrderId = 12345;
            pending.AccountedExecuted = 3m;
            pending.AccountedUsdtFees = 0.1m;
            pending.CancelRequested = true;
            store.Save(expected);
        }

        using var reopened = new SqliteOperationStore(databasePath);
        Equal(JsonSerializer.Serialize(expected), JsonSerializer.Serialize(reopened.Get(expected.Id)));
        Equal(1, reopened.List().Count);
        Equal(OperationStage.StrategyOpen, reopened.List().Single().Stage);
        return Task.CompletedTask;
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
            MaxSpendInr = 1000m,
            MaxPriceInr = 102m,
            FeeReservePercent = 2m,
            VerifiedMinOrderInr = 50m,
            EnableLiveTrading = true
        };
        public MemoryStore Store { get; } = new();
        public FakeClient Client { get; } = new();
        public StringWriter Output { get; } = new();

        public RsiStrategyWorkflow Workflow(Func<string, bool>? confirmation = null) =>
            new(Options, Client, Client, Store, Output, confirmation ?? (_ => true));

        public void SeedOwned(decimal owned, decimal freeUsdt)
        {
            Store.Insert(new OperationRecord
            {
                Id = "22222222222222222222222222222222",
                CreatedAt = Client.ServerTime,
                Stage = OperationStage.StrategyOpen,
                Strategy = new StrategyState
                {
                    Paper = false,
                    InitialUsdtBalance = 100m,
                    OwnedUsdt = owned
                }
            });
            Client.Balances = [new Balance("inr", 10000m), new Balance("usdt", freeUsdt)];
            Client.SetMarket(Client.ServerTime, rising: true);
        }

        public void SetBuyFill(string status, decimal executed, decimal fee = 0m)
        {
            var order = Client.Order ?? throw new InvalidOperationException("No buy was submitted.");
            Client.Order = order with { ExecutedQuantity = executed, Status = status };
            Client.Trades = [new TradeFill(1, order.Id, "usdtinr", "buy", executed, fee, "usdt")];
            Client.Balances = [new Balance("inr", 10000m), new Balance("usdt", 100m + executed - fee)];
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

    private sealed class FakeClient : IWazirXClient, IStrategyExchangeClient
    {
        public DateTimeOffset ServerTime { get; private set; } = new(2026, 11, 8, 12, 0, 0, TimeSpan.Zero);
        public IReadOnlyList<MarketCandle> Candles { get; private set; } = [];
        public IReadOnlyList<Balance> Balances { get; set; } = [new Balance("inr", 10000m), new Balance("usdt", 100m)];
        public IReadOnlyList<TradeFill> Trades { get; set; } = [];
        public OrderInfo? Order { get; set; }
        public Action<string, decimal, decimal>? BeforeBuy { get; set; }
        public int BuyCalls { get; private set; }
        public int SellCalls { get; private set; }
        public int CancellationCalls { get; private set; }
        public int WithdrawalCalls { get; private set; }
        public int OrderReads { get; private set; }
        public decimal LastSellQuantity { get; private set; }
        public int Mutations => BuyCalls + SellCalls + CancellationCalls + WithdrawalCalls;

        public FakeClient() => SetMarket(ServerTime, rising: false);

        public void SetMarket(DateTimeOffset now, bool rising)
        {
            ServerTime = now;
            Candles = Enumerable.Range(0, RsiStrategy.MinimumCompletedCandles).Select(index =>
            {
                var close = 100m + (rising ? 1m : -1m) * index * 0.001m;
                return new MarketCandle(now - RsiStrategy.Interval * (RsiStrategy.MinimumCompletedCandles - index),
                    close, close + 0.01m, close - 0.01m, close, 10000m);
            }).ToArray();
        }

        public Task<SymbolInfo> GetSymbolAsync(CancellationToken cancellationToken) => Task.FromResult(
            new SymbolInfo("usdtinr", "usdt", "inr", "trading", true, true, 6,
                0.01m, 0.01m, 100000m, 0.001m, 0.001m, 100000m, 50m));
        public Task<decimal> GetBestAskAsync(CancellationToken cancellationToken) => Task.FromResult(100m);
        public Task<MarketQuote> GetQuoteAsync(CancellationToken cancellationToken) => Task.FromResult(new MarketQuote(99.99m, 100m));
        public Task<DateTimeOffset> GetServerTimeAsync(CancellationToken cancellationToken) => Task.FromResult(ServerTime);
        public Task<IReadOnlyList<MarketCandle>> GetCandlesAsync(CancellationToken cancellationToken) => Task.FromResult(Candles);
        public Task<IReadOnlyList<Balance>> GetBalancesAsync(CancellationToken cancellationToken) => Task.FromResult(Balances);

        public Task<OrderInfo> PlaceBuyAsync(string clientId, decimal quantity, decimal price, CancellationToken cancellationToken)
        {
            BuyCalls++;
            var order = new OrderInfo(12345, clientId, "usdtinr", "buy", "limit", price, quantity, 0m, "wait");
            Order = order;
            BeforeBuy?.Invoke(clientId, quantity, price);
            return Task.FromResult(order);
        }

        public Task<OrderInfo> PlaceSellAsync(string clientId, decimal quantity, decimal price, CancellationToken cancellationToken)
        {
            SellCalls++;
            LastSellQuantity = quantity;
            var order = new OrderInfo(12346, clientId, "usdtinr", "sell", "limit", price, quantity, 0m, "wait");
            Order = order;
            return Task.FromResult(order);
        }

        public Task<OrderInfo> GetStrategyOrderAsync(string clientId, CancellationToken cancellationToken)
        {
            OrderReads++;
            var order = Order ?? throw new InvalidOperationException("Synthetic order was not configured.");
            Equal(clientId, order.ClientOrderId);
            return Task.FromResult(order);
        }

        public Task<OrderInfo> GetOrderAsync(string clientId, CancellationToken cancellationToken) =>
            GetStrategyOrderAsync(clientId, cancellationToken);

        public Task<OrderInfo> CancelStrategyOrderAsync(long orderId, CancellationToken cancellationToken)
        {
            CancellationCalls++;
            var order = Order ?? throw new InvalidOperationException("Synthetic order was not configured.");
            Equal(orderId, order.Id);
            Order = order with { Status = "cancel" };
            return Task.FromResult(Order!);
        }

        public Task<OrderInfo> CancelOrderAsync(long orderId, CancellationToken cancellationToken) =>
            CancelStrategyOrderAsync(orderId, cancellationToken);
        public Task<IReadOnlyList<TradeFill>> GetStrategyTradesAsync(long orderId, string side,
            CancellationToken cancellationToken) => Task.FromResult(Trades);
        public Task<IReadOnlyList<TradeFill>> GetTradesAsync(long orderId, CancellationToken cancellationToken) => Task.FromResult(Trades);
        public Task<IReadOnlyList<NetworkInfo>> GetNetworksAsync(CancellationToken cancellationToken) =>
            throw new InvalidOperationException("RSI trading must not query withdrawal networks.");
        public Task<IReadOnlyList<AddressEntry>> GetAddressesAsync(CancellationToken cancellationToken) =>
            throw new InvalidOperationException("RSI trading must not query withdrawal addresses.");
        public Task<IReadOnlyList<WithdrawalInfo>> GetWithdrawalsAsync(string clientId, CancellationToken cancellationToken) =>
            throw new InvalidOperationException("RSI trading must not query withdrawal history.");

        public Task<WithdrawalReceipt> WithdrawAsync(string clientId, long addressBookId, decimal amount,
            string consent, CancellationToken cancellationToken)
        {
            WithdrawalCalls++;
            throw new InvalidOperationException("RSI trading must not withdraw USDT.");
        }
    }

    private sealed class TemporaryDirectory : IDisposable
    {
        private readonly DirectoryInfo directory = Directory.CreateTempSubdirectory("MyBot.StrategyChecks-");
        public string Path => directory.FullName;

        public void Dispose() => directory.Delete(recursive: true);
    }
}
