using System;
using SimpleDroneGCS.Simulator.Core;

namespace SimpleDroneGCS.Simulator.Control
{
    // =========================================================================
    // Константы режимов (custom_mode из MAVLink HEARTBEAT)
    // =========================================================================

    /// <summary>Custom mode номера для ArduPlane (используется для VTOL).</summary>
    public static class PlaneMode
    {
        public const uint Manual = 0;
        public const uint Circle = 1;
        public const uint Stabilize = 2;
        public const uint Training = 3;
        public const uint Acro = 4;
        public const uint FbwA = 5;
        public const uint FbwB = 6;
        public const uint Cruise = 7;
        public const uint Auto = 10;
        public const uint Rtl = 11;
        public const uint Loiter = 12;
        public const uint Takeoff = 13;
        public const uint Guided = 15;
        public const uint QStabilize = 17;
        public const uint QHover = 18;
        public const uint QLoiter = 19;
        public const uint QLand = 20;
        public const uint QRtl = 21;
    }

    /// <summary>Custom mode номера для ArduCopter.</summary>
    public static class CopterMode
    {
        public const uint Stabilize = 0;
        public const uint Acro = 1;
        public const uint AltHold = 2;
        public const uint Auto = 3;
        public const uint Guided = 4;
        public const uint Loiter = 5;
        public const uint Rtl = 6;
        public const uint Circle = 7;
        public const uint Land = 9;
        public const uint PosHold = 16;
        public const uint Brake = 17;
        public const uint SmartRtl = 21;
    }

    // =========================================================================
    // Flight Controller
    // =========================================================================

    /// <summary>
    /// Связывает команды от GCS (ARM/DISARM, SET_MODE, GUIDED target) с
    /// <see cref="MissionExecutor"/> и <see cref="ISimVehicle"/>.
    /// <para>
    /// На каждом тике <see cref="Update"/> выдаёт <see cref="ControlCommand"/>
    /// в зависимости от <see cref="SimState.CustomMode"/>.
    /// </para>
    /// <para>
    /// <b>Thread-safety:</b> методы команд (<see cref="TryArm"/>,
    /// <see cref="SetMode"/>, ...) могут вызываться из MAVLink-потока.
    /// <see cref="Update"/> — из потока SimClock. Внутренние поля защищены.
    /// </para>
    /// </summary>
    public sealed class FlightController
    {
        private readonly SimState _state;
        private readonly MissionExecutor _executor;

        // GUIDED target (обновляется извне, читается в Update).
        private readonly object _guidedLock = new();
        private double _guidedLat, _guidedLon, _guidedAlt;
        private bool _guidedHasTarget;

        // -------------------- События --------------------

        /// <summary>Изменился custom_mode (для HEARTBEAT).</summary>
        public event EventHandler<uint> ModeChanged;

        /// <summary>Изменился статус ARM.</summary>
        public event EventHandler<bool> ArmStateChanged;

        /// <summary>STATUSTEXT сообщение (для MAVLink, лога, UI).</summary>
        public event EventHandler<string> StatusText;

        // -------------------- Constructor --------------------

        public FlightController(SimState state, MissionExecutor executor)
        {
            _state = state ?? throw new ArgumentNullException(nameof(state));
            _executor = executor ?? throw new ArgumentNullException(nameof(executor));
        }

        // =====================================================================
        // Команды от GCS
        // =====================================================================

