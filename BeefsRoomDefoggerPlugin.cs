using BepInEx;
using BepInEx.Configuration;
using BepInEx.Logging;
using HarmonyLib;
using StationeersModProfileLib;
using UnityEngine;

namespace BeefsRoomDefogger
{
    [BepInPlugin(PluginInfo.PLUGIN_GUID, PluginInfo.PLUGIN_NAME, PluginInfo.PLUGIN_VERSION)]
    public class BeefsRoomDefoggerPlugin : BaseUnityPlugin
    {
        public static ConfigEntry<bool> EnableRoomDefogger;
        public static ConfigEntry<float> IndoorFogBuffer;
        public static ConfigEntry<float> AdjustmentSpeed;
        public static ConfigEntry<float> ExtraFog;
        public static ConfigEntry<bool> StormChanges;
        public static ManualLogSource Log;

        private void Awake()
        {
            Log = Logger;
            ModProfiler.SetLogger(Logger);

            EnableRoomDefogger = Config.Bind("General", "EnableRoomDefogger", true,
                "Enable defogger. Disables fogs in sealed/pressurized rooms.");

            IndoorFogBuffer = Config.Bind("General", "IndoorFogBuffer", 8.0f,
                new ConfigDescription("Buffer distance. If you see fog in your main room in an airlock, increase this.", new AcceptableValueRange<float>(0f, 30f)));

            AdjustmentSpeed = Config.Bind("General", "AdjustmentSpeed", 5.0f,
                new ConfigDescription("How fast to adjust the fog. Smaller = slower", new AcceptableValueRange<float>(0.1f, 20f)));

            ExtraFog = Config.Bind("General", "ExtraFog", 0.0f,
                new ConfigDescription("Extra fog distance for outdoor atmospheric scattering. Higher = more foggy outside.", new AcceptableValueRange<float>(0f, 20f)));

            StormChanges = Config.Bind("Weather", "StormChanges", true,
            "Dims the sun during storm and prevent storm particles from spawning inside terrain walled rooms.");

            Log.LogInfo($"Plugin {PluginInfo.PLUGIN_NAME} is loaded!");

            var harmony = new Harmony(PluginInfo.PLUGIN_GUID);
            harmony.PatchAll();
            Log.LogInfo("Patched");
        }
    }
}