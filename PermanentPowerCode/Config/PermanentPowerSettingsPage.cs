using MegaCrit.Sts2.Core.Entities.Cards;
using MegaCrit.Sts2.Core.Models;
using PermanentPower.Localization;
using STS2RitsuLib;
using STS2RitsuLib.Models;
using STS2RitsuLib.Settings;
using STS2RitsuLib.Utils.Persistence;

namespace PermanentPower.Config;

/// <summary>
/// 设置页，通过 RitsuLib 代码流式注册构建。
/// 须在 <c>ModelRegistryInitializedEvent</c> 之后注册，单卡列表依赖 <c>ModelDb.AllCards</c>。
/// </summary>
internal static class PermanentPowerSettingsPage
{
    private const string DataKey = "settings";
    private const string PageId = "main";

    // 特殊分组的稳定 key，显示名另行本地化解析。
    private const string ColorlessKey = "<colorless>";
    private const string UnclassifiedKey = "<unclassified>";

    /// <summary>分类的固定排序，按卡池 id 匹配，与语言无关。</summary>
    private static readonly string[] PoolOrder =
    [
        "IRONCLAD",   // 铁甲战士
        "SILENT",     // 静默猎手
        "REGENT",     // 储君
        "NECRO",      // 亡灵契约师（骨头）
        "DEFECT",     // 故障机器人
    ];

    private static readonly PermanentPowerSettings Fallback = new();

    /// <summary>整份配置的读取绑定。</summary>
    private static ModSettingsValueBinding<PermanentPowerSettings, PermanentPowerSettings>? _root;

    private static bool _pageRegistered;

    /// <summary>当前配置，读取失败时返回默认值。</summary>
    internal static PermanentPowerSettings Current
    {
        get
        {
            try
            {
                if (_root is not null) return _root.Read();
            }
            catch (Exception ex)
            {
                Entry.Logger.Info($"[PermanentPower] 读取配置失败，使用默认值: {ex.GetType().Name}");
            }

            return Fallback;
        }
    }

    /// <summary>判断该能力牌是否启用常驻效果。</summary>
    internal static bool IsCardAllowed(CardModel card)
    {
        try
        {
            var settings = Current;

            if (!settings.Enabled) return false;
            if (!settings.AffectModCards && !IsVanillaCard(card)) return false;

            // 未配置的卡默认启用。
            var id = IdOf(card);
            return !settings.CardOverrides.TryGetValue(id, out var allowed) || allowed;
        }
        catch (Exception ex)
        {
            Entry.Logger.Info($"[PermanentPower] 判定卡牌开关出错，按生效处理: {ex.GetType().Name}");
            return true;
        }
    }

    internal static void Register()
    {
        // 数据可提前注册，页面须等 ModelDb 就绪。
        TryRegisterData();

        RitsuLibFramework.SubscribeLifecycle<ModelRegistryInitializedEvent>(_ => TryRegisterPage());
    }

    // ───────────────────────────── 注册 ─────────────────────────────

    private static void TryRegisterData()
    {
        try
        {
            using (RitsuLibFramework.BeginModDataRegistration(
                       Entry.ModId, initializeProfileIfReady: true))
            {
                RitsuLibFramework.GetDataStore(Entry.ModId).Register(
                    DataKey,
                    "permanent_power_settings",
                    SaveScope.Profile,
                    static () => new PermanentPowerSettings(),
                    autoCreateIfMissing: true);
            }

            Entry.Logger.Info("[PermanentPower] 配置数据已注册。");
        }
        catch (Exception ex)
        {
            Entry.Logger.Info($"[PermanentPower] 配置数据注册失败: {ex.GetType().Name}: {ex.Message}");
        }
    }

    private static void TryRegisterPage()
    {
        if (_pageRegistered) return;
        _pageRegistered = true;

        try
        {
            // 存档可能尚未就绪，此处只建绑定，不立即读取。
            _root = new ModSettingsValueBinding<PermanentPowerSettings, PermanentPowerSettings>(
                Entry.ModId, DataKey, SaveScope.Profile,
                static s => s,
                static (_, _) => { });

            RitsuLibFramework.RegisterModSettings(Entry.ModId, BuildPage, PageId);
            Entry.Logger.Info("[PermanentPower] 配置页已注册。");
        }
        catch (Exception ex)
        {
            Entry.Logger.Info($"[PermanentPower] 配置页注册失败: {ex.GetType().Name}: {ex.Message}");
        }
    }

    // ───────────────────────────── 页面 ─────────────────────────────

    private static void BuildPage(ModSettingsPageBuilder page)
    {
        page.WithTitle(ModSettingsText.Literal("PermanentPower"));

        AddGeneralSection(page);
        AddCardSections(page);
    }

    private static void AddGeneralSection(ModSettingsPageBuilder page)
    {
        page.AddSection("general", section =>
        {
            section.WithTitle(ModText.Text(ModText.SectionGeneral, "通用"));

            section.AddToggle(
                "enabled",
                ModText.Text(ModText.Enabled, "启用跨战斗生效"),
                Field(static s => s.Enabled, static (s, v) => s.Enabled = v),
                ModText.Text(ModText.EnabledHint, "关闭后，能力牌不再跨战斗生效"));

            section.AddToggle(
                "affectModCards",
                ModText.Text(ModText.ModCards, "对 mod 卡牌生效"),
                Field(static s => s.AffectModCards, static (s, v) => s.AffectModCards = v),
                ModText.Text(ModText.ModCardsHint, "关闭后，其它 mod 添加的卡牌不再跨战斗生效"));
        });
    }

