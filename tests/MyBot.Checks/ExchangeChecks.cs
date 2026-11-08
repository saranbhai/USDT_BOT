using System.Globalization;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using MyBot.Core;
using MyBot.Exchange;

public static class ExchangeChecks
{
    private const string ApiKey = "syntheticApiKeyForOfflineChecks";
    private const string Secret = "syntheticSecretForOfflineChecks";
    private const string ClientId = "bOfflinePurchase123";
    private const string WithdrawalClientId = "wOfflineTransfer123";
    private const string Consent = "I consent: USDT + fees & destination=verified / café? 100%";
    private const string NetworkJson = """
        [{"coin":"USDT","networkList":[{
          "network":"TESTNET","name":"Synthetic offline network","withdrawEnable":true,
          "withdrawFee":"0.25","withdrawMin":"1","withdrawMax":"1000",
          "withdrawIntegerMultiple":"0.000001","memoRequired":false,
          "withdrawConsent":{"message":"Exact consent from the exchange"}
        }]}]
        """;
    private const string SymbolJson = """
        {"symbols":[{
          "symbol":"usdtinr","status":"trading","baseAsset":"usdt","quoteAsset":"inr",
          "baseAssetPrecision":6,"orderTypes":["limit"],"isSpotTradingAllowed":true,
          "filters":[{"filterType":"PRICE_FILTER","minPrice":"0.01","tickSize":"0.01"}]
        }]}
        """;
    private const string CurrentNetworkJson = """
        [{"currency":"usdt","name":"Synthetic USDT","networkList":[{
          "network":"TESTNET","name":"Synthetic offline network","withdrawEnable":true,
          "withdrawFee":"0.25","minWithdrawAmount":"1","maxWithdrawAmount":"1000",
          "precision":6,"withdrawConsent":{"message":"Exact consent from the exchange"}
        }]}]
        """;

    public static async Task RunAsync()
    {
        await SignedGetAsync();
        await SignedBuyAsync();
        await SignedWithdrawalAsync();
        await FailedMutationIsNotRetriedAsync(false);
        await FailedMutationIsNotRetriedAsync(true);
        await ConstructorRejectsInvalidCredentialsAsync();
        await MissingFieldDiagnosticsAsync();
        await CurrentCoinMetadataAsync();
        await ConflictingCoinMetadataAsync();
        await MissingWithdrawalMetadataAsync();
        await MissingAddressMetadataAsync();
        await MissingSymbolMetadataAsync();
        await RawWithdrawalStatusAsync();
    }

    private static async Task SignedGetAsync()
    {
        var handler = new OfflineHandler(_ => Json("""
            [{"asset":"inr","free":"1234.56","locked":"0.0"}]
            """));
        using var http = new HttpClient(handler) { BaseAddress = new Uri("https://unused.invalid/") };
        var balances = await Client(http).GetBalancesAsync(CancellationToken.None);
        Equal(1, balances.Count, "balance count");
        Equal(1234.56m, balances[0].Free, "decimal balance parsing");
        var request = SingleRequest(handler, "/sapi/v1/funds");
        Equal(HttpMethod.Get, request.Method, "signed GET method");
        Equal("", request.Body, "signed GET body");
        var fields = AssertSignature(request, handler.ServerTime);
        Equal("recvWindow=5000&timestamp=" + fields["timestamp"],
            WithoutSignature(request.Query), "signed GET payload");
    }

