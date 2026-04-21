using System;

namespace SimpleDroneGCS.Simulator.Physics
{
    /// <summary>
    /// Международная стандартная атмосфера (ISA / МСА) и физические константы.
    /// <para>
    /// Только pure functions, state отсутствует — thread-safe автоматически.
    /// </para>
    /// <para>
    /// Формулы из ICAO Doc 7488-CD, валидны для высот −1000м … +20000м.
    /// Для нашего применения (VTOL до 3 км) — с запасом.
    /// </para>
    /// </summary>
    public static class Atmosphere
    {
        // =====================================================================
        // Фундаментальные константы
        // =====================================================================

        /// <summary>Ускорение свободного падения на уровне моря, м/с².</summary>
        public const double G = 9.80665;

        /// <summary>Плотность воздуха на уровне моря (ρ₀), кг/м³. МСА.</summary>
        public const double SeaLevelDensity = 1.225;

        /// <summary>Давление на уровне моря (p₀), Па. МСА.</summary>
        public const double SeaLevelPressure = 101325.0;

        /// <summary>Температура на уровне моря (T₀), К (15°C). МСА.</summary>
        public const double SeaLevelTemperatureK = 288.15;

        /// <summary>Температурный градиент тропосферы (L), К/м.</summary>
        public const double LapseRate = 0.0065;

        /// <summary>Удельная газовая постоянная сухого воздуха (R), Дж/(кг·К).</summary>
        public const double GasConstantAir = 287.058;

        /// <summary>Верхняя граница тропосферы, м (выше — изотермическая стратосфера).</summary>
        public const double StratosphereBase = 11000.0;

        // =====================================================================
        // Модель МСА
        // =====================================================================

        /// <summary>
        /// Температура воздуха на высоте <paramref name="altitudeAmslM"/>, К.
        /// До 11 км — линейный спад, выше — const (нижняя стратосфера изотермическая).
        /// </summary>
        public static double AirTemperatureK(double altitudeAmslM)
        {
            if (altitudeAmslM < StratosphereBase)
                return SeaLevelTemperatureK - LapseRate * altitudeAmslM;

            // Изотермический слой 11 км … 20 км: T ≈ 216.65 К.
            return SeaLevelTemperatureK - LapseRate * StratosphereBase;
        }

        /// <summary>
        /// Атмосферное давление на высоте <paramref name="altitudeAmslM"/>, Па.
        /// Используется для возможной имитации барометра и в расчёте плотности.
        /// </summary>
        public static double AirPressure(double altitudeAmslM)
        {
            if (altitudeAmslM < StratosphereBase)
            {
                // Тропосфера: p = p₀ · (1 − L·h/T₀)^(g/(L·R))
                double tRatio = 1.0 - LapseRate * altitudeAmslM / SeaLevelTemperatureK;
                if (tRatio <= 0) tRatio = 1e-9; // защита от отрицательных при аномальных h
                return SeaLevelPressure * Math.Pow(tRatio, G / (LapseRate * GasConstantAir));
            }

            // Стратосфера: p = p₁₁ · exp(−g·(h−h₁₁)/(R·T₁₁))
            double p11 = SeaLevelPressure * Math.Pow(
                1 - LapseRate * StratosphereBase / SeaLevelTemperatureK,
                G / (LapseRate * GasConstantAir));
            double t11 = SeaLevelTemperatureK - LapseRate * StratosphereBase;
            return p11 * Math.Exp(-G * (altitudeAmslM - StratosphereBase) / (GasConstantAir * t11));
        }

        /// <summary>
        /// Плотность воздуха на высоте <paramref name="altitudeAmslM"/>, кг/м³.
        /// <para>
        /// Критическая для FW-режима VTOL: <c>Lift = ½·ρ·v²·S·C_L</c>.
        /// На 0 м = 1.225, на 1000 м ≈ 1.112, на 3000 м ≈ 0.909.
        /// </para>
        /// </summary>
        public static double AirDensity(double altitudeAmslM)
        {
            double t = AirTemperatureK(altitudeAmslM);
            if (t <= 0) t = 1.0; // защита от деления на 0
            return AirPressure(altitudeAmslM) / (GasConstantAir * t);
        }
    }

    // =========================================================================
    // Ветер
    // =========================================================================

    /// <summary>
    /// Модель постоянного ветра. Мутабельна (изменяется из UI), читается физикой
    /// на каждом тике. Thread-safe.
    /// <para>
    /// В MVP — константный вектор. В будущем: турбулентность, порывы, сдвиг.
    /// </para>
    /// <para>
    /// Используется соглашение метеорологии: <see cref="DirectionDeg"/> —
    /// откуда дует (как в MAVLink WIND message). "Ветер с севера" = 0°, вектор
    /// направлен на юг.
    /// </para>
    /// </summary>
    public sealed class WindModel
    {
        private readonly object _lock = new();
        private double _directionDeg;
        private double _speedMs;
        private double _verticalMs;

        /// <summary>Безветрие (все компоненты = 0).</summary>
        public WindModel() { }

        /// <param name="directionDeg">Откуда дует, [0..360).</param>
        /// <param name="speedMs">Горизонтальная скорость, м/с.</param>
        /// <param name="verticalMs">Вертикальная составляющая (вверх +), м/с.</param>
        public WindModel(double directionDeg, double speedMs, double verticalMs = 0.0)
        {
            Set(directionDeg, speedMs, verticalMs);
        }

        /// <summary>Откуда дует, градусы [0..360).</summary>
        public double DirectionDeg { get { lock (_lock) return _directionDeg; } }

        /// <summary>Горизонтальная скорость ветра, м/с (≥ 0).</summary>
        public double SpeedMs { get { lock (_lock) return _speedMs; } }

        /// <summary>Вертикальная составляющая ветра (вверх положительно), м/с.</summary>
        public double VerticalMs { get { lock (_lock) return _verticalMs; } }

        /// <summary>Безветрие (скорости практически нулевые).</summary>
        public bool IsCalm
        {
            get
            {
                lock (_lock)
                    return _speedMs < 0.01 && Math.Abs(_verticalMs) < 0.01;
            }
        }

        /// <summary>
        /// Задать ветер. Нормализует направление в [0..360),
        /// <paramref name="speedMs"/> clamp'ится к [0..∞).
        /// </summary>
        public void Set(double directionDeg, double speedMs, double verticalMs = 0.0)
        {
            // Нормализация направления в [0..360).
            double dir = directionDeg % 360.0;
            if (dir < 0) dir += 360.0;

            lock (_lock)
            {
                _directionDeg = dir;
                _speedMs = Math.Max(0.0, speedMs);
                _verticalMs = verticalMs;
            }
        }

        /// <summary>
        /// Получить вектор ветра в NED (North-East-Down), м/с.
        /// <para>
        /// Метеорологическое соглашение: ветер "с севера" (<see cref="DirectionDeg"/> = 0°)
        /// направлен на юг → <c>vn = −speed</c>, <c>ve = 0</c>.
        /// </para>
        /// </summary>
        public void GetWindNed(out double vn, out double ve, out double vd)
        {
            double dir, spd, vert;
            lock (_lock)
            {
                dir = _directionDeg;
                spd = _speedMs;
                vert = _verticalMs;
            }

            double rad = dir * Math.PI / 180.0;
            // "с" направления → вектор смотрит в противоположную сторону.
            vn = -spd * Math.Cos(rad);
            ve = -spd * Math.Sin(rad);
            // NED: down = положительно, up-wind → отрицательный vd.
            vd = -vert;
        }
    }
}