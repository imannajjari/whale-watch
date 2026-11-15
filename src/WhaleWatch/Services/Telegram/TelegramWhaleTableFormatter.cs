using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using WhaleWatch.Abstractions.SmartMoney;
using WhaleWatch.Models.SmartMoney;

namespace WhaleWatch.Services.Telegram;

public sealed class TelegramWhaleTableFormatter
{
    public static async Task<string> FormatPositionsTableAsync(
        ISmartMoneyRepository repository,
        CancellationToken cancellationToken = default)
    {
        var positions = await repository.GetAllSnapshotPositionsAsync(cancellationToken);
        if (positions.Count == 0)
        {
            return "📊 <b>گزارش معاملات نهنگ‌ها</b>\n\nدر حال حاضر هیچ پوزیشن بازی در پایگاه داده ثبت نشده است.";
        }

        var sb = new StringBuilder();
        sb.AppendLine("📊 <b>خلاصه پوزیشن‌های باز نهنگ‌ها</b>");
        sb.AppendLine("<code>----------------------------------------</code>");

        var summary = positions
            .GroupBy(p => new { p.Coin, p.Direction })
            .Select(g => new
            {
                g.Key.Coin,
                g.Key.Direction,
                Count = g.Count(),
                TotalVal = g.Sum(x => x.PositionValueUsd),
                TotalPnl = g.Sum(x => x.UnrealizedPnlUsd)
            })
            .OrderByDescending(x => x.TotalVal)
            .ToList();

        decimal totalValue = positions.Sum(p => p.PositionValueUsd);
        decimal totalPnl = positions.Sum(p => p.UnrealizedPnlUsd);

        foreach (var item in summary)
        {
            string dirEmoji = item.Direction == "Long" ? "🟢" : "🔴";
            string pnlSign = item.TotalPnl >= 0 ? "+" : "";
            sb.AppendLine($"{dirEmoji} <b>{item.Coin} {item.Direction}</b> ({item.Count}x):");
            sb.AppendLine($"   حجم: ${item.TotalVal:N0} | سود: {pnlSign}${item.TotalPnl:N0}");
        }

        sb.AppendLine("<code>----------------------------------------</code>");
        string overallSign = totalPnl >= 0 ? "+" : "";
        sb.AppendLine($"<b>مجموع ارزش باز:</b> ${totalValue:N0}");
        sb.AppendLine($"<b>سود شناور کل:</b> {overallSign}${totalPnl:N0}");
        sb.AppendLine($"<i>به‌روزرسانی: {DateTimeOffset.UtcNow:yyyy-MM-dd HH:mm} UTC</i>");

        return sb.ToString();
    }
}
