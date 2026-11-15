using System.Text.Json;
using WhaleWatch.Models.SmartMoney;
using WhaleWatch.Models.Telegram;

namespace WhaleWatch.Models;

public sealed class AppConfig
{
    public SmartMoneySettings SmartMoney { get; set; } = new();
    public TelegramSettings Telegram { get; set; } = new();
    public CloudflareKvSettings CloudflareKv { get; set; } = new();

    public static AppConfig Load()
    {
        string[] searchPaths =
        [
            Path.Combine(Directory.GetCurrentDirectory(), "appsettings.json"),
            Path.Combine(AppContext.BaseDirectory, "appsettings.json")
        ];

        AppConfig? config = null;
        foreach (string path in searchPaths.Where(File.Exists))
        {
            try
            {
                config = JsonSerializer.Deserialize<AppConfig>(File.ReadAllText(path), new JsonSerializerOptions
                {
                    PropertyNameCaseInsensitive = true,
                    ReadCommentHandling = JsonCommentHandling.Skip
                });

                if (config != null)
                    break;
            }
            catch (JsonException ex)
            {
                Console.Error.WriteLine($"[Config Warning] Could not read {path}: {ex.Message}");
            }
        }

        config ??= new AppConfig();
        ApplyEnvironment(config, Environment.GetEnvironmentVariable);
        return config;
    }

    // Secrets and the display time zone come from the environment (GitHub Actions secrets); an empty value changes nothing.
    public static void ApplyEnvironment(AppConfig config, Func<string, string?> getVariable)
    {
        string? token = getVariable("TELEGRAM_BOT_TOKEN");
        if (!string.IsNullOrWhiteSpace(token))
        {
            config.Telegram.BotToken = token;
            config.Telegram.Enabled = true;
        }

        string? chatId = getVariable("TELEGRAM_CHAT_ID");
        if (!string.IsNullOrWhiteSpace(chatId))
            config.Telegram.ChatId = chatId;

        string? zone = getVariable("TELEGRAM_DISPLAY_TIMEZONE");
        if (!string.IsNullOrWhiteSpace(zone))
            config.Telegram.DisplayTimeZone = zone;

        string? cloudflareToken = getVariable("CLOUDFLARE_API_TOKEN");
        if (!string.IsNullOrWhiteSpace(cloudflareToken))
            config.CloudflareKv.ApiToken = cloudflareToken;

        string? accountId = getVariable("CLOUDFLARE_ACCOUNT_ID");
        if (!string.IsNullOrWhiteSpace(accountId))
            config.CloudflareKv.AccountId = accountId;

        string? namespaceId = getVariable("CLOUDFLARE_KV_NAMESPACE_ID");
        if (!string.IsNullOrWhiteSpace(namespaceId))
            config.CloudflareKv.NamespaceId = namespaceId;
    }
}
