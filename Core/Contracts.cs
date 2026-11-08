namespace MyBot.Core;

public sealed class BotOptions
{
    public decimal MaxSpendInr { get; set; }
    public decimal MaxPriceInr { get; set; }
    public decimal FeeReservePercent { get; set; } = 2m;
    public decimal VerifiedMinOrderInr { get; set; }
    public decimal MaxWithdrawalFeeUsdt { get; set; }
    public bool UseExchangeWithdrawalFee { get; set; }
    public bool EnableLiveTrading { get; set; }
    public string DestinationLabel { get; set; } = "";
    public List<DestinationRoute> Routes { get; set; } = [];
}

public sealed class DestinationRoute
{
    public string Network { get; set; } = "";
    public long AddressBookId { get; set; }
    public string Address { get; set; } = "";
    public decimal MinimumDepositUsdt { get; set; }
    public bool DepositEnabledConfirmed { get; set; }
}

public sealed record NetworkInfo(string Network, string Name, bool WithdrawEnabled,
    decimal Fee, decimal Minimum, decimal Maximum, decimal? IntegerMultiple,
    bool MemoRequired, string Consent);
public sealed record AddressEntry(long Id, string Currency, string Network, string Address,
    bool WithdrawAllowed, bool IsUniversal, DateTimeOffset? AllowedAfter);
public sealed record SymbolInfo(string Symbol, string BaseAsset, string QuoteAsset,
    string Status, bool SpotAllowed, bool LimitAllowed, int BasePrecision,
    decimal TickSize, decimal MinimumPrice, decimal? MaximumPrice,
    decimal? QuantityStep, decimal? MinimumQuantity, decimal? MaximumQuantity,
    decimal? MinimumNotional);
public sealed record Balance(string Asset, decimal Free);
public sealed record OrderInfo(long Id, string ClientOrderId, string Symbol, string Side,
    string Type, decimal Price, decimal OriginalQuantity, decimal ExecutedQuantity, string Status);
public sealed record TradeFill(long Id, long OrderId, string Symbol, string Side,
    decimal Quantity, decimal Fee, string FeeCurrency);
public sealed record WithdrawalReceipt(string Id, string ClientId);
public sealed record WithdrawalInfo(string Id, string ClientId, string Coin, string Network,
    string Address, decimal Amount, string RawStatus, string TransactionId);
public sealed record PurchasePlan(decimal Price, decimal Quantity, decimal MaximumOrderCostInr,
    decimal BudgetWithReserveInr, NetworkInfo Network, DestinationRoute Route,
    decimal WithdrawalAmount, decimal ConservativeReceiveAmount);

public interface IWazirXClient
{
    Task<SymbolInfo> GetSymbolAsync(CancellationToken cancellationToken);
    Task<decimal> GetBestAskAsync(CancellationToken cancellationToken);
    Task<IReadOnlyList<NetworkInfo>> GetNetworksAsync(CancellationToken cancellationToken);
    Task<IReadOnlyList<AddressEntry>> GetAddressesAsync(CancellationToken cancellationToken);
    Task<IReadOnlyList<Balance>> GetBalancesAsync(CancellationToken cancellationToken);
    Task<OrderInfo> PlaceBuyAsync(string clientId, decimal quantity, decimal price, CancellationToken cancellationToken);
    Task<OrderInfo> GetOrderAsync(string clientId, CancellationToken cancellationToken);
    Task<OrderInfo> CancelOrderAsync(long orderId, CancellationToken cancellationToken);
    Task<IReadOnlyList<TradeFill>> GetTradesAsync(long orderId, CancellationToken cancellationToken);
    Task<WithdrawalReceipt> WithdrawAsync(string clientId, long addressBookId, decimal amount,
        string consent, CancellationToken cancellationToken);
    Task<IReadOnlyList<WithdrawalInfo>> GetWithdrawalsAsync(string clientId, CancellationToken cancellationToken);
}

public enum OperationStage
{
    BuySubmitting,
    BuyAcknowledged,
    BuyFilled,
    BuyCanceled,
    WithdrawalSubmitting,
    WithdrawalAcknowledged,
    DepositConfirmed,
    StrategyPaper,
    StrategyFlat,
    StrategyOpen
}

public sealed class OperationRecord
{
    public string Id { get; set; } = "";
    public DateTimeOffset CreatedAt { get; set; }
    public OperationStage Stage { get; set; }
    public string DestinationLabel { get; set; } = "";
    public string Network { get; set; } = "";
    public long AddressBookId { get; set; }
    public string Address { get; set; } = "";
    public decimal MinimumDepositUsdt { get; set; }
    public decimal LimitPrice { get; set; }
    public decimal Quantity { get; set; }
    public decimal InitialUsdtBalance { get; set; }
    public long? OrderId { get; set; }
    public decimal? AcquiredUsdt { get; set; }
    public decimal? WithdrawalAmount { get; set; }
    public decimal? WithdrawalFee { get; set; }
    public string? WithdrawalId { get; set; }
    public StrategyState? Strategy { get; set; }
    public string BuyClientId => "b" + Id;
    public string WithdrawalClientId => "w" + Id;
}

public interface IOperationStore : IDisposable
{
    void Insert(OperationRecord operation);
    void Save(OperationRecord operation);
    OperationRecord Get(string id);
    IReadOnlyList<OperationRecord> List();
}
