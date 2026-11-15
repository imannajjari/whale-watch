using System;

namespace WhaleWatch.Models.SmartMoney;

public sealed record WhaleEventGroup(
    string Symbol,
    string Coin,
    string Direction,
    string Action,
    int FillCount,
    decimal TotalSize,
    decimal TotalNotionalUsd,
    decimal AveragePrice,
    decimal MinPrice,
    decimal MaxPrice,
    DateTimeOffset FirstTimeUtc,
    DateTimeOffset LastTimeUtc,
    decimal TotalClosedPnlUsd);
