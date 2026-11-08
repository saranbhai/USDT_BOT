namespace MyBot.Core;

public interface IStrategyExchangeClient
{
    Task<IReadOnlyList<MarketCandle>> GetCandlesAsync(CancellationToken cancellationToken);
    Task<MarketQuote> GetQuoteAsync(CancellationToken cancellationToken);
    Task<DateTimeOffset> GetServerTimeAsync(CancellationToken cancellationToken);
    Task<OrderInfo> PlaceSellAsync(string clientId, decimal quantity, decimal price,
        CancellationToken cancellationToken);
    Task<OrderInfo> GetStrategyOrderAsync(string clientId, CancellationToken cancellationToken);
    Task<OrderInfo> CancelStrategyOrderAsync(long orderId, CancellationToken cancellationToken);
    Task<IReadOnlyList<TradeFill>> GetStrategyTradesAsync(long orderId, string side,
        CancellationToken cancellationToken);
}
