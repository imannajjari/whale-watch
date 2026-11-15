using WhaleWatch.Services.SmartMoney;
using Xunit;

namespace WhaleWatch.Tests.SmartMoney;

public sealed class HyperliquidSmartMoneySymbolMapperTests
{
    [Theory]
    [InlineData("BTC", "BTCUSDT", 1)]
    [InlineData("eth", "ETHUSDT", 1)]
    [InlineData("ETHUSDT", "ETHUSDT", 1)]
    [InlineData("kPEPE", "PEPEUSDT", 1000)]
    [InlineData("kSHIB", "SHIBUSDT", 1000)]
    [InlineData("kBONK", "BONKUSDT", 1000)]
    public void Map_HandlesStandardAndKiloPrefixedSymbols(string coin, string expectedSymbol, decimal expectedScale)
    {
        var result = HyperliquidSmartMoneySymbolMapper.Map(coin);

        Assert.NotNull(result);
        Assert.Equal(expectedSymbol, result!.Symbol);
        Assert.Equal(expectedScale, result.UnitScaleFactor);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void Map_ReturnsNullForBlankCoin(string? coin)
    {
        Assert.Null(HyperliquidSmartMoneySymbolMapper.Map(coin));
    }
}
