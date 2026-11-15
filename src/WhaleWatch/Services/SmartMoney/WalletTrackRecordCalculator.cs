using System;
using System.Collections.Generic;
using System.Linq;
using WhaleWatch.Models.SmartMoney;

namespace WhaleWatch.Services.SmartMoney;

public sealed class WalletTrackRecordCalculator(SmartMoneySettings settings)
{
    public const decimal MaxProfitFactor = 10m;

    private readonly SmartMoneySettings _settings = settings ?? throw new ArgumentNullException(nameof(settings));

    public WalletTrackRecord Calculate(
        string walletAddress,
        IReadOnlyList<WalletFillEvent> fills,
        IReadOnlyList<WalletPositionSnapshot> openPositions,
        DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(fills);
        ArgumentNullException.ThrowIfNull(openPositions);

        var tradePnls = ClosedTradePnls(fills);
        int wins = tradePnls.Count(p => p > 0m);
        int losses = tradePnls.Count(p => p < 0m);
        decimal grossWin = tradePnls.Where(p => p > 0m).Sum();
        decimal grossLoss = -tradePnls.Where(p => p < 0m).Sum();
        decimal unrealizedLoss = -openPositions.Where(p => p.UnrealizedPnlUsd < 0m).Sum(p => p.UnrealizedPnlUsd);

        decimal averageWin = wins > 0 ? grossWin / wins : 0m;
        decimal averageLoss = losses > 0 ? grossLoss / losses : 0m;
        decimal smoothedWinRate = SmoothedWinRate(wins, tradePnls.Count);
        decimal profitFactor = ProfitFactor(grossWin, grossLoss + unrealizedLoss);

        bool qualified = tradePnls.Count >= _settings.TrackRecordMinTrades
            && averageWin >= _settings.TrackRecordMinAverageWinUsd;
        decimal score = qualified ? 0.5m * smoothedWinRate + 0.5m * profitFactor / (profitFactor + 1m) : 0m;

        return new WalletTrackRecord(
            walletAddress,
            tradePnls.Count,
            wins,
            smoothedWinRate,
            profitFactor,
            averageWin,
            averageLoss,
            unrealizedLoss,
            qualified,
            score,
            now);
    }

    private decimal SmoothedWinRate(int wins, int trades)
    {
        decimal prior = Math.Max(0, _settings.TrackRecordPriorTrades);
        decimal denominator = trades + prior;
        return denominator == 0m ? 0m : (wins + 0.5m * prior) / denominator;
    }

    private static decimal ProfitFactor(decimal grossWin, decimal grossLoss)
    {
        if (grossLoss <= 0m)
            return grossWin > 0m ? MaxProfitFactor : 0m;

        return Math.Min(grossWin / grossLoss, MaxProfitFactor);
    }

    private List<decimal> ClosedTradePnls(IReadOnlyList<WalletFillEvent> fills)
    {
        long gapMs = Math.Max(1, _settings.TrackRecordClusterMinutes) * 60_000L;
        var pnls = new List<decimal>();

        var closes = fills.Where(f => f.Direction.StartsWith("Close", StringComparison.OrdinalIgnoreCase));
        foreach (var group in closes.GroupBy(f => (f.Coin, f.Direction)))
        {
            long lastMs = long.MinValue;
            decimal current = 0m;
            bool open = false;
            foreach (var fill in group.OrderBy(f => f.TimestampMs))
            {
                if (open && fill.TimestampMs - lastMs > gapMs)
                {
                    pnls.Add(current);
                    current = 0m;
                }

                current += fill.ClosedPnlUsd;
                lastMs = fill.TimestampMs;
                open = true;
            }

            if (open)
                pnls.Add(current);
        }

        return pnls;
    }
}
