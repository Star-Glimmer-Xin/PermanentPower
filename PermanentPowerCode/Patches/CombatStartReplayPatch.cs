using System.Reflection;
using HarmonyLib;
using PermanentPower.Permanent;
using MegaCrit.Sts2.Core.Combat;
using MegaCrit.Sts2.Core.Hooks;
using MegaCrit.Sts2.Core.Runs;

namespace PermanentPower.Patches;

/// <summary>
/// 把能力牌重放接到 <c>Hook.BeforeCombatStart</c> 的返回 Task 上。
/// 该 Hook 早于第一个回合的全部开场钩子，重放出的能力因此能被回合开始的遍历正常访问；
/// 游戏会 await 返回 Task，重放期间玩家无法操作。
/// </summary>
internal static class CombatStartReplayPatch
{
    private static bool _installed;

    /// <summary>安装战斗开始 Hook 补丁，启动时调用一次。</summary>
    internal static void Install()
    {
        if (_installed) return;
        _installed = true;

        try
        {
            var target = typeof(Hook).GetMethod(
                nameof(Hook.BeforeCombatStart),
                BindingFlags.Public | BindingFlags.Static,
                binder: null,
                types: [typeof(IRunState), typeof(ICombatState)],
                modifiers: null);

            if (target is null)
            {
                Entry.Logger.Info("[PermanentPower] 找不到 Hook.BeforeCombatStart，重放未接入。");
                return;
            }

            var postfix = new HarmonyMethod(typeof(CombatStartReplayPatch).GetMethod(
                nameof(Postfix), BindingFlags.Static | BindingFlags.NonPublic));

            PatchHost.Harmony.Patch(target, postfix: postfix);
            Entry.Logger.Info("[PermanentPower] 战斗开始重放已接入游戏 Hook 链。");
        }
        catch (Exception ex)
        {
            Entry.Logger.Info($"[PermanentPower] 接入战斗开始 Hook 失败: {ex.GetType().Name}: {ex.Message}");
        }
    }

    /// <summary>用重放链替换返回的 Task，游戏 await 它即会等到重放结束。</summary>
    private static void Postfix(ICombatState combatState, ref Task __result) =>
        __result = ChainAsync(__result, combatState);

    private static async Task ChainAsync(Task original, ICombatState combatState)
    {
        if (original is not null) await original;
        await PermanentPowerService.ReplayAtCombatStartAsync(combatState);
    }
}
