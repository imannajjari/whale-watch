using System;
using System.Collections.Generic;
using System.IO;
using System.Net.Http;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using WhaleWatch.Abstractions.SmartMoney;
using WhaleWatch.Abstractions.Telegram;
using WhaleWatch.Models.Telegram;

namespace WhaleWatch.Services.Telegram;

public sealed class TelegramUpdateProcessor(
    HttpClient httpClient,
    TelegramSettings settings,
    ITelegramNotifier notifier,
    ISmartMoneyRepository repository,
    TextWriter? log = null)
{
    private readonly TextWriter _log = log ?? Console.Out;
    private readonly HttpClient _httpClient = httpClient ?? throw new ArgumentNullException(nameof(httpClient));
    private readonly TelegramSettings _settings = settings ?? throw new ArgumentNullException(nameof(settings));
    private readonly ITelegramNotifier _notifier = notifier ?? throw new ArgumentNullException(nameof(notifier));
    private readonly ISmartMoneyRepository _repository = repository ?? throw new ArgumentNullException(nameof(repository));

    private async Task<bool> IsWebhookActiveAsync(CancellationToken cancellationToken)
    {
        try
        {
            string url = $"https://api.telegram.org/bot{_settings.BotToken}/getWebhookInfo";
            using var response = await _httpClient.GetAsync(url, cancellationToken);
            if (!response.IsSuccessStatusCode)
                return false;

            using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync(cancellationToken));
            return doc.RootElement.TryGetProperty("result", out var result)
                && result.ValueKind == JsonValueKind.Object
                && result.TryGetProperty("url", out var webhookUrl)
                && !string.IsNullOrWhiteSpace(webhookUrl.GetString());
        }
        catch (Exception ex) when (ex is HttpRequestException or JsonException || (ex is TaskCanceledException && !cancellationToken.IsCancellationRequested))
        {
            _log.WriteLine($"[Telegram Warning] Could not read the webhook state ({ex.GetType().Name}); trying getUpdates.");
            return false;
        }
    }

    public async Task ProcessPendingUpdatesAsync(CancellationToken cancellationToken = default)
    {
        if (!_settings.Enabled || string.IsNullOrWhiteSpace(_settings.BotToken))
            return;

        try
        {
            if (await IsWebhookActiveAsync(cancellationToken))
            {
                _log.WriteLine("Telegram updates are delivered through a webhook; getUpdates skipped.");
                return;
            }

            string url = $"https://api.telegram.org/bot{_settings.BotToken}/getUpdates";
            using var response = await _httpClient.GetAsync(url, cancellationToken);
            if (!response.IsSuccessStatusCode)
            {
                _log.WriteLine($"[Telegram Warning] getUpdates failed: HTTP {(int)response.StatusCode}. A webhook may be set on this bot.");
                return;
            }

            string json = await response.Content.ReadAsStringAsync(cancellationToken);
            using var doc = JsonDocument.Parse(json);
            if (!doc.RootElement.TryGetProperty("result", out var results) || results.ValueKind != JsonValueKind.Array)
                return;

            long lastUpdateId = 0;
            bool shouldReplyTable = false;

            foreach (var update in results.EnumerateArray())
            {
                if (update.TryGetProperty("update_id", out var idElem))
                {
                    lastUpdateId = idElem.GetInt64();
                }

                if (update.TryGetProperty("message", out var msgElem))
                {
                    if (msgElem.TryGetProperty("text", out var textElem))
                    {
                        string text = textElem.GetString() ?? string.Empty;
                        if (text.Contains("وضعیت معاملات نهنگ‌ها", StringComparison.OrdinalIgnoreCase) ||
                            text.Equals("/status", StringComparison.OrdinalIgnoreCase) ||
                            text.Equals("/whales", StringComparison.OrdinalIgnoreCase) ||
                            text.Equals("/start", StringComparison.OrdinalIgnoreCase))
                        {
                            shouldReplyTable = true;
                        }
                    }
                }
            }

            if (shouldReplyTable)
            {
                string report = await TelegramWhaleTableFormatter.FormatPositionsTableAsync(_repository, cancellationToken);
                var keyboard = new
                {
                    keyboard = new[]
                    {
                        new[] { new { text = "📊 وضعیت معاملات نهنگ‌ها" } }
                    },
                    resize_keyboard = true
                };

                await _notifier.SendMessageWithKeyboardAsync(report, keyboard, cancellationToken);
            }

            if (lastUpdateId > 0)
            {
                // Acknowledge updates by setting offset
                string ackUrl = $"https://api.telegram.org/bot{_settings.BotToken}/getUpdates?offset={lastUpdateId + 1}";
                await _httpClient.GetAsync(ackUrl, cancellationToken);
            }
        }
        catch (Exception ex)
        {
            Console.ForegroundColor = ConsoleColor.Yellow;
            Console.WriteLine($"[Telegram Warning] Error processing updates: {ex.Message}");
            Console.ResetColor();
        }
    }
}
