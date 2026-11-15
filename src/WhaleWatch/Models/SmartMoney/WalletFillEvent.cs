namespace WhaleWatch.Models.SmartMoney;

public sealed record WalletFillEvent(
    string WalletAddress,
    string Coin,
    string Direction,
    decimal Size,
    decimal Price,
    long TimestampMs,
    decimal StartPosition,
    decimal ClosedPnlUsd,
    string Hash);