    private static async Task SignedBuyAsync()
    {
        var previousCulture = CultureInfo.CurrentCulture;
        var previousUiCulture = CultureInfo.CurrentUICulture;
        try
        {
            CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo("fr-FR");
            CultureInfo.CurrentUICulture = CultureInfo.GetCultureInfo("fr-FR");
            var handler = new OfflineHandler(_ => Json(OrderJson()));
            using var http = new HttpClient(handler);
            var order = await Client(http).PlaceBuyAsync(ClientId, 1.25m, 89.12m, CancellationToken.None);
            Equal(1.25m, order.OriginalQuantity, "buy quantity");
            Equal(89.12m, order.Price, "buy price");
            var request = SingleRequest(handler, "/sapi/v1/order");
            Equal(HttpMethod.Post, request.Method, "buy method");
            Equal("", request.Query, "POST parameters do not appear in the URL");
            Equal("application/x-www-form-urlencoded", request.ContentType, "buy content type");
            var fields = AssertSignature(request, handler.ServerTime);
            Equal("buy", fields["side"], "lowercase buy side");
            Equal("limit", fields["type"], "lowercase order type");
            Equal("1.25", fields["quantity"], "culture-invariant quantity");
            Equal("89.12", fields["price"], "culture-invariant price");
            Equal("symbol=usdtinr&side=buy&type=limit&quantity=1.25&price=89.12&clientOrderId="
                + ClientId + "&recvWindow=5000&timestamp=" + fields["timestamp"],
                WithoutSignature(request.Body), "exact buy form");
        }
        finally
        {
            CultureInfo.CurrentCulture = previousCulture;
            CultureInfo.CurrentUICulture = previousUiCulture;
        }
    }

    private static async Task SignedWithdrawalAsync()
    {
        var handler = new OfflineHandler(_ => Json(JsonSerializer.Serialize(new
        {
            id = 1234567,
            withdrawOrderId = WithdrawalClientId
        })));
        using var http = new HttpClient(handler);
        var receipt = await Client(http).WithdrawAsync(WithdrawalClientId, 980, 1.234567m,
            Consent, CancellationToken.None);
        Equal("1234567", receipt.Id, "numeric withdrawal receipt identifier");
        Equal(WithdrawalClientId, receipt.ClientId, "withdrawal client identifier");
        var request = SingleRequest(handler, "/sapi/v1/crypto/withdraw");
        Equal(HttpMethod.Post, request.Method, "withdrawal method");
        Equal("", request.Query, "withdrawal URL query");
        Equal("application/x-www-form-urlencoded", request.ContentType, "withdrawal content type");
        var fields = AssertSignature(request, handler.ServerTime);
        Equal(Consent, fields["withdrawConsent"], "exact consent survives form encoding");
        Equal("1.234567", fields["amount"], "withdrawal amount encoding");
        Equal("coin=usdt&addressBookId=980&amount=1.234567&withdrawConsent="
            + "I+consent%3A+USDT+%2B+fees+%26+destination%3Dverified+%2F+caf%C3%A9%3F+100%25"
            + "&extra=MyBot+transfer&withdrawOrderId=" + WithdrawalClientId
            + "&recvWindow=5000&timestamp=" + fields["timestamp"],
            WithoutSignature(request.Body), "exact percent-encoded withdrawal form");
    }

    private static async Task FailedMutationIsNotRetriedAsync(bool timeout)
    {
        const string privateResponse = "private upstream error body that must not be logged";
        var handler = new OfflineHandler(_ => timeout
            ? Task.FromException<HttpResponseMessage>(new TaskCanceledException(ApiKey + Secret + privateResponse))
            : Task.FromResult(Json(JsonSerializer.Serialize(new
            {
                code = -1000,
                msg = ApiKey + Secret + privateResponse
            }), HttpStatusCode.ServiceUnavailable)));
        using var http = new HttpClient(handler);
        var exception = await ThrowsAsync<WazirXApiException>(() =>
            Client(http).PlaceBuyAsync(ClientId, 1.25m, 89.12m, CancellationToken.None));
        Equal(1, handler.Requests.Count(request => request.Path == "/sapi/v1/order"),
            "a failed financial mutation must not be retried");
        Equal(2, handler.Requests.Count, "only server time and the single mutation were requested");
        if (!timeout)
        {
            Equal<HttpStatusCode?>(HttpStatusCode.ServiceUnavailable, exception.StatusCode, "safe HTTP status");
            Equal<int?>(-1000, exception.ApiCode, "safe numeric API code");
        }

        AssertSanitized(exception, privateResponse);
    }

