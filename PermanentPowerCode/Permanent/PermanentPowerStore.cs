using MegaCrit.Sts2.Core.Runs;
using STS2RitsuLib.RunData;

namespace PermanentPower.Permanent;

/// <summary>一条固化记录：卡牌 id 与升级层数。</summary>
/// <param name="Category">卡牌 ModelId.Category，例如 CARD</param>
/// <param name="Entry">卡牌 ModelId.Entry，例如 CAPACITOR</param>
/// <param name="Upgrades">已升级层数</param>
internal sealed record PermanentPowerCard(string Category, string Entry, int Upgrades)
{
    /// <summary>存档字符串的字段分隔符。</summary>
    private const char Separator = '|';

    internal string Encode() => $"{Category}{Separator}{Entry}{Separator}{Upgrades}";

    internal static PermanentPowerCard? TryDecode(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return null;

        var parts = raw.Split(Separator);
        if (parts.Length != 3) return null;
        if (!int.TryParse(parts[2], out var upgrades)) return null;

        return new PermanentPowerCard(parts[0], parts[1], upgrades);
    }
}

/// <summary>固化卡牌的 run 存档读写。</summary>
internal static class PermanentPowerStore
{
    private const string SlotKey = "permanentPowerCards";

    private static RunSavedData<List<string>>? _slot;

    /// <summary>注册 run 存档槽位，启动时调用一次。</summary>
    internal static void Initialize()
    {
        _slot = RunSavedDataStore
            .For(Entry.ModId)
            .Register(SlotKey, static () => new List<string>());

        Entry.Logger.Info("[PermanentPower] 常驻能力存档槽已注册。");
    }

    /// <summary>按记录顺序读出并解码全部固化卡牌。</summary>
    internal static List<PermanentPowerCard> Load(RunState run)
    {
        var result = new List<PermanentPowerCard>();
        if (_slot is null) return result;

        foreach (var raw in _slot.Get(run))
        {
            var card = PermanentPowerCard.TryDecode(raw);
            if (card is not null) result.Add(card);
        }

        return result;
    }

    /// <summary>追加一条固化记录，不去重。</summary>
    internal static void Append(RunState run, PermanentPowerCard card)
    {
        if (_slot is null)
        {
            Entry.Logger.Info("[PermanentPower] 存档槽未初始化，无法记录。");
            return;
        }

        var encoded = card.Encode();
        _slot.Modify(run, list => list.Add(encoded));
    }
}
