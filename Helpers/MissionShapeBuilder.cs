using System;
using System.Collections.Generic;

namespace SimpleDroneGCS.Helpers
{
    /// <summary>
    /// Режим секторного поиска — определяет плотность покрытия азимутов.
    /// </summary>
    public enum SectorMode
    {
        /// <summary>3 треугольника с шагом 30°. 9 вершин, 3 «слепые зоны» по 60° (60-120, 180-240, 300-360).
        /// Короче, быстрее, дешевле по топливу. Достаточно когда направление поиска примерно известно.</summary>
        Standard,

        /// <summary>4 треугольника с шагом 30°. 12 вершин с шагом 30° — полное покрытие 360° без слепых зон.
        /// На 33% длиннее по пути, зато гарантия что ни один азимут не пропущен.</summary>
        Dense
    }

    /// <summary>
    /// Генератор массивов точек миссии для типовых фигур.
    /// 
    /// Все методы детерминированы и не зависят от UI — на вход параметры,
    /// на выход список точек (lat, lon, alt). UI-слой потом оборачивает
    /// каждую точку в WaypointItem и вставляет в миссию.
    /// 
    /// Геодезическая модель: сфера R = 6371 км (Haversine / прямая задача).
    /// Точность для радиусов до ~50 км — доли процента (см. чат от 19.05.2026).
    /// </summary>
    public static class MissionShapeBuilder
    {
        public const double EARTH_RADIUS_M = 6_371_000.0;

        /// <summary>Одна точка фигуры. UI потом превращает в WaypointItem.</summary>
        public readonly struct ShapePoint
        {
            public readonly double Latitude;
            public readonly double Longitude;
            public readonly double Altitude;
            public ShapePoint(double lat, double lon, double alt)
            {
                Latitude = lat;
                Longitude = lon;
                Altitude = alt;
            }
        }

        // ────────────────────────────────────────────────────────────────────
        //  ПРИМИТИВЫ (прямая и обратная геодезические задачи на сфере)
        // ────────────────────────────────────────────────────────────────────

        /// <summary>
        /// Прямая задача: сдвиг от точки на distM метров в направлении bearingDeg°.
        /// Bearing отсчитывается от севера по часовой стрелке (0=N, 90=E, 180=S, 270=W).
        /// Результат всегда в стандартных диапазонах: lat ∈ [-90, 90], lon ∈ [-180, 180].
        /// </summary>
        public static (double lat, double lon) MoveByBearing(
            double lat, double lon, double bearingDeg, double distM)
        {
            double br = bearingDeg * Math.PI / 180.0;
            double lat1 = lat * Math.PI / 180.0;
            double lon1 = lon * Math.PI / 180.0;
            double d = distM / EARTH_RADIUS_M;

            double lat2 = Math.Asin(
                Math.Sin(lat1) * Math.Cos(d) +
                Math.Cos(lat1) * Math.Sin(d) * Math.Cos(br));

            double lon2 = lon1 + Math.Atan2(
                Math.Sin(br) * Math.Sin(d) * Math.Cos(lat1),
                Math.Cos(d) - Math.Sin(lat1) * Math.Sin(lat2));

            // Нормализация долготы в [-π, π] — на случай перехода через антимеридиан (180°).
            // Без неё переход через 180° даст значения вроде 180.26°, что некорректно
            // как нормализованная долгота (хотя сама точка геометрически правильная).
            lon2 = ((lon2 + 3 * Math.PI) % (2 * Math.PI)) - Math.PI;

            return (lat2 * 180.0 / Math.PI, lon2 * 180.0 / Math.PI);
        }

        /// <summary>
        /// Обратная задача: дистанция между двумя точками (Haversine, метры).
        /// Дублирует логику FlightPlanView.CalculateDistanceLatLng для самодостаточности.
        /// </summary>
        public static double DistanceMeters(double lat1, double lon1, double lat2, double lon2)
        {
            double dLat = (lat2 - lat1) * Math.PI / 180.0;
            double dLon = (lon2 - lon1) * Math.PI / 180.0;
            double a = Math.Sin(dLat / 2) * Math.Sin(dLat / 2) +
                       Math.Cos(lat1 * Math.PI / 180.0) * Math.Cos(lat2 * Math.PI / 180.0) *
                       Math.Sin(dLon / 2) * Math.Sin(dLon / 2);
            double c = 2 * Math.Atan2(Math.Sqrt(a), Math.Sqrt(1 - a));
            return EARTH_RADIUS_M * c;
        }