    private static Task ConstructorRejectsInvalidCredentialsAsync()
    {
        var handler = new OfflineHandler(_ => Json("{}"));
        using var http = new HttpClient(handler);
        foreach (var credentials in new[] { (ApiKey + "\n", Secret), (ApiKey, Secret + "\r"), ("", Secret), (ApiKey, "") })
        {
            try
            {
                _ = new WazirXClient(http, credentials.Item1, credentials.Item2);
            }
            catch (ArgumentException exception)
            {
                AssertSanitized(exception);
                continue;
            }

            throw new InvalidOperationException("Invalid credentials were accepted.");
        }

        Equal(0, handler.Requests.Count, "invalid credentials must not reach a handler");
        return Task.CompletedTask;
    }

    private static async Task MissingFieldDiagnosticsAsync()
    {
        const string privateDetail = "synthetic private response detail";
        foreach (var field in new[] { "coin", "networkList", "withdrawMin", "name" })
        {
            var document = JsonNode.Parse(NetworkJson)!;
            var target = field is "coin" or "networkList"
                ? document[0]!.AsObject()
                : document[0]!["networkList"]![0]!.AsObject();
            target.Remove(field);
            target["privateDetail"] = ApiKey + Secret + privateDetail;
            var handler = new OfflineHandler(_ => Json(document.ToJsonString()));
            using var http = new HttpClient(handler);
            var exception = await ThrowsAsync<WazirXApiException>(() =>
                Client(http).GetNetworksAsync(CancellationToken.None));
            Require(exception.Message.Contains($"required field '{field}'", StringComparison.Ordinal),
                "Missing-field errors must identify the expected field.");
            AssertSanitized(exception, privateDetail);
            Equal(0, handler.Requests.Count(request => request.Method != HttpMethod.Get),
                "Missing-field diagnostics must not submit financial requests.");
        }

        var clockHandler = new OfflineHandler(_ => Json(NetworkJson))
        {
            ClockResponseJson = JsonSerializer.Serialize(new { privateDetail = ApiKey + Secret + privateDetail })
        };
        using var clockHttp = new HttpClient(clockHandler);
        var clockException = await ThrowsAsync<WazirXApiException>(() =>
            Client(clockHttp).GetNetworksAsync(CancellationToken.None));
        Require(clockException.Message.Contains("required field 'serverTime'", StringComparison.Ordinal),
            "Clock response errors must identify serverTime before any wallet request.");
        AssertSanitized(clockException, privateDetail);
        Equal(1, clockHandler.Requests.Count, "An invalid clock response must stop before requesting wallet data.");
    }

    private static async Task CurrentCoinMetadataAsync()
    {
        var handler = new OfflineHandler(_ => Json(CurrentNetworkJson));
        using var http = new HttpClient(handler);
        var network = (await Client(http).GetNetworksAsync(CancellationToken.None)).Single();
        Equal(0.25m, network.Fee, "current-format withdrawal fee");
        Equal(1m, network.Minimum, "current-format withdrawal minimum");
        Equal(1000m, network.Maximum, "current-format withdrawal maximum");
        Equal<decimal?>(0.000001m, network.IntegerMultiple, "decimal places converted to an exact increment");
        Equal(false, network.MemoRequired, "current-format network without a memo object");
        Equal("Exact consent from the exchange", network.Consent, "current-format exact consent");

        foreach (var precision in new[] { 0m, 28m })
        {
            var document = JsonNode.Parse(CurrentNetworkJson)!;
            document[0]!["networkList"]![0]!["precision"] = precision;
            var precisionHandler = new OfflineHandler(_ => Json(document.ToJsonString()));
            using var precisionHttp = new HttpClient(precisionHandler);
            var parsed = (await Client(precisionHttp).GetNetworksAsync(CancellationToken.None)).Single();
            Equal<decimal?>(precision == 0 ? 1m : 0.0000000000000000000000000001m,
                parsed.IntegerMultiple, "supported decimal-precision boundaries");
        }

        var memoDocument = JsonNode.Parse(CurrentNetworkJson)!;
        memoDocument[0]!["networkList"]![0]!["memo"] = new JsonObject
        {
            ["regex"] = "^[0-9]+$",
            ["label"] = "Synthetic tag"
        };
        var memoHandler = new OfflineHandler(_ => Json(memoDocument.ToJsonString()));
        using var memoHttp = new HttpClient(memoHandler);
        var memoNetwork = (await Client(memoHttp).GetNetworksAsync(CancellationToken.None)).Single();
        Equal(true, memoNetwork.MemoRequired, "current-format memo regex is recognized");
        try
        {
            TransferPlanner.ValidateRoute(memoNetwork, new DestinationRoute
            {
                Network = "TESTNET", AddressBookId = 1, Address = "synthetic-address",
                MinimumDepositUsdt = 1m, DepositEnabledConfirmed = true
            }, [], DateTimeOffset.UtcNow);
        }
        catch (InvalidOperationException exception) when (exception.Message.Contains("Memo-required", StringComparison.Ordinal))
        {
            return;
        }
        throw new InvalidOperationException("A memo-required current-format network was not blocked by the planner.");
    }

