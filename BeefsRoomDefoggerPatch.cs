using System;
using Assets.Scripts;
using Assets.Scripts.Atmospherics;
using Assets.Scripts.GridSystem;
using Assets.Scripts.Networking;
using Assets.Scripts.Objects.Entities;
using Assets.Scripts.Util;
using HarmonyLib;
using UnityEngine;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using StationeersModProfileLib;

namespace BeefsRoomDefogger
{
    public static class BeefsRoomController
    {
        private static bool _sealingCheckInProgress = false;
        private static double _worldTemperatureRange = 100.0;

        public static readonly Dictionary<long, RoomSealingInfo> RoomSealingCache = new Dictionary<long, RoomSealingInfo>();
        private static readonly Dictionary<Grid3, Atmosphere> TraversalAtmosphereCache = new Dictionary<Grid3, Atmosphere>();
        private static readonly Dictionary<Grid3, Room> TraversalRoomCache = new Dictionary<Grid3, Room>();

        private const int MaxTraversalDepth = 3; // How many room connections to check
        private const float CacheExpirySeconds = 600f; // 10min
        private const int MaxIterations = 50; //max loops for traverse

        public enum RoomVentingState
        {
            Sealed,
            Venting
        }

        public struct RoomSealingInfo
        {
            public readonly RoomVentingState VentingState;
            public readonly float SimilarityRatio;
            public readonly float LastChecked;
            public readonly int TraversalDepth;

            public RoomSealingInfo(RoomVentingState ventingState, float similarityRatio, float lastChecked, int depth)
            {
                VentingState = ventingState;
                SimilarityRatio = similarityRatio;
                LastChecked = lastChecked;
                TraversalDepth = depth;
            }

            public bool IsStale => Time.time - LastChecked > CacheExpirySeconds;
        }

        public static void Initialize()
        {
            _worldTemperatureRange = WorldTempRange();
            BeefsRoomDefoggerPlugin.Log.LogInfo($"BeefsRoomController initialized with temp range: {_worldTemperatureRange:F1}K");
        }

        public static Room GetRoom()
        {
            try
            {
                var localHuman = Human.LocalHuman;
                if (localHuman == null) return null;

                var playerPos = localHuman.Transform.position;
                var adjustedPos = playerPos + Vector3.up * 0.25f; // add .25 transform otherwise we fall thru floor sometimes
                var playerGrid = new WorldGrid(adjustedPos);
                var room = RoomController.World?.GetRoom(playerGrid);

                return room;
            }
            catch (System.Exception ex)
            {
                BeefsRoomDefoggerPlugin.Log.LogError($"Error getting room: {ex.Message}");
                return null;
            }
        }

        public static RoomSealingInfo? GetCachedRoomState(long roomId)
        {
            if (RoomSealingCache.TryGetValue(roomId, out var info))
                return info;
            return null;
        }

        public static void ScheduleRoomCheck(Room room, MonoBehaviour caller)
        {
            if (room == null || _sealingCheckInProgress) return;
            caller.StartCoroutine(CheckRoomStateCoroutine(room));
        }

        public static void CleanupCache()
        {
            var staleRooms = new List<long>();
            foreach (var kvp in RoomSealingCache)
            {
                if (kvp.Value.IsStale)
                {
                    var room = RoomController.Get(kvp.Key);
                    if (room == null)
                    {
                        staleRooms.Add(kvp.Key);
                    }
                }
            }

            foreach (var roomId in staleRooms)
            {
                RoomSealingCache.Remove(roomId);
            }
        }

        public static void ClearCache()
        {
            RoomSealingCache.Clear();
            TraversalAtmosphereCache.Clear();
            TraversalRoomCache.Clear();
            _sealingCheckInProgress = false;
        }