        // ────────────────────────────────────────────────────────────────────
        //  ФИГУРЫ
        // ────────────────────────────────────────────────────────────────────

        /// <summary>
        /// 1. КРУГ — N точек по окружности вокруг центра.
        /// Используется для: орбитальной разведки POI, патрулирования вокруг точки.
        /// </summary>
        /// <param name="centerLat">Широта центра</param>
        /// <param name="centerLon">Долгота центра</param>
        /// <param name="radiusM">Радиус окружности в метрах</param>
        /// <param name="numPoints">Количество точек (4..360, рекомендуется 8-24)</param>
        /// <param name="altitude">Высота на всех точках (м)</param>
        /// <param name="clockwise">true = по часовой стрелке (с севера на восток)</param>
        /// <param name="startBearingDeg">С какого азимута начать (0 = с севера)</param>
        public static List<ShapePoint> Circle(
            double centerLat, double centerLon,
            double radiusM, int numPoints,
            double altitude,
            bool clockwise = true,
            double startBearingDeg = 0.0)
        {
            ValidateCoords(centerLat, centerLon, "Circle.center");
            ValidatePositive(radiusM, "radiusM");
            if (numPoints < 3) numPoints = 3;

            var points = new List<ShapePoint>(numPoints);
            double step = 360.0 / numPoints;
            int sign = clockwise ? 1 : -1;

            for (int i = 0; i < numPoints; i++)
            {
                double bearing = NormalizeBearing(startBearingDeg + sign * i * step);
                var (lat, lon) = MoveByBearing(centerLat, centerLon, bearing, radiusM);
                points.Add(new ShapePoint(lat, lon, altitude));
            }

            return points;
        }

        /// <summary>
        /// 2. ПРЯМОУГОЛЬНИК — 4 точки по углам прямоугольника заданного размера,
        /// опционально повёрнутого на rotationDeg вокруг центра.
        /// Используется для: патруля периметра зоны.
        /// </summary>
        /// <param name="centerLat">Широта центра прямоугольника</param>
        /// <param name="centerLon">Долгота центра</param>
        /// <param name="widthM">Ширина (запад-восток до поворота), м</param>
        /// <param name="heightM">Высота/длина (юг-север до поворота), м</param>
        /// <param name="altitude">Высота полёта (м)</param>
        /// <param name="rotationDeg">Поворот всего прямоугольника вокруг центра, ° по часовой</param>
        /// <param name="clockwise">true = обход по часовой (NE→SE→SW→NW), false = против</param>
        public static List<ShapePoint> Rectangle(
            double centerLat, double centerLon,
            double widthM, double heightM,
            double altitude,
            double rotationDeg = 0.0,
            bool clockwise = true)
        {
            ValidateCoords(centerLat, centerLon, "Rectangle.center");
            ValidatePositive(widthM, "widthM");
            ValidatePositive(heightM, "heightM");

            // Полудиагональ и азимут до угла (без поворота): NE угол
            double halfW = widthM / 2.0;
            double halfH = heightM / 2.0;
            double diag = Math.Sqrt(halfW * halfW + halfH * halfH);
            // Угол от севера до NE-вершины (в локальной системе): atan2(восток, север)
            double cornerNE = Math.Atan2(halfW, halfH) * 180.0 / Math.PI;

            // Базовые азимуты 4 углов (NE, SE, SW, NW) — относительно севера, по часовой
            // Обход всегда начинается с NE-угла, независимо от направления:
            //   CW:  NE → SE → SW → NW
            //   CCW: NE → NW → SW → SE
            double[] baseAngles = clockwise
                ? new[] { cornerNE, 180 - cornerNE, 180 + cornerNE, 360 - cornerNE }
                : new[] { cornerNE, 360 - cornerNE, 180 + cornerNE, 180 - cornerNE };

            var points = new List<ShapePoint>(4);
            foreach (double a in baseAngles)
            {
                double bearing = NormalizeBearing(a + rotationDeg);
                var (lat, lon) = MoveByBearing(centerLat, centerLon, bearing, diag);
                points.Add(new ShapePoint(lat, lon, altitude));
            }

            return points;
        }

