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
using Assets.Scripts.Weather;
using Unity.Mathematics;
using Unity.Collections;
using Weather;
using System.Reflection;

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
            public readonly HashSet<int3> VentingGrids;

            public RoomSealingInfo(RoomVentingState ventingState, float similarityRatio, float lastChecked, int depth, HashSet<int3> ventingGrids)
            {
                VentingState = ventingState;
                SimilarityRatio = similarityRatio;
                LastChecked = lastChecked;
                TraversalDepth = depth;
                VentingGrids = ventingGrids ?? new HashSet<int3>();
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
            HashSet<int3> ventingGrids = new HashSet<int3>();

            if (roomAtmos != null)
            {
                yield return IsAtmosphereGroupVentingCoroutine(roomAtmos, (state, similarity, grids) => {
                    ventingState = state;
                    similarityRatio = similarity;
                    ventingGrids = grids;
                });
            }

            RoomSealingCache[room.RoomId] = new RoomSealingInfo(ventingState, similarityRatio, Time.time, 1, ventingGrids);
            _sealingCheckInProgress = false;
        }

        private static IEnumerator IsAtmosphereGroupVentingCoroutine(
            Atmosphere startAtmos,
            System.Action<RoomVentingState, float, HashSet<int3>> callback)
        {
            // ~0.09ms but is coroutine so is fine
            // using var _ = ModProfiler.Profile();
            if (startAtmos?.Room == null)
            {
                callback(RoomVentingState.Venting, 1.0f, new HashSet<int3>());
                yield break;
            }

            TraversalAtmosphereCache.Clear();
            TraversalRoomCache.Clear();

            var visitedRooms = new HashSet<long>();
            var roomsToCheck = new Queue<(Room room, int depth)>();
            var connectedRoomGroup = new HashSet<long>();
            var ventingGrids = new HashSet<int3>();

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
                            ventingGrids.Add(grid.Value);
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

            CacheConnectedRoomsResult(connectedRoomGroup, ventingState, similarityRatio, ventingGrids);

            callback(ventingState, similarityRatio, ventingGrids);
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
            float similarityRatio,
            HashSet<int3> ventingGrids)
        {
            float currentTime = Time.time;
            foreach (var roomId in visitedRoomIds)
            {
                RoomSealingCache[roomId] = new RoomSealingInfo(ventingState, similarityRatio, currentTime, 1, ventingGrids);
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

        public static HashSet<Room> GetRoomsFromStormGrid(Dictionary<int3, byte> stormRoomGrids)
        {
            var rooms = new HashSet<Room>();

            foreach (var gridInt3 in stormRoomGrids.Keys)
            {
                var grid = new Grid3(gridInt3.x, gridInt3.y, gridInt3.z);
                var room = RoomController.World?.GetRoom(grid);
                if (room != null)
                {
                    rooms.Add(room);
                }
            }

            return rooms;
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

    [HarmonyPatch(typeof(StormLocal), "UpdateStormField")]
    public static class StormLocalRoomBlocker
    {
        private static FieldInfo _blockedAirGridsField = AccessTools.Field(typeof(StormLocal), "_blockedAirGrids");
        private static FieldInfo _roomGridsField = AccessTools.Field(typeof(StormLocal), "_roomGrids");

        private static HashSet<int3> _roomGridsToAdd = new HashSet<int3>();
        private static HashSet<int3> _lastAddedGrids = new HashSet<int3>();
        private static float _lastSealedRoomsUpdate = 0f;

        [HarmonyPostfix]
        public static void AddRoomGrids(StormLocal __instance)
        {
            try
            {
                if (!BeefsRoomDefoggerPlugin.EnableRoomDefogger.Value)
                    return;

                if (!BeefsRoomDefoggerPlugin.StormChanges.Value)
                    return;

                if (Time.time - _lastSealedRoomsUpdate > 1f)
                {
                    bool roomStateChanged = UpdateSealedRoomsFromStormData(__instance);
                    _lastSealedRoomsUpdate = Time.time;

                    if (roomStateChanged)
                    {
                        __instance.MarkAsStale();
                    }
                }

                if (_roomGridsToAdd.Count == 0)
                    return;

                var blockedAirGrids = _blockedAirGridsField.GetValue(__instance) as NativeHashMap<int3, byte>?;

                if (!blockedAirGrids.HasValue || !blockedAirGrids.Value.IsCreated)
                    return;

                var blockedAirGridsMap = blockedAirGrids.Value;

                foreach (var grid in _lastAddedGrids)
                {
                    if (!_roomGridsToAdd.Contains(grid))
                    {
                        blockedAirGridsMap.Remove(grid);
                    }
                }

                var newAddedGrids = new HashSet<int3>();

                foreach (var grid in _roomGridsToAdd)
                {
                    blockedAirGridsMap.TryAdd(grid, 0);
                    newAddedGrids.Add(grid);
                }

                _lastAddedGrids = newAddedGrids;
            }
            catch (System.Exception ex)
            {
                BeefsRoomDefoggerPlugin.Log.LogError($"Error adding room grids: {ex.Message}\n{ex.StackTrace}");
            }
        }

        private static bool UpdateSealedRoomsFromStormData(StormLocal stormLocal)
        {
            var previousGrids = new HashSet<int3>(_roomGridsToAdd);
            _roomGridsToAdd.Clear();

            var roomGrids = _roomGridsField.GetValue(stormLocal) as NativeHashMap<int3, byte>?;

            if (!roomGrids.HasValue || !roomGrids.Value.IsCreated)
                return false;

            var stormRoomGridsDict = new Dictionary<int3, byte>();
            var keys = roomGrids.Value.GetKeyArray(Allocator.Temp);
            try
            {
                foreach (var key in keys)
                {
                    stormRoomGridsDict[key] = roomGrids.Value[key];
                }
            }
            finally
            {
                keys.Dispose();
            }

            var roomsInStormArea = BeefsRoomController.GetRoomsFromStormGrid(stormRoomGridsDict);

            var fogController = UnityEngine.Object.FindObjectOfType<FogControlPatcher>();
            if (fogController != null)
            {
                foreach (var room in roomsInStormArea)
                {
                    var state = BeefsRoomController.GetCachedRoomState(room.RoomId);
                    if (state == null)
                    {
                        BeefsRoomController.ScheduleRoomCheck(room, fogController);
                    }
                }
            }

            UpdateSealedRoomsAndPunchHoleForVentingGrid(roomsInStormArea);
            // UpdateSealedRooms(roomsInStormArea);

            return !_roomGridsToAdd.SetEquals(previousGrids);
        }

        private static void UpdateSealedRoomsAndPunchHoleForVentingGrid(HashSet<Room> roomsInStormArea)
        {
            foreach (var room in roomsInStormArea)
            {
                var state = BeefsRoomController.GetCachedRoomState(room.RoomId);

                if (state == null)
                {
                    var roomGridsToBlock = new HashSet<int3>();
                    foreach (var gridWrapper in room.Grids)
                        roomGridsToBlock.Add(gridWrapper.Value);

                    ExpandRoom(roomGridsToBlock);
                    _roomGridsToAdd.UnionWith(roomGridsToBlock);
                    continue;
                }

                var roomGridsToBlockFinal = new HashSet<int3>();
                foreach (var gridWrapper in room.Grids)
                    roomGridsToBlockFinal.Add(gridWrapper.Value);

                ExpandRoom(roomGridsToBlockFinal);
                _roomGridsToAdd.UnionWith(roomGridsToBlockFinal);

                foreach (var ventingGrid in state.Value.VentingGrids)
                {
                    PunchAHoleWhereItsVenting(ventingGrid, roomGridsToBlockFinal);
                }
            }
        }

        // private static void UpdateSealedRooms(HashSet<Room> roomsInStormArea)
        // {
        //     var sealedRoomGrids = new HashSet<int3>();
        //
        //     foreach (var room in roomsInStormArea)
        //     {
        //         var state = BeefsRoomController.GetCachedRoomState(room.RoomId);
        //
        //         if (state == null || state.Value.VentingState == BeefsRoomController.RoomVentingState.Sealed)
        //         {
        //             foreach (var gridWrapper in room.Grids)
        //                 sealedRoomGrids.Add(gridWrapper.Value);
        //         }
        //     }
        //
        //     ExpandRoom(sealedRoomGrids);
        //     _roomGridsToAdd.UnionWith(sealedRoomGrids);
        // }

        private static void ExpandRoom(HashSet<int3> roomGrids)
        {
            int expansionStep = 20;
            int gridsToExpand = 1;

            var expandedGrids = new HashSet<int3>();
            foreach (var grid in roomGrids.ToList())
            {
                for (int dx = -gridsToExpand; dx <= gridsToExpand; dx++)
                {
                    for (int dy = -gridsToExpand; dy <= gridsToExpand; dy++)
                    {
                        for (int dz = -gridsToExpand; dz <= gridsToExpand; dz++)
                        {
                            if (dx == 0 && dy == 0 && dz == 0)
                                continue;

                            expandedGrids.Add(new int3(
                                grid.x + dx * expansionStep,
                                grid.y + dy * expansionStep,
                                grid.z + dz * expansionStep
                            ));
                        }
                    }
                }
            }

            roomGrids.UnionWith(expandedGrids);
        }

        private static void PunchAHoleWhereItsVenting(int3 ventingGrid, HashSet<int3> blockedGrids)
        {
            int expansionStep = 20;
            int gridsToExpand = 1;

            for (int dx = -gridsToExpand; dx <= gridsToExpand; dx++)
            {
                for (int dy = -gridsToExpand; dy <= gridsToExpand; dy++)
                {
                    for (int dz = -gridsToExpand; dz <= gridsToExpand; dz++)
                    {
                        var openingGrid = new int3(
                            ventingGrid.x + dx * expansionStep,
                            ventingGrid.y + dy * expansionStep,
                            ventingGrid.z + dz * expansionStep
                        );

                        blockedGrids.Remove(openingGrid);
                        _roomGridsToAdd.Remove(openingGrid);
                    }
                }
            }
        }
    }

    [HarmonyPatch(typeof(StormLocal), "Update")]
    public static class StormLocalFogPatcher
    {
        private static float _targetStormFogStart = float.MinValue;
        private static float _targetStormFogEnd = float.MinValue;
        private static float _currentStormFogStart = float.MinValue;
        private static float _currentStormFogEnd = float.MinValue;
        private static bool _isInitialized = false;

        private const float FogOffset = -5f;

        [HarmonyPostfix]
        public static void UpdatePostfix(StormLocal __instance)
        {
            try
            {
                if (!BeefsRoomDefoggerPlugin.EnableRoomDefogger.Value || !__instance.IsStormActive || WeatherManager.CurrentWeatherEvent?.Fog == null)
                    return;

                if (!BeefsRoomDefoggerPlugin.StormChanges.Value)
                    return;

                if (WorldManager.Instance?.WorldSun?.TargetLight == null)
                {
                    _isInitialized = false;
                    return;
                }

                if (!_isInitialized)
                {
                    _currentStormFogStart = RenderSettings.fogStartDistance;
                    _currentStormFogEnd = RenderSettings.fogEndDistance;
                    _targetStormFogStart = _currentStormFogStart;
                    _targetStormFogEnd = _currentStormFogEnd;
                    _isInitialized = true;
                }

                float roomBoundaryDistance = FogControlPatcher.GetRoomBoundaryDistance();
                bool isPlayerInSealedRoom = FogControlPatcher.IsPlayerInSealedRoom();

                float newTargetStart, newTargetEnd;
                if (isPlayerInSealedRoom && roomBoundaryDistance > 0f)
                {
                    newTargetStart = roomBoundaryDistance + WeatherManager.CurrentWeatherEvent.Fog.StartDistance;
                    newTargetEnd = roomBoundaryDistance + WeatherManager.CurrentWeatherEvent.Fog.EndDistance;
                }
                else
                {
                    newTargetStart = Mathf.Max(0f, WeatherManager.CurrentWeatherEvent.Fog.StartDistance + FogOffset);
                    newTargetEnd = Mathf.Max(0f, WeatherManager.CurrentWeatherEvent.Fog.EndDistance + FogOffset);
                }

                if (Mathf.Abs(newTargetStart - _targetStormFogStart) > 0.1f || Mathf.Abs(newTargetEnd - _targetStormFogEnd) > 0.1f)
                {
                    _targetStormFogStart = newTargetStart;
                    _targetStormFogEnd = newTargetEnd;
                }

                float maxMove = BeefsRoomDefoggerPlugin.AdjustmentSpeed.Value * Time.deltaTime;
                _currentStormFogStart = Mathf.MoveTowards(_currentStormFogStart, _targetStormFogStart, maxMove);
                _currentStormFogEnd = Mathf.MoveTowards(_currentStormFogEnd, _targetStormFogEnd, maxMove);

                if (Mathf.Abs(RenderSettings.fogStartDistance - _currentStormFogStart) > 0.1f ||
                    Mathf.Abs(RenderSettings.fogEndDistance - _currentStormFogEnd) > 0.1f)
                {
                    RenderSettings.fogStartDistance = _currentStormFogStart;
                    RenderSettings.fogEndDistance = _currentStormFogEnd;
                }
            }
            catch (System.Exception ex)
            {
                BeefsRoomDefoggerPlugin.Log.LogError($"Error in storm fog update: {ex.Message}");
            }
        }
    }

    [HarmonyPatch(typeof(StormLocal), "Update")]
    public static class StormLensFlareDimmer
    {
        private static Component _sunFlares = null;
        private static PropertyInfo _opacityProperty = null;
        // private static PropertyInfo _dampingFactorProperty = null;
        private static float _originalOpacity = 1f;
        // private static float _originalDampingFactor = 0.1f;
        private static bool _isInitialized = false;

        [HarmonyPostfix]
        public static void DimLensFlaresDuringStorm(StormLocal __instance)
        {
            try
            {
                if (!BeefsRoomDefoggerPlugin.EnableRoomDefogger.Value)
                    return;

                if (!BeefsRoomDefoggerPlugin.StormChanges.Value)
                    return;

                if (WeatherManager.CurrentWeatherEvent != null &&
                    WeatherManager.CurrentWeatherEvent.Id == "SolarStorm")
                    return;


                if (WorldManager.Instance?.WorldSun?.TargetLight == null)
                {
                    if (_isInitialized)
                    {
                        RestoreLensFlares();
                    }
                    return;
                }

                if (!_isInitialized)
                {
                    InitializeLensFlares();
                }

                if (_sunFlares == null || _opacityProperty == null) // || _dampingFactorProperty == null)
                {
                    return;
                }

                if (__instance.IsStormActive)
                {
                    _opacityProperty.SetValue(_sunFlares, _originalOpacity * 0.05f);
                    // _dampingFactorProperty.SetValue(_sunFlares, 0.01f);
                }
                else
                {
                    _opacityProperty.SetValue(_sunFlares, _originalOpacity);
                    // _dampingFactorProperty.SetValue(_sunFlares, _originalDampingFactor);
                }
            }
            catch (System.Exception ex)
            {
                BeefsRoomDefoggerPlugin.Log.LogError($"Error! The sun cannot be dimmed it's too powerful: {ex.Message}");
            }
        }

        private static void InitializeLensFlares()
        {
            try
            {
                if (RenderSettings.sun != null)
                {
                    var allComponents = RenderSettings.sun.GetComponents<Component>();
                    foreach (var component in allComponents)
                    {
                        if (component.GetType().Name == "EasyFlares")
                        {
                            _sunFlares = component;
                            break;
                        }
                    }

                    if (_sunFlares != null)
                    {
                        _opacityProperty = _sunFlares.GetType().GetProperty("Opacity");
                        // _dampingFactorProperty = _sunFlares.GetType().GetProperty("DampingFactor");

                        if (_opacityProperty != null) // && _dampingFactorProperty != null)
                        {
                            _originalOpacity = (float)_opacityProperty.GetValue(_sunFlares);
                            // _originalDampingFactor = (float)_dampingFactorProperty.GetValue(_sunFlares);
                            _isInitialized = true;
                        }
                    }
                }
            }
            catch (System.Exception ex)
            {
                BeefsRoomDefoggerPlugin.Log.LogError($"Error initializing sun lens flare control: {ex.Message}");
            }
        }

        private static void RestoreLensFlares()
        {
            try
            {
                if (_sunFlares != null && _opacityProperty != null) // && _dampingFactorProperty != null)
                {
                    _opacityProperty.SetValue(_sunFlares, _originalOpacity);
                    // _dampingFactorProperty.SetValue(_sunFlares, _originalDampingFactor);
                }
                _isInitialized = false;
                _sunFlares = null;
                _opacityProperty = null;
                // _dampingFactorProperty = null;
            }
            catch (System.Exception ex)
            {
                BeefsRoomDefoggerPlugin.Log.LogError($"Error restoring lens flares: {ex.Message}");
            }
        }
    }
}