        private static IEnumerator CheckRoomStateCoroutine(Room room)
        {
            if (_sealingCheckInProgress) yield break;
            _sealingCheckInProgress = true;

            Atmosphere roomAtmos = null;

            foreach (var grid in room.Grids)
            {
                roomAtmos = AtmosphericsController.World.GetAtmosphereLocal(grid);
                if (roomAtmos != null) break;
                if (room.Grids.IndexOf(grid) % 10 == 0)
                    yield return null;
            }

            RoomVentingState ventingState = RoomVentingState.Sealed;
            float similarityRatio = 0.0f;

            if (roomAtmos != null)
            {
                yield return IsAtmosphereGroupVentingCoroutine(roomAtmos, (state, similarity) => {
                    ventingState = state;
                    similarityRatio = similarity;
                });
            }

            RoomSealingCache[room.RoomId] = new RoomSealingInfo(ventingState, similarityRatio, Time.time, 1);
            _sealingCheckInProgress = false;
        }

        private static IEnumerator IsAtmosphereGroupVentingCoroutine(
            Atmosphere startAtmos,
            System.Action<RoomVentingState, float> callback)
        {
            // ~0.09ms but is coroutine so is fine
            // using var _ = ModProfiler.Profile();
            if (startAtmos?.Room == null)
            {
                callback(RoomVentingState.Venting, 1.0f);
                yield break;
            }

            TraversalAtmosphereCache.Clear();
            TraversalRoomCache.Clear();

            var visitedRooms = new HashSet<long>();
            var roomsToCheck = new Queue<(Room room, int depth)>();
            var connectedRoomGroup = new HashSet<long>();

            var startRoom = startAtmos.Room;
            roomsToCheck.Enqueue((startRoom, 0));
            visitedRooms.Add(startRoom.RoomId);
            connectedRoomGroup.Add(startRoom.RoomId);

            int maxIterations = MaxIterations;
            int currentIteration = 0;
            int iterationsSinceYield = 0;
            bool foundVenting = false;

            // I know it looks bad (double foreach in a while loop) but it's not so bad i promise i checked!
            while (roomsToCheck.Count > 0 && currentIteration < maxIterations)
            {
                // ~0.01ms avg to 0.03 worst case
                // using var _2 = ModProfiler.ProfileSubsection("Check Neighbors");
                currentIteration++;
                iterationsSinceYield++;

                if (iterationsSinceYield >= 10)
                {
                    iterationsSinceYield = 0;
                    yield return null;
                }

                var (currentRoom, currentDepth) = roomsToCheck.Dequeue();

                foreach (var grid in currentRoom.Grids)
                {
                    var atmosphere = GetCachedAtmosphere(grid.Value);
                    if (atmosphere == null) continue;

                    foreach (var neighborGrid in atmosphere.OpenNeighbors)
                    {
                        var neighborRoom = GetCachedRoom(neighborGrid);

                        if (neighborRoom == null)
                        {
                            foundVenting = true;
                        }

                        if (neighborRoom != null &&
                            neighborRoom.RoomId != currentRoom.RoomId &&
                            !visitedRooms.Contains(neighborRoom.RoomId) &&
                            currentDepth < MaxTraversalDepth)
                        {
                            visitedRooms.Add(neighborRoom.RoomId);
                            connectedRoomGroup.Add(neighborRoom.RoomId);
                            roomsToCheck.Enqueue((neighborRoom, currentDepth + 1));
                        }
                    }
                }
            }

            RoomVentingState ventingState;
            float similarityRatio;

            if (foundVenting)
            {
                ventingState = RoomVentingState.Venting;
                similarityRatio = 1.0f;
            }
            else
            {
                ventingState = RoomVentingState.Sealed;
                similarityRatio = SimilarityToWorldAtmo(startAtmos);
            }

            CacheConnectedRoomsResult(connectedRoomGroup, ventingState, similarityRatio);

            callback(ventingState, similarityRatio);
        }

        private static Atmosphere GetCachedAtmosphere(Grid3 grid)
        {
            if (!TraversalAtmosphereCache.TryGetValue(grid, out var atmosphere))
            {
                atmosphere = AtmosphericsController.World.GetAtmosphereLocal(new WorldGrid(grid));
                TraversalAtmosphereCache[grid] = atmosphere;
            }
            return atmosphere;
        }

