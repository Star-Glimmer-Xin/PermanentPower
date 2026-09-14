using System.Reflection;
using System.Runtime.CompilerServices;
using PermanentPower.Config;
using HarmonyLib;
using MegaCrit.Sts2.Core.Combat;
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
/// 1. 打出能力牌的前后各取一次玩家身上的能力快照，用**差值**归因出「这张牌自身施加了什么」并记录；
/// 2. 同时把这张牌对应的卡组原牌排队等待移除；
/// 3. 之后每场战斗开始（<c>CombatStartingEvent</c>）按记录顺序逐条重放。
///
/// 记录为什么用快照差值而不是 patch <c>PowerCmd.Apply</c>：泛型版 Apply 在「目标身上已有
/// 该能力」时会走叠层短路分支、不转调非泛型版，所以 patch 归因不到 —— 而「已有能力」
/// 正是本 mod 自己的重放造成的，等于自己堵死自己的记录路径。差值只看结果，重复施加同样能归因。
///
/// 但差值分不清「是谁干的」：出牌期间遗物等也会顺带施加能力（例：暗淡蓝点在每回合第 5 张牌时
/// 给「下回合抽 1」，第 5 张恰是能力牌就会被算到它头上）。所以另有一个 patch **只做排除**，
/// 把 <c>cardSource</c> 不是「能力牌自身」的能力从差值里剔掉 —— 它漏看也只是「不排除」，安全。
/// </summary>
internal static class PermanentPowerService
{
    private const string HarmonyId = "sts2.permanentpower";

    /// <summary>
    /// 待移除的卡组原牌队列。不在施加能力的瞬间直接删 —— 此时卡牌还在结算，
    /// 直接删会和结算竞态；改为排队，等卡牌结算完成 / 战斗结束时确定性排空。
    /// </summary>
    private static readonly List<CardModel> PendingDeckRemoval = [];

    /// <summary>出牌前的能力快照，key 是本次出牌（按引用比较，不依赖游戏是否重写了 Equals）。</summary>
    private static readonly Dictionary<CardPlay, Dictionary<string, (ModelId Id, int Amount)>> PlaySnapshots =
        new(ReferenceEqualityComparer.Instance);

    /// <summary>
    /// 上一次结算后的快照。取不到本次出牌的「出牌前」快照时退回它 ——
    /// 出牌是顺序结算的，上一张结算完的状态就是这一张的出牌前状态。
    /// </summary>
    private static Dictionary<string, (ModelId Id, int Amount)>? _lastResolvedSnapshot;

    /// <summary>重放期间置位，避免重放过程被当成玩家的出牌记一笔。</summary>
    private static bool _isRestoring;

    /// <summary>当前正在结算的出牌。为空表示不在出牌窗口内，不做外来效果排除。</summary>
    private static CardPlay? _currentPlay;

    /// <summary>
    /// 本次出牌期间由**别的来源**（遗物等）顺带施加的能力 id。
    /// 差值只能看出「能力变了」，看不出是谁干的，靠这个集合把它们的账剔掉。
    /// </summary>
    private static readonly HashSet<string> _foreignPowers = new(StringComparer.Ordinal);

    internal static void Install()
    {
        PermanentPowerStore.Initialize();
        InstallApplyPatch();
        InstallCardPlayHooks();
        InstallCombatStartHook();
    }

    // ────────────────────────── 外来效果排除 ──────────────────────────

