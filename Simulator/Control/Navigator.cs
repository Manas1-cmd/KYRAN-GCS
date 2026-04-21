using System;

namespace SimpleDroneGCS.Simulator.Control
{
    /// <summary>
    /// Географические вычисления: дистанции, пеленги, смещения, преобразования координат.
    /// <para>
    /// Все методы pure (нет state) — thread-safe автоматически.
    /// </para>
    /// <para>
    /// Модель Земли — сфера с радиусом <see cref="EarthRadiusM"/> (среднее значение WGS-84).
    /// Ошибка относительно эллипсоида ≤ 0.5% на расстояниях до 100 км — для нашего
    /// симулятора некритично.
    /// </para>
    /// </summary>
    public static class Navigator
    {
        // =====================================================================
        // Константы
        // =====================================================================

        /// <summary>Средний радиус Земли, м.</summary>
        public const double EarthRadiusM = 6_371_000.0;

        /// <summary>Градусы → радианы.</summary>
        public const double DegToRad = Math.PI / 180.0;

        /// <summary>Радианы → градусы.</summary>
        public const double RadToDeg = 180.0 / Math.PI;

        /// <summary>Метров в одном градусе широты (constant ≈ 111 195 м).</summary>
        public const double MetersPerDegLat = EarthRadiusM * DegToRad;

        // =====================================================================
        // Расстояния и пеленги
        // =====================================================================

        /// <summary>
        /// Расстояние между двумя точками по дуге большого круга (haversine), м.
        /// </summary>
        public static double DistanceM(double lat1, double lon1, double lat2, double lon2)
        {
            double phi1 = lat1 * DegToRad;
            double phi2 = lat2 * DegToRad;
            double dPhi = (lat2 - lat1) * DegToRad;
            double dLam = (lon2 - lon1) * DegToRad;

            double sDPhi = Math.Sin(dPhi * 0.5);
            double sDLam = Math.Sin(dLam * 0.5);
            double a = sDPhi * sDPhi + Math.Cos(phi1) * Math.Cos(phi2) * sDLam * sDLam;
            double c = 2.0 * Math.Atan2(Math.Sqrt(a), Math.Sqrt(1.0 - a));
            return EarthRadiusM * c;
        }

        /// <summary>
        /// Начальный пеленг (initial bearing) из точки 1 в точку 2, градусы [0..360).
        /// 0° = север, по часовой.
        /// </summary>
        public static double BearingDeg(double lat1, double lon1, double lat2, double lon2)
        {
            double phi1 = lat1 * DegToRad;
            double phi2 = lat2 * DegToRad;
            double dLam = (lon2 - lon1) * DegToRad;

            double y = Math.Sin(dLam) * Math.Cos(phi2);
            double x = Math.Cos(phi1) * Math.Sin(phi2)
                     - Math.Sin(phi1) * Math.Cos(phi2) * Math.Cos(dLam);

            double theta = Math.Atan2(y, x);
            return NormalizeHeadingDeg(theta * RadToDeg);
        }

        /// <summary>
        /// Новая точка, полученная смещением от исходной по пеленгу
        /// <paramref name="bearingDeg"/> на расстояние <paramref name="distanceM"/>.
        /// </summary>
        public static void OffsetByBearing(
            double lat, double lon, double bearingDeg, double distanceM,
            out double newLat, out double newLon)
        {
            double phi1 = lat * DegToRad;
            double lam1 = lon * DegToRad;
            double theta = bearingDeg * DegToRad;
            double delta = distanceM / EarthRadiusM;

            double sinPhi1 = Math.Sin(phi1);
            double cosPhi1 = Math.Cos(phi1);
            double sinDelta = Math.Sin(delta);
            double cosDelta = Math.Cos(delta);

            double phi2 = Math.Asin(sinPhi1 * cosDelta + cosPhi1 * sinDelta * Math.Cos(theta));
            double lam2 = lam1 + Math.Atan2(
                Math.Sin(theta) * sinDelta * cosPhi1,
                cosDelta - sinPhi1 * Math.Sin(phi2));

            newLat = phi2 * RadToDeg;
            newLon = lam2 * RadToDeg;
        }

