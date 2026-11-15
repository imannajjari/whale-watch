using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using WhaleWatch.Models.SmartMoney;

namespace WhaleWatch.Abstractions.SmartMoney;

public interface ISmartMoneySource
{
    Task<IReadOnlyList<TrackedWallet>> GetLeaderboardAsync(CancellationToken cancellationToken = default);
    Task<IReadOnlyList<WalletPositionSnapshot>> GetOpenPositionsAsync(string walletAddress, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<WalletFillEvent>> GetRecentFillsAsync(string walletAddress, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<WalletProtectiveOrder>> GetOpenProtectiveOrdersAsync(string walletAddress, CancellationToken cancellationToken = default);
}
