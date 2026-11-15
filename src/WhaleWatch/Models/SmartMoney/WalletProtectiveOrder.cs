using System;

namespace WhaleWatch.Models.SmartMoney;

public sealed record WalletProtectiveOrder(
    string WalletAddress,
    long OrderId,
    string Symbol,
    string Coin,
    string OrderKind,
    string Side,
    decimal Price,
    decimal Size,
    bool IsTrigger,
    string TriggerCondition,
    DateTimeOffset UpdatedAtUtc);
