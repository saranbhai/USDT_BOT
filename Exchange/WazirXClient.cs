using System.Diagnostics;
using System.Globalization;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using MyBot.Core;

namespace MyBot.Exchange;

public sealed class WazirXClient : IWazirXClient, IStrategyExchangeClient
{
    private const string ApiOrigin = "https://api.wazirx.com";
    private const string Symbol = "usdtinr";
    private readonly HttpClient _http;
    private readonly string _apiKey;
    private readonly string _secret;
    private readonly SemaphoreSlim _requestGate = new(1, 1);
    private readonly Dictionary<string, long> _lastRequestTimes = [];
    private long _clockSyncedAt;
    private long _serverTimeAtSync;

    public WazirXClient(HttpClient http, string apiKey, string secret)
    {
        ArgumentNullException.ThrowIfNull(http);
        if (string.IsNullOrWhiteSpace(apiKey) || string.IsNullOrWhiteSpace(secret)
            || apiKey.Any(char.IsControl) || secret.Any(char.IsControl))
        {
            throw new ArgumentException("WazirX API credentials are missing or invalid.");
        }

        _http = http;
        _apiKey = apiKey;
        _secret = secret;
    }

    public static HttpClient CreateHttpClient() => new(new SocketsHttpHandler
    {
        AllowAutoRedirect = false,
        UseCookies = false,
        PooledConnectionLifetime = TimeSpan.FromMinutes(5)
    })
    {
        Timeout = TimeSpan.FromSeconds(30),
        MaxResponseContentBufferSize = 8 * 1024 * 1024
    };

    public async Task<SymbolInfo> GetSymbolAsync(CancellationToken cancellationToken)
    {
        using var response = await RequestAsync(HttpMethod.Get, "/sapi/v1/exchangeInfo", [], false, cancellationToken);
        var matches = Array(Required(response.RootElement, "symbols"))
            .Where(item => Text(item, "symbol").Equals(Symbol, StringComparison.OrdinalIgnoreCase)).ToArray();
        if (matches.Length != 1)
        {
            throw InvalidResponse("The USDT/INR trading pair was not uniquely identified.");
        }

        var symbol = matches[0];
        var filters = Array(Required(symbol, "filters"));
        decimal? tickSize = null;
        decimal? minimumPrice = null;
        decimal? maximumPrice = null;
        decimal? quantityStep = null;
        decimal? minimumQuantity = null;
        decimal? maximumQuantity = null;
        decimal? minimumNotional = null;
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var filter in filters)
        {
            var type = Text(filter, "filterType");
            if (!seen.Add(type))
            {
                throw InvalidResponse("The exchange returned duplicate trading filters.");
            }

            switch (type)
            {
                case "PRICE_FILTER":
                    tickSize = Positive(Number(filter, "tickSize"));
                    minimumPrice = Nonnegative(Number(filter, "minPrice"));
                    maximumPrice = OptionalNumber(filter, "maxPrice");
                    if (maximumPrice is { } priceMaximum)
                    {
                        Positive(priceMaximum);
                        if (priceMaximum < minimumPrice.Value)
                        {
                            throw InvalidResponse("The exchange returned inconsistent price limits.");
                        }
                    }
                    break;
                case "LOT_SIZE":
                    quantityStep = Positive(Number(filter, "stepSize"));
                    minimumQuantity = Nonnegative(Number(filter, "minQty"));
                    maximumQuantity = Positive(Number(filter, "maxQty"));
                    if (maximumQuantity.Value < minimumQuantity.Value)
                    {
                        throw InvalidResponse("The exchange returned inconsistent quantity limits.");
                    }
                    break;
                case "MIN_NOTIONAL":
                    minimumNotional = Positive(Number(filter, "minNotional"));
                    break;
                default:
                    throw InvalidResponse("An unsupported trading restriction requires manual review.");
            }
        }

        if (tickSize is null || minimumPrice is null)
        {
            throw InvalidResponse("The exchange did not provide the required price filter.");
        }

        var precisionValue = Integer(symbol, "baseAssetPrecision");
        if (precisionValue is < 0 or > 28)
        {
            throw InvalidResponse("The exchange returned an unsupported asset precision.");
        }

        var baseAsset = Text(symbol, "baseAsset");
        var quoteAsset = Text(symbol, "quoteAsset");
        if (!baseAsset.Equals("usdt", StringComparison.OrdinalIgnoreCase)
            || !quoteAsset.Equals("inr", StringComparison.OrdinalIgnoreCase))
        {
            throw InvalidResponse("The exchange returned a different trading pair.");
        }

