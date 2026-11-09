using System;
using System.Reflection;
using HarmonyLib;
using VRage.Plugins;
using VRage.Utils;

// Define assembly version when compiled by Pulsar
#if !LOCAL_BUILD
[assembly: AssemblyVersion("1.5.4.0")]
[assembly: AssemblyFileVersion("1.5.4.0")]
#endif

namespace PolarsGridMover;

// Init applies the two Harmony patches, the clipboard one for carry mode and the HUD one
// for messages. Everything else happens in GridMoveSession, a session component Pulsar
// registers for every world. Nothing runs per frame here.
// ReSharper disable once UnusedType.Global
public class Plugin : IPlugin
{
    public const string Name = "PolarsGridMover";

    private Harmony harmony;

    // Game log line with the plugin's prefix
    internal static void Log(string message)
    {
        MyLog.Default?.WriteLineAndConsole($"[{Name}] {message}");
    }

    [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.NoInlining)]
    public void Init(object gameInstance)
    {
        harmony = new Harmony(Name);
        CarryMode.Init(harmony);
        HudText.Init(harmony);
    }

    public void Update()
    {
    }

    public void Dispose()
    {
        CarryMode.Dispose();
        HudText.Dispose();
        try
        {
            harmony?.UnpatchAll(Name);
        }
        catch (Exception e)
        {
            Log("Removing the patches failed: " + e);
        }
        harmony = null;
    }
}
