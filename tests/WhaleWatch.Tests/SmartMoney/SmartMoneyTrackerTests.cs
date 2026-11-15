using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using WhaleWatch.Abstractions.SmartMoney;
using WhaleWatch.Abstractions.Telegram;
using WhaleWatch.Models.SmartMoney;
using WhaleWatch.Models.Telegram;
using WhaleWatch.Services.SmartMoney;
using Xunit;

namespace WhaleWatch.Tests.SmartMoney;

public sealed class SmartMoneyTrackerTests : IDisposable
{
    private readonly string _dbPath = Path.Combine(Path.GetTempPath(), $"smart_money_test_{Guid.NewGuid():N}.db");

    [Fact]
    public async Task PollOnceAsync_WhenDisabled_DoesNotCallSourceOrTouchDb()
    {
        var source = new FakeSmartMoneySource();
        using var repo = new SqliteSmartMoneyRepository($"Data Source={_dbPath}");
        var tracker = new SmartMoneyTracker(source, repo, new WalletQualityFilter(), new SmartMoneySettings { Enabled = false });

        var result = await tracker.PollOnceAsync();

        Assert.False(result.Success);
        Assert.Equal(0, source.LeaderboardCalls);
        Assert.False(File.Exists(_dbPath));
    }

    [Fact]
    public async Task PollOnceAsync_DetectsOpenFillAndSnapshotFallbackAndDeduplicates()
    {
        var now = new DateTimeOffset(2026, 11, 12, 12, 0, 0, TimeSpan.Zero);
        var time = new FixedTimeProvider(now);
        var settings = new SmartMoneySettings
        {
            Enabled = true,
            MinClosedTrades = 1,
            MinAccountValueUsd = 10000m,
            MinMonthPnlUsd = 1000m,
            MinMonthRoi = 0.01m,
            MinAllTimePnlUsd = 1000m
        };

        var source = new FakeSmartMoneySource
        {
            Leaderboard = [new TrackedWallet("0xw1", 100000m, 10000m, 0.2m, 50000m, 500000m, now)],
            FillsByWallet =
            {
                ["0xw1"] =
                [
                    new WalletFillEvent("0xw1", "ETH", "Close Long", 1m, 3000m, now.AddMinutes(-30).ToUnixTimeMilliseconds(), 1m, 200m, "h0"),
                    new WalletFillEvent("0xw1", "kPEPE", "Open Long", 100m, 10m, now.AddMinutes(-5).ToUnixTimeMilliseconds(), 0m, 0m, "h1")
                ]
            },
            PositionsByWallet =
            {
                ["0xw1"] =
                [
                    new WalletPositionSnapshot("0xw1", "kPEPE", "Long", 100m, 10m, 1000m, 0m, 3m),
                    new WalletPositionSnapshot("0xw1", "BTC", "Short", 0.5m, 80000m, 40000m, 0m, 2m)
                ]
            },
            ProtectiveByWallet =
            {
                ["0xw1"] =
                [
                    new WalletProtectiveOrder("0xw1", 99L, "BTCUSDT", "BTC", "StopLoss", "B", 82000m, 0.5m, true, "Price above 82000", now)
                ]
            }
        };

        using var repo = new SqliteSmartMoneyRepository($"Data Source={_dbPath}");
        var tracker = new SmartMoneyTracker(source, repo, new WalletQualityFilter(), settings, time);

        var first = await tracker.PollOnceAsync();
        Assert.True(first.Success);
        Assert.Equal(1, first.TrackedWalletsCount);
        Assert.Equal(3, first.NewEventsCount);

        var events = await repo.GetRecentEventsAsync(10);
        Assert.Equal(3, events.Count);
        Assert.Contains(events, e => e.Symbol == "ETHUSDT" && e.Action == "Close" && e.ClosedPnlUsd == 200m);
        Assert.Contains(events, e => e.Symbol == "PEPEUSDT" && e.Action == "Open" && e.Direction == "Long" && e.Size == 100000m && e.Price == 0.01m);
        Assert.Contains(events, e => e.Symbol == "BTCUSDT" && e.Action == "Open" && e.Direction == "Short" && e.SourceKind == "clearinghouseState");

        var protective = await repo.GetProtectiveOrdersAsync("BTCUSDT");
        var sl = Assert.Single(protective);
        Assert.Equal("StopLoss", sl.OrderKind);
        Assert.Equal(82000m, sl.Price);

        // Second poll with identical fills & positions must deduplicate and skip leaderboard refresh.
        var second = await tracker.PollOnceAsync();
        Assert.True(second.Success);
        Assert.Equal(0, second.NewEventsCount);
        Assert.Equal(1, source.LeaderboardCalls);

        // Removing BTC from open positions without a close fill must emit a snapshot Close event.
        source.PositionsByWallet["0xw1"] = [new WalletPositionSnapshot("0xw1", "kPEPE", "Long", 100m, 10m, 1000m, 0m, 3m)];
        var third = await tracker.PollOnceAsync();
        Assert.Equal(1, third.NewEventsCount);
        var afterClose = await repo.GetRecentEventsAsync(10);
        Assert.Contains(afterClose, e => e.Symbol == "BTCUSDT" && e.Action == "Close" && e.SourceKind == "clearinghouseState");
    }