        /// <summary>
        /// Попытка ARM. Если <paramref name="forceArm"/> = true — обход PreArm
        /// проверок (эквивалент MAV_CMD_COMPONENT_ARM_DISARM с param2 = 21196).
        /// </summary>
        /// <param name="forceArm">true = пропустить проверки.</param>
        /// <param name="failReason">Причина отказа (null при успехе).</param>
        /// <returns>true если armed после вызова.</returns>
        public bool TryArm(bool forceArm, out string failReason)
        {
            failReason = null;

            // Idempotent: если уже armed → OK.
            bool wasArmed;
            lock (_state) { }  // нет нужды — state.Armed читается атомарно
            // Снимем через Snapshot чтоб не держать Write-lock лишнее время.
            var snap = _state.Snapshot();
            wasArmed = snap.Armed;
            if (wasArmed) return true;

            // PreArm checks.
            if (!forceArm)
            {
                if (!CheckPreArm(snap, out failReason))
                {
                    EmitStatusText(failReason);
                    return false;
                }
            }

            // ARM + капча HOME если на земле.
            bool onGround = snap.Position.AltRelative < 0.5;
            using (_state.Write())
            {
                _state.Armed = true;
                if (onGround)
                {
                    _state.Home.Set = true;
                    _state.Home.Lat = _state.Position.Lat;
                    _state.Home.Lon = _state.Position.Lon;
                    _state.Home.AltAmsl = _state.Position.AltAmsl;
                }
            }

            ArmStateChanged?.Invoke(this, true);
            EmitStatusText(forceArm ? "Armed (forced)" : "Armed");
            return true;
        }

        /// <summary>
        /// DISARM. <paramref name="force"/> = true разрешает disarm в воздухе
        /// (приведёт к свободному падению в физике).
        /// </summary>
        public void Disarm(bool force)
        {
            var snap = _state.Snapshot();
            if (!snap.Armed) return;

            bool inAir = snap.Position.AltRelative > 0.5;
            if (inAir && !force)
            {
                EmitStatusText("Disarm refused: in air");
                return;
            }

            using (_state.Write()) _state.Armed = false;

            ArmStateChanged?.Invoke(this, false);
            EmitStatusText(inAir ? "Emergency disarm in air!" : "Disarmed");
        }

        /// <summary>
        /// Изменить flight mode (custom_mode). Валидация — минимальная:
        /// значение записывается в state. Событие <see cref="ModeChanged"/>
        /// выпускается для HEARTBEAT.
        /// </summary>
        public void SetMode(uint customMode)
        {
            uint prev;
            var snap = _state.Snapshot();
            prev = snap.CustomMode;
            if (prev == customMode) return;

            using (_state.Write()) _state.CustomMode = customMode;

            // Автозапуск миссии при переходе в AUTO.
            if (IsAutoMode(customMode, snap.Vehicle))
            {
                if (_executor.ItemCount > 0 &&
                    (_executor.State == MissionExecState.Idle ||
                     _executor.State == MissionExecState.Completed))
                {
                    _executor.Start();
                }
                else if (_executor.ItemCount == 0)
                {
                    EmitStatusText("AUTO: no mission loaded");
                }
            }

            ModeChanged?.Invoke(this, customMode);
            EmitStatusText("Mode: " + GetModeName(snap.Vehicle, customMode));
        }

        /// <summary>
        /// Задать цель для GUIDED-режима (от SET_POSITION_TARGET_GLOBAL_INT или
        /// MAV_CMD_DO_REPOSITION). Цель держится пока не заменена или
        /// <see cref="ClearGuidedTarget"/>.
        /// </summary>
        public void SetGuidedTarget(double lat, double lon, double altRelative)
        {
            lock (_guidedLock)
            {
                _guidedLat = lat;
                _guidedLon = lon;
                _guidedAlt = altRelative;
                _guidedHasTarget = true;
            }
            EmitStatusText("Guided target set");
        }

        /// <summary>Сбросить GUIDED-цель → hover текущей точки.</summary>
        public void ClearGuidedTarget()
        {
            lock (_guidedLock) _guidedHasTarget = false;
        }

        /// <summary>Переключить в RTL (выбирает Plane/Copter номер автоматически).</summary>
        public void TriggerRtl()
        {
            var vehicle = _state.Snapshot().Vehicle;
            uint mode = vehicle == VehicleType.Vtol ? PlaneMode.Rtl : CopterMode.Rtl;
            SetMode(mode);
        }

