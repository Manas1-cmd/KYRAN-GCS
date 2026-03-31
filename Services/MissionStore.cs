using SimpleDroneGCS.Models;
using SimpleDroneGCS.Views;
using System.Collections.Concurrent;
using System.Collections.Generic;

namespace SimpleDroneGCS.Services
{
    public static class MissionStore
    {
        private static readonly ConcurrentDictionary<int, List<WaypointItem>>
            _missions = new();

        private static readonly ConcurrentDictionary<int, WaypointItem>
            _homePositions = new();

        public static void SetHome(int vehicleType, WaypointItem home)
        {
            _homePositions[vehicleType] = home;
            System.Diagnostics.Debug.WriteLine(
                $"[MissionStore] HOME сохранён для типа {vehicleType}: " +
                $"{home?.Latitude:F6}, {home?.Longitude:F6}");
        }

        public static WaypointItem GetHome(int vehicleType)
        {
            _homePositions.TryGetValue(vehicleType, out var home);
            return home;
        }

        public static void ClearHome(int vehicleType)
            => _homePositions.TryRemove(vehicleType, out _);

        public static void Set(int vehicleType, List<WaypointItem> mission)
        {
            _missions[vehicleType] = mission;
            System.Diagnostics.Debug.WriteLine(
                $"[MissionStore] Сохранено для типа {vehicleType}: {mission?.Count ?? 0} точек");
        }

        public static List<WaypointItem> Get(int vehicleType)
        {
            _missions.TryGetValue(vehicleType, out var mission);
            return mission;
        }

        public static void Clear(int vehicleType)
            => _missions.TryRemove(vehicleType, out _);

        public static void ClearAll()
        {
            _missions.Clear();
        }

        public static void ClearAllIncludingHome()
        {
            _missions.Clear();
            _homePositions.Clear();
        }

        public static bool HasMission(int vehicleType)
            => _missions.TryGetValue(vehicleType, out var m) && m?.Count > 0;
    }
}