    private static async Task ConflictingCoinMetadataAsync()
    {
        foreach (var field in new[] { "coin", "withdrawMin", "withdrawMax", "withdrawIntegerMultiple", "memoRequired" })
        {
            var document = JsonNode.Parse(CurrentNetworkJson)!;
            var coin = document[0]!.AsObject();
            var network = coin["networkList"]![0]!.AsObject();
            switch (field)
            {
                case "coin": coin["coin"] = "btc"; break;
                case "withdrawMin": network["withdrawMin"] = "2"; break;
                case "withdrawMax": network["withdrawMax"] = "2000"; break;
                case "withdrawIntegerMultiple": network["withdrawIntegerMultiple"] = "0.01"; break;
                case "memoRequired":
                    network["memoRequired"] = false;
                    network["memo"] = new JsonObject { ["regex"] = "^[0-9]+$" };
                    break;
            }
            var handler = new OfflineHandler(_ => Json(document.ToJsonString()));
            using var http = new HttpClient(handler);
            var exception = await ThrowsAsync<WazirXApiException>(() => Client(http).GetNetworksAsync(CancellationToken.None));
            AssertSanitized(exception);
        }

        foreach (var precision in new[] { -1m, 29m, 1.5m })
        {
            var document = JsonNode.Parse(CurrentNetworkJson)!;
            document[0]!["networkList"]![0]!["precision"] = precision;
            var handler = new OfflineHandler(_ => Json(document.ToJsonString()));
            using var http = new HttpClient(handler);
            await ThrowsAsync<WazirXApiException>(() => Client(http).GetNetworksAsync(CancellationToken.None));
        }

        foreach (var field in new[] { "currency", "networkList", "minWithdrawAmount", "maxWithdrawAmount", "precision", "withdrawConsent" })
        {
            var document = JsonNode.Parse(CurrentNetworkJson)!;
            var target = field is "currency" or "networkList"
                ? document[0]!.AsObject() : document[0]!["networkList"]![0]!.AsObject();
            target.Remove(field);
            var handler = new OfflineHandler(_ => Json(document.ToJsonString()));
            using var http = new HttpClient(handler);
            await ThrowsAsync<WazirXApiException>(() => Client(http).GetNetworksAsync(CancellationToken.None));
        }
    }

    private static async Task MissingWithdrawalMetadataAsync()
    {
        foreach (var property in new[]
        {
            "withdrawConsent", "withdrawFee", "withdrawMin", "withdrawMax", "memoRequired", "withdrawEnable"
        })
        {
            var document = JsonNode.Parse(NetworkJson)!;
            document[0]!["networkList"]![0]!.AsObject().Remove(property);
            var handler = new OfflineHandler(_ => Json(document.ToJsonString()));
            using var http = new HttpClient(handler);
            var exception = await ThrowsAsync<WazirXApiException>(() => Client(http).GetNetworksAsync(CancellationToken.None));
            AssertSanitized(exception);
            Equal(0, handler.Requests.Count(request => request.Method != HttpMethod.Get),
                "missing withdrawal metadata cannot submit a mutation");
        }

        var validHandler = new OfflineHandler(_ => Json(NetworkJson));
        using var validHttp = new HttpClient(validHandler);
        var networks = await Client(validHttp).GetNetworksAsync(CancellationToken.None);
        Equal("Exact consent from the exchange", networks.Single().Consent, "network consent metadata");
        Equal(0.25m, networks.Single().Fee, "network fee metadata");
    }