    [Fact]
    public async Task PollOnceAsync_LogsWarningAndContinuesWhenOneWalletFails()
    {
        var now = DateTimeOffset.UtcNow;
        var settings = new SmartMoneySettings
        {
            Enabled = true,
            MinClosedTrades = 0,
            MinAccountValueUsd = 10000m,
            MinMonthPnlUsd = 1000m,
            MinMonthRoi = 0.01m,
            MinAllTimePnlUsd = 1000m
        };

        var source = new FakeSmartMoneySource
        {
            Leaderboard =
            [
                new TrackedWallet("0xbad", 100000m, 10000m, 0.3m, 50000m, 500000m, now),
                new TrackedWallet("0xgood", 100000m, 10000m, 0.2m, 50000m, 500000m, now)
            ],
            FailingWallet = "0xbad",
            FillsByWallet = { ["0xgood"] = [new WalletFillEvent("0xgood", "BTC", "Open Long", 0.1m, 80000m, now.ToUnixTimeMilliseconds(), 0m, 0m, "h2")] },
            PositionsByWallet = { ["0xgood"] = [] }
        };

        using var repo = new SqliteSmartMoneyRepository($"Data Source={_dbPath}");
        var tracker = new SmartMoneyTracker(source, repo, new WalletQualityFilter(), settings);

        var result = await tracker.PollOnceAsync();

        Assert.True(result.Success);
        Assert.Equal(1, result.PolledWalletsCount);
        Assert.Equal(1, result.NewEventsCount);
        Assert.Single(result.Warnings);
    }

    [Theory]
    [InlineData(439947, 0)]
    [InlineData(600000, 1)]
    public async Task PollOnceAsync_SnapshotDiscoveredPosition_AlertsOnCurrentValueAndSaysAlreadyOpen(int currentValueUsd, int expectedMessages)
    {
        var now = new DateTimeOffset(2026, 11, 14, 10, 8, 0, TimeSpan.Zero);
        var source = new FakeSmartMoneySource
        {
            Leaderboard = [new TrackedWallet("0x0000000000000000000000000000000000000001", 100000m, 10000m, 0.2m, 50000m, 500000m, now)],
            PositionsByWallet =
            {
                ["0x0000000000000000000000000000000000000001"] =
                [
                    new WalletPositionSnapshot("0x0000000000000000000000000000000000000001", "SOL", "Short", 5806.24m, 95.7486m, currentValueUsd, 115991m, 20m)
                ]
            }
        };
        var notifier = new RecordingNotifier();

        using var repo = new SqliteSmartMoneyRepository($"Data Source={_dbPath}");
        var tracker = new SmartMoneyTracker(source, repo, new WalletQualityFilter(), WhaleSettings(), new FixedTimeProvider(now), notifier, WhaleTelegramSettings());

        var result = await tracker.PollOnceAsync();

        Assert.Equal(1, result.NewEventsCount);
        Assert.Equal(expectedMessages, notifier.Messages.Count);
        if (expectedMessages == 1)
        {
            Assert.Contains("already open, first seen", notifier.Messages[0]);
            Assert.Contains("<b>Market price:</b> $103.34", notifier.Messages[0]);
            Assert.Contains("<b>First seen:</b> 2026-11-14 10:08:00 UTC (19:08:00 Tokyo)", notifier.Messages[0]);
        }
    }

