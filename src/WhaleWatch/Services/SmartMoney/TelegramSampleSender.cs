using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using WhaleWatch.Abstractions.Telegram;
using WhaleWatch.Models.SmartMoney;
using WhaleWatch.Models.Telegram;

namespace WhaleWatch.Services.SmartMoney;

public sealed class TelegramSampleSender(ITelegramNotifier notifier, WhaleAlertFormatter formatter)
{
    public const string Switch = "--telegram-sample";

    private const string TestPrefix = "\U0001F9EA <b>TEST MESSAGE</b> (sample data, not a real event)\n\n";
    private const string SampleWallet = "0x0000000000000000000000000000000000000001";
    private const string SafeAddress = "0x000000...000001";

    private readonly ITelegramNotifier _notifier = notifier ?? throw new ArgumentNullException(nameof(notifier));
    private readonly WhaleAlertFormatter _formatter = formatter ?? throw new ArgumentNullException(nameof(formatter));

    public static bool IsRequested(IEnumerable<string> args)
    {
        foreach (string arg in args)
        {
            if (arg.Equals(Switch, StringComparison.OrdinalIgnoreCase))
                return true;
        }

        return false;
    }

    public static IReadOnlyList<string> Diagnose(TelegramSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);

        var problems = new List<string>();
        if (string.IsNullOrWhiteSpace(settings.BotToken))
            problems.Add("TELEGRAM_BOT_TOKEN is empty or not set (check the repository secret and the workflow env).");
        if (string.IsNullOrWhiteSpace(settings.ChatId))
            problems.Add("TELEGRAM_CHAT_ID is empty or not set (check the repository secret and the workflow env).");
        if (!settings.Enabled)
            problems.Add("Telegram.Enabled is false (it is switched on automatically when TELEGRAM_BOT_TOKEN is set).");

        return problems;
    }

    public IReadOnlyList<string> BuildMessages(DateTimeOffset now)
    {
        var firstSeen = new SmartMoneyPositionEvent(
            "sample:snap", SampleWallet, "SOLUSDT", "SOL", "Short", "Open",
            5806.24m, 95.7486m, 0m, now, "clearinghouseState");
        var firstSeenPosition = new WalletPositionSnapshot(
            SampleWallet, "SOL", "Short", 5806.24m, 95.7486m, 439947.51m, 115991.85m, 20m, 120.5m, 62000m);

        var fills = new[]
        {
            new SmartMoneyPositionEvent("sample:f1", SampleWallet, "BTCUSDT", "BTC", "Short", "Open", 7.807m, 64239m, 0m, now.AddMinutes(-9), "userFills"),
            new SmartMoneyPositionEvent("sample:f2", SampleWallet, "BTCUSDT", "BTC", "Short", "Open", 5.815m, 64239m, 0m, now.AddMinutes(-9).AddSeconds(4), "userFills"),
            new SmartMoneyPositionEvent("sample:f3", SampleWallet, "BTCUSDT", "BTC", "Short", "Open", 0.34m, 64245m, 0m, now.AddMinutes(-9).AddSeconds(6), "userFills"),
            new SmartMoneyPositionEvent("sample:c1", SampleWallet, "BTCUSDT", "BTC", "Long", "Close", 95.206m, 64196m, -42822m, now.AddMinutes(-9).AddSeconds(-20), "userFills")
        };
        var groups = WhaleAlertFormatter.Group(fills);
        var openGroup = groups[^1].Action == "Open" ? groups[^1] : groups[0];
        var btcShort = new WalletPositionSnapshot(
            SampleWallet, "BTC", "Short", 86.6m, 64236.3m, 5578675.53m, -15562.13m, 40m, 65051.31m, 125441m);
        var stops = new[] { 64750m, 64800m, 64900m, 65000m, 65100m, 65200m }
            .Select((price, i) => new WalletProtectiveOrder(SampleWallet, i + 1, "BTCUSDT", "BTC", "StopLoss", "B", price, 10m, true, "Price above", now))
            .ToList();
        var context = new WhaleAlertContext(stops, groups);

        var detectedClose = new SmartMoneyPositionEvent(
            "sample:snapclose", SampleWallet, "ETHUSDT", "ETH", "Short", "Close",
            400m, 1950m, 8200m, now, "clearinghouseState");

        return
        [
            TestPrefix + _formatter.Format(firstSeen, firstSeenPosition, SafeAddress, new WhaleAlertContext([], [])),
            TestPrefix + _formatter.FormatGroup(openGroup, btcShort, SafeAddress, context),
            TestPrefix + _formatter.Format(detectedClose, null, SafeAddress)
        ];
    }

    public async Task<(int Sent, int Total)> SendAsync(DateTimeOffset now, CancellationToken cancellationToken = default)
    {
        var messages = BuildMessages(now);
        int sent = 0;
        foreach (string message in messages)
        {
            if (await _notifier.SendMessageAsync(message, cancellationToken))
                sent++;
        }

        return (sent, messages.Count);
    }

    public async Task<int> RunAsync(TelegramSettings settings, DateTimeOffset now, TextWriter log, CancellationToken cancellationToken = default)
    {
        var problems = Diagnose(settings);
        if (problems.Count > 0)
        {
            foreach (string problem in problems)
                log.WriteLine($"Telegram sample not sent: {problem}");
            return 1;
        }

        var (sent, total) = await SendAsync(now, cancellationToken);
        log.WriteLine($"Telegram sample messages sent: {sent} of {total}.");
        if (sent != total)
            log.WriteLine("Some messages were not delivered; see any [Telegram Warning] line above for the HTTP status.");

        return sent == total ? 0 : 1;
    }
}
