using System;

namespace WhaleWatch.Models.SmartMoney;

/// <summary>
/// Domain model for an offline shadow evaluation record.
/// </summary>
public record ShadowOutcomeRecord(
    long Id,
    string Symbol,
    string EngineDirection,
    string SmartMoneyStance,
    DateTimeOffset DecisionAtUtc,
    decimal? OutcomeNetR);