    /// <summary>按卡池分组生成「单卡设置」可折叠区块。</summary>
    private static void AddCardSections(ModSettingsPageBuilder page)
    {
        var powerCards = SafeAllPowerCards();
        Entry.Logger.Info($"[PermanentPower] 单卡设置枚举到 {powerCards.Count} 张能力牌。");

        var groups = powerCards
            .GroupBy(GroupKeyOf)
            .OrderBy(static g => OrderIndex(g.Key))
            .ThenBy(static g => DisplayNameOf(g.Key), StringComparer.CurrentCulture)
            .ToList();

        if (groups.Count == 0)
        {
            page.AddSection("cards_empty", section => section
                .WithTitle(ModText.Text(ModText.SectionCards, "单卡设置"))
                .AddParagraph("emptyNote", ModText.Text(ModText.SectionCardsEmpty, "没有找到任何能力牌。")));
            return;
        }

        for (var i = 0; i < groups.Count; i++)
        {
            var groupKey = groups[i].Key;
            var cards = groups[i]
                .OrderBy(static c => SortKeyOfCard(c), StringComparer.CurrentCulture)
                .ToList();

            page.AddSection($"cards_{i}", section =>
            {
                // 标题惰性求值以跟随当前语言。
                section.WithTitle(ModText.CardsSectionTitle(() => DisplayNameOf(groupKey), cards.Count));
                section.Collapsible(startCollapsed: true);

                foreach (var card in cards)
                {
                    var id = IdOf(card);

                    // 使用原生 AddToggle 以保持样式一致。
                    section.AddToggle(
                        "card_" + id.Replace('/', '_'),
                        ModSettingsText.Dynamic(() => card.Title ?? id),
                        Field<bool>(
                            s => s.CardOverrides.TryGetValue(id, out var allowed) ? allowed : true,
                            (s, v) => s.CardOverrides[id] = v));
                }
            });
        }
    }

    // ────────────────────────── 分组与命名 ──────────────────────────

    private static string IdOf(CardModel card) => $"{card.Id.Category}/{card.Id.Entry}";

    /// <summary>判断是否原版卡牌。</summary>
    private static bool IsVanillaCard(CardModel card) =>
        card.GetType().Assembly == typeof(CardModel).Assembly;

    /// <summary>计算分组的稳定 key，无色卡池合并为一项。</summary>
    private static string GroupKeyOf(CardModel card)
    {
        try
        {
            var pool = card.Pool;
            if (pool is null) return UnclassifiedKey;
            if (pool.IsColorless) return ColorlessKey;

            return $"{pool.Id.Category}/{pool.Id.Entry}";
        }
        catch
        {
            return UnclassifiedKey;
        }
    }

    private static int OrderIndex(string groupKey)
    {
        if (groupKey == ColorlessKey) return 1000;
        if (groupKey == UnclassifiedKey) return 1001;

        for (var i = 0; i < PoolOrder.Length; i++)
        {
            if (groupKey.Contains(PoolOrder[i], StringComparison.OrdinalIgnoreCase)) return i;
        }

        return 500; // 其它卡池（含 mod 角色）
    }

    /// <summary>分组 key 转显示名，优先角色名，其次卡池名。</summary>
    private static string DisplayNameOf(string groupKey)
    {
        if (groupKey == ColorlessKey) return ModText.Get(ModText.GroupColorless, "无色");
        if (groupKey == UnclassifiedKey) return ModText.Get(ModText.GroupUnclassified, "未分类");

        try
        {
            var pool = ModelDb.AllCardPools.FirstOrDefault(
                p => $"{p.Id.Category}/{p.Id.Entry}" == groupKey);

            if (pool is not null)
            {
                var character = ModelDb.AllCharacters.FirstOrDefault(
                    c => ReferenceEquals(c.CardPool, pool));

                // 使用 RitsuLib 的标题解析器，以覆盖 mod 角色注册的标题。
                if (character is not null && character.TryResolveTitle(out var characterTitle))
                {
                    var name = characterTitle.GetFormattedText();
                    if (!string.IsNullOrWhiteSpace(name)) return name;
                }

                if (!string.IsNullOrWhiteSpace(pool.Title)) return pool.Title;
            }
        }
        catch (Exception ex)
        {
            Entry.Logger.Info($"[PermanentPower] 解析卡池名失败 ({groupKey}): {ex.GetType().Name}");
        }

        return "未分类";
    }

    private static string SortKeyOfCard(CardModel card)
    {
        try { return card.Title ?? string.Empty; }
        catch { return string.Empty; }
    }

    /// <summary>枚举所有能力牌，失败时返回空表。</summary>
    private static List<CardModel> SafeAllPowerCards()
    {
        try
        {
            return ModelDb.AllCards
                .Where(static c => c.Type == CardType.Power)
                .ToList();
        }
        catch (Exception ex)
        {
            Entry.Logger.Info($"[PermanentPower] 枚举能力牌失败: {ex.GetType().Name}: {ex.Message}");
            return [];
        }
    }

    /// <summary>为配置字段创建绑定，共用同一个 dataKey 与 Profile 作用域。</summary>
    private static IModSettingsValueBinding<TValue> Field<TValue>(
        Func<PermanentPowerSettings, TValue> getter,
        Action<PermanentPowerSettings, TValue> setter)
        => new ModSettingsValueBinding<PermanentPowerSettings, TValue>(
            Entry.ModId, DataKey, SaveScope.Profile, getter, setter);
}