        /// <summary>
        /// 3. ЛИНИЯ — отрезок от A до B, разбитый на (numSegments + 1) точек.
        /// Используется для: маршрута A→B с промежуточными контрольными точками.
        /// Точки расположены равномерно по дистанции (линейная интерполяция).
        /// Для коротких расстояний (до ~10 км) погрешность относительно ортодромии незаметна.
        /// </summary>
        public static List<ShapePoint> Line(
            double startLat, double startLon,
            double endLat, double endLon,
            int numSegments,
            double startAltitude,
            double endAltitude)
        {
            ValidateCoords(startLat, startLon, "Line.start");
            ValidateCoords(endLat, endLon, "Line.end");
            if (numSegments < 1) numSegments = 1;
            var points = new List<ShapePoint>(numSegments + 1);

            for (int i = 0; i <= numSegments; i++)
            {
                double t = (double)i / numSegments;
                double lat = startLat + (endLat - startLat) * t;
                double lon = startLon + (endLon - startLon) * t;
                double alt = startAltitude + (endAltitude - startAltitude) * t;
                points.Add(new ShapePoint(lat, lon, alt));
            }

            return points;
        }

        /// <summary>
        /// 4. ЗИГЗАГ / ЛУЖАЙКА (Lawnmower) — параллельные полосы внутри прямоугольной зоны.
        /// Эквивалент Survey Grid из Mission Planner для прямоугольной области.
        /// Используется для: сканирования зоны, разведки, площадной съёмки.
        /// 
        /// Алгоритм (с гарантией точного spacing):
        ///   - numIntervals = ceil(widthM / spacingM) — сколько промежутков между полосами
        ///   - numLines = numIntervals + 1 полос
        ///   - actualWidth = numIntervals · spacingM (может быть слегка больше widthM,
        ///     чтобы заданный spacing сохранился ТОЧНО — это важно если оператор
        ///     рассчитал spacing под камеру/датчик)
        ///   - Полосы расположены симметрично вокруг центра, с шагом ровно spacingM
        ///   - Чередуем направление полос (serpentine — без пустых перелётов)
        ///   - Опционально overshoot — пролёт за границу для плавного разворота
        /// 
        /// Гарантия: расстояние между двумя соседними полосами всегда РОВНО spacingM.
        /// </summary>
        /// <param name="centerLat">Широта центра зоны</param>
        /// <param name="centerLon">Долгота центра зоны</param>
        /// <param name="widthM">Ширина зоны (вдоль полос), м</param>
        /// <param name="heightM">Длина зоны (поперёк полос), м</param>
        /// <param name="spacingM">Расстояние между параллельными полосами, м (соблюдается точно)</param>
        /// <param name="altitude">Высота полёта (м)</param>
        /// <param name="rotationDeg">Поворот всего паттерна вокруг центра, °</param>
        /// <param name="overshootM">Перебег за границы для плавного разворота (0 = точно по границе)</param>
        public static List<ShapePoint> Lawnmower(
            double centerLat, double centerLon,
            double widthM, double heightM,
            double spacingM,
            double altitude,
            double rotationDeg = 0.0,
            double overshootM = 0.0)
        {
            ValidateCoords(centerLat, centerLon, "Lawnmower.center");
            ValidatePositive(widthM, "widthM");
            ValidatePositive(heightM, "heightM");
            ValidatePositive(spacingM, "spacingM");
            if (overshootM < 0) overshootM = 0;

            // Точный spacing: numIntervals определяется так, чтобы покрыть всю widthM,
            // но фактический шаг между полосами остаётся РОВНО spacingM.
            int numIntervals = Math.Max(1, (int)Math.Ceiling(widthM / spacingM));
            int numLines = numIntervals + 1;

            // Фактическая ширина покрытия (≥ widthM) — лишние сантиметры по краям
            // не страшны: для съёмки лучше чуть-чуть перекрытие чем «слепая» полоса.
            double actualWidth = numIntervals * spacingM;
            double halfActualW = actualWidth / 2.0;
            double halfH = heightM / 2.0 + overshootM;

            // rotation→радианы
            double rotRad = rotationDeg * Math.PI / 180.0;

            var points = new List<ShapePoint>(numLines * 2);

            for (int i = 0; i < numLines; i++)
            {
                // Локальная X-координата полосы: от -halfActualW до +halfActualW с шагом spacingM
                double xLocal = -halfActualW + i * spacingM;
                // Чередуем направление: чётные полосы — снизу вверх, нечётные — наоборот
                bool reverse = (i % 2 == 1);
                double yStart = reverse ? +halfH : -halfH;
                double yEnd = reverse ? -halfH : +halfH;

                points.Add(LocalToGlobal(centerLat, centerLon, xLocal, yStart, rotRad, altitude));
                points.Add(LocalToGlobal(centerLat, centerLon, xLocal, yEnd, rotRad, altitude));
            }

            return points;
        }