    [Fact]
    public async Task PollOnceAsync_FillOpen_AlertShowsTheFillTime()
    {
        var now = new DateTimeOffset(2026, 11, 14, 10, 8, 0, TimeSpan.Zero);
        var fillTime = now.AddMinutes(-5);
        var source = new FakeSmartMoneySource
        {
            Leaderboard = [new TrackedWallet("0xw1", 100000m, 10000m, 0.2m, 50000m, 500000m, now)],
            FillsByWallet =
            {
                ["0xw1"] = [new WalletFillEvent("0xw1", "SOL", "Open Short", 10000m, 80m, fillTime.ToUnixTimeMilliseconds(), 0m, 0m, "h1")]
            },
            PositionsByWallet =
            {
                ["0xw1"] = [new WalletPositionSnapshot("0xw1", "SOL", "Short", 10000m, 80m, 790000m, 5000m, 5m)]
            }
        };
        var notifier = new RecordingNotifier();

        using var repo = new SqliteSmartMoneyRepository($"Data Source={_dbPath}");
        var tracker = new SmartMoneyTracker(source, repo, new WalletQualityFilter(), WhaleSettings(), new FixedTimeProvider(now), notifier, WhaleTelegramSettings());

        await tracker.PollOnceAsync();

        var message = Assert.Single(notifier.Messages);
        Assert.Contains("WHALE SHORT OPENED", message);
        Assert.Contains("<b>Trade time:</b> 2026-11-14 10:03:00 UTC (19:03:00 Tokyo)", message);
    }

    [Fact]
    public async Task PollOnceAsync_KiloCoinSnapshot_ScalesMarketPriceByTheUnitFactor()
    {
        var now = new DateTimeOffset(2026, 11, 14, 10, 8, 0, TimeSpan.Zero);
        var source = new FakeSmartMoneySource
        {
            Leaderboard = [new TrackedWallet("0xk1", 100000m, 10000m, 0.2m, 50000m, 500000m, now)],
            PositionsByWallet =
            {
                ["0xk1"] = [new WalletPositionSnapshot("0xk1", "kPEPE", "Long", 200000m, 4m, 1000000m, 200000m, 5m)]
            }
        };
        var notifier = new RecordingNotifier();

        using var repo = new SqliteSmartMoneyRepository($"Data Source={_dbPath}");
        var tracker = new SmartMoneyTracker(source, repo, new WalletQualityFilter(), WhaleSettings(), new FixedTimeProvider(now), notifier, WhaleTelegramSettings());

        await tracker.PollOnceAsync();

        var message = Assert.Single(notifier.Messages);
        Assert.Contains("#PEPEUSDT", message);
        Assert.Contains("<b>Entry price:</b> $0.004", message);
        Assert.Contains("<b>Market price:</b> $0.005 (+25.0% vs entry)", message);
    }

    [Fact]
    public async Task PollOnceAsync_PositionDisappearsBetweenChecks_AlertsAsDetectedNotFirstSeen()
    {
        var first = new DateTimeOffset(2026, 11, 14, 10, 8, 0, TimeSpan.Zero);
        var second = first.AddMinutes(30);
        var source = new FakeSmartMoneySource
        {
            Leaderboard = [new TrackedWallet("0xw1", 100000m, 10000m, 0.2m, 50000m, 500000m, first)],
            PositionsByWallet =
            {
                ["0xw1"] = [new WalletPositionSnapshot("0xw1", "SOL", "Short", 10000m, 80m, 790000m, 5000m, 5m)]
            }
        };
        var notifier = new RecordingNotifier();

        using var repo = new SqliteSmartMoneyRepository($"Data Source={_dbPath}");
        await new SmartMoneyTracker(source, repo, new WalletQualityFilter(), WhaleSettings(), new FixedTimeProvider(first), notifier, WhaleTelegramSettings()).PollOnceAsync();
        notifier.Messages.Clear();
        source.PositionsByWallet["0xw1"] = [];

        await new SmartMoneyTracker(source, repo, new WalletQualityFilter(), WhaleSettings(), new FixedTimeProvider(second), notifier, WhaleTelegramSettings()).PollOnceAsync();

        var message = Assert.Single(notifier.Messages);
        Assert.Contains("WHALE CLOSED (detected between checks)", message);
        Assert.Contains("<b>Detected:</b> 2026-11-14 10:38:00 UTC (19:38:00 Tokyo)", message);
        Assert.DoesNotContain("First seen", message);
    }

