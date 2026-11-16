using System;
using System.Linq;
using WhaleWatch.Models.SmartMoney;
using WhaleWatch.Services.SmartMoney;
using Xunit;

namespace WhaleWatch.Tests.SmartMoney;

public sealed class WhaleAlertFormatterTests
{
    private static readonly DateTimeOffset SeenAt = new(2026, 11, 14, 10, 8, 0, TimeSpan.Zero);

    private static SmartMoneyPositionEvent SolSnapshotEvent() => new(
        "snap:0x0001:SOL:Short:1", "0x0000000000000000000000000000000000000001", "SOLUSDT", "SOL", "Short", "Open",
        5806.24m, 95.7486m, 0m, SeenAt, "clearinghouseState");

    private static WalletPositionSnapshot SolPosition(decimal valueUsd) => new(
        "0x0000000000000000000000000000000000000001", "SOL", "Short", 5806.24m, 95.7486m, valueUsd, 115991.85m, 20m);

    [Fact]
    public void Format_SnapshotOpen_SaysAlreadyOpenAndShowsMarketPriceProfitAndTimes()
    {
        var formatter = new WhaleAlertFormatter("Asia/Tokyo");

        string msg = formatter.Format(SolSnapshotEvent(), SolPosition(439947.51416m), "0x000000...000001");

        Assert.Contains("already open, first seen", msg);
        Assert.DoesNotContain("OPENED", msg);
        Assert.Contains("<b>Entry price:</b> $95.75", msg);
        Assert.Contains("<b>Market price:</b> $75.77 (-20.9% vs entry)", msg);
        Assert.Contains("<b>Unrealized PnL:</b> $115,992", msg);
        Assert.Contains("<b>Leverage:</b> 20x", msg);
        Assert.Contains("($439,948 USD at market)", msg);
        Assert.Contains("<b>First seen:</b> 2026-11-14 10:08:00 UTC (19:08:00 Tokyo)", msg);
        Assert.Contains(WhaleAlertTexts.FirstSeenNote, msg);
        Assert.EndsWith(WhaleAlertTexts.FirstSeenNote, msg, StringComparison.Ordinal);
    }

    [Fact]
    public void Format_FillOpen_ShowsTradeTimeAndFillPrice()
    {
        var formatter = new WhaleAlertFormatter("Asia/Tokyo");
        var ev = new SmartMoneyPositionEvent("fill:1", "0xabc", "ETHUSDT", "ETH", "Short", "Open", 100m, 2000m, 0m, SeenAt, "userFills");

        string msg = formatter.Format(ev, null, "0xabc");

        Assert.Contains("WHALE SHORT OPENED", msg);
        Assert.Contains("<b>Trade time:</b> 2026-11-14 10:08:00 UTC (19:08:00 Tokyo)", msg);
        Assert.Contains("<b>Price:</b> $2,000.00", msg);
        Assert.DoesNotContain("Market price", msg);
        Assert.DoesNotContain("already open", msg);
    }

    [Theory]
    [InlineData(1500, "WHALE CLOSED (PROFIT)")]
    [InlineData(-1500, "WHALE CLOSED (LOSS)")]
    public void Format_Close_ShowsOutcomeAndPnl(int pnl, string header)
    {
        var formatter = new WhaleAlertFormatter("Asia/Tokyo");
        var ev = new SmartMoneyPositionEvent("close:1", "0xabc", "BTCUSDT", "BTC", "Long", "Close", 1m, 70000m, pnl, SeenAt, "userFills");

        string msg = formatter.Format(ev, null, "0xabc");

        Assert.Contains(header, msg);
        Assert.Contains("<b>PnL:</b>", msg);
        Assert.Contains("<b>Trade time:</b>", msg);
    }

    [Fact]
    public void ShouldAlert_SnapshotOpen_UsesCurrentValueNotEntryNotional()
    {
        var ev = SolSnapshotEvent();

        Assert.False(WhaleAlertFormatter.ShouldAlert(ev, SolPosition(439947m), 500000m));
        Assert.True(WhaleAlertFormatter.ShouldAlert(ev, SolPosition(600000m), 500000m));
    }

