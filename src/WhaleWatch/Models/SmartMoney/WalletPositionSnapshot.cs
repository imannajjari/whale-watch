namespace WhaleWatch.Models.SmartMoney;

public sealed record WalletPositionSnapshot(
    string WalletAddress,
    string Coin,
    string Direction,
    decimal Size,
    decimal EntryPrice,
    decimal PositionValueUsd,
    decimal UnrealizedPnlUsd,
    decimal Leverage,
    decimal? LiquidationPrice = null,
    decimal? AccountValueUsd = null);