        var orderTypes = Array(Required(symbol, "orderTypes"));
        return new SymbolInfo(Text(symbol, "symbol"), baseAsset, quoteAsset,
            Text(symbol, "status"), Boolean(symbol, "isSpotTradingAllowed"),
            orderTypes.Any(item => TextValue(item).Equals("limit", StringComparison.Ordinal)),
            (int)precisionValue, tickSize.Value, minimumPrice.Value, maximumPrice,
            quantityStep, minimumQuantity, maximumQuantity, minimumNotional);
    }

    public async Task<decimal> GetBestAskAsync(CancellationToken cancellationToken)
    {
        using var response = await RequestAsync(HttpMethod.Get, "/sapi/v1/depth",
            [("symbol", Symbol), ("limit", "5")], false, cancellationToken);
        var asks = Array(Required(response.RootElement, "asks"));
        if (asks.Length == 0)
        {
            throw InvalidResponse("There are no USDT/INR sell offers available.");
        }

        var prices = asks.Select(ask =>
        {
            var values = Array(ask);
            if (values.Length != 2)
            {
                throw InvalidResponse("The exchange returned an unsupported order book.");
            }

            Positive(NumberValue(values[1]));
            return Positive(NumberValue(values[0]));
        });
        return prices.Min();
    }

    public async Task<IReadOnlyList<MarketCandle>> GetCandlesAsync(CancellationToken cancellationToken)
    {
        using var response = await RequestAsync(HttpMethod.Get, "/sapi/v1/klines",
            [("symbol", Symbol), ("interval", "30m"), ("limit", "1000")], false, cancellationToken);
        var rows = Array(response.RootElement);
        if (rows.Length == 0 || rows.Length > 1000)
        {
            throw InvalidResponse("The exchange returned an unsupported candle count.");
        }

        var candles = new List<MarketCandle>(rows.Length);
        var seen = new HashSet<long>();
        foreach (var row in rows)
        {
            var values = Array(row);
            if (values.Length != 6)
            {
                throw InvalidResponse("The exchange returned an unsupported candle structure.");
            }

            var seconds = NumberValue(values[0]);
            if (seconds <= 0 || seconds > 253402300799m || seconds != decimal.Truncate(seconds)
                || seconds % 1800 != 0 || !seen.Add((long)seconds))
            {
                throw InvalidResponse("The exchange returned an invalid or duplicate 30-minute candle timestamp.");
            }

            var open = Positive(NumberValue(values[1]));
            var high = Positive(NumberValue(values[2]));
            var low = Positive(NumberValue(values[3]));
            var close = Positive(NumberValue(values[4]));
            var volume = Nonnegative(NumberValue(values[5]));
            if (low > high || open < low || open > high || close < low || close > high)
            {
                throw InvalidResponse("The exchange returned inconsistent candle prices.");
            }

            candles.Add(new MarketCandle(DateTimeOffset.FromUnixTimeSeconds((long)seconds),
                open, high, low, close, volume));
        }

        return candles.OrderBy(candle => candle.OpenTime).ToArray();
    }

    public async Task<MarketQuote> GetQuoteAsync(CancellationToken cancellationToken)
    {
        using var response = await RequestAsync(HttpMethod.Get, "/sapi/v1/depth",
            [("symbol", Symbol), ("limit", "5")], false, cancellationToken);
        var bids = ReadBookPrices(response.RootElement, "bids");
        var asks = ReadBookPrices(response.RootElement, "asks");
        var bid = bids.Max();
        var ask = asks.Min();
        if (bid > ask)
        {
            throw InvalidResponse("The exchange returned a crossed order book.");
        }

        return new MarketQuote(bid, ask);
    }

    public async Task<DateTimeOffset> GetServerTimeAsync(CancellationToken cancellationToken)
    {
        using var response = await RequestAsync(HttpMethod.Get, "/sapi/v1/time", [], false, cancellationToken);
        var milliseconds = PositiveId(Integer(response.RootElement, "serverTime"));
        if (milliseconds > 253402300799999L)
        {
            throw InvalidResponse("The exchange returned an invalid server timestamp.");
        }

        return DateTimeOffset.FromUnixTimeMilliseconds(milliseconds);
    }

    public async Task<IReadOnlyList<NetworkInfo>> GetNetworksAsync(CancellationToken cancellationToken)
    {
        using var response = await RequestAsync(HttpMethod.Get, "/sapi/v1/coins", [], true, cancellationToken);
        var coins = Array(response.RootElement)
            .Where(coin => ReadCoinCode(coin).Equals("usdt", StringComparison.OrdinalIgnoreCase)).ToArray();
        if (coins.Length != 1)
        {
            throw InvalidResponse("The exchange did not uniquely identify USDT withdrawal information.");
        }

        var result = new List<NetworkInfo>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var network in Array(Required(coins[0], "networkList")))
        {
            var code = Text(network, "network");
            if (!seen.Add(code))
            {
                throw InvalidResponse("The exchange returned duplicate withdrawal networks.");
            }

            var enabled = Boolean(network, "withdrawEnable");
            var consent = "";
            if (network.TryGetProperty("withdrawConsent", out var consentObject)
                && consentObject.ValueKind == JsonValueKind.Object)
            {
                consent = Text(consentObject, "message");
            }
            else if (enabled)
            {
                throw InvalidResponse("The enabled withdrawal network has no required consent text.");
            }

            var minimum = Nonnegative(ReadAliasedNumber(network, "withdrawMin", "minWithdrawAmount"));
            var maximum = Positive(ReadAliasedNumber(network, "withdrawMax", "maxWithdrawAmount"));
            var multiple = ReadWithdrawalStep(network);
            if (maximum < minimum || multiple is <= 0)
            {
                throw InvalidResponse("The exchange returned unsupported withdrawal amount limits.");
            }

            result.Add(new NetworkInfo(code, Text(network, "name"), enabled,
                Nonnegative(Number(network, "withdrawFee")), minimum, maximum, multiple,
                ReadMemoRequired(network), consent));
        }

        return result;
    }

    public async Task<IReadOnlyList<AddressEntry>> GetAddressesAsync(CancellationToken cancellationToken)
    {
        using var response = await RequestAsync(HttpMethod.Get, "/sapi/v1/crypto/withdraw/address-book",
            [("coin", "usdt")], true, cancellationToken);
        var result = new List<AddressEntry>();
        var seen = new HashSet<long>();
        foreach (var address in Array(response.RootElement))
        {
            var id = PositiveId(Integer(address, "id"));
            if (!seen.Add(id))
            {
                throw InvalidResponse("The exchange returned duplicate address book identifiers.");
            }

            var allowedAfterValue = Required(address, "withdrawAllowedAfter");
            DateTimeOffset? allowedAfter = null;
            if (allowedAfterValue.ValueKind != JsonValueKind.Null)
            {
                if (!DateTimeOffset.TryParse(TextValue(allowedAfterValue), CultureInfo.InvariantCulture,
                    DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var parsed))
                {
                    throw InvalidResponse("The address book withdrawal cooldown could not be read.");
                }

                allowedAfter = parsed;
            }

            result.Add(new AddressEntry(id, Text(address, "currency"),
                Text(Required(address, "networkObj"), "network"), Text(address, "address"),
                Boolean(address, "isWithdrawAllowed"), Boolean(address, "isUniversal"), allowedAfter));
        }

        return result;
    }

    public async Task<IReadOnlyList<Balance>> GetBalancesAsync(CancellationToken cancellationToken)
    {
        using var response = await RequestAsync(HttpMethod.Get, "/sapi/v1/funds", [], true, cancellationToken);
        var result = new List<Balance>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var balance in Array(response.RootElement))
        {
            var asset = Text(balance, "asset");
            if (!seen.Add(asset))
            {
                throw InvalidResponse("The exchange returned duplicate asset balances.");
            }

            result.Add(new Balance(asset, Nonnegative(Number(balance, "free"))));
        }

        return result;
    }

    public async Task<OrderInfo> PlaceBuyAsync(string clientId, decimal quantity, decimal price,
        CancellationToken cancellationToken)
    {
        ValidateClientId(clientId);
        if (quantity <= 0 || price <= 0)
        {
            throw new ArgumentException("Buy quantity and price must be positive.");
        }

        using var response = await RequestAsync(HttpMethod.Post, "/sapi/v1/order",
            [("symbol", Symbol), ("side", "buy"), ("type", "limit"),
                ("quantity", Format(quantity)), ("price", Format(price)), ("clientOrderId", clientId)],
            true, cancellationToken);
        var order = ReadOrder(response.RootElement);
        if (order.ClientOrderId != clientId || order.OriginalQuantity != quantity || order.Price != price)
        {
            throw InvalidResponse("The buy response differs from the submitted order; reconcile before continuing.");
        }

        return order;
    }

    public async Task<OrderInfo> GetOrderAsync(string clientId, CancellationToken cancellationToken)
    {
        ValidateClientId(clientId);
        using var response = await RequestAsync(HttpMethod.Get, "/sapi/v1/order",
            [("clientOrderId", clientId)], true, cancellationToken);
        var order = ReadOrder(response.RootElement);
        if (order.ClientOrderId != clientId)
        {
            throw InvalidResponse("The exchange returned a different client order identifier.");
        }

        return order;
    }

    public async Task<OrderInfo> PlaceSellAsync(string clientId, decimal quantity, decimal price,
        CancellationToken cancellationToken)
    {
        ValidateClientId(clientId);
        if (quantity <= 0 || price <= 0)
        {
            throw new ArgumentException("Sell quantity and price must be positive.");
        }

        using var response = await RequestAsync(HttpMethod.Post, "/sapi/v1/order",
            [("symbol", Symbol), ("side", "sell"), ("type", "limit"),
                ("quantity", Format(quantity)), ("price", Format(price)), ("clientOrderId", clientId)],
            true, cancellationToken);
        var order = ReadOrder(response.RootElement, "sell");
        if (order.ClientOrderId != clientId || order.OriginalQuantity != quantity || order.Price != price)
        {
            throw InvalidResponse("The sell response differs from the submitted order; reconcile before continuing.");
        }

        return order;
    }

    public async Task<OrderInfo> GetStrategyOrderAsync(string clientId, CancellationToken cancellationToken)
    {
        ValidateClientId(clientId);
        using var response = await RequestAsync(HttpMethod.Get, "/sapi/v1/order",
            [("clientOrderId", clientId)], true, cancellationToken);
        var order = ReadOrder(response.RootElement, null);
        if (order.ClientOrderId != clientId)
        {
            throw InvalidResponse("The exchange returned a different client order identifier.");
        }

        return order;
    }

    public async Task<OrderInfo> CancelStrategyOrderAsync(long orderId, CancellationToken cancellationToken)
    {
        if (orderId <= 0)
        {
            throw new ArgumentException("The order identifier must be positive.");
        }

        using var response = await RequestAsync(HttpMethod.Delete, "/sapi/v1/order",
            [("symbol", Symbol), ("orderId", orderId.ToString(CultureInfo.InvariantCulture))],
            true, cancellationToken);
        var order = ReadOrder(response.RootElement, null);
        if (order.Id != orderId)
        {
            throw InvalidResponse("The exchange returned a different canceled order identifier.");
        }

        return order;
    }

    public async Task<OrderInfo> CancelOrderAsync(long orderId, CancellationToken cancellationToken)
    {
        if (orderId <= 0)
        {
            throw new ArgumentException("The order identifier must be positive.");
        }

        using var response = await RequestAsync(HttpMethod.Delete, "/sapi/v1/order",
            [("symbol", Symbol), ("orderId", orderId.ToString(CultureInfo.InvariantCulture))],
            true, cancellationToken);
        var order = ReadOrder(response.RootElement);
        if (order.Id != orderId)
        {
            throw InvalidResponse("The exchange returned a different canceled order identifier.");
        }

        return order;
    }

    public Task<IReadOnlyList<TradeFill>> GetTradesAsync(long orderId, CancellationToken cancellationToken)
        => ReadTradesAsync(orderId, "buy", cancellationToken);

    public Task<IReadOnlyList<TradeFill>> GetStrategyTradesAsync(long orderId, string side,
        CancellationToken cancellationToken)
    {
        if (side is not ("buy" or "sell"))
        {
            throw new ArgumentException("A strategy fill side must be buy or sell.");
        }

        return ReadTradesAsync(orderId, side, cancellationToken);
    }

    private async Task<IReadOnlyList<TradeFill>> ReadTradesAsync(long orderId, string expectedSide,
        CancellationToken cancellationToken)
    {
        if (orderId <= 0)
        {
            throw new ArgumentException("The order identifier must be positive.");
        }

        using var response = await RequestAsync(HttpMethod.Get, "/sapi/v1/myTrades",
            [("symbol", Symbol), ("orderId", orderId.ToString(CultureInfo.InvariantCulture)),
                ("fromId", "0"), ("limit", "1000")], true, cancellationToken);
        var trades = Array(response.RootElement);
        if (trades.Length >= 1000)
        {
            throw InvalidResponse("Trade history reached its page limit; complete fill accounting requires manual reconciliation.");
        }

        var result = new List<TradeFill>();
        var seen = new HashSet<long>();
        foreach (var trade in trades)
        {
            var id = PositiveId(Integer(trade, "id"));
            var actualOrderId = PositiveId(Integer(trade, "orderId"));
            var symbol = Text(trade, "symbol");
            var side = Text(trade, "side");
            if (!seen.Add(id) || actualOrderId != orderId
                || !symbol.Equals(Symbol, StringComparison.OrdinalIgnoreCase) || side != expectedSide)
            {
                throw InvalidResponse("Trade history contains duplicate or unexpected fills.");
            }

            result.Add(new TradeFill(id, actualOrderId, symbol, side, Positive(Number(trade, "qty")),
                Nonnegative(Number(trade, "fee")), Text(trade, "feeCurrency")));
        }

        return result;
    }

    public async Task<WithdrawalReceipt> WithdrawAsync(string clientId, long addressBookId, decimal amount,
        string consent, CancellationToken cancellationToken)
    {
        ValidateClientId(clientId);
        if (addressBookId <= 0 || amount <= 0 || string.IsNullOrWhiteSpace(consent))
        {
            throw new ArgumentException("A valid address book entry, positive amount, and exact withdrawal consent are required.");
        }

        using var response = await RequestAsync(HttpMethod.Post, "/sapi/v1/crypto/withdraw",
            [("coin", "usdt"), ("addressBookId", addressBookId.ToString(CultureInfo.InvariantCulture)),
                ("amount", Format(amount)), ("withdrawConsent", consent), ("extra", "MyBot transfer"),
                ("withdrawOrderId", clientId)], true, cancellationToken);
        var id = Identifier(response.RootElement, "id");
        var returnedClientId = Text(response.RootElement, "withdrawOrderId");
        if (returnedClientId != clientId)
        {
            throw InvalidResponse("The withdrawal response has a different client identifier; reconcile before continuing.");
        }

        return new WithdrawalReceipt(id, returnedClientId);
    }

    public async Task<IReadOnlyList<WithdrawalInfo>> GetWithdrawalsAsync(string clientId,
        CancellationToken cancellationToken)
    {
        ValidateClientId(clientId);
        using var response = await RequestAsync(HttpMethod.Get, "/sapi/v1/crypto/withdraws",
            [("coin", "usdt"), ("withdrawOrderId", clientId), ("limit", "1000")], true, cancellationToken);
        var withdrawals = Array(response.RootElement);
        if (withdrawals.Length >= 1000)
        {
            throw InvalidResponse("Withdrawal history reached its page limit; manual reconciliation is required.");
        }

        var result = new List<WithdrawalInfo>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var withdrawal in withdrawals)
        {
            var id = Identifier(withdrawal, "id");
            var returnedClientId = Text(withdrawal, "withdrawOrderId");
            var coin = Text(withdrawal, "coin");
            if (!seen.Add(id) || returnedClientId != clientId
                || !coin.Equals("usdt", StringComparison.OrdinalIgnoreCase))
            {
                throw InvalidResponse("Withdrawal history contains unexpected identifiers or assets.");
            }

            var statusValue = Required(withdrawal, "status");
            var status = statusValue.ValueKind == JsonValueKind.Number
                ? statusValue.GetRawText() : TextValue(statusValue);
            var transactionId = withdrawal.TryGetProperty("txId", out var transactionValue)
                && transactionValue.ValueKind != JsonValueKind.Null ? TextValue(transactionValue, true) : "";
            result.Add(new WithdrawalInfo(id, returnedClientId, coin, Text(withdrawal, "network"),
                Text(withdrawal, "address"), Positive(Number(withdrawal, "amount")), status, transactionId));
        }

        return result;
    }

    private async Task<JsonDocument> RequestAsync(HttpMethod method, string path,
        IReadOnlyList<(string Name, string Value)> parameters, bool signed, CancellationToken cancellationToken)
    {
        await _requestGate.WaitAsync(cancellationToken);
        try
        {
            for (var attempt = 0; ; attempt++)
            {
                try
                {
                    if (signed && (_clockSyncedAt == 0 || Stopwatch.GetElapsedTime(_clockSyncedAt) > TimeSpan.FromSeconds(30)))
                    {
                        await SynchronizeClockAsync(cancellationToken);
                    }

                    await ThrottleAsync(method, path, cancellationToken);
                    var values = parameters.Select(item => new KeyValuePair<string, string>(item.Name, item.Value)).ToList();
                    if (signed)
                    {
                        values.Add(new("recvWindow", "5000"));
                        var timestamp = checked(_serverTimeAtSync + (long)Stopwatch.GetElapsedTime(_clockSyncedAt).TotalMilliseconds);
                        values.Add(new("timestamp", timestamp.ToString(CultureInfo.InvariantCulture)));
                    }

                    using var form = new FormUrlEncodedContent(values);
                    var payload = await form.ReadAsStringAsync(cancellationToken);
                    if (signed)
                    {
                        var signature = HMACSHA256.HashData(Encoding.UTF8.GetBytes(_secret), Encoding.UTF8.GetBytes(payload));
                        payload += "&signature=" + Convert.ToHexString(signature).ToLowerInvariant();
                    }

                    return await SendAsync(method, path, payload, signed, cancellationToken);
                }
                catch (WazirXApiException error) when (method == HttpMethod.Get &&
                    error.StatusCode == HttpStatusCode.TooManyRequests && attempt == 0)
                {
                    var cooldown = error.RetryAfter ?? TimeSpan.FromSeconds(30);
                    var cooldownStartedAt = Stopwatch.GetTimestamp();
                    var remaining = cooldown;
                    while (remaining > TimeSpan.Zero)
                    {
                        await Task.Delay(remaining < TimeSpan.FromMilliseconds(1)
                            ? TimeSpan.FromMilliseconds(1) : remaining, cancellationToken);
                        remaining = cooldown - Stopwatch.GetElapsedTime(cooldownStartedAt);
                    }
                    _clockSyncedAt = 0;
                }
            }
        }
        finally
        {
            _requestGate.Release();
        }
    }

    private async Task SynchronizeClockAsync(CancellationToken cancellationToken)
    {
        await ThrottleAsync(HttpMethod.Get, "/sapi/v1/time", cancellationToken);
        var startedAt = Stopwatch.GetTimestamp();
        using var response = await SendAsync(HttpMethod.Get, "/sapi/v1/time", "", false, cancellationToken);
        var roundTrip = Stopwatch.GetElapsedTime(startedAt);
        if (roundTrip > TimeSpan.FromSeconds(2))
        {
            throw InvalidResponse("Server time synchronization was too slow to sign a financial request safely.");
        }

        var serverTime = PositiveId(Integer(response.RootElement, "serverTime"));
        _serverTimeAtSync = checked(serverTime + (long)(roundTrip.TotalMilliseconds / 2));
        _clockSyncedAt = Stopwatch.GetTimestamp();
    }

    private async Task ThrottleAsync(HttpMethod method, string path, CancellationToken cancellationToken)
    {
        var key = method.Method + " " + path;
        var interval = path switch
        {
            "/sapi/v1/coins" or "/sapi/v1/crypto/withdraw/address-book" or "/sapi/v1/crypto/withdraws"
                => TimeSpan.FromSeconds(12.1),
            "/sapi/v1/crypto/withdraw" => TimeSpan.FromSeconds(60.1),
            "/sapi/v1/depth" or "/sapi/v1/myTrades" => TimeSpan.FromSeconds(0.55),
            "/sapi/v1/order" when method == HttpMethod.Get => TimeSpan.FromSeconds(0.55),
            "/sapi/v1/order" => TimeSpan.FromSeconds(0.11),
            _ => TimeSpan.FromSeconds(1.05)
        };
        if (_lastRequestTimes.TryGetValue(key, out var lastRequestAt))
        {
            var remaining = interval - Stopwatch.GetElapsedTime(lastRequestAt);
            if (remaining > TimeSpan.Zero)
            {
                await Task.Delay(remaining, cancellationToken);
            }
        }

        _lastRequestTimes[key] = Stopwatch.GetTimestamp();
    }

    private async Task<JsonDocument> SendAsync(HttpMethod method, string path, string payload,
        bool signed, CancellationToken cancellationToken)
    {
        var uri = ApiOrigin + path + (method == HttpMethod.Get && payload.Length > 0 ? "?" + payload : "");
        using var request = new HttpRequestMessage(method, uri);
        if (signed)
        {
            request.Headers.Add("X-API-KEY", _apiKey);
        }

        if (method != HttpMethod.Get)
        {
            request.Content = new StringContent(payload, Encoding.UTF8, "application/x-www-form-urlencoded");
        }

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(30));
        try
        {
            using var response = await _http.SendAsync(request, HttpCompletionOption.ResponseContentRead, timeout.Token);
            if (response.RequestMessage?.RequestUri is { } returnedUri
                && (returnedUri.Scheme != Uri.UriSchemeHttps || returnedUri.Host != "api.wazirx.com"))
            {
                throw new WazirXApiException("The HTTP client followed an unexpected redirect. Use WazirXClient.CreateHttpClient().");
            }

            var body = await response.Content.ReadAsStringAsync(timeout.Token);
            if (!response.IsSuccessStatusCode)
            {
                var code = ReadErrorCode(body);
                var retryHeader = response.Headers.RetryAfter;
                TimeSpan? retryAfter = retryHeader?.Delta;
                if (retryAfter is null && retryHeader?.Date is { } retryDate)
                    retryAfter = retryDate - DateTimeOffset.UtcNow;
                if (retryAfter < TimeSpan.Zero)
                    retryAfter = TimeSpan.Zero;
                var cooldown = retryAfter is { } delay
                    ? $" Wait at least {Math.Ceiling(delay.TotalSeconds).ToString(CultureInfo.InvariantCulture)} seconds before another request."
                    : "";
                var reconciliation = method == HttpMethod.Get
                    ? ""
                    : " Do not resubmit a financial request without reconciling its status.";
                throw new WazirXApiException($"WazirX rejected {method.Method} {path} (HTTP {(int)response.StatusCode}"
                    + (code.HasValue ? $"; API code {code.Value}" : "")
                    + ")." + cooldown + reconciliation, response.StatusCode, code)
                {
                    RetryAfter = retryAfter
                };
            }

            return JsonDocument.Parse(body);
        }
        catch (HttpRequestException)
        {
            throw new WazirXApiException("The WazirX connection failed. A submitted financial request may already have been accepted; reconcile before continuing.");
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            throw new WazirXApiException("The WazirX request timed out. A submitted financial request may already have been accepted; reconcile before continuing.");
        }
        catch (OperationCanceledException)
        {
            throw new OperationCanceledException("The WazirX request was canceled. Reconcile any submitted financial request before continuing.", cancellationToken);
        }
        catch (JsonException)
        {
            throw InvalidResponse("The exchange returned invalid JSON; reconcile any submitted financial request before continuing.");
        }
    }

    private static string ReadCoinCode(JsonElement value)
    {
        if (value.ValueKind != JsonValueKind.Object)
        {
            return Text(value, "coin");
        }

        var hasCoin = value.TryGetProperty("coin", out var coin);
        var hasCurrency = value.TryGetProperty("currency", out var currency);
        if (!hasCoin && !hasCurrency)
        {
            return Text(value, "coin");
        }

        var code = TextValue(hasCoin ? coin : currency);
        if (hasCoin && hasCurrency && !code.Equals(TextValue(currency), StringComparison.OrdinalIgnoreCase))
        {
            throw InvalidResponse("The exchange returned conflicting coin and currency identifiers.");
        }

        return code;
    }

    private static decimal ReadAliasedNumber(JsonElement value, string name, string alternateName)
    {
        var hasPrimary = value.TryGetProperty(name, out var primary);
        var hasAlternate = value.TryGetProperty(alternateName, out var alternate);
        if (!hasPrimary && !hasAlternate)
        {
            return Number(value, name);
        }

        var number = NumberValue(hasPrimary ? primary : alternate);
        if (hasPrimary && hasAlternate && number != NumberValue(alternate))
        {
            throw InvalidResponse("The exchange returned conflicting withdrawal amount limits.");
        }

        return number;
    }

    private static decimal? ReadWithdrawalStep(JsonElement network)
    {
        var multiple = OptionalNumber(network, "withdrawIntegerMultiple");
        if (!network.TryGetProperty("precision", out _))
        {
            return multiple;
        }

        var precision = Number(network, "precision");
        if (precision < 0 || precision > 28 || precision != decimal.Truncate(precision))
        {
            throw InvalidResponse("The exchange returned an unsupported withdrawal decimal precision.");
        }

        var precisionStep = 1m;
        for (var digit = 0; digit < (int)precision; digit++)
        {
            precisionStep /= 10m;
        }

        if (multiple.HasValue && multiple.Value != precisionStep)
        {
            throw InvalidResponse("The exchange returned conflicting withdrawal increments.");
        }

        return precisionStep;
    }

    private static bool ReadMemoRequired(JsonElement network)
    {
        var hasExplicitFlag = network.TryGetProperty("memoRequired", out _);
        var currentLayout = network.TryGetProperty("minWithdrawAmount", out _) &&
            network.TryGetProperty("maxWithdrawAmount", out _) && network.TryGetProperty("precision", out _);
        if (!hasExplicitFlag && !currentLayout)
        {
            return Boolean(network, "memoRequired");
        }

        var memoRequired = false;
        if (network.TryGetProperty("memo", out var memo) && memo.ValueKind != JsonValueKind.Null)
        {
            if (memo.ValueKind != JsonValueKind.Object)
            {
                throw InvalidResponse("The exchange returned unsupported memo metadata.");
            }

            if (memo.TryGetProperty("regex", out var regex) && regex.ValueKind != JsonValueKind.Null)
            {
                memoRequired = TextValue(regex, true).Length > 0;
            }
        }

        if (hasExplicitFlag)
        {
            var explicitFlag = Boolean(network, "memoRequired");
            if (!explicitFlag && memoRequired)
            {
                throw InvalidResponse("The exchange returned conflicting memo requirements.");
            }

            return explicitFlag;
        }

        return memoRequired;
    }

    private static decimal[] ReadBookPrices(JsonElement value, string side)
    {
        var levels = Array(Required(value, side));
        if (levels.Length == 0)
        {
            throw InvalidResponse("The exchange returned an empty order book side.");
        }

        return levels.Select(level =>
        {
            var values = Array(level);
            if (values.Length != 2)
            {
                throw InvalidResponse("The exchange returned an unsupported order book.");
            }

            Positive(NumberValue(values[1]));
            return Positive(NumberValue(values[0]));
        }).ToArray();
    }

    private static OrderInfo ReadOrder(JsonElement value, string? expectedSide = "buy")
    {
        var order = new OrderInfo(PositiveId(Integer(value, "id")), Text(value, "clientOrderId"),
            Text(value, "symbol"), Text(value, "side"), Text(value, "type"),
            Positive(Number(value, "price")), Positive(Number(value, "origQty")),
            Nonnegative(Number(value, "executedQty")), Text(value, "status"));
        if (!order.Symbol.Equals(Symbol, StringComparison.OrdinalIgnoreCase)
            || order.Side is not ("buy" or "sell") || (expectedSide is not null && order.Side != expectedSide)
            || order.Type != "limit" || order.ExecutedQuantity > order.OriginalQuantity
            || order.Status is not ("idle" or "wait" or "done" or "cancel"))
        {
            throw InvalidResponse("The exchange returned an unsupported order response.");
        }

        return order;
    }

    private static int? ReadErrorCode(string body)
    {
        try
        {
            using var document = JsonDocument.Parse(body);
            if (document.RootElement.ValueKind == JsonValueKind.Object
                && document.RootElement.TryGetProperty("code", out var code))
            {
                if (code.ValueKind == JsonValueKind.Number && code.TryGetInt32(out var numericCode))
                {
                    return numericCode;
                }

                if (code.ValueKind == JsonValueKind.String
                    && int.TryParse(code.GetString(), NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out numericCode))
                {
                    return numericCode;
                }
            }
        }
        catch (JsonException)
        {
        }

        return null;
    }

    private static JsonElement Required(JsonElement value, string name)
    {
        if (value.ValueKind != JsonValueKind.Object || !value.TryGetProperty(name, out var property))
        {
            throw InvalidResponse($"The exchange response is missing required field '{name}'.");
        }

        return property;
    }

    private static JsonElement[] Array(JsonElement value)
    {
        if (value.ValueKind != JsonValueKind.Array)
        {
            throw InvalidResponse("The exchange returned an unsupported response structure.");
        }

        return value.EnumerateArray().ToArray();
    }

    private static string Text(JsonElement value, string name) => TextValue(Required(value, name));

    private static string TextValue(JsonElement value, bool allowEmpty = false)
    {
        if (value.ValueKind != JsonValueKind.String || value.GetString() is not { } result
            || (!allowEmpty && string.IsNullOrWhiteSpace(result)))
        {
            throw InvalidResponse("The exchange returned an invalid text field.");
        }

        return result;
    }

    private static bool Boolean(JsonElement value, string name)
    {
        var property = Required(value, name);
        if (property.ValueKind is not (JsonValueKind.True or JsonValueKind.False))
        {
            throw InvalidResponse("The exchange returned an invalid permission field.");
        }

        return property.GetBoolean();
    }

    private static long Integer(JsonElement value, string name)
    {
        var property = Required(value, name);
        if (property.ValueKind == JsonValueKind.Number && property.TryGetInt64(out var number))
        {
            return number;
        }

        if (property.ValueKind == JsonValueKind.String
            && long.TryParse(property.GetString(), NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out number))
        {
            return number;
        }

        throw InvalidResponse("The exchange returned an invalid integer field.");
    }

    private static string Identifier(JsonElement value, string name)
    {
        var property = Required(value, name);
        return property.ValueKind == JsonValueKind.Number
            ? PositiveId(Integer(value, name)).ToString(CultureInfo.InvariantCulture)
            : TextValue(property);
    }

    private static decimal Number(JsonElement value, string name) => NumberValue(Required(value, name));

    private static decimal? OptionalNumber(JsonElement value, string name) => value.TryGetProperty(name, out var property)
        ? NumberValue(property) : null;

    private static decimal NumberValue(JsonElement value)
    {
        if (value.ValueKind == JsonValueKind.Number && value.TryGetDecimal(out var number))
        {
            return number;
        }

        if (value.ValueKind == JsonValueKind.String
            && decimal.TryParse(value.GetString(), NumberStyles.AllowLeadingSign | NumberStyles.AllowDecimalPoint,
                CultureInfo.InvariantCulture, out number))
        {
            return number;
        }

        throw InvalidResponse("The exchange returned an invalid decimal amount.");
    }

    private static decimal Positive(decimal value) => value > 0 ? value
        : throw InvalidResponse("The exchange returned a nonpositive amount or increment.");

    private static decimal Nonnegative(decimal value) => value >= 0 ? value
        : throw InvalidResponse("The exchange returned a negative amount.");

    private static long PositiveId(long value) => value > 0 ? value
        : throw InvalidResponse("The exchange returned an invalid identifier or timestamp.");

    private static string Format(decimal value) => value.ToString("0.############################", CultureInfo.InvariantCulture);

    private static void ValidateClientId(string value)
    {
        if (string.IsNullOrEmpty(value) || value.Length > 64 || value.Any(character => !char.IsAsciiLetterOrDigit(character)))
        {
            throw new ArgumentException("A client identifier must contain 1 to 64 ASCII letters and digits.");
        }
    }

    private static WazirXApiException InvalidResponse(string message) => new(message);
}

public sealed class WazirXApiException(string message, HttpStatusCode? statusCode = null, int? apiCode = null)
    : Exception(message)
{
    public HttpStatusCode? StatusCode { get; } = statusCode;
    public int? ApiCode { get; } = apiCode;
    public TimeSpan? RetryAfter { get; init; }
}
