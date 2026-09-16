using HarmonyLib;

namespace PermanentPower.Patches;

/// <summary>本 mod 共用的 Harmony 实例，供各补丁注册与统一卸载。</summary>
internal static class PatchHost
{
    internal static readonly Harmony Harmony = new(Entry.ModId);
}
