using System;
using System.IO;
using System.Threading.Tasks;
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
}
