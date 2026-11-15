using WhaleWatch.Models;

namespace WhaleWatch.Tests;

public sealed class AppConfigTests
{
    private static Func<string, string?> Env(params (string Name, string? Value)[] values) =>
        name => values.FirstOrDefault(v => v.Name == name).Value;

    [Fact]
    public void ApplyEnvironment_ReadsSecretsAndEnablesTelegramWhenATokenExists()
    {
        var config = new AppConfig();

        AppConfig.ApplyEnvironment(config, Env(
            ("TELEGRAM_BOT_TOKEN", "123:abc"), ("TELEGRAM_CHAT_ID", "42"), ("TELEGRAM_DISPLAY_TIMEZONE", "Europe/Berlin"),
            ("CLOUDFLARE_API_TOKEN", "cf"), ("CLOUDFLARE_ACCOUNT_ID", "acct"), ("CLOUDFLARE_KV_NAMESPACE_ID", "ns")));

        Assert.True(config.Telegram.Enabled);
        Assert.Equal("123:abc", config.Telegram.BotToken);
        Assert.Equal("42", config.Telegram.ChatId);
        Assert.Equal("Europe/Berlin", config.Telegram.DisplayTimeZone);
        Assert.True(config.CloudflareKv.IsConfigured);
    }

    [Fact]
    public void ApplyEnvironment_IgnoresEmptyValuesAndKeepsTheDefaults()
    {
        var config = new AppConfig();

        AppConfig.ApplyEnvironment(config, Env(("TELEGRAM_BOT_TOKEN", ""), ("TELEGRAM_CHAT_ID", " "), ("CLOUDFLARE_API_TOKEN", null)));

        Assert.False(config.Telegram.Enabled);
        Assert.Equal(string.Empty, config.Telegram.ChatId);
        Assert.Equal("UTC", config.Telegram.DisplayTimeZone);
        Assert.False(config.CloudflareKv.IsConfigured);
    }
}
