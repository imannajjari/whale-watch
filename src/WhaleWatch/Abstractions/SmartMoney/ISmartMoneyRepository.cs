using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using WhaleWatch.Models.SmartMoney;

namespace WhaleWatch.Abstractions.SmartMoney;

public interface ISmartMoneyRepository
{
    Task InitializeAsync(CancellationToken cancellationToken = default);
    Task UpsertWalletsAsync(IReadOnlyList<TrackedWallet> wallets, DateTimeOffset refreshedAtUtc, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<TrackedWallet>> GetTrackedWalletsAsync(CancellationToken cancellationToken = default);
    Task<DateTimeOffset?> GetLastLeaderboardRefreshUtcAsync(CancellationToken cancellationToken = default);
    Task<int> RecordPositionEventsAsync(IReadOnlyList<SmartMoneyPositionEvent> events, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<SmartMoneyPositionEvent>> RecordAndReturnNewPositionEventsAsync(IReadOnlyList<SmartMoneyPositionEvent> events, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<SmartMoneyPositionEvent>> GetRecentEventsAsync(int limit = 50, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<WalletPositionSnapshot>> GetSnapshotPositionsAsync(string walletAddress, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<WalletPositionSnapshot>> GetAllSnapshotPositionsAsync(CancellationToken cancellationToken = default);
    Task SaveSnapshotPositionsAsync(string walletAddress, IReadOnlyList<WalletPositionSnapshot> positions, CancellationToken cancellationToken = default);
    Task SaveProtectiveOrdersAsync(string walletAddress, IReadOnlyList<WalletProtectiveOrder> orders, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<WalletProtectiveOrder>> GetProtectiveOrdersAsync(string? symbol = null, CancellationToken cancellationToken = default);
    Task<long> RecordShadowOutcomeAsync(ShadowOutcomeRecord record, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<ShadowOutcomeRecord>> GetShadowOutcomesAsync(string? symbol = null, int limit = 100, CancellationToken cancellationToken = default);
    Task SaveWalletTrackRecordAsync(WalletTrackRecord record, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<WalletTrackRecord>> GetWalletTrackRecordsAsync(CancellationToken cancellationToken = default);
    Task<int> UpdateShadowOutcomeResultAsync(long id, decimal outcomeNetR, CancellationToken cancellationToken = default);
}