    [Fact]
    public void ShouldAlert_FillOpen_UsesFillNotionalAndLargeClosedPnlAlsoCounts()
    {
        var fill = new SmartMoneyPositionEvent("fill:1", "0xabc", "SOLUSDT", "SOL", "Short", "Open", 10000m, 80m, 0m, SeenAt, "userFills");
        var smallClose = new SmartMoneyPositionEvent("close:1", "0xabc", "SOLUSDT", "SOL", "Short", "Close", 1m, 80m, 600000m, SeenAt, "userFills");

        Assert.True(WhaleAlertFormatter.ShouldAlert(fill, null, 500000m));
        Assert.False(WhaleAlertFormatter.ShouldAlert(fill, null, 900000m));
        Assert.True(WhaleAlertFormatter.ShouldAlert(smallClose, null, 500000m));
    }

    [Fact]
    public void Format_SnapshotClose_SaysDetectedAndDoesNotClaimFirstSeenOrTradePrice()
    {
        var formatter = new WhaleAlertFormatter("Asia/Tokyo");
        var ev = new SmartMoneyPositionEvent("snapclose:1", "0xabc", "SOLUSDT", "SOL", "Short", "Close", 5806.24m, 95.7486m, 115000m, SeenAt, "clearinghouseState");

        string msg = formatter.Format(ev, null, "0xabc");

        Assert.Contains("WHALE CLOSED (detected between checks)", msg);
        Assert.Contains("<b>Detected:</b> 2026-11-14 10:08:00 UTC (19:08:00 Tokyo)", msg);
        Assert.Contains("<b>Entry price:</b> $95.75", msg);
        Assert.Contains("<b>Unrealized PnL at last check:</b> $115,000.00", msg);
        Assert.Contains(WhaleAlertTexts.SnapshotCloseNote, msg);
        Assert.DoesNotContain("First seen", msg);
        Assert.DoesNotContain("Trade time", msg);
        Assert.DoesNotContain("<b>Price:</b>", msg);
    }

    [Fact]
    public void Format_SnapshotOpenWithZeroPositionValue_DoesNotShowAZeroMarketPrice()
    {
        var formatter = new WhaleAlertFormatter("UTC");

        string msg = formatter.Format(SolSnapshotEvent(), SolPosition(0m), "0x000000...000001");

        Assert.DoesNotContain("Market price", msg);
        Assert.Contains("<b>Price:</b> $95.75", msg);
    }

    [Fact]
    public void Format_SubDollarPrices_KeepTheirSignificantDigits()
    {
        var formatter = new WhaleAlertFormatter("UTC");
        var ev = new SmartMoneyPositionEvent("snap:1", "0xabc", "PEPEUSDT", "kPEPE", "Long", "Open", 200000000m, 0.004m, 0m, SeenAt, "clearinghouseState");
        var current = new WalletPositionSnapshot("0xabc", "kPEPE", "Long", 200000m, 4m, 1000000m, 200000m, 5m);

        string msg = formatter.Format(ev, current, "0xabc");

        Assert.Contains("<b>Entry price:</b> $0.004", msg);
        Assert.Contains("<b>Market price:</b> $0.005 (+25.0% vs entry)", msg);
    }

    private static SmartMoneyPositionEvent BtcFill(string key, string direction, string action, decimal size, decimal price, int secondsAfter, decimal pnl = 0m) => new(
        key, "0x0000000000000000000000000000000000000002", "BTCUSDT", "BTC", direction, action, size, price, pnl, SeenAt.AddSeconds(secondsAfter), "userFills");

    private static WalletPositionSnapshot BtcShort() => new(
        "0x0000000000000000000000000000000000000002", "BTC", "Short", 86.60388m, 64236.3m, 5578675.53m, -15562.13m, 40m, 65051.31m, 125441m);

