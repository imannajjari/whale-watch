using System;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using WhaleWatch.Abstractions.Telegram;
using WhaleWatch.Models.Telegram;

namespace WhaleWatch.Services.Telegram;

public sealed class TelegramNotifier(HttpClient httpClient, TelegramSettings settings) : ITelegramNotifier
{
    private readonly HttpClient _httpClient = httpClient ?? throw new ArgumentNullException(nameof(httpClient));
    private readonly TelegramSettings _settings = settings ?? throw new ArgumentNullException(nameof(settings));

    public Task<bool> SendMessageAsync(string message, CancellationToken cancellationToken = default)
        => SendMessageInternalAsync(message, replyMarkup: null, cancellationToken);

    public Task<bool> SendMessageWithKeyboardAsync(string message, object replyMarkup, CancellationToken cancellationToken = default)
        => SendMessageInternalAsync(message, replyMarkup, cancellationToken);

    private async Task<bool> SendMessageInternalAsync(string message, object? replyMarkup, CancellationToken cancellationToken)
    {
        if (!_settings.Enabled || string.IsNullOrWhiteSpace(_settings.BotToken) || string.IsNullOrWhiteSpace(_settings.ChatId))
            return false;

        if (string.IsNullOrWhiteSpace(message))
            return false;

        try
        {
            string url = $"https://api.telegram.org/bot{_settings.BotToken}/sendMessage";
            var payload = new Dictionary<string, object?>
            {
                ["chat_id"] = _settings.ChatId,
                ["text"] = message,
                ["parse_mode"] = "HTML",
                ["disable_web_page_preview"] = true
            };

            if (replyMarkup != null)
            {
                payload["reply_markup"] = replyMarkup;
            }

            string json = JsonSerializer.Serialize(payload, new JsonSerializerOptions
            {
                Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping
            });
            using var content = new StringContent(json, Encoding.UTF8, "application/json");
            using var response = await _httpClient.PostAsync(url, content, cancellationToken);

            if (!response.IsSuccessStatusCode)
            {
                string errorBody = await response.Content.ReadAsStringAsync(cancellationToken);
                Console.ForegroundColor = ConsoleColor.Yellow;
                Console.WriteLine($"[Telegram Warning] HTTP {(int)response.StatusCode} ({response.ReasonPhrase}): {errorBody}");
                Console.ResetColor();
                return false;
            }

            return true;
        }
        catch (Exception ex)
        {
            Console.ForegroundColor = ConsoleColor.Yellow;
            Console.WriteLine($"[Telegram Warning] Failed to send message: {ex.Message}");
            Console.ResetColor();
            return false;
        }
    }
}
