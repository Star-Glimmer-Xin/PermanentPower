using System.Reflection;
using PermanentPower.Config;
using MegaCrit.Sts2.Core.Combat;
using MegaCrit.Sts2.Core.Commands;
using MegaCrit.Sts2.Core.Entities.Cards;
using MegaCrit.Sts2.Core.Entities.Creatures;
using MegaCrit.Sts2.Core.Entities.Players;
using MegaCrit.Sts2.Core.GameActions.Multiplayer;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Runs;
using STS2RitsuLib;

namespace PermanentPower.Permanent;

/// <summary>
/// 核心机制：玩家打出的能力牌，其效果变成跨战斗常驻。
///
/// 1. 打出能力牌 → 记下这张牌的 **id 与升级层数**；若它出自卡组，再把卡组原牌移除；
/// 2. 之后每场战斗的**第一个玩家回合开始时**，按记录顺序把每张牌重建出来，让它自己再跑一遍效果。
///
/// 记录不区分来源：卡组打出的、药水给的、其它效果临时生成的能力牌，效果一样永久化。
/// 只有「卡组原牌」才需要（也只能）删 —— 临时牌本来就没有卡组原牌。
///
/// 为什么记「卡牌」而不是记「它施加了什么能力」：能力牌的效果不一定是能力 ——
/// 「扩容」加的是充能球栏位、「暴涨」还会扣一个栏位，只盯着能力层数看会把它们整个漏掉。
/// 重放卡牌自身的效果天然覆盖所有情况，也不用区分「这个改动是谁造成的」。
///
/// 重放时机刻意不用战斗开始：卡牌效果可能依赖 <c>PlayerCombatState</c>
/// （充能球栏位就挂在它上面），战斗刚开场时它未必就绪。
/// </summary>
internal static class PermanentPowerService
{
    /// <summary>重放期间置位，避免重放过程中产生的出牌被再记一遍。</summary>
    private static bool _isReplaying;

    /// <summary>本场战斗是否已经重放过。</summary>
    private static bool _replayedThisCombat;

    /// <summary><c>CardModel.OnPlay</c> 是私有的，只能反射调用。</summary>
    private static readonly MethodInfo? BaseOnPlay = typeof(CardModel)
        .GetMethod("OnPlay", BindingFlags.Instance | BindingFlags.NonPublic);

    internal static void Install()
    {
        PermanentPowerStore.Initialize();
        InstallCardPlayHook();
        InstallCombatHooks();
    }

    // ───────────────────────────── 记录 ─────────────────────────────

    private static void InstallCardPlayHook() =>
        RitsuLibFramework.SubscribeLifecycle<CardPlayedEvent>(
            _evt => { _ = OnCardPlayedAsync(_evt); });

    /// <summary>
    /// 打出能力牌 → 存下这张牌、从卡组移除。
    ///
    /// ⚠️ 挂在游戏出牌流程上，任何异常都会打断游戏，必须整体兜住。
    /// </summary>
    private static async Task OnCardPlayedAsync(CardPlayedEvent evt)
    {
        try
        {
            if (_isReplaying) return;

            var card = evt.CardPlay?.Card;
            if (card is null || card.Type != CardType.Power) return;
            if (!PermanentPowerSettingsPage.IsCardAllowed(card)) return;
            if (evt.CombatState?.RunState is not RunState run) return;

            // 记的是这张牌本身（战斗中的实例就够），**来源无所谓** ——
            // 药水、「创造性 AI」给的能力牌，效果一样要永久化。
            var id = card.CanonicalInstance.Id;
            PermanentPowerStore.Append(run, new PermanentPowerCard(
                id.Category, id.Entry, card.CurrentUpgradeLevel));

            Entry.Logger.Info(
                $"[PermanentPower] 固化能力牌「{card.Title}」" +
                $"（{id.Category}/{id.Entry}，{card.CurrentUpgradeLevel} 级升级）");

            // 只有出自卡组的那一张才需要删；临时生成的牌没有卡组原牌可删。
            if (!TryGetDeckCard(card, out var deckCard))
            {
                Entry.Logger.Info($"[PermanentPower] 「{card.Title}」不是从卡组打出的，无卡组原牌可删");
                return;
            }

            await CardPileCmd.RemoveFromDeck(deckCard!, showPreview: false);
            Entry.Logger.Info($"[PermanentPower] 已从卡组移除「{deckCard!.Title}」");
        }
        catch (Exception ex)
        {
            Swallow(ex, nameof(OnCardPlayedAsync));
        }
    }

    /// <summary>
    /// 只认**卡组里的原牌**。多这几道校验是为了避免「已经不在卡组了还去删」——
    /// 回响形态会把同一张牌再打一次，第二次必须在这里被挡掉，
    /// 否则 <c>RemoveFromDeck</c> 会抛 <c>NullReferenceException</c>。
    /// </summary>
    private static bool TryGetDeckCard(CardModel combatCard, out CardModel? deckCard)
    {
        deckCard = combatCard.DeckVersion;

        return deckCard is not null
            && deckCard.Type == CardType.Power
            && deckCard.Pile?.Type == PileType.Deck
            && deckCard.Owner is { } owner
            && owner.Deck.Cards.Contains(deckCard);
    }

    // ───────────────────────────── 重放 ─────────────────────────────

