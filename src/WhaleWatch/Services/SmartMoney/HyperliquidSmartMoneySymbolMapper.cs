using System;
using System.Collections.Generic;
using WhaleWatch.Abstractions.SmartMoney;
using WhaleWatch.Models.SmartMoney;

namespace WhaleWatch.Services.SmartMoney;

public sealed class HyperliquidSmartMoneySymbolMapper
{
    private const decimal KiloScale = 1000m;

    private static readonly HashSet<string> KnownKiloCoins = new(StringComparer.Ordinal)
    {
        "kPEPE", "kSHIB", "kBONK", "kFLOKI", "kLUNC", "kNEIRO", "kDOGS"
    };

    public static readonly HyperliquidSmartMoneySymbolMapper Default = new();

    public static SymbolMappingResult? Map(string? coin)
    {
        if (string.IsNullOrWhiteSpace(coin))
            return null;

        string trimmed = coin.Trim();
        if (KnownKiloCoins.Contains(trimmed) || (trimmed.Length > 1 && trimmed[0] == 'k' && char.IsUpper(trimmed[1])))
        {
            string baseAsset = trimmed[1..].ToUpperInvariant();
            return new SymbolMappingResult($"{baseAsset}USDT", trimmed, KiloScale);
        }

        string normalized = trimmed.ToUpperInvariant();
        if (normalized.EndsWith("USDT", StringComparison.Ordinal))
            return new SymbolMappingResult(normalized, trimmed, 1m);

        return new SymbolMappingResult($"{normalized}USDT", trimmed, 1m);
    }
}