        /// <summary>
        /// 5. РАСШИРЯЮЩИЙСЯ КВАДРАТ (Expanding Square Search, SAR-стандарт IAMSAR).
        /// Используется для: поиска от последней известной точки (потеря цели/связи).
        /// 
        /// Геометрия по IAMSAR:
        ///   - Старт из центра, первый отрезок длиной trackSpacingM в направлении initialBearingDeg
        ///   - После каждого отрезка поворот на 90° (по часовой если CW)
        ///   - Длины отрезков: L, L, 2L, 2L, 3L, 3L, 4L, 4L, …
        ///   - numLoops — сколько витков (полный виток = 4 отрезка)
        /// </summary>
        /// <param name="centerLat">Широта центра (LKP — last known position)</param>
        /// <param name="centerLon">Долгота центра</param>
        /// <param name="trackSpacingM">Базовая длина L первого отрезка, м</param>
        /// <param name="numLoops">Количество витков (1 виток = 4 отрезка)</param>
        /// <param name="altitude">Высота полёта (м)</param>
        /// <param name="initialBearingDeg">Начальное направление первого отрезка (0 = на север)</param>
        /// <param name="clockwise">true = повороты по часовой</param>
        public static List<ShapePoint> ExpandingSquare(
            double centerLat, double centerLon,
            double trackSpacingM,
            int numLoops,
            double altitude,
            double initialBearingDeg = 0.0,
            bool clockwise = true)
        {
            ValidateCoords(centerLat, centerLon, "ExpandingSquare.center");
            ValidatePositive(trackSpacingM, "trackSpacingM");
            if (numLoops < 1) numLoops = 1;

            var points = new List<ShapePoint>();
            // Стартовая точка в центре (LKP)
            double curLat = centerLat;
            double curLon = centerLon;
            points.Add(new ShapePoint(curLat, curLon, altitude));

            double bearing = NormalizeBearing(initialBearingDeg);
            int turnSign = clockwise ? +1 : -1;

            // 4 отрезка на виток. Длины: L,L,2L,2L,3L,3L,...
            int totalLegs = numLoops * 4;
            for (int leg = 1; leg <= totalLegs; leg++)
            {
                int multiplier = (leg + 1) / 2;        // 1,1,2,2,3,3,...
                double legLen = multiplier * trackSpacingM;

                var (nLat, nLon) = MoveByBearing(curLat, curLon, bearing, legLen);
                curLat = nLat; curLon = nLon;
                points.Add(new ShapePoint(curLat, curLon, altitude));

                bearing = NormalizeBearing(bearing + turnSign * 90.0);
            }

            return points;
        }