        private static Room GetCachedRoom(Grid3 grid)
        {
            if (!TraversalRoomCache.TryGetValue(grid, out var room))
            {
                room = RoomController.World.GetRoom(grid);
                TraversalRoomCache[grid] = room;
            }
            return room;
        }

        // This is basically the base-game method but adjusted a bit to use temp and output a float
        private static float SimilarityToWorldAtmo(Atmosphere roomAtmos)
        {
            try
            {
                var globalAtmos = AtmosphericsController.ReadonlyGlobalAtmosphere(roomAtmos.Grid);
                if (globalAtmos == null)
                {
                    return 0f;
                }

                double globalPressureRatio = (globalAtmos.PressureGasses / Chemistry.OneAtmosphere).ToDouble();
                globalPressureRatio = Math.Clamp(globalPressureRatio, 0.04, 1.0);
                double pressureDiff = Math.Abs((globalAtmos.PressureGasses - roomAtmos.PressureGasses).ToDouble());
                double maxPressureDiff = globalAtmos.PressureGasses.ToDouble() * globalPressureRatio;
                double pressureSimilarity = 1.0 - Math.Clamp(pressureDiff / maxPressureDiff, 0.0, 1.0);

                double tempDiff = Math.Abs(globalAtmos.Temperature.ToDouble() - roomAtmos.Temperature.ToDouble());
                double temperatureSimilarity = 1.0 - Math.Clamp(tempDiff / _worldTemperatureRange, 0.0, 1.0);

                double volumeRatio = (globalAtmos.Volume / roomAtmos.Volume).ToDouble();
                double totalGasDiff = 0.0;

                totalGasDiff += Math.Abs((globalAtmos.GasMixture.Oxygen.Quantity - roomAtmos.GasMixture.Oxygen.Quantity * volumeRatio).ToDouble());
                totalGasDiff += Math.Abs((globalAtmos.GasMixture.Nitrogen.Quantity - roomAtmos.GasMixture.Nitrogen.Quantity * volumeRatio).ToDouble());
                totalGasDiff += Math.Abs((globalAtmos.GasMixture.CarbonDioxide.Quantity - roomAtmos.GasMixture.CarbonDioxide.Quantity * volumeRatio).ToDouble());
                totalGasDiff += Math.Abs((globalAtmos.GasMixture.Volatiles.Quantity - roomAtmos.GasMixture.Volatiles.Quantity * volumeRatio).ToDouble());
                totalGasDiff += Math.Abs((globalAtmos.GasMixture.Pollutant.Quantity - roomAtmos.GasMixture.Pollutant.Quantity * volumeRatio).ToDouble());
                totalGasDiff += Math.Abs((globalAtmos.GasMixture.NitrousOxide.Quantity - roomAtmos.GasMixture.NitrousOxide.Quantity * volumeRatio).ToDouble());
                totalGasDiff += Math.Abs((globalAtmos.GasMixture.Steam.Quantity - roomAtmos.GasMixture.Steam.Quantity * volumeRatio).ToDouble());
                totalGasDiff += Math.Abs((globalAtmos.GasMixture.Hydrogen.Quantity - roomAtmos.GasMixture.Hydrogen.Quantity * volumeRatio).ToDouble());

                double maxGasDiff = maxPressureDiff;
                double gasSimilarity = 1.0 - Math.Clamp(totalGasDiff / maxGasDiff, 0.0, 1.0);

                // I dunno if these weights are good, but we'll see after some testing. worst case make per-world settings?
                double overallSimilarity = (pressureSimilarity * 0.2 + temperatureSimilarity * 0.35 + gasSimilarity * 0.45);

                return (float)Math.Clamp(overallSimilarity, 0.0, 1.0);
            }
            catch (System.Exception ex)
            {
                BeefsRoomDefoggerPlugin.Log.LogWarning($"Error calculating similarity: {ex.Message}");
                return 0f;
            }
        }

        private static void CacheConnectedRoomsResult(
            HashSet<long> visitedRoomIds,
            RoomVentingState ventingState,
            float similarityRatio)
        {
            float currentTime = Time.time;
            foreach (var roomId in visitedRoomIds)
            {
                RoomSealingCache[roomId] = new RoomSealingInfo(ventingState, similarityRatio, currentTime, 1);
            }
        }