        /// <summary>
        /// Боковое отклонение (cross-track) точки P от прямой A→B на сфере, м.
        /// <para>
        /// Положительное значение — P справа от трека A→B.
        /// Отрицательное — слева. Ноль — на линии.
        /// </para>
        /// </summary>
        public static double CrossTrackDistanceM(
            double latA, double lonA, double latB, double lonB, double latP, double lonP)
        {
            double d13 = DistanceM(latA, lonA, latP, lonP) / EarthRadiusM;
            double theta13 = BearingDeg(latA, lonA, latP, lonP) * DegToRad;
            double theta12 = BearingDeg(latA, lonA, latB, lonB) * DegToRad;

            double xt = Math.Asin(Math.Sin(d13) * Math.Sin(theta13 - theta12));
            return xt * EarthRadiusM;
        }

        // =====================================================================
        // Инкрементальное обновление позиции (hot path физики)
        // =====================================================================

        /// <summary>
        /// Обновить lat/lon на NED-смещение (м). Используется физикой на каждом
        /// тике для интегрирования позиции от скорости: <c>dn = Vn*dt, de = Ve*dt</c>.
        /// <para>
        /// Плоская аппроксимация. Для dt ≤ 0.1 с и скоростей ≤ 50 м/с —
        /// ошибка ≤ сантиметров.
        /// </para>
        /// <para>
        /// <c>cos(lat)</c> берётся от текущей (обновлённой) широты — чтобы не
        /// накапливать дрейф при долгом полёте с большим изменением широты.
        /// </para>
        /// </summary>
        public static void AdvanceLatLon(ref double lat, ref double lon, double dnM, double deM)
        {
            // Сначала обновляем широту.
            lat += dnM / MetersPerDegLat;

            // Потом долготу с cos от новой широты.
            double cosLat = Math.Cos(lat * DegToRad);
            double mPerDegLon = MetersPerDegLat * cosLat;

            // Защита от полюсов.
            if (Math.Abs(mPerDegLon) > 0.001)
                lon += deM / mPerDegLon;
        }

        // =====================================================================
        // Local ENU (малые расстояния, плоская аппроксимация)
        // =====================================================================

        /// <summary>
        /// Перевести local ENU (м) относительно reference-точки в lat/lon.
        /// Точность ≤ 1 м на расстояниях ≤ 10 км.
        /// </summary>
        public static void LocalEnuToLatLon(
            double refLat, double refLon, double eastM, double northM,
            out double lat, out double lon)
        {
            lat = refLat + northM / MetersPerDegLat;

            double cosRefLat = Math.Cos(refLat * DegToRad);
            double mPerDegLon = MetersPerDegLat * cosRefLat;

            lon = (Math.Abs(mPerDegLon) > 0.001) ? refLon + eastM / mPerDegLon : refLon;
        }

        /// <summary>
        /// Перевести lat/lon в local ENU (м) относительно reference-точки.
        /// Точность ≤ 1 м на расстояниях ≤ 10 км.
        /// </summary>
        public static void LatLonToLocalEnu(
            double refLat, double refLon, double lat, double lon,
            out double eastM, out double northM)
        {
            northM = (lat - refLat) * MetersPerDegLat;

            double cosRefLat = Math.Cos(refLat * DegToRad);
            double mPerDegLon = MetersPerDegLat * cosRefLat;

            eastM = (lon - refLon) * mPerDegLon;
        }

        // =====================================================================
        // Угловые операции
        // =====================================================================

        /// <summary>
        /// Нормализовать курс в диапазон [0..360).
        /// </summary>
        public static double NormalizeHeadingDeg(double deg)
        {
            double d = deg % 360.0;
            return d < 0 ? d + 360.0 : d;
        }

        /// <summary>
        /// Знаковая кратчайшая разность двух курсов, градусы [-180..180].
        /// <para>
        /// Пример: <c>AngleDiffDeg(350, 10) = +20</c> (поворот по часовой через 0°).
        /// </para>
        /// <para>
        /// Пример: <c>AngleDiffDeg(10, 350) = -20</c> (против часовой).
        /// </para>
        /// </summary>
        public static double AngleDiffDeg(double fromDeg, double toDeg)
        {
            // Формула: ((d % 360) + 540) % 360 - 180 — всегда даёт [-180..180].
            double d = (toDeg - fromDeg) % 360.0;
            d = (d + 540.0) % 360.0 - 180.0;
            return d;
        }
    }
}