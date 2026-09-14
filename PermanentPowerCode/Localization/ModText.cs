using System.Reflection;
using STS2RitsuLib.Settings;
using STS2RitsuLib.Utils;

namespace PermanentPower.Localization;

/// <summary>
/// 设置页的文案表。JSON 以**内嵌资源**编进 dll —— 本 mod 不发 pck，
/// 散装文件根本到不了玩家机器上，而 RitsuLib 的 <see cref="I18N" /> 支持从内嵌资源读。
///
/// 资源名约定（读 RitsuLib 的 I18N.cs 源码核实）：必须是
/// <c>{resourceFolder}.{语言}.json</c>，且**语言那一段里不能再出现点** ——
/// 所以 JSON 不能放子目录，csproj 里必须用 <c>LogicalName</c> 把路径拍平。
/// 非英语会自动回退到 eng。
/// </summary>
internal static class ModText
{
    private const string ResourceFolder = "PermanentPower.Localization";

    private static readonly I18N Table = new(
        instanceName: "PermanentPower",
        resourceFolders: [ResourceFolder],
        resourceAssembly: Assembly.GetExecutingAssembly());

    // 键名带 pp. 前缀，避免和别的 mod 撞。发布后不要改 —— 玩家存档里的界面元数据引用它们。
    internal const string SectionGeneral = "pp.section.general";
    internal const string Enabled = "pp.toggle.enabled";
    internal const string EnabledHint = "pp.toggle.enabled.hint";
    internal const string ModCards = "pp.toggle.modcards";
    internal const string ModCardsHint = "pp.toggle.modcards.hint";
    internal const string SectionCards = "pp.section.cards";
    internal const string SectionCardsEmpty = "pp.section.cards.empty";
    internal const string GroupColorless = "pp.group.colorless";
    internal const string GroupUnclassified = "pp.group.unclassified";

    private const string SectionCardsTitle = "pp.section.cards.title";

    /// <summary>取一条文案。fallback 只在连 eng 都缺这个键时才用得上。</summary>
    internal static ModSettingsText Text(string key, string fallback) =>
        ModSettingsText.I18N(Table, key, fallback);

    /// <summary>取一条纯文本（不交给设置页渲染，比如分组名）。</summary>
    internal static string Get(string key, string fallback) => Table.Get(key, fallback);

    /// <summary>
    /// 带占位符的文案，如「铁甲战士（23）」。
    ///
    /// ⚠️ 分组名要**传工厂而不是字符串** —— 卡池名来自游戏的 <c>LocString</c>，
    /// 在 builder 里提前求值会把它按当时的语言固化；切换语言时 RitsuLib 只重算
    /// <c>Dynamic</c> 的文本、不会重跑 builder，于是名字会一直停在旧语言。
    /// </summary>
    internal static ModSettingsText CardsSectionTitle(Func<string> groupName, int count) =>
        ModSettingsText.Dynamic(
            () => string.Format(Table.Get(SectionCardsTitle, "{0} ({1})"), groupName(), count));
}
