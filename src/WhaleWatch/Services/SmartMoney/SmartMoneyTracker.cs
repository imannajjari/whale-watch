using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using WhaleWatch.Abstractions.SmartMoney;
using WhaleWatch.Abstractions.Telegram;
using WhaleWatch.Models.SmartMoney;
using WhaleWatch.Models.Telegram;

namespace WhaleWatch.Services.SmartMoney;

public sealed class SmartMoneyTracker(
    ISmartMoneySource source,
    ISmartMoneyRepository repository,
    WalletQualityFilter filter,
    SmartMoneySettings settings,
    TimeProvider? timeProvider = null,
    ITelegramNotifier? telegramNotifier = null,
    TelegramSettings? telegramSettings = null)
{
    private readonly ISmartMoneySource _source = source ?? throw new ArgumentNullException(nameof(source));
    private readonly ISmartMoneyRepository _repository = repository ?? throw new ArgumentNullException(nameof(repository));
    private readonly WalletQualityFilter _filter = filter ?? throw new ArgumentNullException(nameof(filter));
    private readonly SmartMoneySettings _settings = settings ?? throw new ArgumentNullException(nameof(settings));
    private readonly TimeProvider _time = timeProvider ?? TimeProvider.System;
    private readonly ITelegramNotifier? _telegram = telegramNotifier;
    private readonly TelegramSettings? _telegramSettings = telegramSettings;
    private readonly WhaleAlertFormatter _whaleFormatter = new(telegramSettings?.DisplayTimeZone);
    private readonly WalletTrackRecordCalculator _trackRecords = new(settings);

    public TimeSpan PollInterval => TimeSpan.FromSeconds(Math.Max(15, _settings.PollIntervalSeconds));

    public async Task<SmartMoneyPollResult> PollOnceAsync(CancellationToken cancellationToken = default)
    {
        if (!_settings.Enabled)
        {
            return new SmartMoneyPollResult(
                false,
                0,
                0,
                0,
                [],
                "SmartMoney tracking is disabled in configuration.");
        }

        var warnings = new List<string>();
        try
        {
            await _repository.InitializeAsync(cancellationToken);
            var wallets = await EnsureFreshWalletsAsync(warnings, cancellationToken);
            int polled = 0;
            int totalInserted = 0;

            foreach (var wallet in wallets)
            {
                cancellationToken.ThrowIfCancellationRequested();
                try
                {
                    int inserted = await PollWalletAsync(wallet, warnings, cancellationToken);
                    polled++;
                    totalInserted += inserted;
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    warnings.Add($"Wallet {wallet.Address}: {ex.Message}");
                }
            }

            return new SmartMoneyPollResult(true, wallets.Count, polled, totalInserted, warnings);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            warnings.Add($"SmartMoney poll failed: {ex.Message}");
            return new SmartMoneyPollResult(false, 0, 0, 0, warnings, ex.Message);
        }
    }

    private async Task<IReadOnlyList<TrackedWallet>> EnsureFreshWalletsAsync(
        List<string> warnings,
        CancellationToken cancellationToken)
    {
        DateTimeOffset now = _time.GetUtcNow();
        DateTimeOffset? lastRefresh = await _repository.GetLastLeaderboardRefreshUtcAsync(cancellationToken);
        var existing = await _repository.GetTrackedWalletsAsync(cancellationToken);

        int refreshHours = Math.Max(1, _settings.LeaderboardRefreshHours);
        bool isStale = !lastRefresh.HasValue || (now - lastRefresh.Value) >= TimeSpan.FromHours(refreshHours);
        if (!isStale && existing.Count > 0)
            return existing;

        try
        {
            var raw = await _source.GetLeaderboardAsync(cancellationToken);
            var selected = _filter.SelectTopWallets(raw, _settings);
            await _repository.UpsertWalletsAsync(selected, now, cancellationToken);
            return selected;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            warnings.Add($"Leaderboard refresh failed ({ex.Message}); using cached wallets.");
            return existing;
        }
    }

    private async Task<int> PollWalletAsync(
        TrackedWallet wallet,
        List<string> warnings,
        CancellationToken cancellationToken)
    {
        var fills = await _source.GetRecentFillsAsync(wallet.Address, cancellationToken);
        var currentPositions = await _source.GetOpenPositionsAsync(wallet.Address, cancellationToken);
        var protectiveOrders = await _source.GetOpenProtectiveOrdersAsync(wallet.Address, cancellationToken);
        var previousPositions = await _repository.GetSnapshotPositionsAsync(wallet.Address, cancellationToken);

        bool enoughClosedTrades = _filter.HasMinimumClosedTrades(fills, _settings);
        bool largePosition = _filter.HoldsLargePosition(currentPositions, _settings);

        if (!enoughClosedTrades && largePosition)
            warnings.Add($"Wallet {wallet.Address} tracked despite fewer than {_settings.MinClosedTrades} closed trades: it holds a position of at least {_settings.LargePositionBypassUsd:N0} USD.");

        if (!enoughClosedTrades && !largePosition)
        {
            await SaveTrackRecordAsync(wallet.Address, fills, currentPositions, cancellationToken);
            warnings.Add($"Wallet {wallet.Address} skipped: fewer than {_settings.MinClosedTrades} closed trades.");
            await _repository.SaveSnapshotPositionsAsync(wallet.Address, currentPositions, cancellationToken);
            await _repository.SaveProtectiveOrdersAsync(wallet.Address, protectiveOrders, cancellationToken);
            return 0;
        }

        var events = BuildPositionEvents(wallet.Address, fills, previousPositions, currentPositions);
        var newlyInserted = await _repository.RecordAndReturnNewPositionEventsAsync(events, cancellationToken);
        await _repository.SaveSnapshotPositionsAsync(wallet.Address, currentPositions, cancellationToken);
        await _repository.SaveProtectiveOrdersAsync(wallet.Address, protectiveOrders, cancellationToken);

        if (_telegram != null && _telegramSettings?.NotifyOnWhaleActivity == true && newlyInserted.Count > 0)
        {
            await NotifyWhaleEventsAsync(wallet.Address, newlyInserted, currentPositions, protectiveOrders, cancellationToken);
        }

        await SaveTrackRecordAsync(wallet.Address, fills, currentPositions, cancellationToken);

        return newlyInserted.Count;
    }

    private async Task SaveTrackRecordAsync(
        string walletAddress,
        IReadOnlyList<WalletFillEvent> fills,
        IReadOnlyList<WalletPositionSnapshot> currentPositions,
        CancellationToken cancellationToken)
    {
        if (!_settings.ShadowModeEnabled)
            return;

        var record = _trackRecords.Calculate(walletAddress, fills, currentPositions, _time.GetUtcNow());
        await _repository.SaveWalletTrackRecordAsync(record, cancellationToken);
    }

    private async Task NotifyWhaleEventsAsync(
        string walletAddress,
        IReadOnlyList<SmartMoneyPositionEvent> newlyInsertedEvents,
        IReadOnlyList<WalletPositionSnapshot> currentPositions,
        IReadOnlyList<WalletProtectiveOrder> protectiveOrders,
        CancellationToken cancellationToken)
    {
        if (_telegram == null || _telegramSettings?.NotifyOnWhaleActivity != true)
            return;

        try
        {
            decimal threshold = _telegramSettings?.MinWhalePositionAlertUsd ?? 500000m;
            decimal minFill = _telegramSettings?.MinWhaleFillAlertUsd ?? 50000m;
            string safeAddress = walletAddress.Length >= 14
                ? $"{walletAddress[..8]}...{walletAddress[^6..]}"
                : walletAddress;

            var currentByKey = new Dictionary<(string Coin, string Direction), WalletPositionSnapshot>();
            foreach (var position in currentPositions)
                currentByKey[(position.Coin, position.Direction)] = position;

            var fillGroups = WhaleAlertFormatter.Group(newlyInsertedEvents);
            var context = new WhaleAlertContext(protectiveOrders, fillGroups);

            foreach (var ev in newlyInsertedEvents.Where(WhaleAlertFormatter.IsSnapshotEvent))
            {
                currentByKey.TryGetValue((ev.Coin, ev.Direction), out var current);
                if (!WhaleAlertFormatter.ShouldAlert(ev, current, threshold))
                    continue;

                await _telegram.SendMessageAsync(_whaleFormatter.Format(ev, current, safeAddress, context), cancellationToken);
            }

            foreach (var group in fillGroups)
            {
                currentByKey.TryGetValue((group.Coin, group.Direction), out var current);
                if (!WhaleAlertFormatter.ShouldAlert(group, current, threshold, minFill))
                    continue;

                await _telegram.SendMessageAsync(_whaleFormatter.FormatGroup(group, current, safeAddress, context), cancellationToken);
            }
        }
        catch (Exception ex)
        {
            Console.ForegroundColor = ConsoleColor.Yellow;
            Console.WriteLine($"[SmartMoney Warning] Failed to dispatch whale alert: {ex.Message}");
            Console.ResetColor();
        }
    }

    private List<SmartMoneyPositionEvent> BuildPositionEvents(
        string walletAddress,
        IReadOnlyList<WalletFillEvent> fills,
        IReadOnlyList<WalletPositionSnapshot> previousPositions,
        IReadOnlyList<WalletPositionSnapshot> currentPositions)
    {
        var events = new List<SmartMoneyPositionEvent>();
        var fillCovered = new HashSet<(string Coin, string Direction, string Action)>();
        DateTimeOffset now = _time.GetUtcNow();
        long lookbackWindowMs = Math.Max(_settings.PollIntervalSeconds * 2L, 3600L) * 1000L;
        long cutoffMs = now.ToUnixTimeMilliseconds() - lookbackWindowMs;

        foreach (var fill in fills)
        {
            if (fill.TimestampMs < cutoffMs)
                continue;

            (string Action, string Direction)? parsed = ParseFillAction(fill.Direction);
            if (parsed is null)
                continue;

            var mapped = HyperliquidSmartMoneySymbolMapper.Map(fill.Coin);
            if (mapped is null)
                continue;

            (string action, string dir) = parsed.Value;
            fillCovered.Add((mapped.Coin, dir, action));
            string prefix = action == "Open" ? "fill" : "close";
            string key = $"{prefix}:{walletAddress}:{fill.Coin}:{dir}:{fill.TimestampMs}:{fill.Hash}";
            events.Add(new SmartMoneyPositionEvent(
                key,
                walletAddress,
                mapped.Symbol,
                mapped.Coin,
                dir,
                action,
                fill.Size * mapped.UnitScaleFactor,
                fill.Price / mapped.UnitScaleFactor,
                action == "Close" ? fill.ClosedPnlUsd : 0m,
                DateTimeOffset.FromUnixTimeMilliseconds(fill.TimestampMs),
                "userFills"));
        }

        var prevKeys = previousPositions.Select(p => (p.Coin, p.Direction)).ToHashSet();
        var curKeys = currentPositions.Select(p => (p.Coin, p.Direction)).ToHashSet();

        foreach (var cur in currentPositions)
        {
            if (prevKeys.Contains((cur.Coin, cur.Direction)) || fillCovered.Contains((cur.Coin, cur.Direction, "Open")))
                continue;

            var mapped = HyperliquidSmartMoneySymbolMapper.Map(cur.Coin);
            if (mapped is null)
                continue;

            events.Add(new SmartMoneyPositionEvent(
                $"snap:{walletAddress}:{cur.Coin}:{cur.Direction}:{now.ToUnixTimeSeconds()}",
                walletAddress,
                mapped.Symbol,
                mapped.Coin,
                cur.Direction,
                "Open",
                cur.Size * mapped.UnitScaleFactor,
                cur.EntryPrice / mapped.UnitScaleFactor,
                0m,
                now,
                "clearinghouseState"));
        }

        foreach (var prev in previousPositions)
        {
            if (curKeys.Contains((prev.Coin, prev.Direction)) || fillCovered.Contains((prev.Coin, prev.Direction, "Close")))
                continue;

            var mapped = HyperliquidSmartMoneySymbolMapper.Map(prev.Coin);
            if (mapped is null)
                continue;

            events.Add(new SmartMoneyPositionEvent(
                $"snapclose:{walletAddress}:{prev.Coin}:{prev.Direction}:{now.ToUnixTimeSeconds()}",
                walletAddress,
                mapped.Symbol,
                mapped.Coin,
                prev.Direction,
                "Close",
                prev.Size * mapped.UnitScaleFactor,
                prev.EntryPrice / mapped.UnitScaleFactor,
                prev.UnrealizedPnlUsd,
                now,
                "clearinghouseState"));
        }

        return events;
    }

    private static (string Action, string Direction)? ParseFillAction(string fillDirection)
    {
        if (fillDirection.Equals("Open Long", StringComparison.OrdinalIgnoreCase))
            return ("Open", "Long");
        if (fillDirection.Equals("Open Short", StringComparison.OrdinalIgnoreCase))
            return ("Open", "Short");
        if (fillDirection.Equals("Close Long", StringComparison.OrdinalIgnoreCase))
            return ("Close", "Long");
        if (fillDirection.Equals("Close Short", StringComparison.OrdinalIgnoreCase))
            return ("Close", "Short");
        return null;
    }
}