    private static WalletProtectiveOrder Stop(decimal price, string side = "B", string kind = "StopLoss", string coin = "BTC") => new(
        "0x0000000000000000000000000000000000000002", (long)price, "BTCUSDT", coin, kind, side, price, 10m, true, "Price above", SeenAt);

    private static IReadOnlyList<WalletProtectiveOrder> EightStops() =>
        [Stop(64750m), Stop(64800m), Stop(64900m), Stop(65000m), Stop(65050m), Stop(65100m), Stop(65200m), Stop(65300m)];

    [Fact]
    public void Group_MergesFillsOfTheSameWalletCoinDirectionAndAction_AndSkipsSnapshotEvents()
    {
        var events = new[]
        {
            BtcFill("a", "Short", "Open", 5m, 64000m, 5),
            BtcFill("b", "Short", "Open", 15m, 64400m, 1),
            BtcFill("c", "Long", "Close", 2m, 64200m, 3, -100m),
            SolSnapshotEvent()
        };

        var groups = WhaleAlertFormatter.Group(events);

        Assert.Equal(2, groups.Count);
        var open = Assert.Single(groups, g => g.Action == "Open");
        Assert.Equal(2, open.FillCount);
        Assert.Equal(20m, open.TotalSize);
        Assert.Equal(5m * 64000m + 15m * 64400m, open.TotalNotionalUsd);
        Assert.Equal(64300m, open.AveragePrice);
        Assert.Equal(64000m, open.MinPrice);
        Assert.Equal(64400m, open.MaxPrice);
        Assert.Equal(SeenAt.AddSeconds(1), open.FirstTimeUtc);
        Assert.Equal(SeenAt.AddSeconds(5), open.LastTimeUtc);
        Assert.Equal(-100m, groups.Single(g => g.Action == "Close").TotalClosedPnlUsd);
        Assert.Equal("Open", groups[0].Action);
    }

    [Fact]
    public void ShouldAlert_Group_UsesTheTotalSoManySmallFillsCanAlert()
    {
        var fills = Enumerable.Range(0, 12).Select(i => BtcFill($"f{i}", "Short", "Open", 0.8m, 64000m, i)).ToList();
        var group = Assert.Single(WhaleAlertFormatter.Group(fills));

        Assert.All(fills, f => Assert.False(WhaleAlertFormatter.ShouldAlert(f, null, 500000m)));
        Assert.True(WhaleAlertFormatter.ShouldAlert(group, null, 500000m, 50000m));
        Assert.False(WhaleAlertFormatter.ShouldAlert(group, null, 700000m, 50000m));
    }

    [Theory]
    [InlineData("Open", 600000, 60000, true)]
    [InlineData("Open", 600000, 49000, false)]
    [InlineData("Open", 400000, 60000, false)]
    [InlineData("Close", 600000, 60000, false)]
    public void ShouldAlert_Group_AlertsOnALargePositionOnlyWhenTheRunTradedEnough(string action, int positionValueUsd, int fillNotionalUsd, bool expected)
    {
        var fills = new[] { BtcFill("f1", "Short", action, fillNotionalUsd / 64000m, 64000m, 0) };
        var group = Assert.Single(WhaleAlertFormatter.Group(fills));
        var position = new WalletPositionSnapshot("0xw1", "BTC", "Short", 10m, 64000m, positionValueUsd, 0m, 5m);

        Assert.Equal(expected, WhaleAlertFormatter.ShouldAlert(group, position, 500000m, 50000m));
    }

