using MegaCrit.Sts2.Core.Entities.Cards;
using MegaCrit.Sts2.Core.Models;
using PermanentPower.Localization;
using STS2RitsuLib;
using STS2RitsuLib.Models;
using STS2RitsuLib.Settings;
using STS2RitsuLib.Utils.Persistence;

namespace PermanentPower.Config;

/// <summary>
/// 设置页。用 RitsuLib 的代码流式注册构建。
///
/// 整页都延到 <c>ModelRegistryInitializedEvent</c> 之后才注册：单卡列表要枚举
/// <c>ModelDb.AllCards</c>，而它在 <c>Entry.Initialize()</c> 阶段还不可用
/// （会抛 <c>KeyNotFoundException: 'CHARACTER.IRONCLAD' not present</c>）。
/// </summary>
internal static class PermanentPowerSettingsPage
{
    private const string DataKey = "settings";
    private const string PageId = "main";

    // 分组的稳定 key（与语言无关）；显示名另行惰性解析。
    private const string ColorlessKey = "<colorless>";
    private const string UnclassifiedKey = "<unclassified>";

    /// <summary>分类固定顺序。按卡池 id 里的英文标识匹配，不受游戏语言影响。</summary>
    private static readonly string[] PoolOrder =
    [
        "IRONCLAD",   // 铁甲战士
        "SILENT",     // 静默猎手
        "REGENT",     // 储君
        "NECRO",      // 亡灵契约师（骨头）
        "DEFECT",     // 故障机器人
    ];

    private static readonly PermanentPowerSettings Fallback = new();

    /// <summary>读整份配置用的绑定；写入由各字段自己的绑定负责。</summary>
    private static ModSettingsValueBinding<PermanentPowerSettings, PermanentPowerSettings>? _root;

    private static bool _pageRegistered;

    /// <summary>当前配置。读取失败时退回默认值，绝不抛异常（会被能力施加路径调用）。</summary>
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

    /// <summary>这张能力牌是否允许触发常驻效果。</summary>
    internal static bool IsCardAllowed(CardModel card)
    {
        try
        {
            var settings = Current;

            if (!settings.Enabled) return false;
            if (!settings.AffectModCards && !IsVanillaCard(card)) return false;

            // 没被显式配置过的卡默认生效。
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
        // 数据可以早注册，页面必须等 ModelDb 就绪。两者各自兜底，互不拖累。
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
            // 此时存档可能还没就绪，所以只建绑定、不立刻 Read()。
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

    /// <summary>「单卡设置」：按卡池（角色 / 无色）分组，每组一个可折叠区块。</summary>
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
                // 标题惰性求值：注册时本地化可能还没就绪，而且卡池名要跟着当前语言走。
                section.WithTitle(ModText.CardsSectionTitle(() => DisplayNameOf(groupKey), cards.Count));
                section.Collapsible(startCollapsed: true);

                foreach (var card in cards)
                {
                    var id = IdOf(card);

                    // 用原生 AddToggle，样式才能和 RitsuLib 其它条目一致。
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

    /// <summary>是不是原版卡牌。用 Assembly 判断，比 id 前缀可靠。</summary>
    private static bool IsVanillaCard(CardModel card) =>
        card.GetType().Assembly == typeof(CardModel).Assembly;

    /// <summary>与语言无关的分组 key。无色卡池统一合并成一个分类。</summary>
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

        return 500; // 其它（含 mod 角色）排在后半段
    }

    /// <summary>
    /// 分组 key → 显示名。优先用角色的本地化名字（<c>CharacterModel.Title</c>），拿不到再退回卡池名。
    /// </summary>
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

                // 用 RitsuLib 的标题解析：它先走「注册过的解析器」、再回落原版模型族，
                // 直接读 character.Title 拿不到 mod 角色注册的标题覆盖。
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

    /// <summary>枚举所有能力牌。任何异常都吞掉并返回空表，避免影响游戏启动。</summary>
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

    /// <summary>为一个字段建绑定。所有字段共用同一个 dataKey + Profile 作用域。</summary>
    private static IModSettingsValueBinding<TValue> Field<TValue>(
        Func<PermanentPowerSettings, TValue> getter,
        Action<PermanentPowerSettings, TValue> setter)
        => new ModSettingsValueBinding<PermanentPowerSettings, TValue>(
            Entry.ModId, DataKey, SaveScope.Profile, getter, setter);
}
