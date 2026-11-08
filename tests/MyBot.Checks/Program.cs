using System.Text.Json;
using Microsoft.Data.Sqlite;
using MyBot.Core;
using MyBot.Persistence;

return OfflineChecks.Run();

internal static class OfflineChecks
{
    private static readonly DateTimeOffset Now = new(2026, 11, 7, 12, 0, 0, TimeSpan.Zero);

    public static int Run()
    {
        (string Name, Action Check)[] checks =
        [
            ("Configured enabled route with the lowest fee wins", CheapestConfiguredRoute),
            ("Disabled, memo-required, absent, and unconfigured routes cannot win", IneligibleRoutesIgnored),
            ("No available configured route blocks buying", NoAvailableRoute),
            ("An unconfigured cheap route cannot be selected", NoUnconfiguredFallback),
            ("Saved address must match exactly", SavedAddressMismatch),
            ("Saved address network must match", SavedNetworkMismatch),
            ("Saved address book ID must match", SavedIdMismatch),
            ("Saved address currency must be USDT", SavedCurrencyMismatch),
            ("Ambiguous saved address book IDs are rejected", AmbiguousSavedId),
            ("Masked destination addresses are rejected", MaskedAddress),
            ("Memo-required networks are rejected", MemoRequiredRoute),
            ("Address cooldown blocks until the specified time", AddressCooldown),
            ("Disabled and universal saved addresses are rejected", DisabledSavedAddress),
            ("Receiving deposit enablement must be explicitly confirmed", UnconfirmedReceiver),
            ("Network metadata must match the configured route", NetworkMismatch),
            ("Duplicate configured networks are rejected", DuplicateConfiguredNetwork),
            ("A withdrawal above the fee cap is rejected", FeeCap),
            ("Exchange fee mode uses live fees and preserves explicit cap behavior", ExchangeFeeMode),
            ("Missing or invalid withdrawal step fails closed", MissingWithdrawalStep),
            ("Source withdrawal minimum applies after the fee reserve", SourceWithdrawalMinimum),
            ("Receiver minimum applies after conservative fee deduction", ReceiverDepositMinimum),
            ("Source withdrawal maximum is enforced", SourceWithdrawalMaximum),
            ("Existing USDT cannot increase the newly acquired amount", AcquiredAmountCap),
            ("Free USDT caps the withdrawal", FreeBalanceCap),
            ("Decimal order rounding stays within the budget and price cap", DecimalBudgetFloor),
            ("An ask above the rounded price cap blocks buying", AskAboveLimit),
            ("Insufficient INR blocks buying without reducing the order", InsufficientInr),
            ("Missing, duplicate, and negative INR metadata is rejected", InvalidInrMetadata),
            ("Order minimum notional is enforced", OrderMinimum),
            ("Quantity step must fit base precision", IncompatibleQuantityStep),
            ("Decimal step rounding preserves exact boundaries", DecimalRoundDown),
            ("SQLite Save and Get persist the complete operation across reopen", StoreRoundTrip),
            ("Duplicate insertion cannot overwrite an operation", StoreDuplicateInsert),
            ("Missing journal records cannot be read or updated", StoreMissingRecord),
            ("Existing empty, corrupt, and schema-less journals are rejected", InvalidJournals),
            ("Corrupt journal records fail closed", InvalidJournalRecord),
            ("Disposed journal cannot be used", StoreDisposal),
            ("Process lock blocks collisions and can be reacquired", LockCollisionAndReacquire),
            ("Workflow journal boundaries and exchange ambiguity fail safely", () => WorkflowChecks.RunAsync().GetAwaiter().GetResult()),
            ("Exchange signing, metadata, and mutation failures remain safe", () => ExchangeChecks.RunAsync().GetAwaiter().GetResult()),
            ("Rate-limited reads retry safely while financial requests remain single-attempt", () => RateLimitChecks.RunAsync().GetAwaiter().GetResult()),
            ("Completed 30-minute Wilder RSI(10) signals remain exact", RsiStrategyChecks.Run),
            ("Strategy market data and sell requests remain isolated", () => StrategyExchangeChecks.RunAsync().GetAwaiter().GetResult()),
            ("RSI strategy paper trading, inventory, and journal boundaries remain safe", () => StrategyWorkflowChecks.RunAsync().GetAwaiter().GetResult())
        ];

        var failed = 0;
        foreach (var (name, check) in checks)
        {
            try
            {
                check();
                Console.WriteLine($"PASS {name}");
            }
            catch (Exception exception)
            {
                failed++;
                Console.Error.WriteLine($"FAIL {name}: {exception.GetType().Name}: {exception.Message}");
            }
        }

        Console.WriteLine($"{checks.Length - failed}/{checks.Length} offline checks passed. No exchange requests were made.");
        return failed == 0 ? 0 : 1;
    }