        /// <summary>
        /// 6. СЕКТОРНЫЙ ПОИСК — равносторонние треугольники с общим центром,
        /// каждый повёрнут на 30° относительно предыдущего.
        /// Используется для: интенсивного поиска вокруг конкретной точки (POI/LKP).
        /// 
        /// Геометрия (Standard, 3 треугольника):
        ///   - Треугольник 0: вершины на 0°, 120°, 240° (плюс initialBearingDeg)
        ///   - Треугольник 1: 30°, 150°, 270°
        ///   - Треугольник 2: 60°, 180°, 300°
        ///   - Путь: центр → V1 → V2 → V3 → центр (для каждого треугольника)
        ///   - 13 точек, 9 уникальных внешних вершин
        ///   - 3 «слепые зоны» по 60° между группами (60↔120, 180↔240, 300↔360)
        /// 
        /// Геометрия (Dense, 4 треугольника):
        ///   - Добавляется треугольник 3: 90°, 210°, 330°
        ///   - 17 точек, 12 уникальных внешних вершин с шагом 30°
        ///   - Полное покрытие 360° без слепых зон
        ///   - Путь на ~33% длиннее чем Standard
        /// 
        /// Альтернатива: классический IAMSAR Sector Search строит треугольник
        ///   через 3 ноги (out-R, side-R, back-R) с поворотами 120°, но даёт
        ///   худшее покрытие и более сложную траекторию.
        /// </summary>
        /// <param name="centerLat">Широта центра</param>
        /// <param name="centerLon">Долгота центра</param>
        /// <param name="radiusM">Радиус поиска (длина ноги от центра до вершины), м</param>
        /// <param name="altitude">Высота полёта (м)</param>
        /// <param name="initialBearingDeg">С какого азимута стартовать (0 = на север)</param>
        /// <param name="mode">Standard (3 сектора, быстрее) или Dense (4 сектора, полное 360°)</param>
        public static List<ShapePoint> SectorSearch(
            double centerLat, double centerLon,
            double radiusM,
            double altitude,
            double initialBearingDeg = 0.0,
            SectorMode mode = SectorMode.Standard)
        {
            ValidateCoords(centerLat, centerLon, "SectorSearch.center");
            ValidatePositive(radiusM, "radiusM");

            var points = new List<ShapePoint>();
            var center = new ShapePoint(centerLat, centerLon, altitude);
            points.Add(center);

            // Standard = 3 сектора (есть слепые зоны), Dense = 4 сектора (полное покрытие)
            int numSectors = (mode == SectorMode.Dense) ? 4 : 3;

            // 4 сектора с шагом 30° дают вершины на 0,30,60,90,...,330° — все 12 кратные 30°
            // 3 сектора с шагом 30° дают 9 вершин (пропускают 90,210,330)
            for (int sector = 0; sector < numSectors; sector++)
            {
                double sectorOffset = sector * 30.0;
                double bear1 = NormalizeBearing(initialBearingDeg + sectorOffset);
                double bear2 = NormalizeBearing(bear1 + 120.0); // вершина 2 треугольника
                double bear3 = NormalizeBearing(bear1 + 240.0); // вершина 3 треугольника

                // Уходим из центра на 1-ю вершину
                var (l1, n1) = MoveByBearing(centerLat, centerLon, bear1, radiusM);
                points.Add(new ShapePoint(l1, n1, altitude));

                // Прямая ко 2-й вершине (через окрестность центра)
                var (l2, n2) = MoveByBearing(centerLat, centerLon, bear2, radiusM);
                points.Add(new ShapePoint(l2, n2, altitude));

                // Прямая к 3-й вершине
                var (l3, n3) = MoveByBearing(centerLat, centerLon, bear3, radiusM);
                points.Add(new ShapePoint(l3, n3, altitude));

                // Возврат в центр (закрытие треугольника + опорная точка для след. сектора)
                points.Add(center);
            }

            return points;
        }

        /// <summary>
        /// 7. СПИРАЛЬ АРХИМЕДА — плавная альтернатива Expanding Square.
        /// Для самолётного режима VTOL предпочтительнее: нет острых углов 90°.
        /// Используется для: поиска от центра, сканирования области с плавным расширением.
        /// 
        /// Алгоритм: r(θ) = r₀ + (spacingPerLoop / 2π) · θ
        /// Чем больше pointsPerLoop — тем более гладкая спираль.
        /// </summary>
        /// <param name="centerLat">Широта центра</param>
        /// <param name="centerLon">Долгота центра</param>
        /// <param name="startRadiusM">Начальный радиус (>0, чтобы не было точки в самом центре)</param>
        /// <param name="spacingPerLoopM">Прирост радиуса за один полный виток (= расстояние между витками)</param>
        /// <param name="numLoops">Количество витков</param>
        /// <param name="pointsPerLoop">Точек на один виток (8..36, рекомендуется 12-18)</param>
        /// <param name="altitude">Высота полёта (м)</param>
        /// <param name="clockwise">Направление закрутки</param>
        /// <param name="startBearingDeg">С какого азимута стартует первая точка</param>
        public static List<ShapePoint> Spiral(
            double centerLat, double centerLon,
            double startRadiusM,
            double spacingPerLoopM,
            int numLoops,
            int pointsPerLoop,
            double altitude,
            bool clockwise = true,
            double startBearingDeg = 0.0)
        {
            ValidateCoords(centerLat, centerLon, "Spiral.center");
            ValidatePositive(spacingPerLoopM, "spacingPerLoopM");
            if (startRadiusM <= 0) startRadiusM = 1.0;
            if (numLoops < 1) numLoops = 1;
            if (pointsPerLoop < 6) pointsPerLoop = 6;

            int totalPoints = numLoops * pointsPerLoop + 1;
            var points = new List<ShapePoint>(totalPoints);

            double angleStep = 360.0 / pointsPerLoop;
            int sign = clockwise ? 1 : -1;

            // r = r₀ + (spacing / 360°) · θ°
            double radiusGrowthPerDeg = spacingPerLoopM / 360.0;

            for (int i = 0; i < totalPoints; i++)
            {
                double angleFromStart = sign * i * angleStep;
                double radius = startRadiusM + i * angleStep * radiusGrowthPerDeg;
                double bearing = NormalizeBearing(startBearingDeg + angleFromStart);

                var (lat, lon) = MoveByBearing(centerLat, centerLon, bearing, radius);
                points.Add(new ShapePoint(lat, lon, altitude));
            }

            return points;
        }

