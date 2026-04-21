using System;
using System.Threading;

namespace SimpleDroneGCS.Simulator.Core
{
    public enum VehicleType : byte
    {
        Vtol = 0,
        Copter = 1,
    }

    public enum LandedStateKind : byte
    {
        Undefined = 0,
        OnGround = 1,
        InAir = 2,
        Takeoff = 3,
        Landing = 4,
    }

    // =========================================================================
    // Подструктуры состояния
    // =========================================================================

    /// <summary>Позиция в WGS-84.</summary>
    public struct PositionState
    {
        /// <summary>Широта, градусы.</summary>
        public double Lat;
        /// <summary>Долгота, градусы.</summary>
        public double Lon;
        /// <summary>Высота над уровнем моря (AMSL), м.</summary>
        public double AltAmsl;
        /// <summary>Высота над HOME, м (обновляется вместе с AltAmsl).</summary>
        public double AltRelative;
    }

    /// <summary>HOME point. Фиксируется обычно при ARM на земле.</summary>
    public struct HomeState
    {
        /// <summary>HOME установлен. До этого Home считается невалидным для возврата.</summary>
        public bool Set;
        public double Lat;
        public double Lon;
        /// <summary>AMSL, м.</summary>
        public double AltAmsl;
    }

    /// <summary>
    /// Ориентация и угловые скорости в body-frame.
    /// Все углы в радианах, скорости в рад/с (стандарт MAVLink ATTITUDE).
    /// </summary>
    public struct AttitudeState
    {
        public double Roll;
        public double Pitch;
        public double Yaw;
        public double RollSpeed;
        public double PitchSpeed;
        public double YawSpeed;
    }

    /// <summary>
    /// Скорости. Raw компоненты в NED (North-East-Down), производные — как computed.
    /// </summary>
    public struct VelocityState
    {
        /// <summary>Скорость на север, м/с.</summary>
        public double Vn;
        /// <summary>Скорость на восток, м/с.</summary>
        public double Ve;
        /// <summary>Скорость вниз, м/с (положительно = снижение).</summary>
        public double Vd;

        /// <summary>Воздушная скорость (TAS), м/с. Важно для VTOL в FW-режиме.</summary>
        public double AirSpeed;

        /// <summary>Текущий throttle, % (0..100).</summary>
        public ushort ThrottlePercent;

        /// <summary>Наземная скорость = √(Vn² + Ve²), м/с. Computed.</summary>
        public readonly double GroundSpeed => Math.Sqrt(Vn * Vn + Ve * Ve);

        /// <summary>Вертикальная скорость = -Vd, м/с. Computed.</summary>
        public readonly double Climb => -Vd;
    }

    /// <summary>Батарея.</summary>
    public struct BatteryState
    {
        public double VoltageV;
        public double CurrentA;
        /// <summary>Заряд, 0..100.</summary>
        public double Percent;
        /// <summary>Потрачено ёмкости, мА·ч.</summary>
        public double ConsumedMah;
    }

    /// <summary>GPS (для MAVLink GPS_RAW_INT).</summary>
    public struct GpsState
    {
        /// <summary>
        /// MAVLink GPS_FIX_TYPE: 0=no fix, 2=2D, 3=3D, 4=DGPS, 5=RTK float, 6=RTK fixed.
        /// </summary>
        public byte FixType;
        public byte Satellites;
        /// <summary>HDOP (1.0 = хороший, 99.99 = нет).</summary>
        public double Hdop;
        public double Vdop;
        /// <summary>Оценка горизонтальной точности, м.</summary>
        public double Eph;
        /// <summary>Оценка вертикальной точности, м.</summary>
        public double Epv;
    }

    /// <summary>Здоровье EKF (для EKF_STATUS_REPORT и ESTIMATOR_STATUS).</summary>
    public struct EkfHealthState
    {
        public bool Healthy;
        public double VelVariance;
        public double PosHorizVariance;
        public double PosVertVariance;
        public double CompassVariance;
        public double TerrainAltVariance;
        /// <summary>Битовая маска EKF_STATUS_FLAGS.</summary>
        public ushort Flags;
    }

    /// <summary>Вибрации (для MAVLink VIBRATION).</summary>
    public struct VibrationState
    {
        public double X;
        public double Y;
        public double Z;
        public uint Clipping0;
        public uint Clipping1;
        public uint Clipping2;
    }

    /// <summary>Rangefinder (лидар/радар для точной посадки).</summary>
    public struct RangefinderState
    {
        public bool Enabled;
        /// <summary>Расстояние до поверхности, м.</summary>
        public double Distance;
        public byte Quality;
    }

    /// <summary>Ветер (для MAVLink WIND).</summary>
    public struct WindState
    {
        public double DirectionDeg;
        public double SpeedMs;
        public double SpeedZMs;
    }

