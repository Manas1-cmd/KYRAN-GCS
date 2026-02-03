using System;

namespace SimpleDroneGCS.Models
{
    /// <summary>
    /// Телеметрия дрона - все данные с MAVLink
    /// </summary>
    public class Telemetry
    {
        // ATTITUDE - углы ориентации
        public double Roll { get; set; }          // Крен (градусы)
        public double Pitch { get; set; }         // Тангаж (градусы)
        public double Yaw { get; set; }           // Рыскание/Курс (градусы)

        // VFR_HUD - высота, скорость
        public double Altitude { get; set; }      // Высота над уровнем моря (метры)
        public double RelativeAltitude { get; set; } // Высота от HOME (метры) - НОВОЕ
        public double Speed { get; set; }         // Скорость земная (м/с)
        public double GroundSpeed => Speed;       // Алиас для совместимости
        public double Airspeed { get; set; }      // Воздушная скорость (м/с)
        public double ClimbRate { get; set; }     // Вертикальная скорость (м/с)
        public double Heading { get; set; }       // Курс (градусы)
        public double Throttle { get; set; }      // Газ (%)

        // GPS_RAW_INT - GPS данные
        public double Latitude { get; set; }      // Широта
        public double Longitude { get; set; }     // Долгота
        public double GpsAltitude { get; set; }   // Высота GPS (метры)
        public int SatellitesVisible { get; set; }// Видимых спутников
        public int GpsFixType { get; set; }       // 0=нет, 1=нет фикса, 2=2D, 3=3D

        // SYS_STATUS - батарея и система
        public double BatteryVoltage { get; set; }// Напряжение (В)
        public double BatteryCurrent { get; set; }// Ток (А)
        public int BatteryPercent { get; set; }   // Заряд (%)

        // VTOL моторы (для QuadPlane)
        public int Motor1Percent { get; set; }    // Мотор 1 (%)
        public int Motor2Percent { get; set; }    // Мотор 2 (%)
        public int Motor3Percent { get; set; }    // Мотор 3 (%)
        public int Motor4Percent { get; set; }    // Мотор 4 (%)
        public int PusherPercent { get; set; }    // Толкающий мотор (%)

        // HEARTBEAT - статус
        public string FlightMode { get; set; }    // Режим полёта
        public bool Armed { get; set; }           // Вооружён/Разоружён
        public bool IsArmed => Armed;             // Алиас для совместимости
        public byte SystemStatus { get; set; }    // Статус системы
        public byte BaseMode { get; set; }        // Базовый режим
        public uint CustomMode { get; set; }      // Кастомный режим

        // MISSION_CURRENT - текущая миссия
        public ushort CurrentWaypoint { get; set; }// Текущая точка маршрута

        // Дополнительные вычисляемые поля
        public int SignalStrength { get; set; }   // Уровень сигнала (%)
        public double DistanceFromHome { get; set; }// Расстояние от стартовой точки (м)

        // System IDs
        public byte SystemId { get; set; }
        public byte ComponentId { get; set; }

        // Время последнего обновления
        public DateTime LastUpdate { get; set; }

        public Telemetry()
        {
            FlightMode = "UNKNOWN";
            LastUpdate = DateTime.Now;
            SignalStrength = 0;
            DistanceFromHome = 0;
        }

        /// <summary>
        /// Проверка свежести данных (данные старше 3 секунд считаются устаревшими)
        /// </summary>
        public bool IsStale()
        {
            return (DateTime.Now - LastUpdate).TotalSeconds > 3;
        }
    }
}