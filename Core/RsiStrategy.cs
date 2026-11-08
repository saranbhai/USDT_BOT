namespace MyBot.Core;

public sealed record MarketCandle(DateTimeOffset OpenTime, decimal Open, decimal High,
    decimal Low, decimal Close, decimal Volume);
public sealed record MarketQuote(decimal Bid, decimal Ask);
public sealed record RsiEvaluation(DateTimeOffset CandleOpenTime, decimal Rsi,
    RsiSignal Signal, decimal Close);

public enum RsiSignal
{
    Hold,
    Buy,
    Sell
}

public static class RsiStrategy
{
    public static readonly TimeSpan Interval = TimeSpan.FromMinutes(30);
    public const int Period = 10;
    public const int MinimumCompletedCandles = 250;
    public const decimal BuyThreshold = 30m;
    public const decimal SellThreshold = 70m;

    public static RsiEvaluation Evaluate(IReadOnlyList<MarketCandle> candles,
        DateTimeOffset serverTime, bool hasPosition)
    {
        ArgumentNullException.ThrowIfNull(candles);
        var currentOpenTicks = serverTime.UtcTicks - serverTime.UtcTicks % Interval.Ticks;
        if (candles.Any(candle => candle.OpenTime.UtcTicks % Interval.Ticks != 0 ||
            candle.OpenTime.UtcTicks > currentOpenTicks))
        {
            throw new InvalidOperationException("RSI candles must have UTC-aligned 30-minute timestamps and cannot be in the future.");
        }

        var completed = candles.Where(candle => candle.OpenTime.UtcTicks < currentOpenTicks)
            .OrderBy(candle => candle.OpenTime).ToArray();
        if (completed.Length < MinimumCompletedCandles)
        {
            throw new InvalidOperationException($"RSI requires at least {MinimumCompletedCandles} completed 30-minute candles.");
        }

        for (var index = 0; index < completed.Length; index++)
        {
            var candle = completed[index];
            if (candle.Open <= 0 || candle.High <= 0 || candle.Low <= 0 || candle.Close <= 0 ||
                candle.Volume < 0 || candle.High < Math.Max(candle.Open, candle.Close) ||
                candle.Low > Math.Min(candle.Open, candle.Close) || candle.Low > candle.High)
            {
                throw new InvalidOperationException("RSI candles contain invalid prices or volume.");
            }

            if (index > 0 && candle.OpenTime - completed[index - 1].OpenTime != Interval)
            {
                throw new InvalidOperationException("RSI candle history contains duplicate timestamps or missing 30-minute intervals.");
            }
        }

        var latest = completed[^1];
        if (latest.OpenTime.UtcTicks != currentOpenTicks - Interval.Ticks)
        {
            throw new InvalidOperationException("The latest completed 30-minute RSI candle is missing.");
        }

        var averageGain = 0m;
        var averageLoss = 0m;
        for (var index = 1; index <= Period; index++)
        {
            var change = completed[index].Close - completed[index - 1].Close;
            averageGain += Math.Max(change, 0m);
            averageLoss += Math.Max(-change, 0m);
        }

        averageGain /= Period;
        averageLoss /= Period;
        for (var index = Period + 1; index < completed.Length; index++)
        {
            var change = completed[index].Close - completed[index - 1].Close;
            averageGain = (averageGain * (Period - 1) + Math.Max(change, 0m)) / Period;
            averageLoss = (averageLoss * (Period - 1) + Math.Max(-change, 0m)) / Period;
        }

        var rsi = averageGain == 0 && averageLoss == 0 ? 50m :
            100m * averageGain / (averageGain + averageLoss);
        var signal = !hasPosition && rsi < BuyThreshold ? RsiSignal.Buy :
            hasPosition && rsi > SellThreshold ? RsiSignal.Sell : RsiSignal.Hold;
        return new RsiEvaluation(latest.OpenTime, rsi, signal, latest.Close);
    }
}