    private static void CheapestConfiguredRoute()
    {
        var options = Options(Route("networkA", 10), Route("networkB", 20));
        var plan = Plan(options, [Network("networkA", 0.20m), Network("networkB", 0.05m)],
            [Address(options.Routes[0]), Address(options.Routes[1])]);
        Equal("networkB", plan.Network.Network);
        Equal(20L, plan.Route.AddressBookId);
        Equal(0.05m, plan.Network.Fee);
    }

    private static void IneligibleRoutesIgnored()
    {
        var options = Options(Route("networkA", 10), Route("networkB", 20),
            Route("networkC", 30), Route("networkD", 40));
        var networks = new[]
        {
            Network("networkA", 0.01m) with { WithdrawEnabled = false },
            Network("networkB", 0.20m),
            Network("networkC", 0.02m) with { MemoRequired = true },
            Network("unconfiguredNetwork", 0m)
        };
        var plan = Plan(options, networks, options.Routes.Select(Address).ToArray());
        Equal("networkB", plan.Network.Network);
    }

    private static void NoAvailableRoute()
    {
        var options = Options();
        Throws<InvalidOperationException>(() => Plan(options,
            [Network() with { WithdrawEnabled = false }], [Address(options.Routes[0])]));
    }

    private static void NoUnconfiguredFallback()
    {
        var options = Options();
        Throws<InvalidOperationException>(() => Plan(options, [Network("networkB", 0.01m)],
            [Address(options.Routes[0])]));
    }

    private static void SavedAddressMismatch()
    {
        var route = Route();
        Throws<InvalidOperationException>(() => Validate(route,
            Address(route) with { Address = "0x2222222222222222222222222222222222222222" }));
    }

    private static void SavedNetworkMismatch()
    {
        var route = Route();
        Throws<InvalidOperationException>(() => Validate(route, Address(route) with { Network = "networkB" }));
    }

    private static void SavedIdMismatch()
    {
        var route = Route();
        Throws<InvalidOperationException>(() => Validate(route, Address(route) with { Id = 999 }));
    }

    private static void SavedCurrencyMismatch()
    {
        var route = Route();
        Throws<InvalidOperationException>(() => Validate(route, Address(route) with { Currency = "btc" }));
    }

    private static void AmbiguousSavedId()
    {
        var route = Route();
        Throws<InvalidOperationException>(() => TransferPlanner.ValidateRoute(Network(), route,
            [Address(route), Address(route)], Now));
    }

    private static void MaskedAddress()
    {
        foreach (var masked in new[] { "0x111***111", "0x111...111", "0x111\u2026111" })
        {
            var route = Route();
            route.Address = masked;
            Throws<InvalidOperationException>(() => TransferPlanner.ValidateOptions(Options(route)));
        }
    }

    private static void MemoRequiredRoute()
    {
        var route = Route();
        Throws<InvalidOperationException>(() => TransferPlanner.ValidateRoute(
            Network() with { MemoRequired = true }, route, [Address(route)], Now));
    }

    private static void AddressCooldown()
    {
        var route = Route();
        Throws<InvalidOperationException>(() => Validate(route,
            Address(route) with { AllowedAfter = Now.AddSeconds(1) }));
        Validate(route, Address(route) with { AllowedAfter = Now });
    }

