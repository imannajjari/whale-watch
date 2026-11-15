namespace WhaleWatch.Models.Telegram;

public sealed class CloudflareKvSettings
{
    // Must match TABLE_KEY in cloudflare/whale-bot/src/bot.js, which reads this key.
    public const string DefaultKey = "whale-table";

    public string ApiToken { get; set; } = string.Empty;
    public string AccountId { get; set; } = string.Empty;
    public string NamespaceId { get; set; } = string.Empty;
    public string Key { get; set; } = DefaultKey;

    public bool IsConfigured =>
        !string.IsNullOrWhiteSpace(ApiToken)
        && !string.IsNullOrWhiteSpace(AccountId)
        && !string.IsNullOrWhiteSpace(NamespaceId)
        && !string.IsNullOrWhiteSpace(Key);
}
