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

        private readonly List<UpdatePopupItem> _allPopups = new List<UpdatePopupItem>();
        private Queue<UpdatePopupItem> _popupQueue;
        private UpdatePopupItem _currentPopup;

        private Rect _popupRect;
        private Vector2 _scrollPos;
        private bool _showPopup = false;
        private bool _pendingShow = false;
        private float _guiScale = 1.0f;
        private int _lastScreenHeight = 0;
        private int _lastScreenWidth = 0;
        private bool _popupRectInitialized = false;

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
                "- Disabled storm particle changes (fog effect remains stays) in multiplayer until replication is resolved\n" +
                "Changelog v1.1.2:\n" +
                "- Fixed bug causing error in coroutine" +
                "- Stopped logging room updates" +
                "- Added scaling for the update popup",
                defaultSeen: false);

            _popupQueue = new Queue<UpdatePopupItem>();
            foreach (var p in _allPopups)
            {
                if (!p.Config.Value)
                {
                    _popupQueue.Enqueue(p);
                }
            }

            _pendingShow = _popupQueue.Count > 0;

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
            _allPopups.Add(item);
            return cfg;
        }

        private void Update()
        {
            if (_pendingShow && _currentPopup == null && _popupQueue != null && _popupQueue.Count > 0 &&
                IsInGameWorld())
            {
                if (popupDelay > 0f)
                {
                    popupDelay -= Time.deltaTime;
                    return;
                }
                _currentPopup = _popupQueue.Dequeue();
                StartShowingPopup(_currentPopup);
            }

            if (_showPopup && !IsInGameWorld())
            {
                _showPopup = false;
            }

            if (_popupRectInitialized && (Screen.height != _lastScreenHeight || Screen.width != _lastScreenWidth))
            {
                _popupRectInitialized = false;
                _lastScreenHeight = Screen.height;
                _lastScreenWidth = Screen.width;
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
            _popupRectInitialized = false;
            _scrollPos = Vector2.zero;
            _showPopup = true;
        }

        private void InitializePopupRect()
        {
            if (_popupRectInitialized) return;

            float screenHeight = Screen.height;
            float screenWidth = Screen.width;
            float baseHeight = 1080f;

            _guiScale = Mathf.Max(1.0f, screenHeight / baseHeight);

            float baseWidth = 520f;
            float basePopupHeight = 320f;

            float scaledWidth = baseWidth * _guiScale;
            float scaledHeight = basePopupHeight * _guiScale;

            _popupRect = new Rect((screenWidth - scaledWidth) / 2f, (screenHeight - scaledHeight) / 2f, scaledWidth, scaledHeight);
            _lastScreenHeight = (int)screenHeight;
            _lastScreenWidth = (int)screenWidth;
            _popupRectInitialized = true;
        }

        private void OnGUI()
        {
            Color oldColor = GUI.color;
            if (!_showPopup || _currentPopup == null) return;
            if (!IsInGameWorld()) return;

            if (!_popupRectInitialized)
                InitializePopupRect();

            Matrix4x4 oldMatrix = GUI.matrix;
            GUI.matrix = Matrix4x4.Scale(new Vector3(_guiScale, _guiScale, 1.0f));

            Rect scaledRect = new Rect(
                _popupRect.x / _guiScale,
                _popupRect.y / _guiScale,
                _popupRect.width / _guiScale,
                _popupRect.height / _guiScale
            );

            GUI.color = Color.white;
            GUI.backgroundColor = Color.red;
            scaledRect = GUI.ModalWindow(987987987, scaledRect, DrawPopupWindow, _currentPopup.Title);

            _popupRect = new Rect(
                scaledRect.x * _guiScale,
                scaledRect.y * _guiScale,
                scaledRect.width * _guiScale,
                scaledRect.height * _guiScale
            );

            GUI.matrix = oldMatrix;
            GUI.color = oldColor;
        }

        private void DrawPopupWindow(int id)
        {
            GUILayout.BeginVertical();
            var wrapStyle = new GUIStyle(GUI.skin.label) { wordWrap = true };

            float scaledHeight = (_popupRect.height / _guiScale) - 80;
            _scrollPos = GUILayout.BeginScrollView(_scrollPos, GUILayout.Height(scaledHeight));
            GUILayout.Label(_currentPopup.Changelog, wrapStyle, GUILayout.ExpandWidth(true));
            GUILayout.EndScrollView();

            GUILayout.Space(8);

            GUILayout.BeginHorizontal();
            GUILayout.FlexibleSpace();
            if (GUILayout.Button("OK - Don't show this update again", GUILayout.Height(30), GUILayout.Width(250)))
            {
                _currentPopup.Config.Value = true;
                Config.Save();
                if (_popupQueue.Count > 0)
                {
                    _currentPopup = _popupQueue.Dequeue();
                    _scrollPos = Vector2.zero;
                    _popupRectInitialized = false;
                    _showPopup = true;
                }
                else
                {
                    _currentPopup = null;
                    _showPopup = false;
                    _pendingShow = false;
                }
            }

            GUILayout.FlexibleSpace();
            GUILayout.EndHorizontal();

            GUILayout.EndVertical();
            GUI.DragWindow();
        }
    }
}