    private static void DisabledSavedAddress()
    {
        var route = Route();
        Throws<InvalidOperationException>(() => Validate(route, Address(route) with { WithdrawAllowed = false }));
        Throws<InvalidOperationException>(() => Validate(route, Address(route) with { IsUniversal = true }));
    }

    private static void UnconfirmedReceiver()
    {
        var route = Route();
        route.DepositEnabledConfirmed = false;
        Throws<InvalidOperationException>(() => Validate(route, Address(route)));
    }

    private static void NetworkMismatch()
    {
        var route = Route();
        Throws<InvalidOperationException>(() => TransferPlanner.ValidateRoute(Network("networkB"),
            route, [Address(route)], Now));
    }

    private static void DuplicateConfiguredNetwork()
    {
        Throws<InvalidOperationException>(() => TransferPlanner.ValidateOptions(
            Options(Route("networkA", 10), Route("NETWORKA", 20))));
    }

    private static void FeeCap()
    {
        Throws<InvalidOperationException>(() => TransferPlanner.PlanWithdrawal(10m, 10m,
            Network() with { Fee = 0.21m }, Route(), 0.20m));
        var atCap = TransferPlanner.PlanWithdrawal(10m, 10m, Network(), Route(), 0.20m);
        Equal(9.8m, atCap.Amount);
    }

    private static void ExchangeFeeMode()
    {
        var options = Options();
        options.UseExchangeWithdrawalFee = true;
        options.MaxWithdrawalFeeUsdt = 0m;
        var plan = Plan(options, [Network() with { Fee = 0.4m }], [Address(options.Routes[0])]);
        Equal(0.4m, plan.Network.Fee);
        Equal(9.206m, plan.WithdrawalAmount);
        Equal(8.806m, plan.ConservativeReceiveAmount);

        var noFee = TransferPlanner.PlanWithdrawal(10m, 10m,
            Network() with { Fee = 0m }, Route(), 0m, true);
        Equal(10m, noFee.Amount);
        Equal(10m, noFee.ConservativeReceive);
        Throws<InvalidOperationException>(() => TransferPlanner.PlanWithdrawal(10m, 10m,
            Network(), Route(), 0m));
        Throws<InvalidOperationException>(() => TransferPlanner.PlanWithdrawal(10m, 10m,
            Network(), Route(), -1m, true));

        options.UseExchangeWithdrawalFee = false;
        Throws<InvalidOperationException>(() => TransferPlanner.ValidateOptions(options));
        options.UseExchangeWithdrawalFee = true;
        options.MaxWithdrawalFeeUsdt = -1m;
        Throws<InvalidOperationException>(() => TransferPlanner.ValidateOptions(options));
    }

    private static void MissingWithdrawalStep()
    {
        foreach (decimal? step in new decimal?[] { null, 0m, -0.01m })
        {
            Throws<InvalidOperationException>(() => TransferPlanner.PlanWithdrawal(10m, 10m,
                Network() with { IntegerMultiple = step }, Route(), 1m));
        }
    }

    private static void SourceWithdrawalMinimum()
    {
        var route = Route();
        route.MinimumDepositUsdt = 0.01m;
        var network = Network() with { Minimum = 10m };
        Throws<InvalidOperationException>(() => TransferPlanner.PlanWithdrawal(10m, 10m, network, route, 1m));
        var boundary = TransferPlanner.PlanWithdrawal(10.2m, 10.2m, network, route, 1m);
        Equal(10m, boundary.Amount);
    }

    private static void ReceiverDepositMinimum()
    {
        var route = Route();
        route.MinimumDepositUsdt = 9.7m;
        Throws<InvalidOperationException>(() => TransferPlanner.PlanWithdrawal(10m, 10m,
            Network() with { Fee = 0.2m, IntegerMultiple = 1m }, route, 1m));
        route.MinimumDepositUsdt = 9.6m;
        var boundary = TransferPlanner.PlanWithdrawal(10m, 10m, Network(), route, 1m);
        Equal(9.6m, boundary.ConservativeReceive);
    }