        // ────────────────────────────────────────────────────────────────────
        //  ВНУТРЕННИЕ ХЕЛПЕРЫ
        // ────────────────────────────────────────────────────────────────────

        /// <summary>
        /// Преобразует точку в локальной системе координат (x — восток, y — север)
        /// в глобальную lat/lon относительно центра, с учётом поворота на rotRad.
        /// </summary>
        private static ShapePoint LocalToGlobal(
            double centerLat, double centerLon,
            double xLocal, double yLocal,
            double rotRad, double altitude)
        {
            // Поворачиваем (xLocal, yLocal) на rotRad по часовой стрелке (поскольку
            // компас отсчитывает азимут по часовой от севера).
            double cos = Math.Cos(rotRad);
            double sin = Math.Sin(rotRad);
            double xRot = xLocal * cos + yLocal * sin;
            double yRot = -xLocal * sin + yLocal * cos;

            // Получаем расстояние и азимут от центра до повёрнутой точки
            double dist = Math.Sqrt(xRot * xRot + yRot * yRot);
            if (dist < 1e-6)
                return new ShapePoint(centerLat, centerLon, altitude);

            // Азимут: atan2(восток, север) даёт направление от севера по часовой
            double bearing = Math.Atan2(xRot, yRot) * 180.0 / Math.PI;
            bearing = NormalizeBearing(bearing);

            var (lat, lon) = MoveByBearing(centerLat, centerLon, bearing, dist);
            return new ShapePoint(lat, lon, altitude);
        }

        /// <summary>Привести азимут к диапазону [0, 360).</summary>
        private static double NormalizeBearing(double deg)
        {
            double r = deg % 360.0;
            if (r < 0) r += 360.0;
            return r;
        }

        /// <summary>
        /// Проверка что координаты — корректные значения: не NaN, не Infinity,
        /// lat ∈ [-90, 90], lon ∈ [-180, 180]. Бросает ArgumentException иначе.
        /// </summary>
        private static void ValidateCoords(double lat, double lon, string what = "center")
        {
            if (double.IsNaN(lat) || double.IsInfinity(lat))
                throw new ArgumentException($"{what}: latitude is NaN/Infinity");
            if (double.IsNaN(lon) || double.IsInfinity(lon))
                throw new ArgumentException($"{what}: longitude is NaN/Infinity");
            if (lat < -90.0 || lat > 90.0)
                throw new ArgumentException($"{what}: latitude {lat} вне [-90, 90]");
            if (lon < -180.0 || lon > 180.0)
                throw new ArgumentException($"{what}: longitude {lon} вне [-180, 180]");
        }

        /// <summary>Проверка что число конечное и положительное.</summary>
        private static void ValidatePositive(double value, string name)
        {
            if (double.IsNaN(value) || double.IsInfinity(value))
                throw new ArgumentException($"{name} is NaN/Infinity");
            if (value <= 0)
                throw new ArgumentException($"{name} должен быть > 0, получено {value}");
        }
    }
}