    private static async Task MissingAddressMetadataAsync()
    {
        const string addressJson = """
            [{"id":980,"currency":"usdt","address":"synthetic-offline-address",
              "isUniversal":false,"isWithdrawAllowed":true,
              "networkObj":{"network":"TESTNET"},"withdrawAllowedAfter":null}]
            """;
        foreach (var property in new[] { "withdrawAllowedAfter", "isWithdrawAllowed", "networkObj" })
        {
            var document = JsonNode.Parse(addressJson)!;
            document[0]!.AsObject().Remove(property);
            var handler = new OfflineHandler(_ => Json(document.ToJsonString()));
            using var http = new HttpClient(handler);
            var exception = await ThrowsAsync<WazirXApiException>(() => Client(http).GetAddressesAsync(CancellationToken.None));
            Require(exception.Message.Contains($"required field '{property}'", StringComparison.Ordinal),
                "Address-book errors must identify the missing field.");
            AssertSanitized(exception);
        }

        var validHandler = new OfflineHandler(_ => Json(addressJson));
        using var validHttp = new HttpClient(validHandler);
        var addresses = await Client(validHttp).GetAddressesAsync(CancellationToken.None);
        Equal<DateTimeOffset?>(null, addresses.Single().AllowedAfter, "explicit null address cooldown");
    }

    private static async Task MissingSymbolMetadataAsync()
    {
        foreach (var property in new[] { "minPrice", "tickSize" })
        {
            var document = JsonNode.Parse(SymbolJson)!;
            document["symbols"]![0]!["filters"]![0]!.AsObject().Remove(property);
            var handler = new OfflineHandler(_ => Json(document.ToJsonString()));
            using var http = new HttpClient(handler);
            await ThrowsAsync<WazirXApiException>(() => Client(http).GetSymbolAsync(CancellationToken.None));
        }

        var unknown = JsonNode.Parse(SymbolJson)!;
        unknown["symbols"]![0]!["filters"]!.AsArray().Add(new JsonObject { ["filterType"] = "UNKNOWN_LIMIT" });
        var unknownHandler = new OfflineHandler(_ => Json(unknown.ToJsonString()));
        using var unknownHttp = new HttpClient(unknownHandler);
        await ThrowsAsync<WazirXApiException>(() => Client(unknownHttp).GetSymbolAsync(CancellationToken.None));
    }

    private static async Task RawWithdrawalStatusAsync()
    {
        foreach (var status in new[] { 1, 987654 })
        {
            var handler = new OfflineHandler(_ => Json(JsonSerializer.Serialize(new[]
            {
                new
                {
                    id = "synthetic-withdrawal-id",
                    withdrawOrderId = WithdrawalClientId,
                    coin = "USDT",
                    network = "TESTNET",
                    address = "synthetic-offline-address",
                    amount = "1.25",
                    status,
                    txId = ""
                }
            })));
            using var http = new HttpClient(handler);
            var withdrawals = await Client(http).GetWithdrawalsAsync(WithdrawalClientId, CancellationToken.None);
            Equal(status.ToString(CultureInfo.InvariantCulture), withdrawals.Single().RawStatus,
                "numeric withdrawal status must remain uninterpreted");
            var request = SingleRequest(handler, "/sapi/v1/crypto/withdraws");
            var fields = AssertSignature(request, handler.ServerTime);
            Equal(WithdrawalClientId, fields["withdrawOrderId"], "history lookup client identifier");
        }
    }

    private static WazirXClient Client(HttpClient http) => new(http, ApiKey, Secret);