    private static void SourceWithdrawalMaximum()
    {
        Throws<InvalidOperationException>(() => TransferPlanner.PlanWithdrawal(10m, 10m,
            Network() with { Maximum = 9.7m }, Route(), 1m));
    }

    private static void AcquiredAmountCap()
    {
        var result = TransferPlanner.PlanWithdrawal(10.1234m, 100m,
            Network() with { IntegerMultiple = 0.01m }, Route(), 1m);
        Equal(9.92m, result.Amount);
        Equal(9.72m, result.ConservativeReceive);
    }

    private static void FreeBalanceCap()
    {
        var result = TransferPlanner.PlanWithdrawal(100m, 10.1234m,
            Network() with { IntegerMultiple = 0.01m }, Route(), 1m);
        Equal(9.92m, result.Amount);
        Equal(9.72m, result.ConservativeReceive);
    }

    private static void DecimalBudgetFloor()
    {
        var options = Options();
        options.MaxPriceInr = 100.009m;
        var plan = Plan(options, [Network()], [Address(options.Routes[0])]);
        Equal(100m, plan.Price);
        Equal(9.803m, plan.Quantity);
        Equal(980.3m, plan.MaximumOrderCostInr);
        Equal(999.906m, plan.BudgetWithReserveInr);
        Equal(9.406m, plan.WithdrawalAmount);
        Equal(9.206m, plan.ConservativeReceiveAmount);
        Require(plan.BudgetWithReserveInr <= options.MaxSpendInr, "Rounded order exceeds its budget.");
        Require((plan.Quantity + Symbol().QuantityStep!.Value) * plan.Price * 1.02m > options.MaxSpendInr,
            "The next quantity step should exceed the budget.");
    }

    private static void AskAboveLimit()
    {
        var options = Options();
        options.MaxPriceInr = 100.009m;
        Throws<InvalidOperationException>(() => TransferPlanner.CreatePlan(options, Symbol(), 100.001m,
            [Network()], [Address(options.Routes[0])], Balances(), Now));
    }

    private static void InsufficientInr()
    {
        var options = Options();
        Throws<InvalidOperationException>(() => TransferPlanner.CreatePlan(options, Symbol(), 99m,
            [Network()], [Address(options.Routes[0])], [new Balance("inr", 999.905m)], Now));
    }

    private static void OrderMinimum()
    {
        var options = Options();
        Throws<InvalidOperationException>(() => TransferPlanner.CreatePlan(options,
            Symbol() with { MinimumNotional = 981m }, 99m,
            [Network()], [Address(options.Routes[0])], Balances(), Now));
    }

    private static void InvalidInrMetadata()
    {
        var options = Options();
        Balance[][] invalidBalances =
        [
            [new("usdt", 100m)],
            [new("inr", 10000m), new("INR", 10000m)],
            [new("inr", -1m)]
        ];
        foreach (var balances in invalidBalances)
        {
            Throws<InvalidOperationException>(() => TransferPlanner.CreatePlan(options, Symbol(), 99m,
                [Network()], [Address(options.Routes[0])], balances, Now));
        }
    }

    private static void IncompatibleQuantityStep()
    {
        var options = Options();
        Throws<InvalidOperationException>(() => TransferPlanner.CreatePlan(options,
            Symbol() with { BasePrecision = 2, QuantityStep = 0.001m }, 99m,
            [Network()], [Address(options.Routes[0])], Balances(), Now));
    }

    private static void DecimalRoundDown()
    {
        Equal(0.3m, TransferPlanner.RoundDown(0.3m, 0.1m));
        Equal(1.99m, TransferPlanner.RoundDown(1.999m, 0.01m));
        Equal(0m, TransferPlanner.RoundDown(0.009m, 0.01m));
        Throws<ArgumentOutOfRangeException>(() => TransferPlanner.RoundDown(1m, 0m));
        Throws<ArgumentOutOfRangeException>(() => TransferPlanner.RoundDown(-1m, 0.1m));
    }

