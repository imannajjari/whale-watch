using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using WhaleWatch.Models.SmartMoney;

namespace WhaleWatch.Services.SmartMoney;

public sealed class WhaleAlertFormatter
{
    private const string SnapshotSource = "clearinghouseState";
    private const decimal LiquidationWarnPercent = 5m;
    private const decimal ExposureWarnMultiple = 10m;

    private readonly TimeZoneInfo? _zone;
    private readonly string _zoneLabel;

    public WhaleAlertFormatter(string? displayTimeZoneId)
    {
        _zoneLabel = "UTC";
        if (string.IsNullOrWhiteSpace(displayTimeZoneId) || displayTimeZoneId.Equals("UTC", StringComparison.OrdinalIgnoreCase))
            return;

        try
        {
            _zone = TimeZoneInfo.FindSystemTimeZoneById(displayTimeZoneId.Trim());
            string id = displayTimeZoneId.Trim();
            _zoneLabel = id[(id.LastIndexOf('/') + 1)..].Replace('_', ' ');
        }
        catch (Exception ex) when (ex is TimeZoneNotFoundException or InvalidTimeZoneException)
        {
            _zone = null;
        }
    }

    public static bool IsSnapshotEvent(SmartMoneyPositionEvent ev) =>
        string.Equals(ev.SourceKind, SnapshotSource, StringComparison.Ordinal);

    public static decimal AlertValueUsd(SmartMoneyPositionEvent ev, WalletPositionSnapshot? current) =>
        IsSnapshotEvent(ev) && ev.Action == "Open" && current is not null
            ? current.PositionValueUsd
            : ev.Size * ev.Price;

    public static bool ShouldAlert(SmartMoneyPositionEvent ev, WalletPositionSnapshot? current, decimal thresholdUsd) =>
        AlertValueUsd(ev, current) >= thresholdUsd || Math.Abs(ev.ClosedPnlUsd) >= thresholdUsd;

    public static IReadOnlyList<WhaleEventGroup> Group(IEnumerable<SmartMoneyPositionEvent> events) =>
        events
            .Where(e => !IsSnapshotEvent(e))
            .GroupBy(e => (e.Symbol, e.Coin, e.Direction, e.Action))
            .Select(g =>
            {
                var fills = g.ToList();
                decimal size = fills.Sum(e => e.Size);
                decimal notional = fills.Sum(e => e.Size * e.Price);
                return new WhaleEventGroup(
                    g.Key.Symbol, g.Key.Coin, g.Key.Direction, g.Key.Action,
                    fills.Count, size, notional, size > 0m ? notional / size : 0m,
                    fills.Min(e => e.Price), fills.Max(e => e.Price),
                    fills.Min(e => e.OpenedAtUtc), fills.Max(e => e.OpenedAtUtc),
                    fills.Sum(e => e.ClosedPnlUsd));
            })
            .OrderBy(g => g.FirstTimeUtc)
            .ToList();

    public static bool ShouldAlert(WhaleEventGroup group, decimal thresholdUsd) =>
        group.TotalNotionalUsd >= thresholdUsd || Math.Abs(group.TotalClosedPnlUsd) >= thresholdUsd;

