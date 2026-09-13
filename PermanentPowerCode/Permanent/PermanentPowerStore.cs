using MegaCrit.Sts2.Core.Runs;
using STS2RitsuLib.RunData;

namespace PermanentPower.Permanent;

/// <summary>一条「已常驻的能力牌效果」：玩家打出某张能力牌时，该牌自身效果施加的那个能力。</summary>
/// <param name="Category">能力的 ModelId.Category，例如 POWER</param>
/// <param name="Entry">能力的 ModelId.Entry，例如 VOID_FORM_POWER</param>
/// <param name="Amount">当时的施加数值（不合并、不累加，原样保留供重放）</param>
internal sealed record PermanentPowerEntry(string Category, string Entry, int Amount)
{
    /// <summary>编码成单个字符串，便于在 run 存档里以 List&lt;string&gt; 形式保存。</summary>
    private const char Separator = '|';

    internal string Encode() => $"{Category}{Separator}{Entry}{Separator}{Amount}";

    internal static PermanentPowerEntry? TryDecode(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return null;

        var parts = raw.Split(Separator);
        if (parts.Length != 3) return null;
        if (!int.TryParse(parts[2], out var amount)) return null;

        return new PermanentPowerEntry(parts[0], parts[1], amount);
    }
}

/// <summary>
/// 跨战斗存储：把「已经常驻的能力」存进本局（run）存档。
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

    /// <summary>读出并解码全部常驻能力。顺序 = 玩家打出的先后顺序，重放依赖这个顺序。</summary>
    internal static List<PermanentPowerEntry> Load(RunState run)
    {
        var result = new List<PermanentPowerEntry>();
        if (_slot is null) return result;

        foreach (var raw in _slot.Get(run))
        {
            var entry = PermanentPowerEntry.TryDecode(raw);
            if (entry is not null) result.Add(entry);
        }

        return result;
    }

    /// <summary>追加一条记录。不做任何去重 / 合并 —— 玩家打出几次就记几条，忠实重放。</summary>
    internal static void Append(RunState run, PermanentPowerEntry entry)
    {
        if (_slot is null)
        {
            Entry.Logger.Info("[PermanentPower] 存档槽未初始化，无法记录。");
            return;
        }

        var encoded = entry.Encode();
        _slot.Modify(run, list => list.Add(encoded));
    }
}
