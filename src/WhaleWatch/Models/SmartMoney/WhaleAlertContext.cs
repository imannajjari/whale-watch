using System.Collections.Generic;

namespace WhaleWatch.Models.SmartMoney;

public sealed record WhaleAlertContext(
    IReadOnlyList<WalletProtectiveOrder> Orders,
    IReadOnlyList<WhaleEventGroup> RunGroups);