    public string FormatGroup(WhaleEventGroup group, WalletPositionSnapshot? current, string safeAddress, WhaleAlertContext? context = null)
    {
        var inv = CultureInfo.InvariantCulture;
        bool isOpen = group.Action == "Open";
        bool many = group.FillCount > 1;
        string fills = many ? $" ({group.FillCount} fills)" : string.Empty;

        string header = isOpen
            ? $"{(group.Direction == "Long" ? "\U0001F7E2" : "\U0001F534")} <b>WHALE {group.Direction.ToUpperInvariant()} OPENED{fills}</b>"
            : group.TotalClosedPnlUsd >= 0
                ? $"\u2705 <b>WHALE CLOSED (PROFIT){fills}</b>"
                : $"\u274C <b>WHALE CLOSED (LOSS){fills}</b>";

        var sb = new StringBuilder();
        sb.Append(header).Append("\n\n");
        sb.Append(inv, $"<b>Asset:</b> #{group.Symbol}\n");
        sb.Append(inv, $"<b>Action:</b> {group.Action} ({group.Direction})\n");
        if (many)
        {
            sb.Append(inv, $"<b>This run:</b> {group.FillCount} fills, {group.TotalSize:N2} (${group.TotalNotionalUsd:N0} USD)\n");
            sb.Append(inv, $"<b>Avg price:</b> ${FormatPrice(group.AveragePrice)} (range ${FormatPrice(group.MinPrice)} to ${FormatPrice(group.MaxPrice)})\n");
        }
        else
        {
            sb.Append(inv, $"<b>Size:</b> {group.TotalSize:N2} (${group.TotalNotionalUsd:N0} USD)\n");
            sb.Append(inv, $"<b>Price:</b> ${FormatPrice(group.AveragePrice)}\n");
        }

        if (group.TotalClosedPnlUsd != 0m)
            sb.Append(inv, $"<b>PnL:</b> {Usd(group.TotalClosedPnlUsd, "N2")}\n");

        sb.Append(inv, $"<b>Trade time:</b> {FormatTime(group.FirstTimeUtc)}\n");
        if (group.LastTimeUtc > group.FirstTimeUtc)
            sb.Append(inv, $"<b>Last fill:</b> {FormatTime(group.LastTimeUtc)}\n");

        var analysis = new WhaleAnalysis();
        analysis.AddNote(many ? WhaleAlertTexts.ManyFillsNote(group.FillCount) : WhaleAlertTexts.SingleFillNote);
        AppendFlip(sb, group, context);
        AppendGroupPosition(sb, group, current, context, analysis);

        sb.Append(inv, $"<b>Wallet:</b> <code>{safeAddress}</code>");
        AppendAnalysis(sb, analysis);
        return sb.ToString();
    }

    private static void AppendFlip(StringBuilder sb, WhaleEventGroup group, WhaleAlertContext? context)
    {
        if (context is null)
            return;

        var inv = CultureInfo.InvariantCulture;
        var opposite = context.RunGroups.FirstOrDefault(g =>
            g.Coin == group.Coin && g.Direction != group.Direction && g.Action != group.Action);
        if (opposite is null)
            return;

        string verb = opposite.Action == "Close" ? "closed" : "opened";
        string pnl = opposite.TotalClosedPnlUsd != 0m ? string.Create(inv, $", PnL {Usd(opposite.TotalClosedPnlUsd, "N0")}") : string.Empty;
        sb.Append(inv, $"<b>Same run:</b> {verb} {opposite.Direction} {opposite.TotalSize:N2} (${opposite.TotalNotionalUsd:N0}{pnl})\n");
    }

    private void AppendGroupPosition(StringBuilder sb, WhaleEventGroup group, WalletPositionSnapshot? current, WhaleAlertContext? context, WhaleAnalysis analysis)
    {
        var inv = CultureInfo.InvariantCulture;
        if (current is null || current.Size <= 0m)
        {
            if (group.Action == "Close")
                sb.Append(inv, $"<b>{WhaleAlertTexts.PositionNowLabel}:</b> no open {group.Direction} position left\n");
            return;
        }

        decimal scale = Scale(current.Coin);
        decimal size = current.Size * scale;
        decimal market = MarketPrice(current, scale);
        decimal entry = current.EntryPrice / scale;

        sb.Append(inv, $"<b>{WhaleAlertTexts.PositionNowLabel}:</b> {size:N2} (${current.PositionValueUsd:N0} at market)\n");
        if (group.Action == "Open" && size > 0m)
            sb.Append(inv, $"<b>This run's fills:</b> {group.TotalSize / size * 100m:0}% of that position\n");

        sb.Append(inv, $"<b>Avg entry:</b> ${FormatPrice(entry)}\n");
        if (market > 0m)
        {
            string move = entry > 0m ? string.Create(inv, $" ({(market - entry) / entry * 100m:+0.0;-0.0}% vs entry)") : string.Empty;
            sb.Append(inv, $"<b>Market price:</b> ${FormatPrice(market)}{move}\n");
        }

        sb.Append(inv, $"<b>Unrealized PnL:</b> {Usd(current.UnrealizedPnlUsd, "N0")}\n");
        if (current.Leverage > 0m)
            sb.Append(inv, $"<b>Leverage:</b> {current.Leverage:0.#}x\n");

        AppendRisk(sb, current, context, analysis);
    }

