namespace WhaleWatch.Models.SmartMoney;

public sealed record SymbolMappingResult(
    string Symbol,
    string Coin,
    decimal UnitScaleFactor);