    private static WalletFillEvent Fill(string hash, string direction, decimal size, decimal price, DateTimeOffset at, decimal pnl = 0m) =>
        new("0xw1", "BTC", direction, size, price, at.ToUnixTimeMilliseconds(), 0m, pnl, hash);

    private static FakeSmartMoneySource BtcSource(DateTimeOffset now, List<WalletFillEvent> fills) => new()
    {
        Leaderboard = [new TrackedWallet("0xw1", 100000m, 10000m, 0.2m, 50000m, 500000m, now)],
        FillsByWallet = { ["0xw1"] = fills },
        PositionsByWallet =
        {
            ["0xw1"] = [new WalletPositionSnapshot("0xw1", "BTC", "Short", 86.6m, 64236.3m, 5578000m, -15000m, 40m, 65051.31m, 125441m)]
        },
        ProtectiveByWallet =
        {
            ["0xw1"] = [new WalletProtectiveOrder("0xw1", 1, "BTCUSDT", "BTC", "StopLoss", "B", 64750m, 10m, true, "Price above", now)]
        }
    };

    [Fact]
    public async Task PollOnceAsync_ManySmallFillsInOneRun_SendOneAlertWithTheTotalAndThePositionContext()
    {
        var now = new DateTimeOffset(2026, 11, 15, 6, 20, 0, TimeSpan.Zero);
        var fills = new List<WalletFillEvent>();
        for (int i = 0; i < 12; i++)
            fills.Add(Fill($"h{i}", "Open Short", 0.8m, 64000m, now.AddMinutes(-9).AddSeconds(i)));
        var notifier = new RecordingNotifier();

        using var repo = new SqliteSmartMoneyRepository($"Data Source={_dbPath}");
        var tracker = new SmartMoneyTracker(BtcSource(now, fills), repo, new WalletQualityFilter(), WhaleSettings(), new FixedTimeProvider(now), notifier, WhaleTelegramSettings());

        var result = await tracker.PollOnceAsync();

        Assert.Equal(12, result.NewEventsCount);
        var message = Assert.Single(notifier.Messages);
        Assert.Contains("WHALE SHORT OPENED (12 fills)", message);
        Assert.Contains("<b>This run:</b> 12 fills, 9.60 ($614,400 USD)", message);
        Assert.Contains("<b>Position now:</b> 86.60", message);
        Assert.Contains("<b>Liquidation:</b> $65,051.31", message);
        Assert.Contains("<b>Stops:</b> 1 orders, 10.00 in total, nearest $64,750.00", message);
    }

    [Fact]
    public async Task PollOnceAsync_FillsWhoseTotalStaysUnderTheThreshold_SendNothing()
    {
        var now = new DateTimeOffset(2026, 11, 15, 6, 20, 0, TimeSpan.Zero);
        var fills = new List<WalletFillEvent>();
        for (int i = 0; i < 5; i++)
            fills.Add(Fill($"h{i}", "Open Short", 0.8m, 64000m, now.AddMinutes(-9).AddSeconds(i)));
        var notifier = new RecordingNotifier();

        using var repo = new SqliteSmartMoneyRepository($"Data Source={_dbPath}");
        var tracker = new SmartMoneyTracker(BtcSource(now, fills), repo, new WalletQualityFilter(), WhaleSettings(), new FixedTimeProvider(now), notifier, WhaleTelegramSettings());

        var result = await tracker.PollOnceAsync();

        Assert.Equal(5, result.NewEventsCount);
        Assert.Empty(notifier.Messages);
    }

