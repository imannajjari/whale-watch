using System;
using System.IO;
using System.Threading.Tasks;
using WhaleWatch.Models.SmartMoney;
using WhaleWatch.Services.SmartMoney;
using Xunit;

namespace WhaleWatch.Tests.SmartMoney;

public sealed class SqliteSmartMoneyRepositoryTests : IDisposable
{
    private readonly string _dbPath = Path.Combine(Path.GetTempPath(), $"smart_money_repo_test_{Guid.NewGuid():N}.db");

    public void Dispose()
    {
        try
        {
            if (File.Exists(_dbPath))
                File.Delete(_dbPath);
        }
        catch
        {
            // Best effort cleanup
        }
    }

    [Fact]
    public async Task InitializeAsync_WithRawFilePath_NormalizesAndInitializesSuccessfully()
    {
        using var repo = new SqliteSmartMoneyRepository(_dbPath);
        await repo.InitializeAsync();

        Assert.True(File.Exists(_dbPath));
    }

    [Fact]
    public async Task InitializeAsync_WithFullConnectionString_InitializesSuccessfully()
    {
        using var repo = new SqliteSmartMoneyRepository($"Data Source={_dbPath}");
        await repo.InitializeAsync();

        Assert.True(File.Exists(_dbPath));
    }

    [Fact]
    public async Task InitializeAsync_WithWhitespacePaddedConnectionString_NormalizesWithoutError()
    {
        using var repo = new SqliteSmartMoneyRepository($"   Data Source={_dbPath}   ");
        await repo.InitializeAsync();

        Assert.True(File.Exists(_dbPath));
    }

    [Fact]
    public async Task InitializeAsync_WithDataSourceWithoutSpace_NormalizesWithoutError()
    {
        using var repo = new SqliteSmartMoneyRepository($"DataSource={_dbPath}");
        await repo.InitializeAsync();

        Assert.True(File.Exists(_dbPath));
    }

    [Fact]
    public async Task SaveWalletTrackRecordAsync_UpsertsOneRowPerWalletAndRoundTrips()
    {
        using var repo = new SqliteSmartMoneyRepository(_dbPath);
        var at = new DateTimeOffset(2026, 11, 15, 12, 0, 0, TimeSpan.Zero);

        await repo.SaveWalletTrackRecordAsync(new WalletTrackRecord("0xa", 10, 6, 0.55m, 2.5m, 400m, 200m, 30m, false, 0m, at));
        await repo.SaveWalletTrackRecordAsync(new WalletTrackRecord("0xa", 20, 15, 0.7m, 4m, 500m, 150m, 0m, true, 0.75m, at.AddHours(1)));
        await repo.SaveWalletTrackRecordAsync(new WalletTrackRecord("0xb", 3, 1, 0.5m, 1m, 50m, 50m, 0m, false, 0m, at));

        var records = await repo.GetWalletTrackRecordsAsync();

        Assert.Equal(2, records.Count);
        var a = Assert.Single(records, r => r.Address == "0xa");
        Assert.Equal(20, a.ClosedTrades);
        Assert.Equal(0.7m, a.SmoothedWinRate);
        Assert.True(a.IsQualified);
        Assert.Equal(0.75m, a.Score);
        Assert.Equal(at.AddHours(1), a.UpdatedAtUtc);
    }

    [Fact]
    public async Task RecordShadowOutcomeAsync_StoresTheScoreWeightedStance()
    {
        using var repo = new SqliteSmartMoneyRepository(_dbPath);
        var at = DateTimeOffset.UtcNow;

        await repo.RecordShadowOutcomeAsync(new ShadowOutcomeRecord(0, "BTCUSDT", "Short", "Bearish", at, null, "Mixed"));
        await repo.RecordShadowOutcomeAsync(new ShadowOutcomeRecord(0, "ETHUSDT", "Long", "Bullish", at.AddMinutes(1), null));

        var outcomes = await repo.GetShadowOutcomesAsync();

        Assert.Equal("Mixed", Assert.Single(outcomes, o => o.Symbol == "BTCUSDT").ScoreWeightedStance);
        Assert.Null(Assert.Single(outcomes, o => o.Symbol == "ETHUSDT").ScoreWeightedStance);
    }

    [Fact]
    public async Task InitializeAsync_AddsTheStanceColumnToAnExistingShadowTable()
    {
        await using (var connection = new Microsoft.Data.Sqlite.SqliteConnection($"Data Source={_dbPath}"))
        {
            await connection.OpenAsync();
            await using var cmd = connection.CreateCommand();
            cmd.CommandText = @"CREATE TABLE shadow_outcomes (
                id INTEGER PRIMARY KEY AUTOINCREMENT, symbol TEXT NOT NULL, engine_direction TEXT NOT NULL,
                smart_money_stance TEXT NOT NULL, decision_at_utc TEXT NOT NULL, outcome_net_r TEXT NULL);
                INSERT INTO shadow_outcomes (symbol, engine_direction, smart_money_stance, decision_at_utc)
                VALUES ('BTCUSDT', 'Long', 'Bullish', '2026-11-14T10:00:00.0000000+00:00');";
            await cmd.ExecuteNonQueryAsync();
        }

        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        using var repo = new SqliteSmartMoneyRepository(_dbPath);

        var outcomes = await repo.GetShadowOutcomesAsync();

        var old = Assert.Single(outcomes);
        Assert.Null(old.ScoreWeightedStance);
    }
}