    /// <summary>
    /// 把「不是这张牌自身施加的能力」记下来，供差值阶段剔除。
    ///
    /// ⚠️ 这个 patch <b>不负责记录</b>，只做排除。因为泛型版 Apply 在「目标身上已有该能力」
    /// 时会走叠层短路分支、不经过这里，所以它看到的必然不全 —— 用它记录会漏，
    /// 用它排除却是安全的。
    /// </summary>
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
            Entry.Logger.Info(
                "[PermanentPower] !! 未找到 PowerCmd.Apply 目标重载，外来效果排除将失效（遗物副作用可能被误记）");
            return;
        }

        var prefix = typeof(PermanentPowerService).GetMethod(
            nameof(ApplyPrefix), BindingFlags.NonPublic | BindingFlags.Static);

        new Harmony(HarmonyId).Patch(target, prefix: new HarmonyMethod(prefix));
        Entry.Logger.Info($"[PermanentPower] 已挂载外来效果排除点 -> {target}");
    }

    /// <summary>
    /// <c>cardSource</c> 为 null（遗物 / 能力触发）或不是能力牌（攻击牌顺带施加的虚弱等）的，
    /// 都不是「能力牌自身的效果」，一律记进外来集合。
    ///
    /// ⚠️ 挂在全游戏的能力施加路径上，任何异常都会打断游戏流程，必须整体兜住。
    /// </summary>
    private static void ApplyPrefix(PowerModel power, CardModel cardSource)
    {
        try
        {
            if (_isRestoring || _currentPlay is null || power is null) return;
            if (cardSource is not null && cardSource.Type == CardType.Power) return;

            _foreignPowers.Add(KeyOf(power.Id));
        }
        catch (Exception ex)
        {
            Swallow(ex, nameof(ApplyPrefix));
        }
    }

    // ───────────────────────────── 记录 ─────────────────────────────

    private static void InstallCardPlayHooks()
    {
        // 参数不能叫 _ —— 那样方法体里的 _ = xxx 会被当成给参数赋值，而不是丢弃。
        RitsuLibFramework.SubscribeLifecycle<CardPlayingEvent>(CaptureBeforePlay);

        RitsuLibFramework.SubscribeLifecycle<CardPlayedEvent>(
            _evt => { _ = RecordAfterPlayAsync(_evt); });

        RitsuLibFramework.SubscribeLifecycle<CombatEndedEvent>(
            _evt => { _ = DrainRemovalsAsync("战斗结束"); });
    }

    /// <summary>出牌前记下玩家身上所有能力的层数。非能力牌直接跳过，省一次枚举。</summary>
    private static void CaptureBeforePlay(CardPlayingEvent evt)
    {
        try
        {
            var play = evt.CardPlay;
            if (play is null) return;

            // 每次出牌都重置窗口：外来能力的排除只对本次出牌有效。
            _currentPlay = play;
            _foreignPowers.Clear();

            if (play.Card is null || play.Card.Type != CardType.Power) return;
            if (evt.CombatState is null) return;

            var owner = ResolveOwner(evt.CombatState, play);
            if (owner is null) return;

            PlaySnapshots[play] = SnapshotPowers(owner);
        }
        catch (Exception ex)
        {
            Swallow(ex, nameof(CaptureBeforePlay));
        }
    }

    /// <summary>
    /// 结算后做两件事：用快照差值记录本次施加的能力；把打出的这张能力牌的卡组原牌排队移除。
    ///
    /// 归因条件：打出的必须是允许的能力牌。不再要求「效果施加到玩家自己身上」——
    /// 打出者就是能力的所有者，快照也是照着这个人的能力取的。
    ///
    /// ⚠️ 挂在游戏出牌流程上，任何异常都会打断游戏，必须整体兜住。
    /// </summary>
    private static async Task RecordAfterPlayAsync(CardPlayedEvent evt)
    {
        try
        {
            if (_isRestoring)
            {
                // 不静默跳过：重放期间本不该出现出牌，真出现了要能在日志里看见。
                Entry.Logger.Info("[PermanentPower] 重放期间检测到出牌，跳过记录。");
                return;
            }

            var play = evt.CardPlay;
            if (play is null || play.Card is null) return;

            var card = play.Card;
            if (card.Type != CardType.Power) return;
            if (!PermanentPowerSettingsPage.IsCardAllowed(card)) return;

            if (evt.CombatState is null) return;
            if (evt.CombatState.RunState is not RunState run) return;

            var owner = ResolveOwner(evt.CombatState, play);
            if (owner is null) return;

            RecordGainedPowers(run, play, card, owner);

            // 归因已经算完，关掉出牌窗口，避免后续无关的施加被记成外来效果。
            _currentPlay = null;
            _foreignPowers.Clear();

            EnqueueDeckRemoval(card);
            await DrainRemovalsAsync("卡牌结算完成");
        }
        catch (Exception ex)
        {
            Swallow(ex, nameof(RecordAfterPlayAsync));
        }
    }

    /// <summary>
    /// 快照差值归因：结算后比结算前多出来的层数，就是这张牌自身施加的效果。
    /// 已存在的能力只记增量 —— 重放会先把它施加一遍，那部分不该重复记账。
    /// </summary>
    private static void RecordGainedPowers(RunState run, CardPlay play, CardModel card, Creature owner)
    {
        if (!PlaySnapshots.Remove(play, out var before))
        {
            before = _lastResolvedSnapshot;
            Entry.Logger.Info("[PermanentPower] 未取到本次出牌的出牌前快照，退回上一张结算后的状态。");
        }

        var after = SnapshotPowers(owner);
        _lastResolvedSnapshot = after;

        foreach (var (key, current) in after)
        {
            // 本次出牌期间由遗物等别的来源顺带施加的，不算这张牌的账。
            if (_foreignPowers.Contains(key))
            {
                Entry.Logger.Info(
                    $"[PermanentPower] 跳过 {key}：本次出牌期间它由别的来源施加，不算「{card.Title}」的效果");
                continue;
            }

            if (before is not null && before.TryGetValue(key, out var previous))
            {
                var delta = current.Amount - previous.Amount;
                if (delta > 0) AppendAndLog(run, current.Id, delta, card);
                continue;
            }

            // 新出现的能力，原样记录；0 层的标记型能力重放也没意义，跳过。
            if (current.Amount > 0) AppendAndLog(run, current.Id, current.Amount, card);
        }
    }

    private static string KeyOf(ModelId id) => $"{id.Category}/{id.Entry}";

    private static void AppendAndLog(RunState run, ModelId id, int amount, CardModel card)
    {
        PermanentPowerStore.Append(run, new PermanentPowerEntry(id.Category, id.Entry, amount));

        Entry.Logger.Info(
            $"[PermanentPower] 记录常驻能力 {id.Category}/{id.Entry} x{amount} " +
            $"（来自能力牌「{card.Title}」，run#{RuntimeHelpers.GetHashCode(run)}）");
    }

    /// <summary>取玩家身上全部能力的总层数。同一能力可能有多个实例（施加者不同），按 id 求和。</summary>
    private static Dictionary<string, (ModelId Id, int Amount)> SnapshotPowers(Creature owner)
    {
        var map = new Dictionary<string, (ModelId Id, int Amount)>(StringComparer.Ordinal);

        foreach (var power in owner.GetPowerInstances<PowerModel>())
        {
            var id = power.Id;
            var key = KeyOf(id);

            map[key] = map.TryGetValue(key, out var sum)
                ? (sum.Id, sum.Amount + power.Amount)
                : (id, power.Amount);
        }

        return map;
    }

    /// <summary>本次出牌是谁打的就取谁的能力 —— 多人模式下不能一律取第一个玩家。</summary>
    private static Creature? ResolveOwner(ICombatState combatState, CardPlay play)
    {
        foreach (var creature in combatState.PlayerCreatures)
        {
            if (ReferenceEquals(creature.Player, play.Player)) return creature;
        }

        return combatState.PlayerCreatures.FirstOrDefault();
    }

    /// <summary>把「打出的那一张」对应的卡组原牌排队等待移除；同名卡等各自被打出时再处理。</summary>
    private static void EnqueueDeckRemoval(CardModel card)
    {
        try
        {
            // DeckVersion 指向卡组里的原牌；为 null 说明这张不是从卡组打出的
            // （战斗中临时生成等），按「如果是卡组中的」才移除的规则跳过。
            var deckCard = card.DeckVersion;
            if (deckCard is null)
            {
                Entry.Logger.Info(
                    $"[PermanentPower] 「{card.Title}」无卡组原牌（DeckVersion=null），按规则不删牌");
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
            PlaySnapshots.Clear();
            _lastResolvedSnapshot = null;
            _currentPlay = null;
            _foreignPowers.Clear();

            var combatState = evt.CombatState;
            if (combatState is null) return;

            // 与记录时取同一个来源的 RunState —— 若两个来源不是同一实例，
            // 写进去的和读出来的就不是一份存档（实测遇到过重放条数偏少）。
            var run = combatState.RunState as RunState ?? evt.RunState as RunState;
            if (run is null) return;

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

            Entry.Logger.Info(
                $"[PermanentPower] 战斗开始，重放常驻能力 {applied}/{entries.Count} 条" +
                $"（run#{RuntimeHelpers.GetHashCode(run)}）。");
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
