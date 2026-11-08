using System.Globalization;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using MyBot.Exchange;

public static class StrategyExchangeChecks
{
    private const string ApiKey = "syntheticStrategyApiKey";
    private const string Secret = "syntheticStrategySecret";
    private const string ClientId = "sOfflineRsi123";

    public static async Task RunAsync()
    {
        await PublicMarketDataAsync();
        await InvalidCandlesAsync();
        await InvalidQuotesAsync();
        await SignedSellAsync();
        await StrategyOrderReconciliationAsync();
        await StrategyFillSidesAsync();
        await FailedSellIsNotRetriedAsync(false);
        await FailedSellIsNotRetriedAsync(true);
    }

    private static async Task PublicMarketDataAsync()
    {
        var handler = new OfflineHandler(request => Json(request.Path switch
        {
            "/sapi/v1/klines" => """
                [[1767227400,"99.2","99.5","99.1","99.3","1.25"],
                 [1767225600,99,99.2,98.9,99.1,0]]
                """,
            "/sapi/v1/depth" => """
                {"bids":[["98.9","2"],["99.0","1"]],
                 "asks":[["99.3","1"],["99.1","2"]]}
                """,
            _ => throw new InvalidOperationException("Unexpected public request.")
        }));
        using var http = new HttpClient(handler);
        var client = Client(http);
        var candles = await client.GetCandlesAsync(CancellationToken.None);
        Equal(2, candles.Count, "candle count");
        Equal(new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero), candles[0].OpenTime,
            "seconds-based candle timestamp and chronological ordering");
        Equal(0m, candles[0].Volume, "zero-volume candles remain valid");
        Equal(99m, candles[0].Open, "open parsing");
        Equal(99.2m, candles[0].High, "high parsing");
        Equal(98.9m, candles[0].Low, "low parsing");
        Equal(99.1m, candles[0].Close, "close parsing");

        var quote = await client.GetQuoteAsync(CancellationToken.None);
        Equal(99m, quote.Bid, "best bid from unordered levels");
        Equal(99.1m, quote.Ask, "best ask from unordered levels");
        var serverTime = await client.GetServerTimeAsync(CancellationToken.None);
        Equal(DateTimeOffset.FromUnixTimeMilliseconds(handler.ServerTime), serverTime,
            "millisecond-based server clock");

        Equal(3, handler.Requests.Count, "public requests do not trigger signed clock synchronization");
        foreach (var request in handler.Requests)
        {
            Equal(HttpMethod.Get, request.Method, "public request method");
            Equal("https", request.Scheme, "public request scheme");
            Equal("api.wazirx.com", request.Host, "public request host");
            Equal("", request.ApiKey, "public requests omit credentials");
            Equal("", request.Body, "public requests omit form bodies");
            Require(!request.Query.Contains("signature", StringComparison.Ordinal)
                && !request.Query.Contains("timestamp", StringComparison.Ordinal),
                "Public market requests must not be signed.");
        }

