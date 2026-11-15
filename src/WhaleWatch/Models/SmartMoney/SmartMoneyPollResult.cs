using System.Collections.Generic;

namespace WhaleWatch.Models.SmartMoney;

public sealed record SmartMoneyPollResult(
    bool Success,
    int TrackedWalletsCount,
    int PolledWalletsCount,
    int NewEventsCount,
    IReadOnlyList<string> Warnings,
    string? ErrorMessage = null);
