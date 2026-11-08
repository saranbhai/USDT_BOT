using System.Diagnostics;
using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using MyBot.Exchange;

public static class RateLimitChecks
{
    private const string ApiKey = "syntheticRateLimitApiKey";
    private const string Secret = "syntheticRateLimitSecret";
    private const string ClientId = "rOfflineRateLimit123";
    private const string PrivateDetail = "private upstream response content";
    private const long ServerTime = 1767225600000;
    private const string Candles = "[[1767225600,99,100,98,99,1]]";
    private const string Book = "{\"bids\":[[98,1]],\"asks\":[[99,1]]}";
    private const string Funds = "[{\"asset\":\"usdt\",\"free\":\"1\"}]";

    public static async Task RunAsync()
    {
        await PublicReadsRetryOnceAsync();
        await RepeatedRateLimitStopsAsync();
        await SignedReadIsResignedAsync();
        await ClockReadCooldownIsOutsideSynchronizationAsync();
        await MutationsAreNeverRetriedAsync();
        await MutationClockRateLimitStopsAsync();
        await IpBanIsNeverRetriedAsync();
        await MissingCooldownCanBeCanceledAsync();
        await DiagnosticsAreSanitizedAsync();
    }

    private static async Task PublicReadsRetryOnceAsync()
    {
        foreach (var path in new[] { "/sapi/v1/klines", "/sapi/v1/time", "/sapi/v1/depth" })
        {
            var handler = new OfflineHandler((request, attempt) =>
            {
                Equal(path, request.Path, "public endpoint remains unchanged");
                return attempt == 1 ? Limited(TimeSpan.Zero) : Json(path switch
                {
                    "/sapi/v1/klines" => Candles,
                    "/sapi/v1/time" => Clock(ServerTime),
                    _ => Book
                });
            });
            using var http = new HttpClient(handler);
            var client = Client(http);
            switch (path)
            {
                case "/sapi/v1/klines": await client.GetCandlesAsync(CancellationToken.None); break;
                case "/sapi/v1/time": await client.GetServerTimeAsync(CancellationToken.None); break;
                default: await client.GetQuoteAsync(CancellationToken.None); break;
            }

            Equal(2, handler.Requests.Count, "one read-only retry after HTTP 429");
            Require(handler.Requests.All(request => request.Method == HttpMethod.Get && request.ApiKey.Length == 0),
                "Public retries must not acquire credentials or mutate the exchange.");
            Equal(handler.Requests[0].Query, handler.Requests[1].Query, "public retry preserves parameters");
        }
    }

    private static async Task RepeatedRateLimitStopsAsync()
    {
        var handler = new OfflineHandler((_, _) => Limited(TimeSpan.Zero));
        using var http = new HttpClient(handler);
        var exception = await ThrowsAsync<WazirXApiException>(() => Client(http).GetCandlesAsync(CancellationToken.None));
        Equal(2, handler.Requests.Count, "persistent HTTP 429 is bounded to two GET attempts");
        Equal<HttpStatusCode?>(HttpStatusCode.TooManyRequests, exception.StatusCode, "rate-limit status");
        Equal<int?>(2136, exception.ApiCode, "numeric exchange error code");
        Equal<TimeSpan?>(TimeSpan.Zero, RetryAfter(exception), "zero cooldown remains available to callers");
        AssertSafe(exception, "GET", "/sapi/v1/klines");
    }

    private static async Task SignedReadIsResignedAsync()
    {
        var handler = new OfflineHandler((request, attempt) => request.Path switch
        {
            "/sapi/v1/time" => Json(Clock(ServerTime + attempt * 10000)),
            "/sapi/v1/funds" => attempt == 1 ? Limited(TimeSpan.Zero) : Json(Funds),
            _ => throw new InvalidOperationException("Unexpected signed read endpoint.")
        });
        using var http = new HttpClient(handler);
        var balances = await Client(http).GetBalancesAsync(CancellationToken.None);
        Equal(1, balances.Count, "retried balance read succeeds");
        Equal(2, handler.Requests.Count(request => request.Path == "/sapi/v1/time"), "clock resynchronized before signing retry");
        var reads = handler.Requests.Where(request => request.Path == "/sapi/v1/funds").ToArray();
        Equal(2, reads.Length, "signed read attempts");
        var firstTimestamp = AssertSignature(reads[0]);
        var secondTimestamp = AssertSignature(reads[1]);
        Require(secondTimestamp >= firstTimestamp + 5000, "The signed retry must use the refreshed server clock.");
        Require(reads[0].Query != reads[1].Query, "A signed read must not replay its old signature and timestamp.");
    }