        // This one is OK but it's too sensitive for venus
        // private static double WorldTempRange()
        // {
        //     try
        //     {
        //         var globalAtmosData = WorldSetting.Current?.Data?.GlobalAtmosphereData;
        //         if (globalAtmosData?.SolarAngleTemperatureCurveData?.Curve == null)
        //         {
        //             return 100.0;
        //         }
        //
        //         var tempCurve = globalAtmosData.SolarAngleTemperatureCurveData.Curve;
        //
        //         float minWorldTemp = float.MaxValue;
        //         float maxWorldTemp = float.MinValue;
        //
        //         for (float solarAngle = 0f; solarAngle <= 1f; solarAngle += 0.1f)
        //         {
        //             float temp = tempCurve.Evaluate(solarAngle);
        //             minWorldTemp = Mathf.Min(minWorldTemp, temp);
        //             maxWorldTemp = Mathf.Max(maxWorldTemp, temp);
        //         }
        //
        //         double worldTempRange = maxWorldTemp - minWorldTemp;
        //
        //         return Math.Max(worldTempRange, 30.0);
        //     }
        //     catch (System.Exception ex)
        //     {
        //         BeefsRoomDefoggerPlugin.Log.LogWarning($"Error calculating temp range: {ex.Message}");
        //         return 100.0;
        //     }
        // }

        private static double WorldTempRange()
        {
            try
            {
                var globalAtmosData = WorldSetting.Current?.Data?.GlobalAtmosphereData;
                if (globalAtmosData?.SolarAngleTemperatureCurveData?.Curve == null)
                {
                    return 100.0;
                }

                var tempCurve = globalAtmosData.SolarAngleTemperatureCurveData.Curve;

                float minWorldTemp = float.MaxValue;
                float maxWorldTemp = float.MinValue;

                for (float solarAngle = 0f; solarAngle <= 1f; solarAngle += 0.1f)
                {
                    float temp = tempCurve.Evaluate(solarAngle);
                    minWorldTemp = Mathf.Min(minWorldTemp, temp);
                    maxWorldTemp = Mathf.Max(maxWorldTemp, temp);
                }

                double comfyTemp = 298.0;
                double worldTempRange = maxWorldTemp - minWorldTemp;

                double distanceFromComfyTemp = 0.0;
                if (comfyTemp < minWorldTemp)
                {
                    distanceFromComfyTemp = minWorldTemp - comfyTemp;
                }
                else if (comfyTemp > maxWorldTemp)
                {
                    distanceFromComfyTemp = comfyTemp - maxWorldTemp;
                }

                double effectiveRange = Math.Max(worldTempRange, distanceFromComfyTemp);
                return Math.Max(effectiveRange, 30.0);
            }
            catch (System.Exception ex)
            {
                BeefsRoomDefoggerPlugin.Log.LogWarning($"Error calculating temp range: {ex.Message}");
                return 100.0;
            }
        }
    }

  public class FogControlPatcher : MonoBehaviour
    {
        private Room _lastPlayerRoom;
        private float _originalFogStart;
        private float _originalFogEnd;
        private float _nextUpdateTime;
        private bool _initialized = false;
        private float _currentFogDistance = 0f;
        private bool _isInSealedRoom = false;
        private static float _lastCalculatedRoomDistance = -1f;
        private static bool _lastPlayerInSealedRoom = false;
        private static float _lastCalculationTime = 0f;
        private static float _lastFogReductionMultiplier = 0f;

        private const float HighSimilarityThreshold = 0.8f; // high thresh - above fog at max
        private const float LowSimilarityThreshold = 0.3f; // low thresh for fog pushback
        private const float UpdateFrequency = 1.0f; // .5s

