using System;

namespace WhaleWatch.Models.SmartMoney;

public sealed record TrackedWallet(
    string Address,
    decimal AccountValueUsd,
    decimal MonthPnlUsd,
    decimal MonthRoi,
    decimal AllTimePnlUsd,
    decimal AllTimeVolumeUsd,
    DateTimeOffset UpdatedAtUtc);