    private static async Task ClockReadCooldownIsOutsideSynchronizationAsync()
    {
        var cooldown = TimeSpan.FromSeconds(3);
        long? limitedAt = null;
        long? retriedAt = null;
        var handler = new OfflineHandler((request, attempt) =>
        {
            if (request.Path == "/sapi/v1/time")
            {
                if (attempt == 1)
                {
                    limitedAt = Stopwatch.GetTimestamp();
                    return Limited(cooldown);
                }
                retriedAt = Stopwatch.GetTimestamp();
                return Json(Clock(ServerTime));
            }
            return request.Path == "/sapi/v1/funds"
                ? Json(Funds)
                : throw new InvalidOperationException("Unexpected clock retry endpoint.");
        });
        using var http = new HttpClient(handler);
        await Client(http).GetBalancesAsync(CancellationToken.None);
        Require(limitedAt.HasValue && retriedAt.HasValue, "Both rate-limited and retried clock reads must be observed.");
        var elapsed = Stopwatch.GetElapsedTime(limitedAt!.Value, retriedAt!.Value);
        Require(elapsed >= cooldown,
            $"A read retry must honor the exchange cooldown: required {cooldown.TotalMilliseconds:0.###} ms, observed {elapsed.TotalMilliseconds:0.###} ms.");
        Equal(2, handler.Requests.Count(request => request.Path == "/sapi/v1/time"), "clock GET retried once");
        Equal(1, handler.Requests.Count(request => request.Path == "/sapi/v1/funds"), "only the successful clock attempt precedes the signed read");
    }

    private static async Task MutationsAreNeverRetriedAsync()
    {
        foreach (var method in new[] { HttpMethod.Post, HttpMethod.Delete })
        {
            var handler = new OfflineHandler((request, _) => request.Path == "/sapi/v1/time"
                ? Json(Clock(ServerTime)) : Limited(TimeSpan.Zero));
            using var http = new HttpClient(handler);
            var client = Client(http);
            var exception = await ThrowsAsync<WazirXApiException>(() => method == HttpMethod.Post
                ? client.PlaceBuyAsync(ClientId, 1m, 99m, CancellationToken.None)
                : client.CancelStrategyOrderAsync(123, CancellationToken.None));
            Equal(1, handler.Requests.Count(request => request.Method == method && request.Path == "/sapi/v1/order"),
                "a rejected financial request must never be retried");
            Equal(2, handler.Requests.Count, "one clock lookup and one financial request only");
            AssertSafe(exception, method.Method, "/sapi/v1/order");
        }
    }

    private static async Task MutationClockRateLimitStopsAsync()
    {
        foreach (var method in new[] { HttpMethod.Post, HttpMethod.Delete })
        {
            var handler = new OfflineHandler((_, _) => Limited(TimeSpan.Zero));
            using var http = new HttpClient(handler);
            var client = Client(http);
            await ThrowsAsync<WazirXApiException>(() => method == HttpMethod.Post
                ? client.PlaceBuyAsync(ClientId, 1m, 99m, CancellationToken.None)
                : client.CancelStrategyOrderAsync(123, CancellationToken.None));
            Equal(1, handler.Requests.Count, "financial preflight rate limit does not trigger automatic retries");
            Equal("/sapi/v1/time", handler.Requests[0].Path, "mutation is not submitted after failed clock preflight");
        }
    }

    private static async Task IpBanIsNeverRetriedAsync()
    {
        var handler = new OfflineHandler((_, _) => Limited(TimeSpan.Zero, (HttpStatusCode)418));
        using var http = new HttpClient(handler);
        var exception = await ThrowsAsync<WazirXApiException>(() => Client(http).GetCandlesAsync(CancellationToken.None));
        Equal(1, handler.Requests.Count, "HTTP 418 must stop without automatic resumption");
        Equal<HttpStatusCode?>((HttpStatusCode)418, exception.StatusCode, "IP-ban status preserved");
        AssertSafe(exception, "GET", "/sapi/v1/klines");
    }

    private static async Task MissingCooldownCanBeCanceledAsync()
    {
        using var cancellation = new CancellationTokenSource();
        var handler = new OfflineHandler((_, _) =>
        {
            cancellation.CancelAfter(TimeSpan.FromMilliseconds(150));
            return Limited(null);
        });
        using var http = new HttpClient(handler);
        await ThrowsAsync<OperationCanceledException>(() => Client(http).GetCandlesAsync(cancellation.Token));
        Equal(1, handler.Requests.Count, "missing Retry-After uses a cancellable conservative delay before retry");
    }

