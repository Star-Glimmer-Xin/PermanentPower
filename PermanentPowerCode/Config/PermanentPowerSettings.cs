namespace PermanentPower.Config;

/// <summary>玩家可编辑的配置，Profile 作用域（每个存档一套）。</summary>
public sealed class PermanentPowerSettings
{
    /// <summary>总开关，关闭后本 mod 不生效。</summary>
    public bool Enabled { get; set; } = true;

    /// <summary>是否对其它 mod 添加的能力牌生效。</summary>
    public bool AffectModCards { get; set; } = true;

    /// <summary>单卡开关，key 为卡牌 id；未出现的 key 默认启用。</summary>
    public Dictionary<string, bool> CardOverrides { get; set; } = [];
}
