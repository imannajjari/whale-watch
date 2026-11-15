using WhaleWatch.Models;
using WhaleWatch.Services.SmartMoney;
using WhaleWatch.Services.Telegram;

namespace WhaleWatch;

public static class Program
{
    private const string PollSwitch = "--smart-money-poll";

    public static async Task<int> Main(string[] args)
    {
        Console.OutputEncoding = System.Text.Encoding.UTF8;

        using var cts = new CancellationTokenSource();
        Console.CancelKeyPress += (_, e) => { e.Cancel = true; cts.Cancel(); };

        try
        {
            var config = AppConfig.Load();
            using var httpClient = new HttpClient { Timeout = TimeSpan.FromSeconds(30) };

            if (TelegramSampleSender.IsRequested(args))
            {
                var sender = new TelegramSampleSender(
                    new TelegramNotifier(httpClient, config.Telegram),
                    new WhaleAlertFormatter(config.Telegram.DisplayTimeZone));
                return await sender.RunAsync(config.Telegram, DateTimeOffset.UtcNow, Console.Out, cts.Token);
            }

            if (args.Any(a => a.Equals(PollSwitch, StringComparison.OrdinalIgnoreCase)))
                return await PollAsync(config, httpClient, cts.Token);

            Console.WriteLine($"Usage: WhaleWatch {PollSwitch} | {TelegramSampleSender.Switch}");
            return 2;
        }
        catch (OperationCanceledException) when (cts.IsCancellationRequested)
        {
            Console.Error.WriteLine("Cancelled.");
            return 130;
        }
        catch (TaskCanceledException ex)
        {
            Console.Error.WriteLine($"A request timed out: {ex.Message}");
            return 1;
        }
    }

    private static async Task<int> PollAsync(AppConfig config, HttpClient httpClient, CancellationToken cancellationToken)
    {
        var source = new HyperliquidSmartMoneySource(httpClient, config.SmartMoney);
        using var repo = new SqliteSmartMoneyRepository($"Data Source={config.SmartMoney.SqlitePath}");
        var notifier = config.Telegram.Enabled ? new TelegramNotifier(httpClient, config.Telegram) : null;

        var tracker = new SmartMoneyTracker(
            source, repo, new WalletQualityFilter(), config.SmartMoney,
            TimeProvider.System, notifier, config.Telegram);
        var result = await tracker.PollOnceAsync(cancellationToken);

        foreach (string warning in result.Warnings)
            Console.Error.WriteLine($"[smart-money] {warning}");
        Console.WriteLine(result.Success
            ? $"SmartMoney poll complete: tracked={result.TrackedWalletsCount}, polled={result.PolledWalletsCount}, newEvents={result.NewEventsCount}."
            : $"SmartMoney poll skipped/failed: {result.ErrorMessage}");

        if (result.Success && config.CloudflareKv.IsConfigured)
            await new WhaleTableKvPublisher(httpClient, config.CloudflareKv).PublishCurrentTableAsync(repo, cancellationToken);

        if (notifier != null)
            await new TelegramUpdateProcessor(httpClient, config.Telegram, notifier, repo).ProcessPendingUpdatesAsync(cancellationToken);

        return 0;
    }
}
