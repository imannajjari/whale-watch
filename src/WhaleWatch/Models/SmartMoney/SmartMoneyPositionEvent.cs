using System;

namespace WhaleWatch.Models.SmartMoney;

public sealed record SmartMoneyPositionEvent(
    string EventKey,
    string WalletAddress,
    string Symbol,
    string Coin,
    string Direction,
    string Action,
    decimal Size,
    decimal Price,
    decimal ClosedPnlUsd,
    DateTimeOffset OpenedAtUtc,
    string SourceKind);
