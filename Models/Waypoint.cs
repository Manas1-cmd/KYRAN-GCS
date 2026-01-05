namespace SimpleDroneGCS.Models
{
    /// <summary>
    /// Точка маршрута (Waypoint)
    /// </summary>
    public class Waypoint
    {
        public int Sequence { get; set; }         // Порядковый номер в миссии
        public double Latitude { get; set; }      // Широта
        public double Longitude { get; set; }     // Долгота
        public double Altitude { get; set; }      // Высота над точкой HOME (метры)
        public double Radius { get; set; }        // Радиус (метры) - для визуализации

        // MAVLink параметры
        public ushort Command { get; set; }       // MAV_CMD (например, 16 = NAV_WAYPOINT)
        public byte Frame { get; set; }           // MAV_FRAME (например, 3 = GLOBAL_RELATIVE_ALT)
        public byte Current { get; set; }         // 0 = обычная точка, 1 = текущая
        public byte Autocontinue { get; set; }    // 1 = автопереход

        // Параметры команды (зависят от Command)
        public float Param1 { get; set; }         // Hold time (секунды)
        public float Param2 { get; set; }         // Acceptance radius (метры)
        public float Param3 { get; set; }         // Pass radius (метры)
        public float Param4 { get; set; }         // Yaw angle (градусы)

        public Waypoint()
        {
            Radius = 10.0;                        // По умолчанию 10 метров
            Command = 16;                         // NAV_WAYPOINT
            Frame = 3;                            // GLOBAL_RELATIVE_ALT
            Autocontinue = 1;                     // Автопереход
            Param1 = 0;                           // Hold 0 секунд
            Param2 = 5;                           // Acceptance radius 5м
            Param3 = 0;                           // Pass radius 0
            Param4 = 0;                           // Yaw 0 градусов
        }

        /// <summary>
        /// Расстояние до другой точки (метры) - формула Haversine упрощённая
        /// </summary>
        public double DistanceTo(Waypoint other)
        {
            double R = 6371000; // Радиус Земли в метрах
            double dLat = ToRadians(other.Latitude - this.Latitude);
            double dLon = ToRadians(other.Longitude - this.Longitude);
            double a = System.Math.Sin(dLat / 2) * System.Math.Sin(dLat / 2) +
                       System.Math.Cos(ToRadians(this.Latitude)) * System.Math.Cos(ToRadians(other.Latitude)) *
                       System.Math.Sin(dLon / 2) * System.Math.Sin(dLon / 2);
            double c = 2 * System.Math.Atan2(System.Math.Sqrt(a), System.Math.Sqrt(1 - a));
            return R * c;
        }

        private double ToRadians(double degrees)
        {
            return degrees * System.Math.PI / 180.0;
        }

        public override string ToString()
        {
            return $"WP{Sequence}: {Latitude:F6}, {Longitude:F6}, ALT={Altitude:F1}m";
        }
    }
}