    [Fact]
    public void FormatGroup_BtcShortFlip_ShowsFillsPositionLiquidationStopsAndWarnings()
    {
        var formatter = new WhaleAlertFormatter("Asia/Tokyo");
        var open = WhaleAlertFormatter.Group(
        [
            BtcFill("a", "Short", "Open", 7.807m, 64239m, 0),
            BtcFill("b", "Short", "Open", 5.815m, 64239m, 2),
            BtcFill("c", "Short", "Open", 0.34m, 64245m, 4),
            BtcFill("d", "Long", "Close", 95.206m, 64196m, -20, -42822m)
        ]);
        var context = new WhaleAlertContext(EightStops(), open);
        var group = open.Single(g => g.Action == "Open");

        string msg = formatter.FormatGroup(group, BtcShort(), "0x000000...000002", context);

        Assert.Contains("WHALE SHORT OPENED (3 fills)", msg);
        Assert.Contains("<b>This run:</b> 3 fills, 13.96", msg);
        Assert.Contains("<b>Avg price:</b> $64,239.", msg);
        Assert.Contains("<b>Last fill:</b>", msg);
        Assert.Contains("<b>Same run:</b> closed Long 95.21 ($6,111,", msg);
        Assert.Contains("PnL -$42,822", msg);
        Assert.Contains("<b>Position now:</b> 86.60 ($5,578,676 at market)", msg);
        Assert.Contains("<b>This run's fills:</b> 16% of that position", msg);
        Assert.Contains("<b>Avg entry:</b> $64,236.30", msg);
        Assert.Contains("<b>Market price:</b> $64,41", msg);
        Assert.Contains("<b>Leverage:</b> 40x", msg);
        Assert.Contains("<b>Liquidation:</b> $65,051.31 (1.0% above market)", msg);
        Assert.Contains("<b>Account value:</b> $125,441 (this position is 44.5x of it)", msg);
        Assert.Contains("<b>Stops:</b> 8 orders, 80.00 in total, nearest $64,750.00 (0.5% from market)\n", msg);
        Assert.Contains("\n\n<b>" + WhaleAlertTexts.AnalysisHeader + "</b>\n", msg);
        Assert.Contains(WhaleAlertTexts.ManyFillsNote(3), msg);
        Assert.Contains(WhaleAlertTexts.StopsBeyondLiquidation(3), msg);
        Assert.Contains(WhaleAlertTexts.LiquidationClose(1.0m), msg);
        Assert.Contains(WhaleAlertTexts.ExposureHigh(44.5m), msg);
        Assert.True(msg.IndexOf(WhaleAlertTexts.AnalysisHeader, StringComparison.Ordinal) > msg.IndexOf("<b>Wallet:</b>", StringComparison.Ordinal));
        Assert.EndsWith(WhaleAlertTexts.StopsBeyondLiquidation(3), msg, StringComparison.Ordinal);
    }

    [Fact]
    public void FormatGroup_SingleFill_KeepsThePlainSizeAndPriceLines()
    {
        var formatter = new WhaleAlertFormatter("UTC");
        var group = Assert.Single(WhaleAlertFormatter.Group([BtcFill("a", "Short", "Open", 10m, 64000m, 0)]));

        string msg = formatter.FormatGroup(group, null, "0xabc");

        Assert.Contains("WHALE SHORT OPENED", msg);
        Assert.DoesNotContain("fills)", msg);
        Assert.Contains("<b>Size:</b> 10.00 ($640,000 USD)", msg);
        Assert.Contains("<b>Price:</b> $64,000.00", msg);
        Assert.DoesNotContain("Last fill", msg);
        Assert.DoesNotContain("<b>Position now:</b>", msg);
    }

    [Fact]
    public void FormatGroup_CloseWithNoPositionLeft_SaysSoAndShowsTheOutcome()
    {
        var formatter = new WhaleAlertFormatter("UTC");
        var group = Assert.Single(WhaleAlertFormatter.Group(
        [
            BtcFill("a", "Long", "Close", 50m, 64200m, 0, -20000m),
            BtcFill("b", "Long", "Close", 45.2m, 64196m, 3, -22822m)
        ]));

        string msg = formatter.FormatGroup(group, null, "0xabc");

        Assert.Contains("WHALE CLOSED (LOSS) (2 fills)", msg);
        Assert.Contains("<b>PnL:</b> -$42,822.00", msg);
        Assert.Contains("<b>Position now:</b> no open Long position left", msg);
    }

