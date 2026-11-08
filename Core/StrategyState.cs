namespace MyBot.Core;

public sealed class StrategyState
{
    public bool Paper { get; set; }
    public decimal InitialUsdtBalance { get; set; }
    public decimal OwnedUsdt { get; set; }
    public decimal PaperCashInr { get; set; }
    public DateTimeOffset? LastCandle { get; set; }
    public PendingStrategyOrder? Pending { get; set; }
}

public sealed class PendingStrategyOrder
{
    public string ClientId { get; set; } = "";
    public string Side { get; set; } = "";
    public decimal Quantity { get; set; }
    public decimal Price { get; set; }
    public long? ExchangeOrderId { get; set; }
    public decimal AccountedExecuted { get; set; }
    public decimal AccountedUsdtFees { get; set; }
    public bool CancelRequested { get; set; }
}
