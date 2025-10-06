using System.Collections.Generic;
using BepInEx;
using BepInEx.Configuration;
using BepInEx.Logging;
using HarmonyLib;
using StationeersModProfileLib;
using UnityEngine;
using UnityEngine.SceneManagement;

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

        // hidden configs for popups
        private float popupDelay = 1.5f;
        public static ConfigEntry<bool> Update1_1_0_Popup;

        private class UpdatePopupItem
        {
            public string Key;
            public string Title;
            public string Changelog;
            public ConfigEntry<bool> Config;
        }

        private readonly List<UpdatePopupItem> allPopups = new List<UpdatePopupItem>();
        private Queue<UpdatePopupItem> popupQueue;
        private UpdatePopupItem currentPopup;

        private Rect popupRect;
        private Vector2 scrollPos;
        private bool showPopup = false;
        private bool pendingShow = false;

        private void Awake()
        {
            Log = Logger;
            ModProfiler.SetLogger(Logger);

            EnableRoomDefogger = Config.Bind("General", "EnableRoomDefogger", true,
                "Enable defogger. Disables fogs in sealed/pressurized rooms.");

            IndoorFogBuffer = Config.Bind("General", "IndoorFogBuffer", 8.0f,
                new ConfigDescription("Buffer distance. If you see fog in your main room in an airlock, increase this.",
                    new AcceptableValueRange<float>(0f, 30f)));

            AdjustmentSpeed = Config.Bind("General", "AdjustmentSpeed", 5.0f,
                new ConfigDescription("How fast to adjust the fog. Smaller = slower",
                    new AcceptableValueRange<float>(0.1f, 20f)));

            ExtraFog = Config.Bind("General", "ExtraFog", 0.0f,
                new ConfigDescription(
                    "Extra fog distance for outdoor atmospheric scattering. Higher = more foggy outside.",
                    new AcceptableValueRange<float>(0f, 20f)));

            StormChanges = Config.Bind("General", "StormChanges", true,
                "Dims the sun during storm and prevent storm particles from spawning inside terrain walled rooms.");

            Update1_1_0_Popup = AddUpdatePopup(
                "Update1_1_0_Popup",
                "Beef's Room Defogger was Updated to v1.1.1!",
                "Changelog v1.1.0:\n" +
                "- Improved handling of terrain-walled rooms during storms. Now your closed-in/pressurized tunnels won't get storm particles\n" +
                "- Overrides the storm fog effect now as well\n" +
                "- Added sun dimming for storms (minus solar storm of course)\n\n" +
                "Changelog v1.1.1:\n" +
                "- Disabled storm particle changes (fog effect remains stays) in multiplayer until replication is resolved",
                defaultSeen: false);

            popupQueue = new Queue<UpdatePopupItem>();
            foreach (var p in allPopups)
            {
                if (!p.Config.Value)
                {
                    popupQueue.Enqueue(p);
                }
            }

            pendingShow = popupQueue.Count > 0;

            Log.LogInfo($"Plugin {PluginInfo.PLUGIN_NAME} is loaded!");

            var harmony = new Harmony(PluginInfo.PLUGIN_GUID);
            harmony.PatchAll();
            Log.LogInfo("Patched");
        }

        private ConfigEntry<bool> AddUpdatePopup(string key, string title, string changelog, bool defaultSeen = false)
        {
            var cfg = Config.Bind("NO TOUCH - Internal", key, defaultSeen, new ConfigDescription(""));
            var item = new UpdatePopupItem
            {
                Key = key,
                Title = title,
                Changelog = changelog,
                Config = cfg
            };
            allPopups.Add(item);
            return cfg;
        }

        private void Update()
        {
            if (pendingShow && currentPopup == null && popupQueue != null && popupQueue.Count > 0 &&
                IsInGameWorld())
            {
                if (popupDelay > 0f)
                {
                    popupDelay -= Time.deltaTime;
                    return;
                }
                currentPopup = popupQueue.Dequeue();
                StartShowingPopup(currentPopup);
            }

            if (showPopup && !IsInGameWorld())
            {
                showPopup = false;
            }
        }

        public bool IsInGameWorld()
        {
            string sceneName = UnityEngine.SceneManagement.SceneManager.GetActiveScene().name;
            string lowerSceneName = sceneName.ToLower();
            bool isMenu = lowerSceneName.Contains("menu") || lowerSceneName.Contains("splash");
            bool isGameWorld = !isMenu;
            return isGameWorld;
        }

        private void StartShowingPopup(UpdatePopupItem item)
        {
            float w = 520f, h = 320f;
            popupRect = new Rect((Screen.width - w) / 2f, (Screen.height - h) / 2f, w, h);
            scrollPos = Vector2.zero;
            showPopup = true;
        }

        private void OnGUI()
        {
            Color oldColor = GUI.color;
            if (!showPopup || currentPopup == null) return;
            if (!IsInGameWorld()) return;
            GUI.color = Color.white;
            GUI.backgroundColor = Color.red;
            popupRect = GUI.ModalWindow(987987987, popupRect, DrawPopupWindow, currentPopup.Title);
            GUI.color = oldColor;
        }

        private void DrawPopupWindow(int id)
        {
            GUILayout.BeginVertical();
            var wrapStyle = new GUIStyle(GUI.skin.label) { wordWrap = true };
            scrollPos = GUILayout.BeginScrollView(scrollPos, GUILayout.Height(popupRect.height - 80));
            GUILayout.Label(currentPopup.Changelog, wrapStyle, GUILayout.ExpandWidth(true));
            GUILayout.EndScrollView();

            GUILayout.Space(8);

            GUILayout.BeginHorizontal();
            GUILayout.FlexibleSpace();
            if (GUILayout.Button("OK - Don't show this update again", GUILayout.Height(30), GUILayout.Width(250)))
            {
                currentPopup.Config.Value = true;
                Config.Save();
                if (popupQueue.Count > 0)
                {
                    currentPopup = popupQueue.Dequeue();
                    scrollPos = Vector2.zero;
                    float w = 520f, h = 320f;
                    popupRect = new Rect((Screen.width - w) / 2f, (Screen.height - h) / 2f, w, h);
                    showPopup = true;
                }
                else
                {
                    currentPopup = null;
                    showPopup = false;
                    pendingShow = false;

                }
            }

            GUILayout.FlexibleSpace();
            GUILayout.EndHorizontal();

            GUILayout.EndVertical();
            GUI.DragWindow();
        }
    }
}