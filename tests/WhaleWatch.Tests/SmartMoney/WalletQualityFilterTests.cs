using System;
using System.Collections.Generic;
using WhaleWatch.Models.SmartMoney;
using WhaleWatch.Services.SmartMoney;
using Xunit;

namespace WhaleWatch.Tests.SmartMoney;

public sealed class WalletQualityFilterTests
{
    [Fact]
    public void SelectTopWallets_ExcludesLowBalanceNegativeMonthAndMarketMakers()
    {
        var now = DateTimeOffset.UtcNow;
        var settings = new SmartMoneySettings
        {
            MaxTrackedWallets = 2,
            MinAccountValueUsd = 50000m,
            MinMonthPnlUsd = 5000m,
            MinMonthRoi = 0.05m,
            MinAllTimePnlUsd = 25000m,
            MaxVolumeToPnlRatio = 500m
        };

        var candidates = new List<TrackedWallet>
        {
            new("0xsmall", 10000m, 6000m, 0.20m, 30000m, 100000m, now),
            new("0xnegmonth", 100000m, -1000m, -0.01m, 80000m, 500000m, now),
            new("0xmm", 200000m, 10000m, 0.15m, 30000m, 50000000m, now),
            new("0xgood1", 120000m, 15000m, 0.12m, 50000m, 2000000m, now),
            new("0xgood2", 150000m, 30000m, 0.25m, 90000m, 4000000m, now),
            new("0xgood3", 200000m, 12000m, 0.08m, 200000m, 3000000m, now)
        };

        var filter = new WalletQualityFilter();
        var selected = filter.SelectTopWallets(candidates, settings);

        Assert.Equal(2, selected.Count);
        Assert.Equal("0xgood2", selected[0].Address);
        Assert.Equal("0xgood1", selected[1].Address);
    }

    [Fact]
    public void HasMinimumClosedTrades_CountsClosedFills()
    {
        var settings = new SmartMoneySettings { MinClosedTrades = 2 };
        var fills = new List<WalletFillEvent>
        {
            new("0x1", "BTC", "Open Long", 1m, 80000m, 1000, 0m, 0m, "h1"),
            new("0x1", "BTC", "Close Long", 1m, 81000m, 2000, 1m, 1000m, "h2"),
            new("0x1", "ETH", "Close Short", 2m, 3000m, 3000, -2m, 0m, "h3")
        };

        var filter = new WalletQualityFilter();
        Assert.True(filter.HasMinimumClosedTrades(fills, settings));
    }

    [Theory]
    [InlineData(499999, false)]
    [InlineData(500000, true)]
    public void HoldsLargePosition_IsTrueFromTheBypassThreshold(int positionValueUsd, bool expected)
    {
        var settings = new SmartMoneySettings { LargePositionBypassUsd = 500000m };
        var positions = new List<WalletPositionSnapshot>
        {
            new("0x1", "BTC", "Short", 1m, 80000m, positionValueUsd, 0m, 5m)
        };

        var filter = new WalletQualityFilter();
        Assert.Equal(expected, filter.HoldsLargePosition(positions, settings));
    }

    [Fact]
    public void HoldsLargePosition_IsFalseWhenTheBypassIsOff()
    {
        var settings = new SmartMoneySettings { LargePositionBypassUsd = 0m };
        var positions = new List<WalletPositionSnapshot>
        {
            new("0x1", "BTC", "Short", 1m, 80000m, 9000000m, 0m, 5m)
        };

        var filter = new WalletQualityFilter();
        Assert.False(filter.HoldsLargePosition(positions, settings));
    }
}