        private void Start()
        {
            if (!BeefsRoomDefoggerPlugin.EnableRoomDefogger.Value)
            {
                BeefsRoomDefoggerPlugin.Log.LogInfo("Beefs Room Defogger is disabled");
                enabled = false;
                return;
            }
            _originalFogStart = RenderSettings.fogStartDistance;
            _originalFogEnd = RenderSettings.fogEndDistance;
            _initialized = true;
            BeefsRoomDefoggerPlugin.Log.LogInfo($"Beefs Room Defogger loaded");
            InvokeRepeating(nameof(CleanupCache), 30f, 30f);
        }

        private void Update()
        {
            // using var _ = ModProfiler.Profile();

            if (!BeefsRoomDefoggerPlugin.EnableRoomDefogger.Value)
            {
                return;
            }

            // in game?
            if (WorldManager.Instance?.WorldSun?.TargetLight == null)
            {
                if (_initialized)
                {
                    // back at main menu - reset
                    RenderSettings.fogStartDistance = _originalFogStart;
                    RenderSettings.fogEndDistance = _originalFogEnd;
                    _initialized = false;
                    _isInSealedRoom = false;
                    _lastPlayerRoom = null;
                    _currentFogDistance = 0f;
                    _lastCalculatedRoomDistance = -1f;
                    _lastPlayerInSealedRoom = false;
                    _lastCalculationTime = 0f;
                    BeefsRoomController.ClearCache();
                    BeefsRoomDefoggerPlugin.Log.LogInfo("Reset");
                }
                return;
            }

            if (!_initialized)
            {
                _originalFogStart = RenderSettings.fogStartDistance;
                _originalFogEnd = RenderSettings.fogEndDistance;
                BeefsRoomController.Initialize();
                _initialized = true;
                BeefsRoomDefoggerPlugin.Log.LogInfo($"Beefs Room Defogger initialized");
                InvokeRepeating(nameof(CleanupCache), 30f, 30f);
            }

            if (Time.time < _nextUpdateTime)
            {
                return;
            }

            _nextUpdateTime = Time.time + UpdateFrequency;
            var playerRoom = BeefsRoomController.GetRoom();

            // quick exit cuz outside
            if (playerRoom == null)
            {
                if (_isInSealedRoom)
                {
                    RestoreFog("Player is outside (no room)");
                    _isInSealedRoom = false;
                    _lastPlayerRoom = null;
                }
                return;
            }

			BeefsRoomController.ScheduleRoomCheck(playerRoom, this);

			var cachedState = BeefsRoomController.GetCachedRoomState(playerRoom.RoomId);

			BeefsRoomController.RoomVentingState ventingState = BeefsRoomController.RoomVentingState.Sealed;
			float similarityRatio = 0.0f;

			if (cachedState != null)
			{
				if (!cachedState.Value.IsStale)
				{
					// use cache
					ventingState = cachedState.Value.VentingState;
					similarityRatio = cachedState.Value.SimilarityRatio;
				}
				else
				{
					// use cache but its stale
					ventingState = cachedState.Value.VentingState;
					similarityRatio = cachedState.Value.SimilarityRatio;
				}
			}
			else
			{
				// no cache! sealed until otherwise proven
				ventingState = BeefsRoomController.RoomVentingState.Sealed;
				similarityRatio = 0.0f;
			}

            if (ventingState == BeefsRoomController.RoomVentingState.Venting)
            {
                // we're open to atmo
                if (_isInSealedRoom)
                {
                    RestoreFog("Room group is venting");
                }
                _isInSealedRoom = false;
                _lastPlayerRoom = null;
            }
            else
            {
                // we're in a room but maybe it's not yet gucci in here
                float fogReductionMultiplier = CalculateFogMultiplier(similarityRatio);
                _lastFogReductionMultiplier = fogReductionMultiplier;

                if (!_isInSealedRoom || playerRoom != _lastPlayerRoom)
                {
                    BeefsRoomDefoggerPlugin.Log.LogInfo($"Room: {playerRoom?.RoomId}, Similarity: {similarityRatio:F2}, Multiplier: {fogReductionMultiplier:F2}");
                }

                _isInSealedRoom = true;
                _lastPlayerRoom = playerRoom;
                UpdateFogDistance(playerRoom, fogReductionMultiplier);
            }
        }

