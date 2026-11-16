namespace WhaleWatch.Models.Telegram;

public sealed class TelegramSettings
{
    public bool Enabled { get; set; } = false;
    public string BotToken { get; set; } = string.Empty;
    public string ChatId { get; set; } = string.Empty;
    public bool NotifyOnWhaleActivity { get; set; } = true;
    public bool NotifyOnConfirmedSetups { get; set; } = true;
    public decimal MinWhalePositionAlertUsd { get; set; } = 500000m;
    public decimal MinWhaleFillAlertUsd { get; set; } = 50000m;
    public string DisplayTimeZone { get; set; } = "UTC";
}