        /// <summary>Переключить в LAND/QLAND.</summary>
        public void TriggerLand()
        {
            var vehicle = _state.Snapshot().Vehicle;
            uint mode = vehicle == VehicleType.Vtol ? PlaneMode.QLand : CopterMode.Land;
            SetMode(mode);
        }

        /// <summary>
        /// Emergency stop: немедленный disarm вне зависимости от высоты.
        /// Если в воздухе — ВС начнёт свободное падение.
        /// </summary>
        public void EmergencyStop() => Disarm(force: true);

        // =====================================================================
        // Update — основной тик
        // =====================================================================

        /// <summary>
        /// Обработать тик. Возвращает команду для <see cref="ISimVehicle.ApplyControl"/>.
        /// </summary>
        public ControlCommand Update(double dt)
        {
            var snap = _state.Snapshot();

            if (!snap.Armed)
            {
                return new ControlCommand { Mode = ControlMode.Idle };
            }

            uint mode = snap.CustomMode;
            var vehicle = snap.Vehicle;

            // AUTO → миссия.
            if (IsAutoMode(mode, vehicle))
            {
                if (_executor.State == MissionExecState.Idle && _executor.ItemCount > 0)
                    _executor.Start();
                return _executor.Update(dt, _state);
            }

            // RTL / QRTL / SmartRTL → принудительный возврат.
            if (IsRtlMode(mode, vehicle))
            {
                if (!_executor.IsRtlActive) _executor.TriggerRtl();
                return _executor.Update(dt, _state);
            }

            // GUIDED → пользовательская цель.
            if (IsGuidedMode(mode, vehicle))
                return BuildGuidedCommand(snap);

            // LAND / QLAND.
            if (IsLandMode(mode, vehicle))
            {
                return new ControlCommand
                {
                    Mode = ControlMode.Landing,
                    ThrottleMax = 1.0,
                };
            }

            // Все остальные (LOITER/QLOITER/POSHOLD/BRAKE/QHOVER/STABILIZE/...)
            // → hover текущей точки.
            return BuildHoverCommand(snap);
        }

        // =====================================================================
        // PreArm проверки
        // =====================================================================

        private static bool CheckPreArm(SimState snap, out string reason)
        {
            if (snap.Failures.GpsLoss || snap.Gps.FixType < 3)
            {
                reason = "PreArm: GPS fix not 3D";
                return false;
            }
            if (!snap.Ekf.Healthy || snap.Failures.EkfDivergence)
            {
                reason = "PreArm: EKF not healthy";
                return false;
            }
            if (snap.Failures.CompassError)
            {
                reason = "PreArm: Compass error";
                return false;
            }
            if (snap.Battery.Percent < 20.0)
            {
                reason = "PreArm: Battery low";
                return false;
            }
            if (snap.Failures.BatteryCritical)
            {
                reason = "PreArm: Battery critical";
                return false;
            }
            reason = null;
            return true;
        }

        // =====================================================================
        // Command builders
        // =====================================================================

        private ControlCommand BuildHoverCommand(SimState snap)
        {
            return new ControlCommand
            {
                Mode = PickHoverMode(snap),
                HasPositionTarget = true,
                TargetLat = snap.Position.Lat,
                TargetLon = snap.Position.Lon,
                TargetAltRelative = snap.Position.AltRelative,
                ThrottleMax = 1.0,
            };
        }

        private ControlCommand BuildGuidedCommand(SimState snap)
        {
            double lat, lon, alt;
            bool has;
            lock (_guidedLock)
            {
                lat = _guidedLat; lon = _guidedLon; alt = _guidedAlt;
                has = _guidedHasTarget;
            }

            if (!has) return BuildHoverCommand(snap);

            return new ControlCommand
            {
                Mode = PickNavMode(snap),
                HasPositionTarget = true,
                TargetLat = lat,
                TargetLon = lon,
                TargetAltRelative = alt,
                ThrottleMax = 1.0,
            };
        }