    private static void AppendAnalysis(StringBuilder sb, WhaleAnalysis analysis)
    {
        if (analysis.IsEmpty)
            return;

        sb.Append("\n\n<b>").Append(WhaleAlertTexts.AnalysisHeader).Append("</b>");
        foreach (string line in analysis.Lines)
            sb.Append('\n').Append(line);
    }

    private static string Usd(decimal value, string format) =>
        (value < 0m ? "-$" : "$") + Math.Abs(value).ToString(format, CultureInfo.InvariantCulture);

    private static decimal Scale(string coin) =>
        HyperliquidSmartMoneySymbolMapper.Map(coin)?.UnitScaleFactor ?? 1m;

    private static decimal MarketPrice(WalletPositionSnapshot current, decimal scale) =>
        current.Size > 0m && scale > 0m ? current.PositionValueUsd / (current.Size * scale) : 0m;

    private static void AppendRisk(StringBuilder sb, WalletPositionSnapshot current, WhaleAlertContext? context, WhaleAnalysis analysis)
    {
        var inv = CultureInfo.InvariantCulture;
        decimal scale = Scale(current.Coin);
        decimal market = MarketPrice(current, scale);
        bool isShort = current.Direction == "Short";
        decimal? liquidation = current.LiquidationPrice is > 0m ? current.LiquidationPrice.Value / scale : null;

        if (liquidation is not null && market > 0m)
        {
            decimal distance = isShort ? (liquidation.Value - market) / market * 100m : (market - liquidation.Value) / market * 100m;
            sb.Append(inv, $"<b>Liquidation:</b> ${FormatPrice(liquidation.Value)} ({Math.Abs(distance):0.0}% {(isShort ? "above" : "below")} market)\n");
            if (distance < 0m)
                analysis.AddWarning(WhaleAlertTexts.StaleData);
            else if (distance < LiquidationWarnPercent)
                analysis.AddWarning(WhaleAlertTexts.LiquidationClose(distance));
        }

        if (current.AccountValueUsd is > 0m)
        {
            decimal multiple = current.PositionValueUsd / current.AccountValueUsd.Value;
            sb.Append(inv, $"<b>Account value:</b> ${current.AccountValueUsd.Value:N0} (this position is {multiple:0.#}x of it)\n");
            if (multiple >= ExposureWarnMultiple)
                analysis.AddWarning(WhaleAlertTexts.ExposureHigh(multiple));
        }

        if (context is not null)
            AppendStops(sb, current, context, market, liquidation, isShort, analysis);
    }

    private static void AppendStops(StringBuilder sb, WalletPositionSnapshot current, WhaleAlertContext context, decimal market, decimal? liquidation, bool isShort, WhaleAnalysis analysis)
    {
        var inv = CultureInfo.InvariantCulture;
        string closingSide = isShort ? "B" : "A";
        var stops = context.Orders
            .Where(o => o.Coin == current.Coin && o.IsTrigger && o.OrderKind == "StopLoss"
                && string.Equals(o.Side, closingSide, StringComparison.OrdinalIgnoreCase)
                && (market <= 0m || (isShort ? o.Price > market : o.Price < market)))
            .ToList();

        if (stops.Count == 0)
        {
            sb.Append("<b>Stops:</b> none found\n");
            analysis.AddWarning(WhaleAlertTexts.NoStops);
            return;
        }

        decimal nearest = isShort ? stops.Min(o => o.Price) : stops.Max(o => o.Price);
        string distance = market > 0m ? string.Create(inv, $" ({Math.Abs(nearest - market) / market * 100m:0.0}% from market)") : string.Empty;
        if (liquidation is not null)
        {
            int count = stops.Count(o => isShort ? o.Price >= liquidation.Value : o.Price <= liquidation.Value);
            if (count > 0)
                analysis.AddWarning(WhaleAlertTexts.StopsBeyondLiquidation(count));
        }

        sb.Append(inv, $"<b>Stops:</b> {stops.Count} orders, {stops.Sum(o => o.Size):N2} in total, nearest ${FormatPrice(nearest)}{distance}\n");
    }

