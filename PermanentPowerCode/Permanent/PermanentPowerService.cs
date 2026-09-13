using System.Reflection;
using PermanentPower.Config;
using HarmonyLib;
using MegaCrit.Sts2.Core.Commands;
using MegaCrit.Sts2.Core.Entities.Cards;
using MegaCrit.Sts2.Core.Entities.Creatures;
using MegaCrit.Sts2.Core.GameActions.Multiplayer;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Runs;
using STS2RitsuLib;

namespace PermanentPower.Permanent;

/// <summary>
/// 核心机制：玩家打出的能力牌，其自身效果变成跨战斗常驻。
///
/// 1. 打出能力牌 → 游戏内部调用 <c>PowerCmd.Apply</c> → 我们的 prefix 归因出
///    「这是该能力牌自身的效果」并记录；
/// 2. 同时把这张牌对应的卡组原牌排队等待移除；
/// 3. 之后每场战斗开始（<c>CombatStartingEvent</c>）按记录顺序逐条重放。
/// </summary>
internal static class PermanentPowerService
{
    private const string HarmonyId = "sts2.permanentpower";

    /// <summary>
    /// 待移除的卡组原牌队列。不在施加能力的瞬间直接删 —— 此时卡牌还在结算，
    /// 直接删会和结算竞态；改为排队，等卡牌结算完成 / 战斗结束时确定性排空。
    /// </summary>
    private static readonly List<CardModel> PendingDeckRemoval = [];

    /// <summary>重放期间置位，避免把自己施加的能力又当成「玩家打出的能力牌」记一遍。</summary>
    private static bool _isRestoring;

    internal static void Install()
    {
        PermanentPowerStore.Initialize();
        InstallApplyPatch();
        InstallCombatStartHook();
        InstallRemovalDrain();
    }

    // ───────────────────────────── 记录 ─────────────────────────────

    private static void InstallApplyPatch()
    {
        // 精确匹配非泛型 7 参重载：Apply(ctx, PowerModel, Creature, decimal, Creature, CardModel, bool)
        var target = typeof(PowerCmd)
            .GetMethods(BindingFlags.Public | BindingFlags.Static)
            .FirstOrDefault(m =>
                m.Name == "Apply"
                && !m.IsGenericMethod
                && m.GetParameters().Length == 7
                && m.GetParameters()[0].ParameterType.Name == "PlayerChoiceContext");

        if (target is null)
        {
            Entry.Logger.Info("[PermanentPower] !! 未找到 PowerCmd.Apply 目标重载，机制无法生效");
            return;
        }

        var prefix = typeof(PermanentPowerService).GetMethod(
            nameof(ApplyPrefix), BindingFlags.NonPublic | BindingFlags.Static);

        new Harmony(HarmonyId).Patch(target, prefix: new HarmonyMethod(prefix));
        Entry.Logger.Info($"[PermanentPower] 已挂载能力记录点 -> {target}");
    }

    /// <summary>
    /// 归因三条件（均已实机验证）：一是 <c>cardSource != null</c>，排除「暗淡蓝点」触发的
    /// 「下回合抽 1」那类副作用（实测其 cardSource 为 null）；二是 <c>cardSource.Type ==
    /// CardType.Power</c>，排除攻击牌顺带施加的虚弱 / 易伤；三是 <c>target.IsPlayer</c>，
    /// 只关心施加到玩家自己身上。
    ///
    /// ⚠️ 这个 prefix 挂在全游戏的能力施加路径上，任何异常都会打断游戏流程，必须整体兜住。
    /// </summary>
    private static void ApplyPrefix(
        PowerModel power,
        Creature target,
        decimal amount,
        Creature applier,
        CardModel cardSource)
    {
        try
        {
            if (_isRestoring) return;
            if (power is null || cardSource is null || target is null) return;
            if (cardSource.Type != CardType.Power) return;
            if (!target.IsPlayer) return;
            if (!PermanentPowerSettingsPage.IsCardAllowed(cardSource)) return;

            if (target.CombatState?.RunState is not RunState run) return;

            PermanentPowerStore.Append(run, new PermanentPowerEntry(
                power.Id.Category, power.Id.Entry, (int)amount));

            Entry.Logger.Info(
                $"[PermanentPower] 记录常驻能力 {power.Id.Category}/{power.Id.Entry} x{(int)amount} " +
                $"（来自能力牌「{cardSource.Title}」）");

            EnqueueDeckRemoval(cardSource);
        }
        catch (Exception ex)
        {
            Swallow(ex, nameof(ApplyPrefix));
        }
    }