    private static void InstallCombatHooks()
    {
        // 新战斗开始 → 允许重放一次。
        RitsuLibFramework.SubscribeLifecycle<CombatStartingEvent>(_evt => _replayedThisCombat = false);

        RitsuLibFramework.SubscribeLifecycle<PlayerTurnStartedEvent>(
            _evt => { _ = ReplayAsync(_evt); });
    }

    private static async Task ReplayAsync(PlayerTurnStartedEvent evt)
    {
        try
        {
            if (_replayedThisCombat) return;

            var combatState = evt.CombatState;
            if (combatState?.RunState is not RunState run) return;

            var stored = PermanentPowerStore.Load(run);
            if (stored.Count == 0)
            {
                Entry.Logger.Info("[PermanentPower] 第一个玩家回合：本局还没有固化的卡牌。");
                return;
            }

            _replayedThisCombat = true;

            var applied = 0;
            _isReplaying = true;
            try
            {
                for (var i = 0; i < stored.Count; i++)
                {
                    if (await ReplayOneAsync(combatState, evt.Player, evt.ChoiceContext, stored[i], i)) applied++;
                }
            }
            finally
            {
                _isReplaying = false;
            }

            Entry.Logger.Info($"[PermanentPower] 已重放固化能力牌 {applied}/{stored.Count} 张。");
        }
        catch (Exception ex)
        {
            Swallow(ex, nameof(ReplayAsync));
        }
    }

    private static async Task<bool> ReplayOneAsync(
        ICombatState combatState,
        Player player,
        PlayerChoiceContext context,
        PermanentPowerCard stored,
        int index)
    {
        var canonical = ModelDb.GetByIdOrNull<CardModel>(new ModelId(stored.Category, stored.Entry));
        if (canonical is null)
        {
            Entry.Logger.Info(
                $"[PermanentPower] 找不到卡牌 {stored.Category}/{stored.Entry}，跳过" +
                "（可能是旧版记录，或对应的 mod 被卸载了）");
            return false;
        }

        var card = combatState.CreateCard(canonical, player);
        ApplyUpgradeLevels(card, stored.Upgrades);
        card.SetToFreeThisCombat();

        // 卡牌不在任何牌堆里时效果跑不起来，先临时放进 Play 堆，跑完再撤掉。
        var addedToPlayPile = false;
        if (card.Pile is null)
        {
            await CardPileCmd.Add(card, PileType.Play, skipVisuals: true);
            addedToPlayPile = card.Pile?.Type == PileType.Play;
            if (!addedToPlayPile)
            {
                Entry.Logger.Info($"[PermanentPower] 重放 {stored.Entry} 失败：无法放入 Play 堆");
                return false;
            }
        }

        var play = new CardPlay
        {
            Card = card,
            Player = player,
            Target = PickTarget(card, player),
            ResultPile = PileType.None,
            Resources = new ResourceInfo
            {
                EnergySpent = 0,
                EnergyValue = 0,
                StarsSpent = 0,
                StarValue = 0
            },
            IsAutoPlay = true,
            PlayIndex = 0,
            PlayCount = 1
        };

        context.PushModel(card);
        try
        {
            if (GetOnPlayMethod(card, index) is not { } onPlay)
            {
                return false;
            }

            await (Task)onPlay.Invoke(card, [context, play])!;
            if (!player.Creature.IsDead) card.InvokeExecutionFinished();
        }
        finally
        {
            context.PopModel(card);
            if (addedToPlayPile && card.Pile?.IsCombatPile == true)
            {
                await CardPileCmd.RemoveFromCombat(card, skipVisuals: true);
            }
        }

        Entry.Logger.Info($"[PermanentPower] 已重放能力牌「{card.Title}」");
        return true;
    }

    /// <summary>
    /// 目标只在需要时才挑：能给自己就给自己，否则挑第一个活着的敌人。
    /// 原始目标没法跨战斗保留（那场战斗的敌人早没了），所以这里只能取个确定的猜法。
    /// </summary>
    private static Creature? PickTarget(CardModel card, Player player)
    {
        try
        {
            return card.TargetType switch
            {
                TargetType.AnyEnemy => player.Creature.CombatState?.Enemies
                    .FirstOrDefault(static c => c.IsAlive),
                TargetType.AnyAlly or TargetType.AnyPlayer => player.Creature,
                _ => null
            };
        }
        catch
        {
            return null;
        }
    }

    /// <summary>
    /// 找这张牌最派生的那个 <c>OnPlay</c>：<c>DeclaredOnly</c> 逐层往上，
    /// 拿到的是卡牌自己重写的版本，而不是基类的空实现。
    /// </summary>
    private static MethodInfo? GetOnPlayMethod(CardModel card, int index)
    {
        for (var type = card.GetType(); type is not null; type = type.BaseType)
        {
            var method = type.GetMethod(
                "OnPlay", BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.DeclaredOnly);

            if (method is not null) return method;
        }

        if (BaseOnPlay is null)
        {
            Entry.Logger.Info($"[PermanentPower] 找不到 CardModel.OnPlay，无法重放（第 {index} 张）");
        }

        return BaseOnPlay;
    }

    private static void ApplyUpgradeLevels(CardModel card, int upgrades)
    {
        var count = Math.Clamp(upgrades, 0, card.MaxUpgradeLevel);
        for (var i = 0; i < count; i++)
        {
            card.UpgradeInternal();
            card.FinalizeUpgradeInternal();
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
