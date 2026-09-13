using System.Reflection;
using PermanentPower.Config;
using PermanentPower.Permanent;
using MegaCrit.Sts2.Core.Logging;
using MegaCrit.Sts2.Core.Modding;
using STS2RitsuLib;
using STS2RitsuLib.Interop;
using Logger = MegaCrit.Sts2.Core.Logging.Logger;

namespace PermanentPower;

[ModInitializer(nameof(Initialize))]
public partial class Entry
{
    /// <summary>需与 PermanentPower.json 里的 id 一致。</summary>
    public const string ModId = "PermanentPower";

    public static Logger Logger { get; } = new(ModId, LogType.Generic);

    public static void Initialize()
    {
        var assembly = Assembly.GetExecutingAssembly();

        // Godot 脚本注册（让 pck 里的脚本类型能被 Godot 找到）和 RitsuLib 的内容自动注册
        // 是两件事，两个都要保留。
        RitsuLibFramework.EnsureGodotScriptsRegistered(assembly, Logger);

        // 扫描本程序集里的 RegisterCard / RegisterRelic 等 attribute 完成内容注册。
        ModTypeDiscoveryHub.RegisterModAssembly(ModId, assembly);

        PermanentPowerService.Install();
        PermanentPowerSettingsPage.Register();

        Logger.Info("PermanentPower initialized.");
    }
}