    /// <summary>Навигация (для MAVLink NAV_CONTROLLER_OUTPUT).</summary>
    public struct NavStatusState
    {
        public double NavBearingDeg;
        public double TargetBearingDeg;
        /// <summary>Дистанция до текущей WP, м.</summary>
        public double WpDistance;
        public double AltError;
        public double AspdError;
        public double XtrackError;
    }

    /// <summary>Радио-линк (для MAVLink RADIO_STATUS).</summary>
    public struct RadioStatusState
    {
        public byte RssiLocal;
        public byte RssiRemote;
        public byte Noise;
        public byte RemoteNoise;
        public ushort RxErrors;
        public byte TxBuf;
    }

    /// <summary>
    /// Флаги активных отказов. Ставит FailureInjector, читают Physics и MavlinkOutbound.
    /// </summary>
    public struct FailureFlags
    {
        public bool GpsLoss;
        public bool RcFailsafe;
        public bool BatteryLow;
        public bool BatteryCritical;
        /// <summary>Индекс отказавшего мотора (0..N-1) или -1 если нет отказа.</summary>
        public int MotorFailureIndex;
        public bool CompassError;
        public bool EkfDivergence;
    }

    // =========================================================================
    // Главный класс состояния
    // =========================================================================

    /// <summary>
    /// Полное состояние симулированного дрона.
    /// <para>
    /// <b>Writer</b> — поток SimClock (Physics + Control). <b>Readers</b> — MavlinkOutbound thread, UI thread.
    /// </para>
    /// <para>
    /// Thread-safety:
    /// — обновления оборачивать в <c>using (state.Write()) { ... }</c>
    /// — читать через <see cref="Snapshot"/>, который возвращает независимую копию
    /// </para>
    /// <para>
    /// Lock reentrant (Monitor), вложенные Write() допустимы.
    /// </para>
    /// </summary>
    public sealed class SimState
    {
        private readonly object _sync = new();

        // ---- Top-level ----

        /// <summary>Тип ВС.</summary>
        public VehicleType Vehicle;

        /// <summary>Статус ARM.</summary>
        public bool Armed;

        /// <summary>
        /// Номер режима ArduPilot (custom_mode из HEARTBEAT).
        /// Номера разные для Plane и Copter (константы в FlightController).
        /// </summary>
        public uint CustomMode;

        /// <summary>Состояние посадки для EXTENDED_SYS_STATE.</summary>
        public LandedStateKind LandedState;

        /// <summary>Текущая WP для MISSION_CURRENT (0 если нет активной миссии).</summary>
        public ushort CurrentMissionSeq;

        // ---- Groups ----

        public PositionState Position;
        public HomeState Home;
        public AttitudeState Attitude;
        public VelocityState Velocity;
        public BatteryState Battery;
        public GpsState Gps;
        public EkfHealthState Ekf;
        public VibrationState Vibration;
        public RangefinderState Rangefinder;
        public WindState Wind;
        public NavStatusState NavStatus;
        public RadioStatusState Radio;
        public FailureFlags Failures;

        // ---- Arrays (ссылки фиксированы, содержимое меняется) ----

        /// <summary>
        /// PWM всех 16 servo выходов, мкс (1000..2000). Индекс 0 = SERVO1.
        /// Для VTOL X-рамы: SERVO1..4 — лифт-моторы M1..M4, SERVO5 — pusher, SERVO6+ — аэроповерхности.
        /// </summary>
        public readonly ushort[] ServoPwm = new ushort[16];

        /// <summary>PWM всех 18 RC каналов, мкс (обычно 1000..2000).</summary>
        public readonly ushort[] RcChannels = new ushort[18];

        /// <summary>RSSI приёмника RC (0..254, 255 = неизвестно).</summary>
        public byte RcRssi;

        // =====================================================================
        // Lifecycle
        // =====================================================================

        /// <summary>
        /// Создать новое состояние с дефолтами под указанный тип ВС и HOME.
        /// </summary>
        public static SimState CreateDefault(
            VehicleType vehicle, double homeLat, double homeLon, double homeAltAmsl)
        {
            var s = new SimState();
            s.ResetToDefaults(vehicle, homeLat, homeLon, homeAltAmsl);
            return s;
        }