    public string Format(SmartMoneyPositionEvent ev, WalletPositionSnapshot? current, string safeAddress, WhaleAlertContext? context = null)
    {
        bool snapshot = IsSnapshotEvent(ev);
        bool isOpen = ev.Action == "Open";
        bool snapshotClose = snapshot && !isOpen;
        var inv = CultureInfo.InvariantCulture;

        string header = isOpen
            ? snapshot
                ? $"\U0001F4CC <b>WHALE {ev.Direction.ToUpperInvariant()} (already open, first seen)</b>"
                : $"{(ev.Direction == "Long" ? "\U0001F7E2" : "\U0001F534")} <b>WHALE {ev.Direction.ToUpperInvariant()} OPENED</b>"
            : snapshotClose
                ? $"{(ev.ClosedPnlUsd >= 0 ? "\u2705" : "\u274C")} <b>WHALE CLOSED (detected between checks)</b>"
                : ev.ClosedPnlUsd >= 0
                    ? "\u2705 <b>WHALE CLOSED (PROFIT)</b>"
                    : "\u274C <b>WHALE CLOSED (LOSS)</b>";

        bool hasMarket = snapshot && isOpen && current is not null && ev.Size > 0m && current.PositionValueUsd > 0m;
        decimal valueUsd = AlertValueUsd(ev, current);

        var analysis = new WhaleAnalysis();
        var sb = new StringBuilder();
        sb.Append(header).Append("\n\n");
        sb.Append(inv, $"<b>Asset:</b> #{ev.Symbol}\n");
        sb.Append(inv, $"<b>Action:</b> {ev.Action} ({ev.Direction})\n");
        sb.Append(inv, $"<b>Size:</b> {ev.Size:N2} (${valueUsd:N0} USD{(hasMarket ? " at market" : string.Empty)})\n");

        if (hasMarket)
        {
            decimal market = current!.PositionValueUsd / ev.Size;
            string move = ev.Price > 0m ? string.Create(inv, $" ({(market - ev.Price) / ev.Price * 100m:+0.0;-0.0}% vs entry)") : string.Empty;
            sb.Append(inv, $"<b>Entry price:</b> ${FormatPrice(ev.Price)}\n");
            sb.Append(inv, $"<b>Market price:</b> ${FormatPrice(market)}{move}\n");
            sb.Append(inv, $"<b>Unrealized PnL:</b> {Usd(current.UnrealizedPnlUsd, "N0")}\n");
            if (current.Leverage > 0m)
                sb.Append(inv, $"<b>Leverage:</b> {current.Leverage:0.#}x\n");
            AppendRisk(sb, current, context, analysis);
        }
        else
        {
            sb.Append(inv, $"<b>{(snapshotClose ? "Entry price" : "Price")}:</b> ${FormatPrice(ev.Price)}\n");
        }

        if (ev.ClosedPnlUsd != 0m)
            sb.Append(inv, $"<b>{(snapshotClose ? "Unrealized PnL at last check" : "PnL")}:</b> {Usd(ev.ClosedPnlUsd, "N2")}\n");

        string timeLabel = !snapshot ? "Trade time" : isOpen ? "First seen" : "Detected";
        sb.Append(inv, $"<b>{timeLabel}:</b> {FormatTime(ev.OpenedAtUtc)}\n");
        if (snapshotClose)
            analysis.AddNote(WhaleAlertTexts.SnapshotCloseNote);
        else if (snapshot)
            analysis.AddNote(WhaleAlertTexts.FirstSeenNote);

        sb.Append(inv, $"<b>Wallet:</b> <code>{safeAddress}</code>");
        AppendAnalysis(sb, analysis);
        return sb.ToString();
    }

    private static string FormatPrice(decimal price) =>
        price >= 1m
            ? price.ToString("N2", CultureInfo.InvariantCulture)
            : price.ToString("0.00########", CultureInfo.InvariantCulture);

    public string FormatTime(DateTimeOffset time)
    {
        var inv = CultureInfo.InvariantCulture;
        DateTime utc = time.UtcDateTime;
        string text = $"{utc.ToString("yyyy-MM-dd HH:mm:ss", inv)} UTC";
        if (_zone is null)
            return text;

        DateTime local = TimeZoneInfo.ConvertTimeFromUtc(utc, _zone);
        string localText = local.Date == utc.Date
            ? local.ToString("HH:mm:ss", inv)
            : local.ToString("yyyy-MM-dd HH:mm:ss", inv);
        return $"{text} ({localText} {_zoneLabel})";
    }
}
