using System;
using System.Collections.Generic;
using WhaleWatch.Models.SmartMoney;
using WhaleWatch.Services.SmartMoney;
using Xunit;

namespace WhaleWatch.Tests.SmartMoney;

public class WalletTrackRecordCalculatorTests
{
    private static readonly DateTimeOffset Now = new(2026, 11, 15, 12, 0, 0, TimeSpan.Zero);

    private static SmartMoneySettings Settings(int minTrades = 3) => new()
    {
        TrackRecordMinTrades = minTrades,
        TrackRecordPriorTrades = 10,
        TrackRecordMinAverageWinUsd = 100m,
        TrackRecordClusterMinutes = 30
    };

    private static WalletFillEvent Close(string coin, string direction, decimal pnl, int minutesAgo) =>
        new("0xw", coin, direction, 1m, 100m, Now.AddMinutes(-minutesAgo).ToUnixTimeMilliseconds(), 1m, pnl, $"h{coin}{minutesAgo}");

    private static WalletTrackRecord Calculate(SmartMoneySettings settings, IReadOnlyList<WalletFillEvent> fills, IReadOnlyList<WalletPositionSnapshot>? positions = null) =>
        new WalletTrackRecordCalculator(settings).Calculate("0xw", fills, positions ?? [], Now);

    [Fact]
    public void Calculate_MergesCloseFillsWithinTheGapIntoOneTrade()
    {
        var fills = new List<WalletFillEvent>
        {
            Close("ETH", "Close Long", 300m, 200),
            Close("ETH", "Close Long", 200m, 190),
            Close("ETH", "Close Long", -100m, 60),
            Close("BTC", "Close Short", 400m, 30)
        };

        var record = Calculate(Settings(), fills);

        Assert.Equal(3, record.ClosedTrades);
        Assert.Equal(2, record.Wins);
        Assert.Equal(450m, record.AverageWinUsd);
        Assert.Equal(100m, record.AverageLossUsd);
        Assert.Equal(9m, record.ProfitFactor);
    }

    [Fact]
    public void Calculate_SmoothsTheWinRateTowardFiftyPercent()
    {
        var fills = new List<WalletFillEvent>
        {
            Close("ETH", "Close Long", 200m, 300),
            Close("BTC", "Close Long", 200m, 200),
            Close("SOL", "Close Long", 200m, 100)
        };

        var record = Calculate(Settings(), fills);

        Assert.Equal(8m / 13m, record.SmoothedWinRate);
        Assert.True(record.IsQualified);
        Assert.Equal(0.5m * (8m / 13m) + 0.5m * 10m / 11m, record.Score);
    }

    [Fact]
    public void Calculate_CapsTheProfitFactorWhenThereAreNoLosses()
    {
        var fills = new List<WalletFillEvent> { Close("ETH", "Close Long", 500m, 100) };

        var record = Calculate(Settings(1), fills);

        Assert.Equal(WalletTrackRecordCalculator.MaxProfitFactor, record.ProfitFactor);
    }

    [Fact]
    public void Calculate_CountsOpenLossesAgainstTheProfitFactor()
    {
        var fills = new List<WalletFillEvent>
        {
            Close("ETH", "Close Long", 1000m, 300),
            Close("BTC", "Close Long", -100m, 200)
        };
        var positions = new List<WalletPositionSnapshot>
        {
            new("0xw", "SOL", "Long", 10m, 100m, 1000m, -400m, 5m),
            new("0xw", "AVAX", "Long", 10m, 10m, 100m, 150m, 5m)
        };

        var record = Calculate(Settings(2), fills, positions);

        Assert.Equal(400m, record.UnrealizedLossUsd);
        Assert.Equal(2m, record.ProfitFactor);
    }

    [Fact]
    public void Calculate_IsNotQualified_WhenTooFewTrades()
    {
        var fills = new List<WalletFillEvent> { Close("ETH", "Close Long", 500m, 100), Close("BTC", "Close Long", 500m, 200) };

        var record = Calculate(Settings(3), fills);

        Assert.False(record.IsQualified);
        Assert.Equal(0m, record.Score);
    }

    [Fact]
    public void Calculate_IsNotQualified_WhenAverageWinIsScalperSized()
    {
        var fills = new List<WalletFillEvent>
        {
            Close("ETH", "Close Long", 40m, 300),
            Close("BTC", "Close Long", 40m, 200),
            Close("SOL", "Close Long", 40m, 100)
        };

        var record = Calculate(Settings(), fills);

        Assert.False(record.IsQualified);
        Assert.Equal(0m, record.Score);
    }

    [Theory]
    [InlineData("Long > Short")]
    [InlineData("Short > Long")]
    [InlineData("Liquidated Cross Long")]
    [InlineData("Liquidated Isolated Short")]
    public void Calculate_CountsFlipAndLiquidationFillsAsClosedTrades(string direction)
    {
        var fills = new List<WalletFillEvent>
        {
            Close("ETH", "Close Long", 1000m, 300),
            Close("BTC", direction, -500m, 200)
        };

        var record = Calculate(Settings(2), fills);

        Assert.Equal(2, record.ClosedTrades);
        Assert.Equal(1, record.Wins);
        Assert.Equal(500m, record.AverageLossUsd);
        Assert.Equal(2m, record.ProfitFactor);
    }

    [Fact]
    public void Calculate_AgreesWithTheQualityFilterOnWhichFillsCloseATrade()
    {
        var fills = new List<WalletFillEvent>
        {
            Close("ETH", "Close Long", 0m, 500),
            Close("BTC", "Long > Short", -50m, 400),
            Close("SOL", "Liquidated Cross Long", -900m, 300),
            Close("AVAX", "Open Long", 0m, 200)
        };

        var record = Calculate(Settings(1), fills);

        Assert.Equal(3, fills.FindAll(WalletQualityFilter.IsClosingFill).Count);
        Assert.Equal(3, record.ClosedTrades);
    }

    [Fact]
    public void Calculate_IgnoresOpenFills()
    {
        var fills = new List<WalletFillEvent>
        {
            new("0xw", "ETH", "Open Long", 1m, 100m, Now.ToUnixTimeMilliseconds(), 0m, 0m, "h1")
        };

        var record = Calculate(Settings(), fills);

        Assert.Equal(0, record.ClosedTrades);
        Assert.False(record.IsQualified);
    }
}