        private void OnDestroy()
        {
            if (_initialized)
            {
                RenderSettings.fogStartDistance = _originalFogStart;
                RenderSettings.fogEndDistance = _originalFogEnd;
            }
        }

        private float CalculateFogMultiplier(float similarityRatio)
        {
            if (similarityRatio >= HighSimilarityThreshold)
            {
                // it's basically the world
                return 0.0f;
            }
            else if (similarityRatio <= LowSimilarityThreshold)
            {
                // it's 99% for sure inside and probably good atmo
                return 1.0f;
            }
            else
            {
                // lerp between the two
                float t = (similarityRatio - LowSimilarityThreshold) / (HighSimilarityThreshold - LowSimilarityThreshold);
                return Mathf.Lerp(1.0f, 0.0f, t);
            }
        }

        private void UpdateFogDistance(Room room, float fogReductionMultiplier = 1.0f)
        {
            if (room == null) return;

            try
            {
                var localHuman = Human.LocalHuman;
                if (localHuman == null) return;

                var playerPos = localHuman.Transform.position;
                var fogDistance = CalculateFogDistance(room, playerPos);
                var buffer = BeefsRoomDefoggerPlugin.IndoorFogBuffer.Value;
                _lastCalculatedRoomDistance = (fogDistance + buffer) * fogReductionMultiplier;
                _lastPlayerInSealedRoom = fogReductionMultiplier > 0f;
                _lastCalculationTime = Time.time;

                var newFogStart = _lastCalculatedRoomDistance;
                var newFogEnd = newFogStart + 10f;

                if (_currentFogDistance == 0f || Mathf.Abs(newFogStart - _currentFogDistance) > 1f)
                {
                    RenderSettings.fogStartDistance = newFogStart;
                    RenderSettings.fogEndDistance = newFogEnd;
                    _currentFogDistance = newFogStart;
                }
            }
            catch (System.Exception ex)
            {
                BeefsRoomDefoggerPlugin.Log.LogError($"Error updating fog distance: {ex.Message}");
            }
        }

        private float CalculateFogDistance(Room room, Vector3 playerPos)
        {
            if (room?.Grids == null || room.Grids.Count == 0)
            {
                BeefsRoomDefoggerPlugin.Log.LogWarning("Room has no grids??!?");
                return 5f;
            }

            try
            {
                float maxDistance = 0f;

                foreach (var grid in room.Grids)
                {
                    var gridPos = grid.Value.ToVector3();
                    var distance = Vector3.Distance(playerPos, gridPos);
                    maxDistance = Mathf.Max(maxDistance, distance);
                }

                float finalDistance = Mathf.Max(maxDistance, BeefsRoomDefoggerPlugin.IndoorFogBuffer.Value);

                return finalDistance;
            }
            catch (System.Exception ex)
            {
                BeefsRoomDefoggerPlugin.Log.LogError($"Error calculating fog distance: {ex.Message}");
                return 5f;
            }
        }

        private void RestoreFog(string reason)
        {
            RenderSettings.fogStartDistance = _originalFogStart;
            RenderSettings.fogEndDistance = _originalFogEnd;
            _lastCalculatedRoomDistance = -1f;
            _lastPlayerInSealedRoom = false;
            _lastCalculationTime = Time.time;
            _currentFogDistance = 0f;
            // BeefsRoomDefoggerPlugin.Log.LogInfo($"Restored fog: {reason}");
        }

        private void CleanupCache()
        {
            BeefsRoomController.CleanupCache();
        }

        public static bool IsPlayerInSealedRoom()
        {
            return _lastPlayerInSealedRoom;
        }

        public static float GetRoomBoundaryDistance()
        {
            if (!_lastPlayerInSealedRoom || _lastCalculatedRoomDistance < 0f)
                return -1f;

            if (Time.time - _lastCalculationTime > UpdateFrequency)
                return -1f;

            return _lastCalculatedRoomDistance;
        }

        public static float GetFogReductionMultiplier()
        {
            return _lastFogReductionMultiplier;
        }
    }