    private static void StoreRoundTrip()
    {
        using var directory = new TemporaryDirectory();
        var path = Path.Combine(directory.Path, "operations.db");
        var expected = Operation("roundtrip");
        using (var store = new SqliteOperationStore(path))
        {
            store.Insert(expected);
            expected.Stage = OperationStage.WithdrawalAcknowledged;
            expected.AcquiredUsdt = 9.793m;
            expected.WithdrawalAmount = 9.593m;
            expected.WithdrawalFee = 0.2m;
            expected.WithdrawalId = "synthetic-withdrawal-id";
            store.Save(expected);
        }

        using var reopened = new SqliteOperationStore(path);
        Equal(JsonSerializer.Serialize(expected), JsonSerializer.Serialize(reopened.Get(expected.Id)));
        Equal("broundtrip", reopened.Get(expected.Id).BuyClientId);
        Equal("wroundtrip", reopened.Get(expected.Id).WithdrawalClientId);
        Equal(1, reopened.List().Count);
    }

    private static void StoreDuplicateInsert()
    {
        using var directory = new TemporaryDirectory();
        using var store = new SqliteOperationStore(Path.Combine(directory.Path, "operations.db"));
        var original = Operation("duplicate");
        store.Insert(original);
        var duplicate = Operation("duplicate");
        duplicate.Quantity = 999m;
        duplicate.Stage = OperationStage.WithdrawalSubmitting;
        Throws<SqliteException>(() => store.Insert(duplicate));
        Equal(JsonSerializer.Serialize(original), JsonSerializer.Serialize(store.Get(original.Id)));
        Equal(1, store.List().Count);
    }

    private static void StoreMissingRecord()
    {
        using var directory = new TemporaryDirectory();
        using var store = new SqliteOperationStore(Path.Combine(directory.Path, "operations.db"));
        Throws<KeyNotFoundException>(() => store.Get("missing"));
        Throws<InvalidOperationException>(() => store.Save(Operation("missing")));
        Equal(0, store.List().Count);
    }

    private static void InvalidJournals()
    {
        using var directory = new TemporaryDirectory();
        var empty = Path.Combine(directory.Path, "empty.db");
        File.WriteAllBytes(empty, []);
        Throws<InvalidDataException>(() => { using var store = new SqliteOperationStore(empty); });
        var corrupt = Path.Combine(directory.Path, "corrupt.db");
        File.WriteAllText(corrupt, "This is deliberately not a SQLite database.");
        Throws<SqliteException>(() => { using var store = new SqliteOperationStore(corrupt); });
        var missingSchema = Path.Combine(directory.Path, "schema-less.db");
        ExecuteSql(missingSchema, "CREATE TABLE unrelated (id INTEGER);");
        Throws<SqliteException>(() => { using var store = new SqliteOperationStore(missingSchema); });
    }

    private static void InvalidJournalRecord()
    {
        using var directory = new TemporaryDirectory();
        var path = Path.Combine(directory.Path, "operations.db");
        using (var store = new SqliteOperationStore(path))
        {
            store.Insert(Operation("corrupt-record"));
        }

        ExecuteSql(path, "UPDATE operations SET payload = '{invalid-json' WHERE id = 'corrupt-record';");
        using var reopened = new SqliteOperationStore(path);
        Throws<InvalidDataException>(() => reopened.Get("corrupt-record"));
        Throws<InvalidDataException>(() => reopened.List());
    }

    private static void StoreDisposal()
    {
        using var directory = new TemporaryDirectory();
        var path = Path.Combine(directory.Path, "operations.db");
        var store = new SqliteOperationStore(path);
        store.Insert(Operation("disposed"));
        store.Dispose();
        store.Dispose();
        Throws<ObjectDisposedException>(() => store.Get("disposed"));
        Throws<ObjectDisposedException>(() => store.List());
        Throws<ObjectDisposedException>(() => store.Save(Operation("disposed")));
        Throws<ObjectDisposedException>(() => store.Insert(Operation("another")));
        using var reopened = new SqliteOperationStore(path);
        Equal("disposed", reopened.Get("disposed").Id);
    }

