using System.Reflection;
using HarmonyLib;
using MegaCrit.Sts2.Core.Commands;
using MegaCrit.Sts2.Core.Entities.Players;

namespace PermanentPower.Patches;

/// <summary>
/// 重放期间的结束回合抑制。
/// 部分能力牌的 OnPlay 会调用 <c>PlayerCmd.EndTurn</c>，重放时须跳过。
/// 抑制范围由 <see cref="AsyncLocal{T}" /> 限定，玩家手动出牌不受影响。
/// </summary>
internal static class EndTurnSuppression
{
    /// <summary>是否跳过 EndTurn。</summary>
    private static readonly AsyncLocal<bool> Suppressing = new();

    private static bool _installed;

    /// <summary>安装 <c>PlayerCmd.EndTurn</c> 的前缀补丁，启动时调用一次。</summary>
    internal static void Install()
    {
        if (_installed) return;
        _installed = true;

        try
        {
            var target = typeof(PlayerCmd).GetMethod(
                nameof(PlayerCmd.EndTurn),
                BindingFlags.Public | BindingFlags.Static,
                binder: null,
                types: [typeof(Player), typeof(bool), typeof(Func<Task>)],
                modifiers: null);

            if (target is null)
            {
                Entry.Logger.Info("[PermanentPower] 找不到 PlayerCmd.EndTurn，结束回合抑制未安装。");
                return;
            }

            var prefix = new HarmonyMethod(typeof(EndTurnSuppression).GetMethod(
                nameof(Prefix), BindingFlags.Static | BindingFlags.NonPublic));

            new Harmony(Entry.ModId).Patch(target, prefix: prefix);
            Entry.Logger.Info("[PermanentPower] 结束回合抑制已安装。");
        }
        catch (Exception ex)
        {
            Entry.Logger.Info($"[PermanentPower] 结束回合抑制安装失败: {ex.GetType().Name}: {ex.Message}");
        }
    }

    /// <summary>进入抑制作用域，释放后恢复。支持嵌套。</summary>
    internal static IDisposable Begin() => new Scope();

    [HarmonyPriority(Priority.Low)]
    private static bool Prefix() => !Suppressing.Value;

    private sealed class Scope : IDisposable
    {
        private readonly bool _previous;

        internal Scope()
        {
            _previous = Suppressing.Value;
            Suppressing.Value = true;
        }

        public void Dispose() => Suppressing.Value = _previous;
    }
}