    [HarmonyPatch(typeof(GameManager), "Awake")]
    public static class GameManagerPatcher
    {
        [HarmonyPostfix]
        public static void AddFogController(GameManager __instance)
        {
            try
            {
                if (NetworkManager.NetworkRole == NetworkRole.Server)
                {
                    BeefsRoomDefoggerPlugin.Log.LogInfo("Skipping defogger on server");
                    return;
                }

                var fogController = __instance.gameObject.GetComponent<FogControlPatcher>();
                if (fogController == null)
                {
                    __instance.gameObject.AddComponent<FogControlPatcher>();
                    BeefsRoomDefoggerPlugin.Log.LogInfo("Defogger added to GameManager");
                }
                else
                {
                    BeefsRoomDefoggerPlugin.Log.LogInfo("Defogger already exists on GameManager");
                }
            }
            catch (System.Exception ex)
            {
                BeefsRoomDefoggerPlugin.Log.LogError($"Error in GameManager patch: {ex.Message}");
            }
        }
    }

    [HarmonyPatch(typeof(AtmosphericScattering), "UpdateStaticUniforms")]
    public static class AtmosphericScatteringPusher
    {
        private static float _originalWorldNearScatterPush = float.MinValue;
        private static float _originalHeightNearScatterPush = float.MinValue;
        private static float _targetPushValue = float.MinValue;
        private static float _currentAppliedPush = float.MinValue;
        private static bool _isInitialized = false;

        [HarmonyPostfix]
        public static void PushScattering(AtmosphericScattering __instance)
        {
            try
            {
                if (!BeefsRoomDefoggerPlugin.EnableRoomDefogger.Value || NetworkManager.NetworkRole == NetworkRole.Server)
                    return;

                if (WorldManager.Instance?.WorldSun?.TargetLight == null)
                {
                    if (_isInitialized)
                    {
                        if (_originalWorldNearScatterPush != float.MinValue)
                        {
                            __instance.worldNearScatterPush = _originalWorldNearScatterPush;
                            __instance.heightNearScatterPush = _originalHeightNearScatterPush;
                        }
                        _isInitialized = false;
                        BeefsRoomDefoggerPlugin.Log.LogInfo("Reset atmospheric scattering");
                    }
                    return;
                }

                if (!_isInitialized)
                {
                    _originalWorldNearScatterPush = __instance.worldNearScatterPush;
                    _originalHeightNearScatterPush = __instance.heightNearScatterPush;
                    _currentAppliedPush = _originalWorldNearScatterPush;
                    _targetPushValue = _originalWorldNearScatterPush;
                    _isInitialized = true;
                    BeefsRoomDefoggerPlugin.Log.LogInfo($"Scattering defogger initialized");
                }

                if (!_isInitialized) return;

                bool isPlayerInSealedRoom = FogControlPatcher.IsPlayerInSealedRoom();
                float roomBoundaryDistance = FogControlPatcher.GetRoomBoundaryDistance();
                float fogReductionMultiplier = FogControlPatcher.GetFogReductionMultiplier();

                float newTarget;
                if (isPlayerInSealedRoom && roomBoundaryDistance > 0f)
                {
                    newTarget = roomBoundaryDistance - (BeefsRoomDefoggerPlugin.ExtraFog.Value * (1-fogReductionMultiplier));
                }
                else
                {
                    newTarget = _originalWorldNearScatterPush - BeefsRoomDefoggerPlugin.ExtraFog.Value;
                }

                _targetPushValue = newTarget;

                float maxMove = BeefsRoomDefoggerPlugin.AdjustmentSpeed.Value * Time.deltaTime;
                _currentAppliedPush = Mathf.MoveTowards(_currentAppliedPush, _targetPushValue, maxMove);

                __instance.worldNearScatterPush = _currentAppliedPush;
                __instance.heightNearScatterPush = _currentAppliedPush;
            }
            catch (System.Exception ex)
            {
                BeefsRoomDefoggerPlugin.Log.LogError($"Error pushing scattering: {ex.Message}");
            }
        }
    }
}