    private static Dictionary<string, string> AssertSignature(CapturedRequest request, long serverTime)
    {
        Equal("https", request.Scheme, "trusted API scheme");
        Equal("api.wazirx.com", request.Host, "trusted API host");
        Equal(ApiKey, request.ApiKey, "API key header");
        var payload = request.Method == HttpMethod.Get ? request.Query : request.Body;
        var unsigned = WithoutSignature(payload);
        var fields = DecodeForm(payload);
        var expectedSignature = Convert.ToHexString(HMACSHA256.HashData(
            Encoding.UTF8.GetBytes(Secret), Encoding.UTF8.GetBytes(unsigned))).ToLowerInvariant();
        Equal(expectedSignature, fields["signature"], "HMAC of the exact transmitted encoded payload");
        Equal("5000", fields["recvWindow"], "receive window");
        Require(long.TryParse(fields["timestamp"], NumberStyles.None, CultureInfo.InvariantCulture, out var timestamp),
            "Signed timestamp is not an invariant integer.");
        Require(timestamp >= serverTime && timestamp - serverTime < 5000,
            "Signed timestamp did not use the supplied server clock.");
        Require(!payload.Contains(ApiKey, StringComparison.Ordinal) && !payload.Contains(Secret, StringComparison.Ordinal),
            "The signed payload contains credentials.");
        return fields;
    }

    private static string WithoutSignature(string payload)
    {
        var separator = payload.LastIndexOf("&signature=", StringComparison.Ordinal);
        Require(separator > 0, "The signature must follow the signed parameters.");
        Require(!payload[..separator].Contains("signature=", StringComparison.Ordinal), "Duplicate signature field.");
        Require(!payload[(separator + 1)..].Contains('&'), "The signature is not the last form field.");
        return payload[..separator];
    }

    private static Dictionary<string, string> DecodeForm(string payload) => payload.Split('&')
        .Select(part => part.Split('=', 2))
        .ToDictionary(pair => Uri.UnescapeDataString(pair[0].Replace('+', ' ')),
            pair => Uri.UnescapeDataString(pair[1].Replace('+', ' ')), StringComparer.Ordinal);

    private static CapturedRequest SingleRequest(OfflineHandler handler, string path)
    {
        Equal(1, handler.Requests.Count(request => request.Path == "/sapi/v1/time"), "single clock request");
        var clock = handler.Requests.Single(request => request.Path == "/sapi/v1/time");
        Equal("", clock.ApiKey, "public clock request must not carry credentials");
        return handler.Requests.Single(request => request.Path == path);
    }

    private static string OrderJson() => JsonSerializer.Serialize(new
    {
        id = 28,
        clientOrderId = ClientId,
        symbol = "usdtinr",
        price = "89.12",
        origQty = "1.25",
        executedQty = "0",
        status = "wait",
        type = "limit",
        side = "buy"
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

    private static void AssertSanitized(Exception exception, params string[] additionalPrivateValues)
    {
        foreach (var privateValue in new[] { ApiKey, Secret }.Concat(additionalPrivateValues))
        {
            Require(!exception.ToString().Contains(privateValue, StringComparison.Ordinal),
                "An exception exposed credentials or private response content.");
        }
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
        public string? ClockResponseJson { get; init; }
        public List<CapturedRequest> Requests { get; } = [];

        public OfflineHandler(Func<CapturedRequest, HttpResponseMessage> respond)
            : this(request => Task.FromResult(respond(request)))
        {
        }

        public OfflineHandler(Func<CapturedRequest, Task<HttpResponseMessage>> respond) => _respond = respond;

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var uri = request.RequestUri ?? throw new InvalidOperationException("Missing request URI.");
            var captured = new CapturedRequest(request.Method, uri.Scheme, uri.Host, uri.AbsolutePath,
                uri.Query.TrimStart('?'), request.Content is null ? "" : await request.Content.ReadAsStringAsync(cancellationToken),
                request.Headers.TryGetValues("X-API-KEY", out var values) ? string.Join(",", values) : "",
                request.Content?.Headers.ContentType?.MediaType ?? "");
            Requests.Add(captured);
            var response = uri.AbsolutePath == "/sapi/v1/time"
                ? Json(ClockResponseJson ?? JsonSerializer.Serialize(new { serverTime = ServerTime }))
                : await _respond(captured);
            response.RequestMessage = request;
            return response;
        }
    }
}
