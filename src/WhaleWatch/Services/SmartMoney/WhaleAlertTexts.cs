using System.Globalization;

namespace WhaleWatch.Services.SmartMoney;

// Persian wording of the analysis block at the end of a whale alert. The labels above it stay in English.
public static class WhaleAlertTexts
{
    // The English label used in the message body; the notes below point at it by name.
    public const string PositionNowLabel = "Position now";

    public const string AnalysisHeader = "تحلیل:";

    public const string FirstSeenNote =
        "• موقعیت هنگام اولین بررسی از قبل باز بود؛ زمان واقعی بازشدن آن معلوم نیست.";

    public const string SnapshotCloseNote =
        "• موقعیت در این بررسی دیگر باز نبود؛ زمان و قیمت واقعی بسته‌شدن معلوم نیست.";

    public const string SingleFillNote =
        $"• این پیام فقط یک fill (یک اجرای معامله) است، نه کل موقعیت؛ اعداد {PositionNowLabel} وضعیت کیف پول در همین بررسی‌اند.";

    public const string StaleData =
        "⚠ قیمت بازار از قیمت لیکویید گذشته است؛ احتمالاً داده کهنه است.";

    public const string NoStops =
        "⚠ برای این موقعیت حد ضرری پیدا نشد.";

    public static string ManyFillsNote(int fills) =>
        string.Create(CultureInfo.InvariantCulture,
            $"• این پیام مجموع {fills} fill در همین بررسی است، نه کل موقعیت؛ اعداد {PositionNowLabel} وضعیت کیف پول در همین بررسی‌اند.");

    public static string LiquidationClose(decimal percentFromMarket) =>
        string.Create(CultureInfo.InvariantCulture,
            $"⚠ قیمت لیکویید فقط {percentFromMarket:0.0}% با قیمت بازار فاصله دارد.");

    public static string ExposureHigh(decimal multipleOfAccount) =>
        string.Create(CultureInfo.InvariantCulture,
            $"⚠ اندازه‌ی موقعیت {multipleOfAccount:0.#} برابر ارزش حساب است.");

    public static string StopsBeyondLiquidation(int count) =>
        string.Create(CultureInfo.InvariantCulture,
            $"⚠ {count} حد ضرر در قیمت لیکویید یا بعد از آن قرار دارد و عملاً محافظت نمی‌کند.");
}
