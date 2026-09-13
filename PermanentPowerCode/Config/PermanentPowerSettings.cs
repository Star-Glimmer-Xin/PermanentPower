namespace PermanentPower.Config;

/// <summary>玩家可编辑的配置（Profile 作用域 = 每个存档一套）。</summary>
public sealed class PermanentPowerSettings
{
    /// <summary>总开关。关闭后本 mod 完全不起作用。</summary>
    public bool Enabled { get; set; } = true;

    /// <summary>是否对其它 mod 添加的能力牌生效。</summary>
    public bool AffectModCards { get; set; } = true;

    /// <summary>
    /// 单卡开关，key = 卡牌 id（"分类/条目"）。
    /// 只记录被显式关掉的卡，未出现的默认生效 —— 避免存档里堆几百条。
    /// </summary>
    public Dictionary<string, bool> CardOverrides { get; set; } = [];
}
