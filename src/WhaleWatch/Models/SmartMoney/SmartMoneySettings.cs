namespace WhaleWatch.Models.SmartMoney;

public sealed class SmartMoneySettings
{
    public const string DefaultLeaderboardUrl = "https://stats-data.hyperliquid.xyz/Mainnet/leaderboard";
    public const string DefaultBaseUrl = "https://api.hyperliquid.xyz";

    public bool Enabled { get; set; } = false;
    public string SqlitePath { get; set; } = "smart_money.db";
    public string LeaderboardUrl { get; set; } = DefaultLeaderboardUrl;
    public string BaseUrl { get; set; } = DefaultBaseUrl;
    public int LeaderboardRefreshHours { get; set; } = 24;
    public int PollIntervalSeconds { get; set; } = 300;
    public int MaxTrackedWallets { get; set; } = 25;
    public decimal MinAccountValueUsd { get; set; } = 50000m;
    public decimal MinMonthPnlUsd { get; set; } = 5000m;
    public decimal MinMonthRoi { get; set; } = 0.05m;
    public decimal MinAllTimePnlUsd { get; set; } = 25000m;
    public decimal MaxVolumeToPnlRatio { get; set; } = 500m;
    public int MinClosedTrades { get; set; } = 10;

    /// <summary>A wallet holding a position at least this large (USD) is tracked even with fewer than MinClosedTrades closed trades; 0 turns it off.</summary>
    public decimal LargePositionBypassUsd { get; set; } = 500000m;

    /// <summary>Records shadow outcomes for offline validation without impacting live decisions.</summary>
    public bool ShadowModeEnabled { get; set; } = true;

    /// <summary>When true, smart-money confirmation actively adjusts aggregated confidence scores.</summary>
    public bool ConfirmationEnabled { get; set; } = false;

    /// <summary>Confidence score bonus awarded when smart money aligns with the engine decision.</summary>
    public double ConfluenceBonusScore { get; set; } = 5.0;

    /// <summary>Confidence score penalty deducted when smart money opposes the engine decision.</summary>
    public double OpposingPenaltyScore { get; set; } = 15.0;

    /// <summary>Minimum capital dominance ratio (0.50 to 1.0) required to establish a directional stance.</summary>
    public decimal MinWhaleConsensusRatio { get; set; } = 0.65m;

    /// <summary>Minimum closed trades before a wallet gets a track-record score (below it the score is 0).</summary>
    public int TrackRecordMinTrades { get; set; } = 15;

    /// <summary>Pseudo-trades at a 50% win rate that pull a short record toward the middle.</summary>
    public int TrackRecordPriorTrades { get; set; } = 10;

    /// <summary>Minimum average winning trade in USD; excludes scalpers whose edge a delayed copy cannot capture.</summary>
    public decimal TrackRecordMinAverageWinUsd { get; set; } = 100m;

    /// <summary>Close fills of one coin and side closer together than this many minutes count as one trade.</summary>
    public int TrackRecordClusterMinutes { get; set; } = 30;

    /// <summary>Number of recent closed candles within which an un-invalidated strategy signal remains eligible for confluence.</summary>
    public int SignalPersistenceBars { get; set; } = 3;
}