    [Fact]
    public void FormatGroup_LongPosition_PutsLiquidationAndStopsBelowTheMarket()
    {
        var formatter = new WhaleAlertFormatter("UTC");
        var group = Assert.Single(WhaleAlertFormatter.Group([BtcFill("a", "Long", "Open", 10m, 64000m, 0)]));
        var position = new WalletPositionSnapshot("0xabc", "BTC", "Long", 10m, 64000m, 650000m, 10000m, 5m, 52000m, 500000m);
        var context = new WhaleAlertContext([Stop(63000m, "A"), Stop(60000m, "A"), Stop(66000m, "A")], []);

        string msg = formatter.FormatGroup(group, position, "0xabc", context);

        Assert.Contains("<b>Liquidation:</b> $52,000.00 (20.0% below market)", msg);
        Assert.Contains("<b>Stops:</b> 2 orders, 20.00 in total, nearest $63,000.00 (3.1% from market)", msg);
        Assert.DoesNotContain("\u26A0", msg);
    }

    [Fact]
    public void FormatGroup_WithoutAnyStopOrder_SaysNoneWereFound()
    {
        var formatter = new WhaleAlertFormatter("UTC");
        var group = Assert.Single(WhaleAlertFormatter.Group([BtcFill("a", "Short", "Open", 10m, 64000m, 0)]));
        var otherCoinAndKind = new WhaleAlertContext([Stop(65000m, coin: "ETH"), Stop(60000m, kind: "TakeProfit")], []);

        string msg = formatter.FormatGroup(group, BtcShort(), "0xabc", otherCoinAndKind);

        Assert.Contains("<b>Stops:</b> none found", msg);
        Assert.Contains(WhaleAlertTexts.NoStops, msg);
    }

    [Fact]
    public void FormatGroup_WhenTheMarketIsBeyondTheLiquidationPrice_FlagsPossiblyStaleData()
    {
        var formatter = new WhaleAlertFormatter("UTC");
        var group = Assert.Single(WhaleAlertFormatter.Group([BtcFill("a", "Short", "Open", 10m, 64000m, 0)]));
        var position = new WalletPositionSnapshot("0xabc", "BTC", "Short", 10m, 64000m, 700000m, -60000m, 10m, 65000m, null);

        string msg = formatter.FormatGroup(group, position, "0xabc");

        Assert.Contains(WhaleAlertTexts.StaleData, msg);
    }

    [Fact]
    public void FormatGroup_KiloCoin_ScalesTheLiquidationPriceLikeTheOtherPrices()
    {
        var formatter = new WhaleAlertFormatter("UTC");
        var fill = new SmartMoneyPositionEvent("a", "0xabc", "PEPEUSDT", "kPEPE", "Long", "Open", 200000000m, 0.004m, 0m, SeenAt, "userFills");
        var group = Assert.Single(WhaleAlertFormatter.Group([fill]));
        var position = new WalletPositionSnapshot("0xabc", "kPEPE", "Long", 200000m, 4m, 1000000m, 200000m, 5m, 3.2m, null);

        string msg = formatter.FormatGroup(group, position, "0xabc");

        Assert.Contains("<b>Market price:</b> $0.005 (+25.0% vs entry)", msg);
        Assert.Contains("<b>Liquidation:</b> $0.0032 (36.0% below market)", msg);
    }

    [Fact]
    public void Format_FirstSeenPosition_AlsoShowsLiquidationAndStopsWhenContextIsGiven()
    {
        var formatter = new WhaleAlertFormatter("UTC");
        var position = new WalletPositionSnapshot("0x0000000000000000000000000000000000000001", "SOL", "Short", 5806.24m, 95.7486m, 439947.51m, 115991.85m, 20m, 120m, 62000m);

        string msg = formatter.Format(SolSnapshotEvent(), position, "0x000000...000001", new WhaleAlertContext([], []));

        Assert.Contains("already open, first seen", msg);
        Assert.Contains("<b>Liquidation:</b> $120.00 (58.4% above market)", msg);
        Assert.Contains("<b>Stops:</b> none found", msg);
        Assert.Contains("this position is 7.1x of it", msg);
        Assert.True(msg.IndexOf(WhaleAlertTexts.FirstSeenNote, StringComparison.Ordinal) < msg.IndexOf(WhaleAlertTexts.NoStops, StringComparison.Ordinal));
    }

