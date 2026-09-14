using System.Reflection;
using STS2RitsuLib.Settings;
using STS2RitsuLib.Utils;

namespace PermanentPower.Localization;

/// <summary>设置页文案表，文案以 JSON 内嵌资源形式编入 dll。</summary>
internal static class ModText
{
    private const string ResourceFolder = "PermanentPower.Localization";

    private static readonly I18N Table = new(
        instanceName: "PermanentPower",
        resourceFolders: [ResourceFolder],
        resourceAssembly: Assembly.GetExecutingAssembly());

    // 键名带 pp. 前缀以避免跨 mod 冲突，发布后不可更改。
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

    /// <summary>取一条设置页文案。</summary>
    internal static ModSettingsText Text(string key, string fallback) =>
        ModSettingsText.I18N(Table, key, fallback);

    /// <summary>取一条纯文本文案，不交由设置页渲染。</summary>
    internal static string Get(string key, string fallback) => Table.Get(key, fallback);

    /// <summary>
    /// 构造分组标题文案，如「铁甲战士（23）」。
    /// 分组名以工厂传入，确保切换语言后重新求值。
    /// </summary>
    internal static ModSettingsText CardsSectionTitle(Func<string> groupName, int count) =>
        ModSettingsText.Dynamic(
            () => string.Format(Table.Get(SectionCardsTitle, "{0} ({1})"), groupName(), count));
}