    /// <summary>把「打出的那一张」对应的卡组原牌排队等待移除；同名卡等各自被打出时再处理。</summary>
    private static void EnqueueDeckRemoval(CardModel cardSource)
    {
        try
        {
            // DeckVersion 指向卡组里的原牌；为 null 说明这张不是从卡组打出的
            // （战斗中临时生成等），按「如果是卡组中的」才移除的规则跳过。
            var deckCard = cardSource.DeckVersion;
            if (deckCard is null)
            {
                Entry.Logger.Info(
                    $"[PermanentPower] 「{cardSource.Title}」无卡组原牌（DeckVersion=null），按规则不删牌");
                return;
            }

            foreach (var pending in PendingDeckRemoval)
            {
                if (ReferenceEquals(pending, deckCard)) return;
            }

            PendingDeckRemoval.Add(deckCard);
            Entry.Logger.Info($"[PermanentPower] 已排队移除卡组原牌「{deckCard.Title}」");
        }
        catch (Exception ex)
        {
            Swallow(ex, nameof(EnqueueDeckRemoval));
        }
    }

    // ───────────────────────────── 移除 ─────────────────────────────

    /// <summary>在「卡牌结算完成」和「战斗结束」两个时机排空移除队列。</summary>
    private static void InstallRemovalDrain()
    {
        // 参数不能叫 _ —— 那样方法体里的 _ = xxx 会被当成给参数赋值，而不是丢弃。
        RitsuLibFramework.SubscribeLifecycle<CardPlayedEvent>(
            _evt => { _ = DrainRemovalsAsync("卡牌结算完成"); });

        RitsuLibFramework.SubscribeLifecycle<CombatEndedEvent>(
            _evt => { _ = DrainRemovalsAsync("战斗结束"); });
    }

    private static async Task DrainRemovalsAsync(string trigger)
    {
        if (PendingDeckRemoval.Count == 0) return;

        try
        {
            var batch = PendingDeckRemoval.ToList();
            PendingDeckRemoval.Clear();

            foreach (var deckCard in batch)
            {
                try
                {
                    await CardPileCmd.RemoveFromDeck(deckCard, showPreview: false);
                    Entry.Logger.Info($"[PermanentPower] 已从卡组移除「{deckCard.Title}」（{trigger}）");
                }
                catch (Exception ex)
                {
                    Swallow(ex, $"RemoveFromDeck「{deckCard.Title}」");
                }
            }
        }
        catch (Exception ex)
        {
            Swallow(ex, nameof(DrainRemovalsAsync));
        }
    }

    // ───────────────────────────── 重放 ─────────────────────────────

    // 实测 CombatStartingEvent 触发时 CombatState 已就绪（Round=1、PlayerCreatures 有内容），
    // 可以直接在这个时机还原能力。
    private static void InstallCombatStartHook() =>
        RitsuLibFramework.SubscribeLifecycle<CombatStartingEvent>(evt => _ = RestoreAsync(evt));

    private static async Task RestoreAsync(CombatStartingEvent evt)
    {
        try
        {
            // 新战斗开始，清掉上一场的残留（正常情况下已在战斗结束时排空）。
            PendingDeckRemoval.Clear();

            var combatState = evt.CombatState;
            if (combatState is null) return;
            if (evt.RunState is not RunState run) return;

            var entries = PermanentPowerStore.Load(run);
            if (entries.Count == 0) return;

            var player = combatState.PlayerCreatures.FirstOrDefault();
            if (player is null) return;

            // ThrowingPlayerChoiceContext 不接受玩家输入，适合程序化施加效果
            // （施加能力本身不需要玩家做选择）。
            var context = new ThrowingPlayerChoiceContext();

            var applied = 0;
            _isRestoring = true;
            try
            {
                foreach (var entry in entries)
                {
                    var canonical = ModelDb.GetByIdOrNull<PowerModel>(
                        new ModelId(entry.Category, entry.Entry));

                    if (canonical is null)
                    {
                        Entry.Logger.Info(
                            $"[PermanentPower] 找不到能力 {entry.Category}/{entry.Entry}，跳过" +
                            "（可能对应的 mod 被卸载了）");
                        continue;
                    }

                    // ModelDb 返回的是 canonical（模板）实例，直接传给 PowerCmd.Apply 会抛
                    // CanonicalModelException，必须先 MutableClone() 出战斗用实例。
                    if (canonical.MutableClone() is not PowerModel power)
                    {
                        Entry.Logger.Info($"[PermanentPower] {entry.Entry} 无法创建可变实例，跳过");
                        continue;
                    }

                    // 按记录顺序原样重放，层数如何叠加完全交给游戏自己的 PowerStackType 决定。
                    await PowerCmd.Apply(context, power, player, entry.Amount, player, null, false);
                    applied++;
                }
            }
            finally
            {
                _isRestoring = false;
            }

            Entry.Logger.Info($"[PermanentPower] 战斗开始，重放常驻能力 {applied}/{entries.Count} 条。");
        }
        catch (Exception ex)
        {
            Swallow(ex, nameof(RestoreAsync));
        }
    }

    // ───────────────────────────── 兜底 ─────────────────────────────

    /// <summary>记日志且绝不重抛 —— 挂进游戏流程的方法都靠它兜住。</summary>
    private static void Swallow(Exception ex, string where)
    {
        try
        {
            Entry.Logger.Info(
                $"[PermanentPower] {where} 出错（已吞掉，不影响游戏）: {ex.GetType().Name}: {ex.Message}");
        }
        catch
        {
            // 连日志都写不进去就只能算了 —— 绝不能往外抛。
        }
    }
}
