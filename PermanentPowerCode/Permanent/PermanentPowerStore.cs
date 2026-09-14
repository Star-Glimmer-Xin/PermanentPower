using MegaCrit.Sts2.Core.Runs;
using STS2RitsuLib.RunData;

namespace PermanentPower.Permanent;

/// <summary>一张已固化的能力牌。存的是**卡牌身份 + 升级层数**，不是「它施加了什么」——
/// 重放时据此把这张牌原样重建、让它自己再跑一遍效果，所以任何效果都能覆盖。</summary>
/// <param name="Category">卡牌的 ModelId.Category，例如 CARD</param>
/// <param name="Entry">卡牌的 ModelId.Entry，例如 CAPACITOR</param>
/// <param name="Upgrades">已升级层数，重建时逐级还原</param>
internal sealed record PermanentPowerCard(string Category, string Entry, int Upgrades)
{
    /// <summary>编码成单个字符串，便于在 run 存档里以 List&lt;string&gt; 形式保存。</summary>
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

/// <summary>
/// 跨战斗存储：把「已经固化的能力牌」存进本局（run）存档。
/// 用 RitsuLib 的 <see cref="RunSavedData{T}"/>，随本局存档一起读写，所以中断后继续游戏也能保留。
/// </summary>
internal static class PermanentPowerStore
{
    private const string SlotKey = "permanentPowerCards";

    private static RunSavedData<List<string>>? _slot;

    /// <summary>在 <c>Entry.Initialize()</c> 里调用一次，注册存档槽位。</summary>
    internal static void Initialize()
    {
        _slot = RunSavedDataStore
            .For(Entry.ModId)
            .Register(SlotKey, static () => new List<string>());

        Entry.Logger.Info("[PermanentPower] 常驻能力存档槽已注册。");
    }

    /// <summary>读出并解码全部固化卡牌。顺序 = 玩家打出的先后顺序，重放依赖这个顺序。</summary>
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

    /// <summary>追加一条记录。不做任何去重 / 合并 —— 玩家打出几张就记几条，忠实重放。</summary>
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
