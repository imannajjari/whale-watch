using System;
using System.Collections.Generic;
using System.Linq;
using WhaleWatch.Models.SmartMoney;

namespace WhaleWatch.Services.SmartMoney;

public sealed class WalletQualityFilter
{
    public IReadOnlyList<TrackedWallet> SelectTopWallets(
        IReadOnlyList<TrackedWallet> candidates,
        SmartMoneySettings settings)
    {
        ArgumentNullException.ThrowIfNull(candidates);
        ArgumentNullException.ThrowIfNull(settings);

        int cap = Math.Max(0, settings.MaxTrackedWallets);
        if (cap == 0)
            return [];

        return candidates
            .Where(w => IsEligible(w, settings))
            .OrderByDescending(w => w.MonthRoi)
            .ThenByDescending(w => w.AllTimePnlUsd)
            .Take(cap)
            .ToList();
    }

    public bool HasMinimumClosedTrades(
        IReadOnlyList<WalletFillEvent> fills,
        SmartMoneySettings settings)
    {
        ArgumentNullException.ThrowIfNull(fills);
        ArgumentNullException.ThrowIfNull(settings);

        if (settings.MinClosedTrades <= 0)
            return true;

        return fills.Count(IsClosingFill) >= settings.MinClosedTrades;
    }

    // Flips ("Long > Short") and liquidations carry realised PnL but do not start with "Close".
    public static bool IsClosingFill(WalletFillEvent fill) =>
        fill.ClosedPnlUsd != 0m
        || fill.Direction.StartsWith("Close", StringComparison.OrdinalIgnoreCase);

    private static bool IsEligible(TrackedWallet wallet, SmartMoneySettings settings)
    {
        if (string.IsNullOrWhiteSpace(wallet.Address))
            return false;
        if (wallet.AccountValueUsd < settings.MinAccountValueUsd)
            return false;
        if (wallet.MonthPnlUsd < settings.MinMonthPnlUsd)
            return false;
        if (wallet.MonthRoi < settings.MinMonthRoi)
            return false;
        if (wallet.AllTimePnlUsd < settings.MinAllTimePnlUsd)
            return false;
        if (settings.MaxVolumeToPnlRatio > 0m && wallet.AllTimePnlUsd > 0m)
        {
            decimal ratio = wallet.AllTimeVolumeUsd / wallet.AllTimePnlUsd;
            if (ratio > settings.MaxVolumeToPnlRatio)
                return false;
        }

        return true;
    }
}
