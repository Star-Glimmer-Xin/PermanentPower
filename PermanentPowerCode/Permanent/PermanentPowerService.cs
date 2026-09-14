using System.Reflection;
using PermanentPower.Config;
using PermanentPower.Patches;
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
/// 核心机制：打出的能力牌效果跨战斗常驻。
/// 记录卡牌 id 与升级层数，并移除卡组中的原牌；每场战斗的首个玩家回合按记录顺序重放。
/// </summary>
internal static class PermanentPowerService
{
    /// <summary>重放进行中。用于避免重放产生的出牌被重复记录。</summary>
    private static bool _isReplaying;

    /// <summary>本场战斗是否已重放。</summary>
    private static bool _replayedThisCombat;

    /// <summary>基类 <c>OnPlay</c>，仅在派生类未重写时使用。</summary>
    private static readonly MethodInfo? BaseOnPlay = typeof(CardModel)
        .GetMethod("OnPlay", BindingFlags.Instance | BindingFlags.NonPublic);

    internal static void Install()
    {
        PermanentPowerStore.Initialize();
        EndTurnSuppression.Install();
        InstallCardPlayHook();
        InstallCombatHooks();
    }

    // ───────────────────────────── 记录 ─────────────────────────────

    private static void InstallCardPlayHook() =>
        RitsuLibFramework.SubscribeLifecycle<CardPlayedEvent>(
            _evt => { _ = OnCardPlayedAsync(_evt); });

    /// <summary>打出能力牌时记录该牌，并移除对应的卡组原牌。</summary>
    private static async Task OnCardPlayedAsync(CardPlayedEvent evt)
    {
        try
        {
            if (_isReplaying) return;

            var card = evt.CardPlay?.Card;
            if (card is null || card.Type != CardType.Power) return;
            if (!PermanentPowerSettingsPage.IsCardAllowed(card)) return;
            if (evt.CombatState?.RunState is not RunState run) return;

            var id = card.CanonicalInstance.Id;
            PermanentPowerStore.Append(run, new PermanentPowerCard(
                id.Category, id.Entry, card.CurrentUpgradeLevel));

            Entry.Logger.Info(
                $"[PermanentPower] 固化能力牌「{card.Title}」" +
                $"（{id.Category}/{id.Entry}，{card.CurrentUpgradeLevel} 级升级）");

            // 仅出自卡组的牌存在卡组原牌。
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

        // 无牌堆时效果无法执行，临时放入 Play 堆，重放结束后移除。
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

            // 跳过卡牌自带的结束回合。
            using (EndTurnSuppression.Begin())
            {
                await (Task)onPlay.Invoke(card, [context, play])!;
                if (!player.Creature.IsDead) card.InvokeExecutionFinished();
            }
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

    /// <summary>按目标类型选取重放目标：己方为自身，敌方为第一个存活敌人。</summary>
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

    /// <summary>沿继承链查找最派生的 <c>OnPlay</c> 实现。</summary>
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

    /// <summary>记录异常并吞掉，不向外抛出。</summary>
    private static void Swallow(Exception ex, string where)
    {
        try
        {
            Entry.Logger.Info(
                $"[PermanentPower] {where} 出错（已吞掉，不影响游戏）: {ex.GetType().Name}: {ex.Message}");
        }
        catch
        {
            // 日志写入失败时同样不得外抛。
        }
    }
}