        Equal("symbol=usdtinr&interval=30m&limit=1000",
            handler.Requests.Single(request => request.Path == "/sapi/v1/klines").Query,
            "fixed strategy candle request");
    }

    private static async Task InvalidCandlesAsync()
    {
        foreach (var json in new[]
        {
            "[]",
            "[[1767225600000,99,100,98,99,1]]",
            "[[1767225601,99,100,98,99,1]]",
            "[[1767225600,99,100,98,99]]",
            "[[1767225600,99,100,98,99,1,0]]",
            "[[1767225600,101,100,98,99,1]]",
            "[[1767225600,99,100,98,99,-1]]",
            "[[1767225600,99,100,98,99,1],[1767225600,99,100,98,99,1]]"
        })
        {
            var handler = new OfflineHandler(_ => Json(json));
            using var http = new HttpClient(handler);
            await ThrowsAsync<WazirXApiException>(() => Client(http).GetCandlesAsync(CancellationToken.None));
            Equal(1, handler.Requests.Count, "invalid candles cause no signed or financial request");
        }
    }

    private static async Task InvalidQuotesAsync()
    {
        foreach (var json in new[]
        {
            "{\"bids\":[[100,1]],\"asks\":[[99,1]]}",
            "{\"bids\":[],\"asks\":[[99,1]]}",
            "{\"bids\":[[98,1]],\"asks\":[]}",
            "{\"bids\":[[98,0]],\"asks\":[[99,1]]}",
            "{\"bids\":[[98,1]],\"asks\":[[99,-1]]}",
            "{\"bids\":[[0,1]],\"asks\":[[99,1]]}"
        })
        {
            var handler = new OfflineHandler(_ => Json(json));
            using var http = new HttpClient(handler);
            await ThrowsAsync<WazirXApiException>(() => Client(http).GetQuoteAsync(CancellationToken.None));
            Equal(1, handler.Requests.Count, "invalid book causes no signed or financial request");
        }
    }

    private static async Task SignedSellAsync()
    {
        var previousCulture = CultureInfo.CurrentCulture;
        try
        {
            CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo("fr-FR");
            var handler = new OfflineHandler(_ => Json(OrderJson()));
            using var http = new HttpClient(handler);
            var order = await Client(http).PlaceSellAsync(ClientId, 1.25m, 100.12m, CancellationToken.None);
            Equal("sell", order.Side, "sell response side");
            Equal(1.25m, order.OriginalQuantity, "sell response quantity");
            Equal(100.12m, order.Price, "sell response price");
            var request = handler.Requests.Single(item => item.Path == "/sapi/v1/order");
            Equal(HttpMethod.Post, request.Method, "sell method");
            Equal("", request.Query, "sell parameters stay out of the URL");
            Equal("application/x-www-form-urlencoded", request.ContentType, "sell content type");
            var fields = AssertSignature(request, handler.ServerTime);
            Equal("symbol=usdtinr&side=sell&type=limit&quantity=1.25&price=100.12&clientOrderId="
                + ClientId + "&recvWindow=5000&timestamp=" + fields["timestamp"],
                WithoutSignature(request.Body), "invariant sell form preserving existing order parameters");
            Equal("", handler.Requests.Single(item => item.Path == "/sapi/v1/time").ApiKey,
                "signed order clock request omits credentials");
        }
        finally
        {
            CultureInfo.CurrentCulture = previousCulture;
        }
    }

    private static async Task StrategyOrderReconciliationAsync()
    {
        var handler = new OfflineHandler(_ => Json(OrderJson()));
        using (var http = new HttpClient(handler))
        {
            var order = await Client(http).GetStrategyOrderAsync(ClientId, CancellationToken.None);
            Equal("sell", order.Side, "strategy order lookup accepts sell orders");
            var request = handler.Requests.Single(item => item.Path == "/sapi/v1/order");
            Equal(ClientId, AssertSignature(request, handler.ServerTime)["clientOrderId"],
                "strategy lookup uses the persisted client identifier");
        }

        var legacyHandler = new OfflineHandler(_ => Json(OrderJson()));
        using (var http = new HttpClient(legacyHandler))
        {
            await ThrowsAsync<WazirXApiException>(() => Client(http).GetOrderAsync(ClientId, CancellationToken.None));
        }

        var cancelHandler = new OfflineHandler(_ => Json(OrderJson("cancel")));
        using (var http = new HttpClient(cancelHandler))
        {
            var order = await Client(http).CancelStrategyOrderAsync(28, CancellationToken.None);
            Equal("cancel", order.Status, "sell cancellation status");
            var request = cancelHandler.Requests.Single(item => item.Path == "/sapi/v1/order");
            Equal(HttpMethod.Delete, request.Method, "strategy cancel method");
            Equal("28", AssertSignature(request, cancelHandler.ServerTime)["orderId"],
                "strategy cancellation uses the exchange order identifier");
        }
    }

    private static async Task StrategyFillSidesAsync()
    {
        foreach (var side in new[] { "buy", "sell" })
        {
            var handler = new OfflineHandler(_ => Json(FillJson(side)));
            using var http = new HttpClient(handler);
            var fill = (await Client(http).GetStrategyTradesAsync(28, side, CancellationToken.None)).Single();
            Equal(side, fill.Side, "strategy fill side");
            Equal(1.25m, fill.Quantity, "strategy fill quantity");
            var request = handler.Requests.Single(item => item.Path == "/sapi/v1/myTrades");
            var fields = AssertSignature(request, handler.ServerTime);
            Equal("28", fields["orderId"], "fill order filter");
            Equal("usdtinr", fields["symbol"], "fill symbol filter");
        }

        var mismatchHandler = new OfflineHandler(_ => Json(FillJson("buy")));
        using (var http = new HttpClient(mismatchHandler))
        {
            await ThrowsAsync<WazirXApiException>(() =>
                Client(http).GetStrategyTradesAsync(28, "sell", CancellationToken.None));
        }

        var legacyHandler = new OfflineHandler(_ => Json(FillJson("sell")));
        using (var http = new HttpClient(legacyHandler))
        {
            await ThrowsAsync<WazirXApiException>(() => Client(http).GetTradesAsync(28, CancellationToken.None));
        }
    }

    private static async Task FailedSellIsNotRetriedAsync(bool timeout)
    {
        const string privateResponse = "private strategy response detail";
        var handler = new OfflineHandler(_ => timeout
            ? Task.FromException<HttpResponseMessage>(new TaskCanceledException(ApiKey + Secret + privateResponse))
            : Task.FromResult(Json(JsonSerializer.Serialize(new
            {
                code = -1000,
                msg = ApiKey + Secret + privateResponse
            }), HttpStatusCode.ServiceUnavailable)));
        using var http = new HttpClient(handler);
        var exception = await ThrowsAsync<WazirXApiException>(() =>
            Client(http).PlaceSellAsync(ClientId, 1.25m, 100.12m, CancellationToken.None));
        Equal(1, handler.Requests.Count(request => request.Path == "/sapi/v1/order"),
            "ambiguous sell submission is never retried");
        Equal(2, handler.Requests.Count, "only clock and one sell submission were requested");
        foreach (var privateValue in new[] { ApiKey, Secret, privateResponse })
        {
            Require(!exception.ToString().Contains(privateValue, StringComparison.Ordinal),
                "Sell errors must not expose credentials or upstream details.");
        }
    }

    private static WazirXClient Client(HttpClient http) => new(http, ApiKey, Secret);

    private static Dictionary<string, string> AssertSignature(CapturedRequest request, long serverTime)
    {
        Equal("https", request.Scheme, "signed request scheme");
        Equal("api.wazirx.com", request.Host, "signed request host");
        Equal(ApiKey, request.ApiKey, "signed request API key");
        var payload = request.Method == HttpMethod.Get ? request.Query : request.Body;
        var unsigned = WithoutSignature(payload);
        var fields = payload.Split('&').Select(part => part.Split('=', 2)).ToDictionary(
            pair => Uri.UnescapeDataString(pair[0].Replace('+', ' ')),
            pair => Uri.UnescapeDataString(pair[1].Replace('+', ' ')), StringComparer.Ordinal);
        var expectedSignature = Convert.ToHexString(HMACSHA256.HashData(
            Encoding.UTF8.GetBytes(Secret), Encoding.UTF8.GetBytes(unsigned))).ToLowerInvariant();
        Equal(expectedSignature, fields["signature"], "signature over the exact transmitted payload");
        Equal("5000", fields["recvWindow"], "receive window");
        Require(long.TryParse(fields["timestamp"], NumberStyles.None, CultureInfo.InvariantCulture,
            out var timestamp) && timestamp >= serverTime && timestamp - serverTime < 5000,
            "Financial request timestamp must use the supplied server clock.");
        return fields;
    }

    private static string WithoutSignature(string payload)
    {
        var index = payload.LastIndexOf("&signature=", StringComparison.Ordinal);
        Require(index > 0 && !payload[(index + 1)..].Contains('&'), "Signature must be the final form field.");
        return payload[..index];
    }

    private static string OrderJson(string status = "wait") => JsonSerializer.Serialize(new
    {
        id = 28,
        clientOrderId = ClientId,
        symbol = "usdtinr",
        side = "sell",
        type = "limit",
        price = "100.12",
        origQty = "1.25",
        executedQty = "0",
        status
    });

    private static string FillJson(string side) => JsonSerializer.Serialize(new[]
    {
        new { id = 1, orderId = 28, symbol = "usdtinr", side, qty = "1.25", fee = "0", feeCurrency = "inr" }
    });

    private static HttpResponseMessage Json(string body, HttpStatusCode status = HttpStatusCode.OK) => new(status)
    {
        Content = new StringContent(body, Encoding.UTF8, "application/json")
    };

    private static async Task<TException> ThrowsAsync<TException>(Func<Task> action) where TException : Exception
    {
        try
        {
            await action();
        }
        catch (TException exception)
        {
            return exception;
        }

        throw new InvalidOperationException("Expected " + typeof(TException).Name + " was not thrown.");
    }

    private static void Equal<T>(T expected, T actual, string context) => Require(
        EqualityComparer<T>.Default.Equals(expected, actual), "Unexpected " + context + ".");

    private static void Require(bool condition, string message)
    {
        if (!condition)
        {
            throw new InvalidOperationException(message);
        }
    }

    private sealed record CapturedRequest(HttpMethod Method, string Scheme, string Host, string Path,
        string Query, string Body, string ApiKey, string ContentType);

    private sealed class OfflineHandler : HttpMessageHandler
    {
        private readonly Func<CapturedRequest, Task<HttpResponseMessage>> _respond;
        public long ServerTime { get; } = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        public List<CapturedRequest> Requests { get; } = [];

        public OfflineHandler(Func<CapturedRequest, HttpResponseMessage> respond)
            : this(request => Task.FromResult(respond(request)))
        {
        }

        public OfflineHandler(Func<CapturedRequest, Task<HttpResponseMessage>> respond) => _respond = respond;

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            var uri = request.RequestUri ?? throw new InvalidOperationException("Missing request URI.");
            var captured = new CapturedRequest(request.Method, uri.Scheme, uri.Host, uri.AbsolutePath,
                uri.Query.TrimStart('?'), request.Content is null ? "" : await request.Content.ReadAsStringAsync(cancellationToken),
                request.Headers.TryGetValues("X-API-KEY", out var values) ? string.Join(",", values) : "",
                request.Content?.Headers.ContentType?.MediaType ?? "");
            Requests.Add(captured);
            var response = uri.AbsolutePath == "/sapi/v1/time"
                ? Json(JsonSerializer.Serialize(new { serverTime = ServerTime }))
                : await _respond(captured);
            response.RequestMessage = request;
            return response;
        }
    }
}
