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
    /// <summary>mod id，须与 PermanentPower.json 中的 id 一致。</summary>
    public const string ModId = "PermanentPower";

    public static Logger Logger { get; } = new(ModId, LogType.Generic);

    public static void Initialize()
    {
        var assembly = Assembly.GetExecutingAssembly();

        RitsuLibFramework.EnsureGodotScriptsRegistered(assembly, Logger);
        ModTypeDiscoveryHub.RegisterModAssembly(ModId, assembly);

        PermanentPowerService.Install();
        PermanentPowerSettingsPage.Register();

        Logger.Info("PermanentPower initialized.");
    }
}