    [Fact]
    public async Task PollOnceAsync_ClosingALongAndOpeningAShortInOneRun_SendsTwoAlertsAndLinksThem()
    {
        var now = new DateTimeOffset(2026, 11, 15, 6, 20, 0, TimeSpan.Zero);
        var fills = new List<WalletFillEvent>();
        for (int i = 0; i < 4; i++)
            fills.Add(Fill($"c{i}", "Close Long", 2m, 64200m, now.AddMinutes(-9).AddSeconds(i), -1000m));
        for (int i = 0; i < 4; i++)
            fills.Add(Fill($"o{i}", "Open Short", 3m, 64239m, now.AddMinutes(-9).AddSeconds(10 + i)));
        var notifier = new RecordingNotifier();

        using var repo = new SqliteSmartMoneyRepository($"Data Source={_dbPath}");
        var tracker = new SmartMoneyTracker(BtcSource(now, fills), repo, new WalletQualityFilter(), WhaleSettings(), new FixedTimeProvider(now), notifier, WhaleTelegramSettings());

        await tracker.PollOnceAsync();

        Assert.Equal(2, notifier.Messages.Count);
        var open = Assert.Single(notifier.Messages, m => m.Contains("OPENED"));
        Assert.Contains("<b>Same run:</b> closed Long 8.00", open);
        var close = Assert.Single(notifier.Messages, m => m.Contains("WHALE CLOSED"));
        Assert.Contains("<b>Same run:</b> opened Short 12.00", close);
        Assert.Contains("(4 fills)", close);
    }

    private static SmartMoneySettings WhaleSettings() => new()    {
        Enabled = true,
        MinClosedTrades = 0,
        MinAccountValueUsd = 10000m,
        MinMonthPnlUsd = 1000m,
        MinMonthRoi = 0.01m,
        MinAllTimePnlUsd = 1000m
    };

    private static TelegramSettings WhaleTelegramSettings() => new()
    {
        Enabled = true,
        NotifyOnWhaleActivity = true,
        MinWhalePositionAlertUsd = 500000m,
        DisplayTimeZone = "Asia/Tokyo"
    };

    private sealed class RecordingNotifier : ITelegramNotifier
    {
        public List<string> Messages { get; } = [];

        public Task<bool> SendMessageAsync(string message, CancellationToken cancellationToken = default)
        {
            Messages.Add(message);
            return Task.FromResult(true);
        }

        public Task<bool> SendMessageWithKeyboardAsync(string message, object replyMarkup, CancellationToken cancellationToken = default)
        {
            Messages.Add(message);
            return Task.FromResult(true);
        }
    }

    [Theory]
    [InlineData(true, 1)]
    [InlineData(false, 0)]
    public async Task PollOnceAsync_SavesTheWalletTrackRecordOnlyInShadowMode(bool shadowMode, int expectedRecords)
    {
        var now = new DateTimeOffset(2026, 11, 12, 12, 0, 0, TimeSpan.Zero);
        var settings = new SmartMoneySettings
        {
            Enabled = true,
            ShadowModeEnabled = shadowMode,
            MinClosedTrades = 1,
            MinAccountValueUsd = 10000m,
            MinMonthPnlUsd = 1000m,
            MinMonthRoi = 0.01m,
            MinAllTimePnlUsd = 1000m
        };
        var source = new FakeSmartMoneySource
        {
            Leaderboard = [new TrackedWallet("0xw1", 100000m, 10000m, 0.2m, 50000m, 500000m, now)],
            FillsByWallet =
            {
                ["0xw1"] = [new WalletFillEvent("0xw1", "ETH", "Close Long", 1m, 3000m, now.AddMinutes(-30).ToUnixTimeMilliseconds(), 1m, 200m, "h0")]
            },
            PositionsByWallet = { ["0xw1"] = [new WalletPositionSnapshot("0xw1", "BTC", "Short", 0.5m, 80000m, 40000m, -300m, 2m)] }
        };

        using var repo = new SqliteSmartMoneyRepository($"Data Source={_dbPath}");
        var tracker = new SmartMoneyTracker(source, repo, new WalletQualityFilter(), settings, new FixedTimeProvider(now));

        await tracker.PollOnceAsync();

        var records = await repo.GetWalletTrackRecordsAsync();
        Assert.Equal(expectedRecords, records.Count);
        if (expectedRecords == 1)
        {
            Assert.Equal(1, records[0].ClosedTrades);
            Assert.Equal(300m, records[0].UnrealizedLossUsd);
            Assert.False(records[0].IsQualified);
        }
    }

