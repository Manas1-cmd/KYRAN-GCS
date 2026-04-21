using System;

namespace SimpleDroneGCS.Simulator.Core
{
    // =========================================================================
    // Управляющие режимы
    // =========================================================================

    /// <summary>
    /// Базовые режимы управления, которые понимает физика ВС.
    /// <para>
    /// Ortho к ArduPilot-режимам (QHOVER/AUTO/RTL/...): FlightController транслирует
    /// ArduPilot-режим + фазу миссии в один из этих базовых.
    /// </para>
    /// </summary>
    public enum ControlMode : byte
    {
        /// <summary>Disarmed или на земле без команд. Моторы на минимуме.</summary>
        Idle = 0,

        /// <summary>
        /// Мультироторный режим. Держим позицию/высоту или движемся к цели
        /// в горизонтали через наклон рамы.
        /// </summary>
        Multirotor = 1,

        /// <summary>
        /// Самолётный режим (только для ВС с <see cref="ISimVehicle.SupportsFwMode"/> = true).
        /// Летим как plane: подъёмная сила от крыла, тяга от pusher'а.
        /// </summary>
        FixedWing = 2,

        /// <summary>Переход MC → FW (разгон pusher'ом до v_min, лифт-моторы в idle).</summary>
        TransitionToFw = 3,

        /// <summary>Переход FW → MC (торможение drag, запуск лифт-моторов).</summary>
        TransitionToMc = 4,

        /// <summary>Контролируемый взлёт до заданной высоты (vertical).</summary>
        Takeoff = 5,

        /// <summary>Контролируемая посадка (vertical descent).</summary>
        Landing = 6,
    }

    // =========================================================================
    // Команда управления
    // =========================================================================

    /// <summary>
    /// Команда управления, которую <see cref="ISimVehicle"/> получает от FlightController.
    /// <para>
    /// Структура иммутабельна (readonly record struct) — передаётся по значению через <c>in</c>,
    /// zero-GC и thread-safe для передачи.
    /// </para>
    /// <para>
    /// Используется "навигационный" уровень команд: Vehicle сам разбирается как через
    /// каскад position→velocity→attitude→rate→motor получить требуемое поведение.
    /// </para>
    /// <para>
    /// Сентинел <see cref="double.NaN"/> означает "не управлять этим параметром".
    /// Значение 0 — валидное (0° yaw или 0 м/с), поэтому NaN как "нет значения".
    /// </para>
    /// </summary>
    public readonly record struct ControlCommand
    {
        /// <summary>Базовый режим управления.</summary>
        public ControlMode Mode { get; init; } = ControlMode.Idle;

        /// <summary>
        /// Есть ли цель в координатах (lat/lon/alt). Если false — физика держит
        /// текущую позицию (hover для MC, loiter для FW).
        /// </summary>
        public bool HasPositionTarget { get; init; } = false;

        /// <summary>Целевая широта, градусы (действительно при <see cref="HasPositionTarget"/>).</summary>
        public double TargetLat { get; init; } = double.NaN;

        /// <summary>Целевая долгота, градусы.</summary>
        public double TargetLon { get; init; } = double.NaN;

        /// <summary>Целевая высота над HOME, м. NaN = держать текущую.</summary>
        public double TargetAltRelative { get; init; } = double.NaN;

        /// <summary>
        /// Желаемая скорость приближения к цели, м/с. 0 = использовать default'ы ВС
        /// (WPNAV_SPEED для MC, AIRSPEED_CRUISE для FW).
        /// </summary>
        public double TargetSpeedMs { get; init; } = 0.0;

        /// <summary>Желаемый yaw в MC-режимах, градусы [0..360). NaN = не управлять (freewheel).</summary>
        public double TargetYawDeg { get; init; } = double.NaN;

        /// <summary>
        /// Желаемая воздушная скорость в FW-режиме, м/с. 0 = использовать default.
        /// </summary>
        public double TargetAirspeedMs { get; init; } = 0.0;

        /// <summary>Максимальный throttle 0..1. 1.0 = без ограничений.</summary>
        public double ThrottleMax { get; init; } = 1.0;

        /// <summary>Обязательный parameterless ctor для init-инициализаторов в C# 10+.</summary>
        public ControlCommand() { }
    }

    // =========================================================================
    // Интерфейс симулированного ВС
    // =========================================================================

    /// <summary>
    /// Физическая модель симулированного ВС (Copter или VTOL).
    /// <para>
    /// Реализации: <c>CopterDynamics</c>, <c>VtolDynamics</c>.
    /// </para>
    /// <para>
    /// <b>Thread:</b> все методы вызываются из потока SimClock (writer).
    /// Для чтения состояния — через <see cref="SimState.Snapshot"/>.
    /// </para>
    /// <para>
    /// <b>Контракт по исключениям:</b> <see cref="Step"/> и <see cref="ApplyControl"/>
    /// НЕ бросают исключения. При числовом сбое (NaN, overflow) — clamp к безопасным
    /// значениям и продолжают работу.
    /// </para>
    /// </summary>
    public interface ISimVehicle
    {
        // -------------------- Identity & Capabilities --------------------

        /// <summary>Тип ВС (VTOL или Copter).</summary>
        VehicleType Vehicle { get; }

        /// <summary>
        /// Количество моторов. Для VTOL = 5 (4 лифт M1-M4 + pusher M5), для Copter = 4.
        /// Нужно FailureInjector для MotorFailure (индекс в диапазоне).
        /// </summary>
        int MotorCount { get; }

        /// <summary>Поддерживается ли FW-режим и переходы (true для VTOL, false для Copter).</summary>
        bool SupportsFwMode { get; }

        /// <summary>Масса ВС, кг. Для физики и HUD.</summary>
        double MassKg { get; }

        /// <summary>Ёмкость батареи, мА·ч. Для расчёта <c>Battery.Percent</c> из ConsumedMah.</summary>
        double BatteryCapacityMah { get; }

        /// <summary>
        /// Номинальное напряжение полностью заряженной батареи, В.
        /// Для VTOL (6S LiPo) ≈ 25.2, для Copter (4S) ≈ 16.8.
        /// </summary>
        double BatteryNominalV { get; }

        // -------------------- Lifecycle --------------------

        /// <summary>
        /// Сброс внутренних состояний (PID-аккумуляторы, фильтры, фазы переходов).
        /// Вызывается при старте симулятора и при смене HOME/типа ВС.
        /// </summary>
        /// <param name="state">Текущее (уже сброшенное) состояние — для чтения defaults.</param>
        void Reset(SimState state);

        // -------------------- Control --------------------

        /// <summary>
        /// Применить новую команду управления. Обновляет внутренний setpoint.
        /// Вызывается FlightController'ом каждый тик (или при изменении режима).
        /// </summary>
        /// <param name="command">Команда. Передаётся по <c>in</c> — без копирования.</param>
        void ApplyControl(in ControlCommand command);

        // -------------------- Step --------------------

        /// <summary>
        /// Шаг физики на <paramref name="dt"/> секунд.
        /// Интегрирует динамику, обновляет <paramref name="state"/>:
        /// позицию, ориентацию, скорости, батарею, PWM всех моторов/сервоприводов.
        /// </summary>
        /// <param name="dt">Шаг в секундах. 0 = пауза, шаг пропустить.</param>
        /// <param name="state">Состояние для записи. Реализация берёт <c>state.Write()</c> внутри.</param>
        void Step(double dt, SimState state);
    }
}