        /// <summary>
        /// Выбор режима для "стоять на месте": для VTOL на airspeed &gt; 15 м/с
        /// используем FixedWing (loiter в FW), иначе Multirotor.
        /// </summary>
        private static ControlMode PickHoverMode(SimState snap)
        {
            if (snap.Vehicle == VehicleType.Vtol && snap.Velocity.AirSpeed > 15.0)
                return ControlMode.FixedWing;
            return ControlMode.Multirotor;
        }

        /// <summary>
        /// Выбор режима для навигации к точке (GUIDED, LOITER с перемещением).
        /// Пока такой же как hover — VtolDynamics сам обработает переходы.
        /// </summary>
        private static ControlMode PickNavMode(SimState snap) => PickHoverMode(snap);

        // =====================================================================
        // Mode queries
        // =====================================================================

        private static bool IsAutoMode(uint mode, VehicleType vehicle) => vehicle switch
        {
            VehicleType.Vtol => mode == PlaneMode.Auto,
            VehicleType.Copter => mode == CopterMode.Auto,
            _ => false,
        };

        private static bool IsRtlMode(uint mode, VehicleType vehicle) => vehicle switch
        {
            VehicleType.Vtol => mode == PlaneMode.Rtl || mode == PlaneMode.QRtl,
            VehicleType.Copter => mode == CopterMode.Rtl || mode == CopterMode.SmartRtl,
            _ => false,
        };

        private static bool IsGuidedMode(uint mode, VehicleType vehicle) => vehicle switch
        {
            VehicleType.Vtol => mode == PlaneMode.Guided,
            VehicleType.Copter => mode == CopterMode.Guided,
            _ => false,
        };

        private static bool IsLandMode(uint mode, VehicleType vehicle) => vehicle switch
        {
            VehicleType.Vtol => mode == PlaneMode.QLand,
            VehicleType.Copter => mode == CopterMode.Land,
            _ => false,
        };

        // =====================================================================
        // Helpers
        // =====================================================================

        private void EmitStatusText(string text) => StatusText?.Invoke(this, text);

        /// <summary>Имя режима для STATUSTEXT / логов.</summary>
        private static string GetModeName(VehicleType type, uint mode)
        {
            if (type == VehicleType.Vtol)
            {
                return mode switch
                {
                    PlaneMode.Manual => "MANUAL",
                    PlaneMode.Circle => "CIRCLE",
                    PlaneMode.Stabilize => "STABILIZE",
                    PlaneMode.Training => "TRAINING",
                    PlaneMode.Acro => "ACRO",
                    PlaneMode.FbwA => "FBWA",
                    PlaneMode.FbwB => "FBWB",
                    PlaneMode.Cruise => "CRUISE",
                    PlaneMode.Auto => "AUTO",
                    PlaneMode.Rtl => "RTL",
                    PlaneMode.Loiter => "LOITER",
                    PlaneMode.Takeoff => "TAKEOFF",
                    PlaneMode.Guided => "GUIDED",
                    PlaneMode.QStabilize => "QSTABILIZE",
                    PlaneMode.QHover => "QHOVER",
                    PlaneMode.QLoiter => "QLOITER",
                    PlaneMode.QLand => "QLAND",
                    PlaneMode.QRtl => "QRTL",
                    _ => $"MODE({mode})",
                };
            }

            return mode switch
            {
                CopterMode.Stabilize => "STABILIZE",
                CopterMode.Acro => "ACRO",
                CopterMode.AltHold => "ALT_HOLD",
                CopterMode.Auto => "AUTO",
                CopterMode.Guided => "GUIDED",
                CopterMode.Loiter => "LOITER",
                CopterMode.Rtl => "RTL",
                CopterMode.Circle => "CIRCLE",
                CopterMode.Land => "LAND",
                CopterMode.PosHold => "POSHOLD",
                CopterMode.Brake => "BRAKE",
                CopterMode.SmartRtl => "SMART_RTL",
                _ => $"MODE({mode})",
            };
        }
    }
}