        /// <summary>
        /// Сбросить все поля к дефолтам. Под Write-lock.
        /// </summary>
        public void ResetToDefaults(
            VehicleType vehicle, double homeLat, double homeLon, double homeAltAmsl)
        {
            using (Write())
            {
                Vehicle = vehicle;
                Armed = false;
                CurrentMissionSeq = 0;
                LandedState = LandedStateKind.OnGround;

                // VTOL → QSTABILIZE (17, ArduPlane). Copter → STABILIZE (0, ArduCopter).
                CustomMode = vehicle == VehicleType.Vtol ? 17u : 0u;

                // Позиция совпадает с HOME.
                Position.Lat = homeLat;
                Position.Lon = homeLon;
                Position.AltAmsl = homeAltAmsl;
                Position.AltRelative = 0.0;

                // HOME не "Set" пока не произошёл ARM на земле.
                Home.Set = false;
                Home.Lat = homeLat;
                Home.Lon = homeLon;
                Home.AltAmsl = homeAltAmsl;

                Attitude = default;
                Velocity = default;

                // Батарея — полная. VTOL: 6S LiPo (25.2V). Copter: 4S (16.8V).
                Battery.Percent = 100.0;
                Battery.VoltageV = vehicle == VehicleType.Vtol ? 25.2 : 16.8;
                Battery.CurrentA = 0.0;
                Battery.ConsumedMah = 0.0;

                // GPS — сразу 3D-fix для удобства. Для имитации холодного старта
                // использовать FailureInjector.GpsLoss.
                Gps.FixType = 3;
                Gps.Satellites = 14;
                Gps.Hdop = 0.8;
                Gps.Vdop = 1.2;
                Gps.Eph = 1.0;
                Gps.Epv = 1.5;

                Ekf.Healthy = true;
                Ekf.VelVariance = 0.1;
                Ekf.PosHorizVariance = 0.1;
                Ekf.PosVertVariance = 0.1;
                Ekf.CompassVariance = 0.05;
                Ekf.TerrainAltVariance = 0.0;
                Ekf.Flags = 0x1FFF; // основные флаги OK

                Vibration.X = 2.0; Vibration.Y = 2.0; Vibration.Z = 3.0;
                Vibration.Clipping0 = Vibration.Clipping1 = Vibration.Clipping2 = 0;

                Rangefinder = default;
                Wind = default;
                NavStatus = default;

                Radio.RssiLocal = 200;
                Radio.RssiRemote = 195;
                Radio.Noise = 30;
                Radio.RemoteNoise = 32;
                Radio.RxErrors = 0;
                Radio.TxBuf = 90;

                Failures = new FailureFlags { MotorFailureIndex = -1 };

                // Servo PWM — idle disarmed.
                for (int i = 0; i < ServoPwm.Length; i++) ServoPwm[i] = 1000;

                // RC каналы — midpoint, кроме throttle (канал 3, индекс 2) = low.
                for (int i = 0; i < RcChannels.Length; i++) RcChannels[i] = 1500;
                RcChannels[2] = 1000;
                RcRssi = 200;
            }
        }

        // =====================================================================
        // Thread-safety
        // =====================================================================

        /// <summary>
        /// Захватить lock для группы изменений. Обязательно использовать через <c>using</c>:
        /// <code>
        /// using (state.Write())
        /// {
        ///     state.Position.Lat = newLat;
        ///     state.Attitude.Yaw = newYaw;
        /// }
        /// </code>
        /// </summary>
        public WriteScope Write() => new WriteScope(_sync);

        /// <summary>
        /// Получить согласованный снимок состояния. Массивы копируются.
        /// Используется читателями (MAVLink, UI) для безопасной работы без lock.
        /// </summary>
        public SimState Snapshot()
        {
            lock (_sync)
            {
                var copy = new SimState
                {
                    Vehicle = Vehicle,
                    Armed = Armed,
                    CustomMode = CustomMode,
                    LandedState = LandedState,
                    CurrentMissionSeq = CurrentMissionSeq,
                    Position = Position,
                    Home = Home,
                    Attitude = Attitude,
                    Velocity = Velocity,
                    Battery = Battery,
                    Gps = Gps,
                    Ekf = Ekf,
                    Vibration = Vibration,
                    Rangefinder = Rangefinder,
                    Wind = Wind,
                    NavStatus = NavStatus,
                    Radio = Radio,
                    Failures = Failures,
                    RcRssi = RcRssi,
                };
                Array.Copy(ServoPwm, copy.ServoPwm, ServoPwm.Length);
                Array.Copy(RcChannels, copy.RcChannels, RcChannels.Length);
                return copy;
            }
        }

        /// <summary>
        /// Scope для записи. Monitor-based (reentrant), zero-GC struct.
        /// </summary>
        public struct WriteScope : IDisposable
        {
            private readonly object _lock;
            private bool _taken;

            internal WriteScope(object lo)
            {
                _lock = lo;
                _taken = false;
                Monitor.Enter(_lock, ref _taken);
            }

            public void Dispose()
            {
                if (_taken)
                {
                    Monitor.Exit(_lock);
                    _taken = false;
                }
            }
        }
    }
}