    private static async Task DiagnosticsAreSanitizedAsync()
    {
        var cooldown = TimeSpan.FromSeconds(12);
        var handler = new OfflineHandler((request, _) => request.Path == "/sapi/v1/time"
            ? Json(Clock(ServerTime)) : Limited(cooldown));
        using var http = new HttpClient(handler);
        var exception = await ThrowsAsync<WazirXApiException>(() =>
            Client(http).PlaceSellAsync(ClientId, 1m, 100m, CancellationToken.None));
        Equal<TimeSpan?>(cooldown, RetryAfter(exception), "parsed Retry-After exposed without raw headers");
        AssertSafe(exception, "POST", "/sapi/v1/order");
        Require(exception.Message.Contains("12", StringComparison.Ordinal), "The diagnostic must report the exchange cooldown.");

        var compatible = new WazirXApiException("Existing constructor", HttpStatusCode.BadRequest, 1);
        Equal<TimeSpan?>(null, RetryAfter(compatible), "existing three-argument constructor remains compatible");
    }

    private static WazirXClient Client(HttpClient http) => new(http, ApiKey, Secret);

    private static string Clock(long milliseconds) => JsonSerializer.Serialize(new { serverTime = milliseconds });

    private static HttpResponseMessage Limited(TimeSpan? cooldown, HttpStatusCode status = HttpStatusCode.TooManyRequests)
    {
        var response = Json(JsonSerializer.Serialize(new { code = 2136, msg = ApiKey + Secret + PrivateDetail }), status);
        if (cooldown.HasValue)
            response.Headers.RetryAfter = new RetryConditionHeaderValue(cooldown.Value);
        return response;
    }

    private static HttpResponseMessage Json(string body, HttpStatusCode status = HttpStatusCode.OK) => new(status)
    {
        Content = new StringContent(body, Encoding.UTF8, "application/json")
    };

    private static TimeSpan? RetryAfter(WazirXApiException exception)
    {
        var property = typeof(WazirXApiException).GetProperty("RetryAfter");
        Require(property is not null && property.PropertyType == typeof(TimeSpan?),
            "WazirXApiException must expose a nullable TimeSpan RetryAfter property.");
        return (TimeSpan?)property!.GetValue(exception);
    }

    private static long AssertSignature(CapturedRequest request)
    {
        Equal(ApiKey, request.ApiKey, "signed retry API header");
        const string marker = "&signature=";
        var signatureAt = request.Query.LastIndexOf(marker, StringComparison.Ordinal);
        Require(signatureAt >= 0, "Signed read signature missing.");
        var payload = request.Query[..signatureAt];
        var expected = Convert.ToHexString(HMACSHA256.HashData(Encoding.UTF8.GetBytes(Secret), Encoding.UTF8.GetBytes(payload)))
            .ToLowerInvariant();
        Equal(expected, request.Query[(signatureAt + marker.Length)..], "retry signature matches its own parameters");
        var timestamp = payload.Split('&').Single(field => field.StartsWith("timestamp=", StringComparison.Ordinal));
        return long.Parse(timestamp["timestamp=".Length..], CultureInfo.InvariantCulture);
    }

    private static void AssertSafe(Exception exception, string method, string path)
    {
        Require(exception.Message.Contains(method + " " + path, StringComparison.Ordinal),
            "HTTP errors must identify the method and literal endpoint path.");
        foreach (var value in new[] { ApiKey, Secret, PrivateDetail, ClientId, "signature=", "timestamp=", "symbol=", "quantity=", "price=" })
            Require(!exception.ToString().Contains(value, StringComparison.Ordinal), "Diagnostics exposed request or response content.");
    }

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
        throw new InvalidOperationException($"Expected {typeof(TException).Name}.");
    }

    private static void Equal<T>(T expected, T actual, string context) => Require(
        EqualityComparer<T>.Default.Equals(expected, actual), $"{context}: expected '{expected}', actual '{actual}'.");

    private static void Require(bool condition, string message)
    {
        if (!condition)
            throw new InvalidOperationException(message);
    }

    private sealed record CapturedRequest(HttpMethod Method, string Path, string Query, string ApiKey);

    private sealed class OfflineHandler(Func<CapturedRequest, int, HttpResponseMessage> respond) : HttpMessageHandler
    {
        public List<CapturedRequest> Requests { get; } = [];

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var uri = request.RequestUri ?? throw new InvalidOperationException("Missing request URI.");
            var captured = new CapturedRequest(request.Method, uri.AbsolutePath, uri.Query.TrimStart('?'),
                request.Headers.TryGetValues("X-API-KEY", out var values) ? string.Join(",", values) : "");
            Requests.Add(captured);
            var attempt = Requests.Count(item => item.Method == captured.Method && item.Path == captured.Path);
            var response = respond(captured, attempt);
            response.RequestMessage = request;
            return Task.FromResult(response);
        }
    }
}
