using MyBot.Core;

public static class RsiStrategyChecks
{
    private static readonly DateTimeOffset Now = new(2026, 11, 7, 12, 0, 0, TimeSpan.Zero);

    public static void Run()
    {
        WilderSmoothing();
        StrictThresholds();
        PositionSignals();
        IncompleteCandleIgnored();
        MinimumHistory();
        StaleHistory();
        MissingAndDuplicateCandles();
        TimestampValidation();
        InvalidPricesAndVolume();
    }

    private static void WilderSmoothing()
    {
        var candles = Candles([1m, -1m, 2m, -2m, 3m, -3m, 4m, -4m, 5m, -5m, 6m]);
        var evaluation = RsiStrategy.Evaluate(candles, Now, false);
        var expected = 100m * 15.4783358601m / 26.0098201491m;
        if (Math.Abs(evaluation.Rsi - expected) > 0.00000000000000000001m)
        {
            throw new InvalidOperationException($"Wilder RSI expected {expected}, received {evaluation.Rsi}.");
        }

        Equal(candles[^1].OpenTime, evaluation.CandleOpenTime, "latest closed candle timestamp");
        Equal(candles[^1].Close, evaluation.Close, "latest closed candle price");
        Equal(evaluation, RsiStrategy.Evaluate(candles.Reverse().ToArray(), Now, false),
            "unordered history produces the same chronological RSI");
    }

    private static void StrictThresholds()
    {
        var exactlyThirty = RsiStrategy.Evaluate(Candles([10m, -21m]), Now, false);
        Equal(30m, exactlyThirty.Rsi, "exact lower threshold");
        Equal(RsiSignal.Hold, exactlyThirty.Signal, "RSI exactly 30 does not buy");
        var exactlySeventy = RsiStrategy.Evaluate(Candles([70m, -27m]), Now, true);
        Equal(70m, exactlySeventy.Rsi, "exact upper threshold");
        Equal(RsiSignal.Hold, exactlySeventy.Signal, "RSI exactly 70 does not sell");
        Equal(RsiSignal.Buy, RsiStrategy.Evaluate(Candles([10m, -22m]), Now, false).Signal,
            "RSI strictly below 30 buys");
        Equal(RsiSignal.Sell, RsiStrategy.Evaluate(Candles([70m, -26m]), Now, true).Signal,
            "RSI strictly above 70 sells");
    }

    private static void PositionSignals()
    {
        var falling = Candles([-1m]);
        Equal(0m, RsiStrategy.Evaluate(falling, Now, false).Rsi, "all losses give RSI zero");
        Equal(RsiSignal.Buy, RsiStrategy.Evaluate(falling, Now, false).Signal,
            "oversold while flat buys");
        Equal(RsiSignal.Hold, RsiStrategy.Evaluate(falling, Now, true).Signal,
            "oversold while holding does not buy again");
        var rising = Candles([1m]);
        Equal(100m, RsiStrategy.Evaluate(rising, Now, true).Rsi, "all gains give RSI 100");
        Equal(RsiSignal.Sell, RsiStrategy.Evaluate(rising, Now, true).Signal,
            "overbought while holding sells");
        Equal(RsiSignal.Hold, RsiStrategy.Evaluate(rising, Now, false).Signal,
            "overbought while flat cannot sell");
        var flat = RsiStrategy.Evaluate(Candles(), Now, false);
        Equal(50m, flat.Rsi, "unchanged prices give neutral RSI");
        Equal(RsiSignal.Hold, flat.Signal, "unchanged prices hold");
    }

    private static void IncompleteCandleIgnored()
    {
        var completed = Candles();
        var expected = RsiStrategy.Evaluate(completed, Now, false);
        var withPartial = completed.Append(new MarketCandle(Now, 100m, 1000m, 1m, 1000m, 1m)).ToArray();
        Equal(expected, RsiStrategy.Evaluate(withPartial, Now.AddMinutes(15), false),
            "current candle price cannot affect completed-candle RSI");
    }

    private static void MinimumHistory()
    {
        var tooShort = Candles().Skip(1).ToArray();
        Throws(() => RsiStrategy.Evaluate(tooShort, Now, false), "249 completed candles are insufficient");
        var withPartial = tooShort.Append(new MarketCandle(Now, 100m, 100m, 100m, 100m, 1m)).ToArray();
        Throws(() => RsiStrategy.Evaluate(withPartial, Now, false),
            "a partial candle cannot complete the 250-candle warmup");
    }

    private static void StaleHistory()
    {
        var stale = Candles().Select(candle => candle with
        {
            OpenTime = candle.OpenTime - RsiStrategy.Interval
        }).ToArray();
        Throws(() => RsiStrategy.Evaluate(stale, Now, false), "stale latest candle blocks signals");
    }

    private static void MissingAndDuplicateCandles()
    {
        var candles = Candles(count: 251);
        var missing = candles.Where((_, index) => index != 125).ToArray();
        Throws(() => RsiStrategy.Evaluate(missing, Now, false), "a missing interior candle blocks signals");
        var duplicate = candles.Append(candles[125]).ToArray();
        Throws(() => RsiStrategy.Evaluate(duplicate, Now, false), "a duplicate candle blocks signals");
    }

    private static void TimestampValidation()
    {
        var candles = Candles();
        var misaligned = candles.ToArray();
        misaligned[125] = misaligned[125] with { OpenTime = misaligned[125].OpenTime.AddMinutes(1) };
        Throws(() => RsiStrategy.Evaluate(misaligned, Now, false), "misaligned timestamps block signals");
        var future = candles.Append(new MarketCandle(Now.AddMinutes(30), 100m, 100m, 100m, 100m, 1m)).ToArray();
        Throws(() => RsiStrategy.Evaluate(future, Now, false), "future candles block signals");
    }

    private static void InvalidPricesAndVolume()
    {
        var invalidClose = Candles();
        invalidClose[125] = invalidClose[125] with { Close = invalidClose[125].High + 1m };
        Throws(() => RsiStrategy.Evaluate(invalidClose, Now, false), "close above high is rejected");
        var negativeVolume = Candles();
        negativeVolume[125] = negativeVolume[125] with { Volume = -1m };
        Throws(() => RsiStrategy.Evaluate(negativeVolume, Now, false), "negative volume is rejected");
        var zeroPrice = Candles();
        zeroPrice[125] = zeroPrice[125] with { Low = 0m };
        Throws(() => RsiStrategy.Evaluate(zeroPrice, Now, false), "nonpositive prices are rejected");
    }

    private static MarketCandle[] Candles(IReadOnlyList<decimal>? changes = null,
        int count = RsiStrategy.MinimumCompletedCandles)
    {
        changes ??= Array.Empty<decimal>();
        var candles = new MarketCandle[count];
        var price = 100m;
        var start = Now - RsiStrategy.Interval * count;
        for (var index = 0; index < count; index++)
        {
            var open = price;
            if (index >= count - changes.Count)
            {
                price += changes[index - (count - changes.Count)];
            }

            candles[index] = new MarketCandle(start + RsiStrategy.Interval * index,
                open, Math.Max(open, price), Math.Min(open, price), price, 1m);
        }

        return candles;
    }

    private static void Equal<T>(T expected, T actual, string description)
    {
        if (!EqualityComparer<T>.Default.Equals(expected, actual))
        {
            throw new InvalidOperationException($"{description}: expected {expected}, received {actual}.");
        }
    }

    private static void Throws(Action action, string description)
    {
        try
        {
            action();
        }
        catch (InvalidOperationException)
        {
            return;
        }

        throw new InvalidOperationException(description + ": expected InvalidOperationException.");
    }
}
