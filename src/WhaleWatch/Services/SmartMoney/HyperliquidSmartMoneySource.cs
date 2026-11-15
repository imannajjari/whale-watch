using System;
using System.Collections.Generic;
using System.Globalization;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using WhaleWatch.Abstractions.SmartMoney;
using WhaleWatch.Models.SmartMoney;

namespace WhaleWatch.Services.SmartMoney;

public sealed class HyperliquidSmartMoneySource(
    HttpClient httpClient,
    SmartMoneySettings settings,
    TimeSpan? retryDelay = null,
    TimeProvider? timeProvider = null) : ISmartMoneySource
{
    public const int MaxRetries = 4;

    private readonly HttpClient _http = httpClient ?? throw new ArgumentNullException(nameof(httpClient));
    private readonly SmartMoneySettings _settings = settings ?? throw new ArgumentNullException(nameof(settings));
    private readonly TimeSpan _retryDelay = retryDelay ?? TimeSpan.FromSeconds(2);
    private readonly TimeProvider _time = timeProvider ?? TimeProvider.System;

    private string BaseUrl => string.IsNullOrWhiteSpace(_settings.BaseUrl)
        ? SmartMoneySettings.DefaultBaseUrl
        : _settings.BaseUrl.TrimEnd('/');

    private string LeaderboardUrl => string.IsNullOrWhiteSpace(_settings.LeaderboardUrl)
        ? SmartMoneySettings.DefaultLeaderboardUrl
        : _settings.LeaderboardUrl;

    public async Task<IReadOnlyList<TrackedWallet>> GetLeaderboardAsync(CancellationToken cancellationToken = default)
    {
        using var response = await SendWithRetryAsync(
            () => new HttpRequestMessage(HttpMethod.Get, LeaderboardUrl),
            cancellationToken);

        response.EnsureSuccessStatusCode();
        string json = await response.Content.ReadAsStringAsync(cancellationToken);
        using var doc = JsonDocument.Parse(json);

        if (!doc.RootElement.TryGetProperty("leaderboardRows", out var rows) || rows.ValueKind != JsonValueKind.Array)
            return [];

        DateTimeOffset now = _time.GetUtcNow();
        var wallets = new List<TrackedWallet>();
        foreach (var row in rows.EnumerateArray())
        {
            string address = row.TryGetProperty("ethAddress", out var addrEl) ? addrEl.GetString() ?? "" : "";
            if (string.IsNullOrWhiteSpace(address))
                continue;

            decimal accountValue = ReadDecimal(row, "accountValue");
            (decimal monthPnl, decimal monthRoi, _) = ReadWindow(row, "month");
            (decimal allTimePnl, _, decimal allTimeVol) = ReadWindow(row, "allTime");

            wallets.Add(new TrackedWallet(
                address,
                accountValue,
                monthPnl,
                monthRoi,
                allTimePnl,
                allTimeVol,
                now));
        }

        return wallets;
    }

    public async Task<IReadOnlyList<WalletPositionSnapshot>> GetOpenPositionsAsync(
        string walletAddress,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(walletAddress))
            return [];

        string payload = JsonSerializer.Serialize(new { type = "clearinghouseState", user = walletAddress });
        using var response = await SendInfoPostAsync(payload, cancellationToken);
        response.EnsureSuccessStatusCode();

        string json = await response.Content.ReadAsStringAsync(cancellationToken);
        using var doc = JsonDocument.Parse(json);

        if (!doc.RootElement.TryGetProperty("assetPositions", out var positionsEl) || positionsEl.ValueKind != JsonValueKind.Array)
            return [];

        decimal? accountValue = doc.RootElement.TryGetProperty("marginSummary", out var marginEl)
            && ReadDecimal(marginEl, "accountValue") is > 0m and var accountDecimal ? accountDecimal : null;

        var list = new List<WalletPositionSnapshot>();
        foreach (var item in positionsEl.EnumerateArray())
        {
            if (!item.TryGetProperty("position", out var pos))
                continue;

            string coin = pos.TryGetProperty("coin", out var coinEl) ? coinEl.GetString() ?? "" : "";
            decimal signedSize = ReadDecimal(pos, "szi");
            if (string.IsNullOrWhiteSpace(coin) || signedSize == 0m)
                continue;

            string direction = signedSize > 0m ? "Long" : "Short";
            decimal entryPx = ReadDecimal(pos, "entryPx");
            decimal posValue = Math.Abs(ReadDecimal(pos, "positionValue"));
            decimal unrealizedPnl = ReadDecimal(pos, "unrealizedPnl");
            decimal leverage = pos.TryGetProperty("leverage", out var levEl) ? ReadDecimal(levEl, "value") : 0m;

            list.Add(new WalletPositionSnapshot(
                walletAddress,
                coin,
                direction,
                Math.Abs(signedSize),
                entryPx,
                posValue,
                unrealizedPnl,
                leverage,
                ReadDecimal(pos, "liquidationPx") is > 0m and var liquidation ? liquidation : null,
                accountValue));
        }

        return list;
    }

    public async Task<IReadOnlyList<WalletFillEvent>> GetRecentFillsAsync(
        string walletAddress,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(walletAddress))
            return [];

        string payload = JsonSerializer.Serialize(new { type = "userFills", user = walletAddress });
        using var response = await SendInfoPostAsync(payload, cancellationToken);
        response.EnsureSuccessStatusCode();

        string json = await response.Content.ReadAsStringAsync(cancellationToken);
        using var doc = JsonDocument.Parse(json);

        if (doc.RootElement.ValueKind != JsonValueKind.Array)
            return [];

        var fills = new List<WalletFillEvent>();
        foreach (var item in doc.RootElement.EnumerateArray())
        {
            string coin = item.TryGetProperty("coin", out var cEl) ? cEl.GetString() ?? "" : "";
            string dir = item.TryGetProperty("dir", out var dEl) ? dEl.GetString() ?? "" : "";
            string hash = item.TryGetProperty("hash", out var hEl) ? hEl.GetString() ?? "" : "";
            long timeMs = item.TryGetProperty("time", out var tEl) && tEl.TryGetInt64(out long t) ? t : 0L;

            if (string.IsNullOrWhiteSpace(coin) || timeMs <= 0L)
                continue;

            fills.Add(new WalletFillEvent(
                walletAddress,
                coin,
                dir,
                Math.Abs(ReadDecimal(item, "sz")),
                ReadDecimal(item, "px"),
                timeMs,
                ReadDecimal(item, "startPosition"),
                ReadDecimal(item, "closedPnl"),
                hash));
        }

        return fills;
    }

    public async Task<IReadOnlyList<WalletProtectiveOrder>> GetOpenProtectiveOrdersAsync(
        string walletAddress,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(walletAddress))
            return [];

        string payload = JsonSerializer.Serialize(new { type = "frontendOpenOrders", user = walletAddress });
        using var response = await SendInfoPostAsync(payload, cancellationToken);
        response.EnsureSuccessStatusCode();

        string json = await response.Content.ReadAsStringAsync(cancellationToken);
        using var doc = JsonDocument.Parse(json);

        if (doc.RootElement.ValueKind != JsonValueKind.Array)
            return [];

        DateTimeOffset now = _time.GetUtcNow();
        var orders = new List<WalletProtectiveOrder>();
        foreach (var item in doc.RootElement.EnumerateArray())
        {
            bool reduceOnly = item.TryGetProperty("reduceOnly", out var roEl) && roEl.ValueKind == JsonValueKind.True;
            bool isTrigger = item.TryGetProperty("isTrigger", out var trEl) && trEl.ValueKind == JsonValueKind.True;
            bool isTpsl = item.TryGetProperty("isPositionTpsl", out var tpEl) && tpEl.ValueKind == JsonValueKind.True;
            if (!reduceOnly && !isTpsl)
                continue;

            string rawCoin = item.TryGetProperty("coin", out var cEl) ? cEl.GetString() ?? "" : "";
            var mapped = HyperliquidSmartMoneySymbolMapper.Map(rawCoin);
            if (mapped is null)
                continue;

            long oid = item.TryGetProperty("oid", out var oidEl) && oidEl.TryGetInt64(out long id) ? id : 0L;
            if (oid <= 0L)
                continue;

            string orderType = item.TryGetProperty("orderType", out var otEl) ? otEl.GetString() ?? "" : "";
            string side = item.TryGetProperty("side", out var sEl) ? sEl.GetString() ?? "" : "";
            string triggerCond = item.TryGetProperty("triggerCondition", out var tcEl) ? tcEl.GetString() ?? "" : "";

            decimal triggerPx = ReadDecimal(item, "triggerPx");
            decimal limitPx = ReadDecimal(item, "limitPx");
            decimal rawPrice = isTrigger && triggerPx > 0m ? triggerPx : limitPx;
            if (rawPrice <= 0m)
                continue;

            decimal rawSize = Math.Abs(ReadDecimal(item, "sz"));
            string orderKind = ClassifyOrderKind(orderType);

            orders.Add(new WalletProtectiveOrder(
                walletAddress,
                oid,
                mapped.Symbol,
                mapped.Coin,
                orderKind,
                side,
                rawPrice / mapped.UnitScaleFactor,
                rawSize * mapped.UnitScaleFactor,
                isTrigger,
                triggerCond,
                now));
        }

        return orders;
    }

    private static string ClassifyOrderKind(string orderType)
    {
        if (orderType.Contains("Stop", StringComparison.OrdinalIgnoreCase))
            return "StopLoss";
        if (orderType.Contains("Take Profit", StringComparison.OrdinalIgnoreCase))
            return "TakeProfit";
        return "TakeProfitLimit";
    }

    private Task<HttpResponseMessage> SendInfoPostAsync(string payload, CancellationToken cancellationToken) =>
        SendWithRetryAsync(() => new HttpRequestMessage(HttpMethod.Post, $"{BaseUrl}/info")
        {
            Content = new StringContent(payload, Encoding.UTF8, "application/json")
        }, cancellationToken);

    private async Task<HttpResponseMessage> SendWithRetryAsync(
        Func<HttpRequestMessage> buildRequest,
        CancellationToken cancellationToken)
    {
        for (int attempt = 1; ; attempt++)
        {
            using var request = buildRequest();
            var response = await _http.SendAsync(request, cancellationToken);
            if (response.StatusCode != HttpStatusCode.TooManyRequests || attempt > MaxRetries)
                return response;

            response.Dispose();
            await Task.Delay(_retryDelay * attempt, cancellationToken);
        }
    }

    private static (decimal Pnl, decimal Roi, decimal Volume) ReadWindow(JsonElement row, string targetWindow)
    {
        if (!row.TryGetProperty("windowPerformances", out var windows) || windows.ValueKind != JsonValueKind.Array)
            return (0m, 0m, 0m);

        foreach (var entry in windows.EnumerateArray())
        {
            if (entry.ValueKind != JsonValueKind.Array || entry.GetArrayLength() < 2)
                continue;

            string? name = entry[0].GetString();
            if (!string.Equals(name, targetWindow, StringComparison.OrdinalIgnoreCase))
                continue;

            var metrics = entry[1];
            return (ReadDecimal(metrics, "pnl"), ReadDecimal(metrics, "roi"), ReadDecimal(metrics, "vlm"));
        }

        return (0m, 0m, 0m);
    }

    private static decimal ReadDecimal(JsonElement element, string property)
    {
        if (!element.TryGetProperty(property, out var val))
            return 0m;

        if (val.ValueKind == JsonValueKind.Number && val.TryGetDecimal(out decimal d))
            return d;

        if (val.ValueKind == JsonValueKind.String
            && decimal.TryParse(val.GetString(), NumberStyles.Float, CultureInfo.InvariantCulture, out decimal parsed))
            return parsed;

        return 0m;
    }
}