    [Fact]
    public async Task PollOnceAsync_ReplacesAQualifiedTrackRecordWhenTheWalletDropsBelowTheMinimum()
    {
        var now = new DateTimeOffset(2026, 11, 12, 12, 0, 0, TimeSpan.Zero);
        var settings = new SmartMoneySettings
        {
            Enabled = true,
            MinClosedTrades = 2,
            TrackRecordMinTrades = 2,
            MinAccountValueUsd = 10000m,
            MinMonthPnlUsd = 1000m,
            MinMonthRoi = 0.01m,
            MinAllTimePnlUsd = 1000m
        };
        var source = new FakeSmartMoneySource
        {
            Leaderboard = [new TrackedWallet("0xw1", 100000m, 10000m, 0.2m, 50000m, 500000m, now)],
            FillsByWallet =
            {
                ["0xw1"] =
                [
                    new WalletFillEvent("0xw1", "ETH", "Close Long", 1m, 3000m, now.AddMinutes(-90).ToUnixTimeMilliseconds(), 1m, 400m, "a"),
                    new WalletFillEvent("0xw1", "BTC", "Close Long", 1m, 80000m, now.AddMinutes(-60).ToUnixTimeMilliseconds(), 1m, 400m, "b")
                ]
            },
            PositionsByWallet = { ["0xw1"] = [] }
        };

        using var repo = new SqliteSmartMoneyRepository($"Data Source={_dbPath}");
        var tracker = new SmartMoneyTracker(source, repo, new WalletQualityFilter(), settings, new FixedTimeProvider(now));

        await tracker.PollOnceAsync();
        Assert.True((await repo.GetWalletTrackRecordsAsync()).Single().IsQualified);

        source.FillsByWallet["0xw1"] = [];
        await tracker.PollOnceAsync();

        var record = Assert.Single(await repo.GetWalletTrackRecordsAsync());
        Assert.False(record.IsQualified);
        Assert.Equal(0m, record.Score);
        Assert.Equal(0, record.ClosedTrades);
    }

    public void Dispose()
    {
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        if (File.Exists(_dbPath))
            File.Delete(_dbPath);
    }

    private sealed class FakeSmartMoneySource : ISmartMoneySource
    {
        public int LeaderboardCalls { get; private set; }
        public string? FailingWallet { get; init; }
        public IReadOnlyList<TrackedWallet> Leaderboard { get; init; } = [];
        public Dictionary<string, IReadOnlyList<WalletPositionSnapshot>> PositionsByWallet { get; } = new();
        public Dictionary<string, IReadOnlyList<WalletFillEvent>> FillsByWallet { get; } = new();
        public Dictionary<string, IReadOnlyList<WalletProtectiveOrder>> ProtectiveByWallet { get; } = new();

        public Task<IReadOnlyList<TrackedWallet>> GetLeaderboardAsync(CancellationToken cancellationToken = default)
        {
            LeaderboardCalls++;
            return Task.FromResult(Leaderboard);
        }

        public Task<IReadOnlyList<WalletPositionSnapshot>> GetOpenPositionsAsync(string walletAddress, CancellationToken cancellationToken = default)
        {
            if (walletAddress == FailingWallet) throw new HttpRequestException("Simulated 500");
            return Task.FromResult(PositionsByWallet.GetValueOrDefault(walletAddress, []));
        }

        public Task<IReadOnlyList<WalletFillEvent>> GetRecentFillsAsync(string walletAddress, CancellationToken cancellationToken = default)
        {
            if (walletAddress == FailingWallet) throw new HttpRequestException("Simulated 500");
            return Task.FromResult(FillsByWallet.GetValueOrDefault(walletAddress, []));
        }

        public Task<IReadOnlyList<WalletProtectiveOrder>> GetOpenProtectiveOrdersAsync(string walletAddress, CancellationToken cancellationToken = default)
        {
            if (walletAddress == FailingWallet) throw new HttpRequestException("Simulated 500");
            return Task.FromResult(ProtectiveByWallet.GetValueOrDefault(walletAddress, []));
        }
    }

    private sealed class FixedTimeProvider(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }
}