    private static void LockCollisionAndReacquire()
    {
        using var directory = new TemporaryDirectory();
        var path = Path.Combine(directory.Path, "operation.lock");
        using (var first = ProcessLock.Acquire(path))
        {
            Throws<IOException>(() => { using var second = ProcessLock.Acquire(path); });
        }

        using (var reacquired = ProcessLock.Acquire(path))
        {
            Throws<IOException>(() => { using var overlapping = ProcessLock.Acquire(path); });
        }

        using var final = ProcessLock.Acquire(path);
    }

    private static void ExecuteSql(string databasePath, string sql)
    {
        using var connection = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = databasePath,
            Pooling = false
        }.ToString());
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        command.ExecuteNonQuery();
    }

    private static PurchasePlan Plan(BotOptions options, IReadOnlyList<NetworkInfo> networks,
        IReadOnlyList<AddressEntry> addresses) =>
        TransferPlanner.CreatePlan(options, Symbol(), 99m, networks, addresses, Balances(), Now);

    private static void Validate(DestinationRoute route, AddressEntry address) =>
        TransferPlanner.ValidateRoute(Network(), route, [address], Now);

    private static BotOptions Options(params DestinationRoute[] routes) => new()
    {
        MaxSpendInr = 1000m,
        MaxPriceInr = 100m,
        FeeReservePercent = 2m,
        VerifiedMinOrderInr = 100m,
        MaxWithdrawalFeeUsdt = 1m,
        DestinationLabel = "Synthetic offline destination",
        Routes = routes.Length == 0 ? [Route()] : [.. routes]
    };

    private static DestinationRoute Route(string network = "networkA", long id = 10) => new()
    {
        Network = network,
        AddressBookId = id,
        Address = "0x1111111111111111111111111111111111111111",
        MinimumDepositUsdt = 1m,
        DepositEnabledConfirmed = true
    };

    private static AddressEntry Address(DestinationRoute route) =>
        new(route.AddressBookId, "usdt", route.Network, route.Address, true, false, null);

    private static NetworkInfo Network(string network = "networkA", decimal fee = 0.2m) =>
        new(network, "Synthetic network", true, fee, 1m, 10000m, 0.001m, false, "synthetic-consent");

    private static SymbolInfo Symbol() => new("usdtinr", "usdt", "inr", "trading", true, true,
        6, 0.01m, 0.01m, 100000m, 0.001m, 0.001m, 100000m, 100m);

    private static Balance[] Balances() => [new("inr", 10000m), new("usdt", 100m)];

    private static OperationRecord Operation(string id) => new()
    {
        Id = id,
        CreatedAt = Now,
        Stage = OperationStage.BuyAcknowledged,
        DestinationLabel = "Synthetic offline destination",
        Network = "networkA",
        AddressBookId = 10,
        Address = "0x1111111111111111111111111111111111111111",
        MinimumDepositUsdt = 1m,
        LimitPrice = 100m,
        Quantity = 9.803m,
        InitialUsdtBalance = 100m,
        OrderId = 12345
    };

    private static void Equal<T>(T expected, T actual)
    {
        if (!EqualityComparer<T>.Default.Equals(expected, actual))
        {
            throw new InvalidOperationException($"Expected {expected}; received {actual}.");
        }
    }

    private static void Require(bool condition, string message)
    {
        if (!condition)
        {
            throw new InvalidOperationException(message);
        }
    }

    private static void Throws<TException>(Action action) where TException : Exception
    {
        try
        {
            action();
        }
        catch (TException)
        {
            return;
        }

        throw new InvalidOperationException($"Expected {typeof(TException).Name}, but the operation succeeded.");
    }

    private sealed class TemporaryDirectory : IDisposable
    {
        private readonly DirectoryInfo directory = Directory.CreateTempSubdirectory("MyBot.Checks-");
        public string Path => directory.FullName;

        public void Dispose() => directory.Delete(recursive: true);
    }
}
