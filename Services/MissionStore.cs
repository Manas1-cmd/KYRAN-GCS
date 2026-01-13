using SimpleDroneGCS.Models;
using SimpleDroneGCS.Views;
using System.Collections.Generic;

namespace SimpleDroneGCS.Services
{
    /// <summary>
    /// Хранилище активных миссий по типам дрона (синглтон)
    /// </summary>
    public static class MissionStore
    {
        private static Dictionary<int, List<WaypointItem>> _missions = new();

        private static Dictionary<int, WaypointItem> _homePositions = new();

        /// <summary>
        /// Сохранить HOME позицию для типа дрона
        /// </summary>
        public static void SetHome(int vehicleType, WaypointItem home)
        {
            _homePositions[vehicleType] = home;
            System.Diagnostics.Debug.WriteLine($"[MissionStore] HOME сохранён для типа {vehicleType}: {home?.Latitude:F6}, {home?.Longitude:F6}");
        }

        /// <summary>
        /// Получить HOME позицию для типа дрона
        /// </summary>
        public static WaypointItem GetHome(int vehicleType)
        {
            _homePositions.TryGetValue(vehicleType, out var home);
            return home;
        }

        /// <summary>
        /// Очистить HOME для типа
        /// </summary>
        public static void ClearHome(int vehicleType)
        {
            _homePositions.Remove(vehicleType);
        }

        /// <summary>
        /// Сохранить миссию для типа дрона
        /// </summary>
        public static void Set(int vehicleType, List<WaypointItem> mission)
        {
            _missions[vehicleType] = mission;
            System.Diagnostics.Debug.WriteLine($"[MissionStore] Сохранено для типа {vehicleType}: {mission?.Count ?? 0} точек");
        }

        /// <summary>
        /// Получить миссию для типа дрона
        /// </summary>
        public static List<WaypointItem> Get(int vehicleType)
        {
            _missions.TryGetValue(vehicleType, out var mission);
            return mission;
        }

        /// <summary>
        /// Очистить миссию для типа
        /// </summary>
        public static void Clear(int vehicleType)
        {
            _missions.Remove(vehicleType);
        }

        /// <summary>
        /// Очистить все миссии
        /// </summary>
        public static void ClearAll()
        {
            _missions.Clear();
        }

        /// <summary>
        /// Проверить есть ли миссия
        /// </summary>
        public static bool HasMission(int vehicleType)
        {
            return _missions.ContainsKey(vehicleType) && _missions[vehicleType]?.Count > 0;
        }
    }
}