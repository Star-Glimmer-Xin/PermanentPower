using System.Reflection;
using HarmonyLib;
using PermanentPower.Permanent;
using MegaCrit.Sts2.Core.Combat;
using MegaCrit.Sts2.Core.Entities.Players;
using MegaCrit.Sts2.Core.GameActions.Multiplayer;
using MegaCrit.Sts2.Core.Hooks;

namespace PermanentPower.Patches;

/// <summary>
/// 把能力牌重放接到 <c>Hook.AfterPlayerTurnStart</c> 的返回 Task 上。
/// 该 Hook 由游戏 await，重放因此与遗物的回合开始效果同样阻塞玩家操作 ——
/// 若发射后不管，玩家会在重放跑完前出牌，卡牌效果与能力效果交错结算。
/// </summary>
internal static class PlayerTurnStartPatch
{
    private static bool _installed;

    /// <summary>安装回合开始 Hook 补丁，启动时调用一次。</summary>
    internal static void Install()
    {
        if (_installed) return;
        _installed = true;

        try
        {
            var target = typeof(Hook).GetMethod(
                nameof(Hook.AfterPlayerTurnStart),
                BindingFlags.Public | BindingFlags.Static,
                binder: null,
                types: [typeof(ICombatState), typeof(PlayerChoiceContext), typeof(Player)],
                modifiers: null);

            if (target is null)
            {
                Entry.Logger.Info("[PermanentPower] 找不到 Hook.AfterPlayerTurnStart，重放将无法阻塞玩家操作。");
                return;
            }

            var postfix = new HarmonyMethod(typeof(PlayerTurnStartPatch).GetMethod(
                nameof(Postfix), BindingFlags.Static | BindingFlags.NonPublic));

            PatchHost.Harmony.Patch(target, postfix: postfix);
            Entry.Logger.Info("[PermanentPower] 回合开始重放已接入游戏 Hook 链。");
        }
        catch (Exception ex)
        {
            Entry.Logger.Info($"[PermanentPower] 接入回合开始 Hook 失败: {ex.GetType().Name}: {ex.Message}");
        }
    }

    /// <summary>
    /// 先于 RitsuLib 的 <c>Priority.Last</c> 后缀执行，使重放排在原版结算之后、
    /// 其它 mod 的 <c>PlayerTurnStartedEvent</c> 订阅者之前。
    /// </summary>
    [HarmonyPriority(Priority.Low)]
    private static void Postfix(
        ICombatState combatState,
        PlayerChoiceContext choiceContext,
        Player player,
        ref Task __result)
    {
        __result = ChainAsync(__result, combatState, choiceContext, player);
    }

    private static async Task ChainAsync(
        Task original,
        ICombatState combatState,
        PlayerChoiceContext choiceContext,
        Player player)
    {
        if (original is not null) await original;
        await PermanentPowerService.ReplayAtTurnStartAsync(combatState, choiceContext, player);
    }
}
