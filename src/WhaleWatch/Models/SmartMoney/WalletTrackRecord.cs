using System;

namespace WhaleWatch.Models.SmartMoney;

public sealed record WalletTrackRecord(
    string Address,
    int ClosedTrades,
    int Wins,
    decimal SmoothedWinRate,
    decimal ProfitFactor,
    decimal AverageWinUsd,
    decimal AverageLossUsd,
    decimal UnrealizedLossUsd,
    bool IsQualified,
    decimal Score,
    DateTimeOffset UpdatedAtUtc);
