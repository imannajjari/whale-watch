using System;
using System.Net;
using System.Net.Http;
using System.Threading.Tasks;
using WhaleWatch.Models.SmartMoney;
using WhaleWatch.Services.SmartMoney;
using WhaleWatch.Tests.TestSupport;
using Xunit;

namespace WhaleWatch.Tests.SmartMoney;

public sealed class HyperliquidSmartMoneySourceTests
{
    [Fact]
    public async Task GetLeaderboardAsync_ParsesRecordedFixtureAndRetriesOn429()
    {
        int calls = 0;
        const string leaderboardJson = """
        {
          "leaderboardRows": [
            {
              "ethAddress": "0xabc123",
              "accountValue": "125000.5",
              "windowPerformances": [
                ["day", {"pnl": "500", "roi": "0.01", "vlm": "10000"}],
                ["month", {"pnl": "15000.25", "roi": "0.14", "vlm": "400000"}],
                ["allTime", {"pnl": "60000", "roi": "0.80", "vlm": "3000000"}]
              ]
            }
          ]
        }
        """;

        var handler = new RecordingHttpHandler(_ =>
        {
            calls++;
            return calls == 1
                ? (HttpStatusCode.TooManyRequests, "{}")
                : (HttpStatusCode.OK, leaderboardJson);
        });

        using var http = new HttpClient(handler);
        var source = new HyperliquidSmartMoneySource(http, new SmartMoneySettings(), TimeSpan.FromMilliseconds(1));

        var wallets = await source.GetLeaderboardAsync();

        Assert.Equal(2, calls);
        var w = Assert.Single(wallets);
        Assert.Equal("0xabc123", w.Address);
        Assert.Equal(125000.5m, w.AccountValueUsd);
        Assert.Equal(15000.25m, w.MonthPnlUsd);
        Assert.Equal(0.14m, w.MonthRoi);
        Assert.Equal(60000m, w.AllTimePnlUsd);
        Assert.Equal(3000000m, w.AllTimeVolumeUsd);
    }

    [Fact]
    public async Task GetOpenPositionsAndFills_ParseRecordedFixtures()
    {
        const string stateJson = """
        {
          "assetPositions": [
            {
              "position": {
                "coin": "kPEPE",
                "szi": "-5000",
                "entryPx": "0.012",
                "positionValue": "60",
                "unrealizedPnl": "4.5",
                "leverage": {"value": 5}
              }
            }
          ]
        }
        """;

        const string fillsJson = """
        [
          {
            "coin": "BTC",
            "px": "80500.0",
            "sz": "0.25",
            "time": 1790000000000,
            "startPosition": "0",
            "dir": "Open Long",
            "closedPnl": "0",
            "hash": "0xfill1"
          }
        ]
        """;

        int call = 0;
        var handler = new RecordingHttpHandler(_ =>
        {
            call++;
            return call == 1
                ? (HttpStatusCode.OK, stateJson)
                : (HttpStatusCode.OK, fillsJson);
        });

        using var http = new HttpClient(handler);
        var source = new HyperliquidSmartMoneySource(http, new SmartMoneySettings());

        var positions = await source.GetOpenPositionsAsync("0xabc123");
        var fills = await source.GetRecentFillsAsync("0xabc123");

        var p = Assert.Single(positions);
        Assert.Equal("kPEPE", p.Coin);
        Assert.Equal("Short", p.Direction);
        Assert.Equal(5000m, p.Size);
        Assert.Equal(5m, p.Leverage);

        var f = Assert.Single(fills);
        Assert.Equal("BTC", f.Coin);
        Assert.Equal("Open Long", f.Direction);
        Assert.Equal(0.25m, f.Size);
        Assert.Equal("0xfill1", f.Hash);
    }

    [Fact]
    public async Task GetOpenProtectiveOrdersAsync_ParsesStopLossAndReduceOnlyLimits()
    {
        const string ordersJson = """
        [
          {
            "coin": "ETH",
            "side": "B",
            "limitPx": "2366.4",
            "sz": "10.0",
            "oid": 1001,
            "isTrigger": true,
            "triggerPx": "2151.3",
            "triggerCondition": "Price above 2151.3",
            "isPositionTpsl": true,
            "reduceOnly": true,
            "orderType": "Stop Market"
          },
          {
            "coin": "kPEPE",
            "side": "A",
            "limitPx": "15.0",
            "sz": "200.0",
            "oid": 1002,
            "isTrigger": false,
            "triggerPx": "0.0",
            "triggerCondition": "N/A",
            "isPositionTpsl": false,
            "reduceOnly": true,
            "orderType": "Limit"
          },
          {
            "coin": "BTC",
            "side": "B",
            "limitPx": "67000.0",
            "sz": "1.0",
            "oid": 1003,
            "isTrigger": false,
            "triggerPx": "0.0",
            "triggerCondition": "N/A",
            "isPositionTpsl": false,
            "reduceOnly": false,
            "orderType": "Limit"
          }
        ]
        """;

        var handler = new RecordingHttpHandler(_ => (HttpStatusCode.OK, ordersJson));
        using var http = new HttpClient(handler);
        var source = new HyperliquidSmartMoneySource(http, new SmartMoneySettings());

        var orders = await source.GetOpenProtectiveOrdersAsync("0xabc");

        Assert.Equal(2, orders.Count);
        Assert.Contains(orders, o => o.Symbol == "ETHUSDT" && o.OrderKind == "StopLoss" && o.Price == 2151.3m && o.IsTrigger);
        Assert.Contains(orders, o => o.Symbol == "PEPEUSDT" && o.OrderKind == "TakeProfitLimit" && o.Price == 0.015m && o.Size == 200000m);
    }
}