    [Fact]
    public void AnalysisTexts_AreWrittenInPersianAndKeepTheirNumbersInLatinDigits()
    {
        string[] texts =
        [
            WhaleAlertTexts.AnalysisHeader, WhaleAlertTexts.FirstSeenNote, WhaleAlertTexts.SnapshotCloseNote, WhaleAlertTexts.SingleFillNote,
            WhaleAlertTexts.StaleData, WhaleAlertTexts.NoStops, WhaleAlertTexts.ManyFillsNote(12), WhaleAlertTexts.LiquidationClose(1.0m),
            WhaleAlertTexts.ExposureHigh(44.5m), WhaleAlertTexts.StopsBeyondLiquidation(3)
        ];

        Assert.All(texts, text => Assert.Contains(text, c => c >= (char)0x0600 && c <= (char)0x06FF));
        Assert.All(texts, text => Assert.DoesNotContain(text, c => c is >= (char)0x06F0 and <= (char)0x06F9 or >= (char)0x0660 and <= (char)0x0669));
        Assert.Contains("12", WhaleAlertTexts.ManyFillsNote(12));
        Assert.Contains("1.0%", WhaleAlertTexts.LiquidationClose(1.0m));
        Assert.Contains("44.5", WhaleAlertTexts.ExposureHigh(44.5m));
    }

    [Fact]
    public void FillNotes_PointAtTheExactLabelThatTheMessageShows()
    {
        var group = Assert.Single(WhaleAlertFormatter.Group([BtcFill("a", "Short", "Open", 10m, 64000m, 0)]));
        string msg = new WhaleAlertFormatter("UTC").FormatGroup(group, BtcShort(), "0xabc");

        Assert.Contains("<b>" + WhaleAlertTexts.PositionNowLabel + ":</b>", msg);
        Assert.Contains(WhaleAlertTexts.PositionNowLabel, WhaleAlertTexts.SingleFillNote);
        Assert.Contains(WhaleAlertTexts.PositionNowLabel, WhaleAlertTexts.ManyFillsNote(4));
    }

    [Fact]
    public void Format_PlainFillWithNothingToSay_HasNoAnalysisHeader()
    {
        var ev = new SmartMoneyPositionEvent("fill:1", "0xabc", "ETHUSDT", "ETH", "Short", "Open", 100m, 2000m, 0m, SeenAt, "userFills");

        string msg = new WhaleAlertFormatter("UTC").Format(ev, null, "0xabc");

        Assert.DoesNotContain(WhaleAlertTexts.AnalysisHeader, msg);
        Assert.EndsWith("</code>", msg, StringComparison.Ordinal);
    }

    [Fact]
    public void WhaleAnalysis_PutsNotesBeforeWarningsWhateverTheAddingOrder()
    {
        var analysis = new WhaleAnalysis();
        Assert.True(analysis.IsEmpty);

        analysis.AddWarning("w1");
        analysis.AddNote("n1");
        analysis.AddWarning("w2");
        analysis.AddNote("n2");

        Assert.False(analysis.IsEmpty);
        Assert.Equal(["n1", "n2", "w1", "w2"], analysis.Lines);
    }

    [Fact]
    public void FormatTime_ShowsLocalDateWhenItDiffersFromUtc()    {
        var formatter = new WhaleAlertFormatter("Asia/Tokyo");

        string text = formatter.FormatTime(new DateTimeOffset(2026, 11, 14, 21, 0, 0, TimeSpan.Zero));

        Assert.Equal("2026-11-14 21:00:00 UTC (2026-11-15 06:00:00 Tokyo)", text);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("UTC")]
    [InlineData("Not/AZone")]
    public void FormatTime_FallsBackToUtcOnly(string? zone)
    {
        var formatter = new WhaleAlertFormatter(zone);

        Assert.Equal("2026-11-14 10:08:00 UTC", formatter.FormatTime(SeenAt));
    }
}
