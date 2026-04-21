using System;
using SimpleDroneGCS.Simulator.Control;
using SimpleDroneGCS.Simulator.Core;


namespace SimpleDroneGCS.Simulator.Physics
{
    // =========================================================================
    // Конфигурация
    // =========================================================================

    /// <summary>
    /// Параметры квадрокоптера. Передаются в конструктор <see cref="CopterDynamics"/>.
    /// </summary>
    public struct CopterConfig
    {
        /// <summary>Масса, кг.</summary>
        public double MassKg;

        /// <summary>Максимальная суммарная тяга всех моторов, Н.</summary>
        public double MaxThrustN;

        /// <summary>Ёмкость батареи, мА·ч.</summary>
        public double BatteryCapacityMah;

        /// <summary>Напряжение полностью заряженной батареи, В.</summary>
        public double BatteryNominalV;

        /// <summary>Внутреннее сопротивление батареи (для sag под нагрузкой), Ом.</summary>
        public double BatteryInternalR;

        /// <summary>Максимальный угол наклона, °.</summary>
        public double MaxTiltDeg;

        /// <summary>Максимальная скорость набора высоты, м/с.</summary>
        public double MaxClimbRateMs;

        /// <summary>Максимальная скорость снижения, м/с.</summary>
        public double MaxDescentRateMs;

        /// <summary>Максимальная горизонтальная скорость, м/с.</summary>
        public double MaxHorizontalSpeedMs;

        /// <summary>Максимальная угловая скорость yaw, °/с.</summary>
        public double MaxYawRateDegPerSec;

        /// <summary>Тау фильтра attitude (первый порядок), сек.</summary>
        public double AttitudeTauSec;

        /// <summary>Тау фильтра spool-up моторов, сек.</summary>
        public double MotorTauSec;

        /// <summary>Коэффициент линейного drag, Н·с/м (на единицу массы не делить).</summary>
        public double DragCoef;

        /// <summary>Ток холостого хода (авионика + idle ESC), А.</summary>
        public double IdleCurrentA;

        /// <summary>Дополнительный ток при полном газе (без idle), А.</summary>
        public double MaxExtraCurrentA;

        /// <summary>P-коэффициент контроллера высоты (alt error → target climb).</summary>
        public double K_Alt;

        /// <summary>P-коэффициент контроллера вертикальной скорости (Vd error → thrust).</summary>
        public double K_Vd;

        /// <summary>P-коэффициент контроллера горизонтальной скорости (V error → accel).</summary>
        public double K_Vel;

        /// <summary>P-коэффициент контроллера yaw (angle error → yaw rate).</summary>
        public double K_Yaw;

        /// <summary>Значения по умолчанию для среднего 450-500 мм квадрокоптера.</summary>
        public static CopterConfig CreateDefault() => new CopterConfig
        {
            MassKg = 2.0,
            MaxThrustN = 40.0,          // T/W ≈ 2:1
            BatteryCapacityMah = 5200,  // 4S 5200 mAh
            BatteryNominalV = 16.8,     // 4S полный заряд
            BatteryInternalR = 0.025,
            MaxTiltDeg = 35,            // ArduPilot ANGLE_MAX
            MaxClimbRateMs = 5.0,
            MaxDescentRateMs = 3.0,
            MaxHorizontalSpeedMs = 10.0,
            MaxYawRateDegPerSec = 180.0,
            AttitudeTauSec = 0.20,
            MotorTauSec = 0.10,
            DragCoef = 0.5,
            IdleCurrentA = 2.0,
            MaxExtraCurrentA = 28.0,
            K_Alt = 1.0,
            K_Vd = 3.0,
            K_Vel = 1.5,
            K_Yaw = 2.0,
        };
    }

    // =========================================================================
    // CopterDynamics
    // =========================================================================

    /// <summary>
    /// Физическая модель квадрокоптера (X-конфигурация).
    /// <para>
    /// Каскадный P-регулятор внутри: alt→climb_rate, pos→vel→accel→tilt, yaw→yaw_rate.
    /// Attitude и моторы — first-order lag. Drag линейный. Full DCM body→NED для thrust.
    /// </para>
    /// <para>
    /// Писатели: поток SimClock. Чтение: через <see cref="SimState.Snapshot"/>.
    /// Метод <see cref="Step"/> оборачивает изменения в <c>state.Write()</c>.
    /// </para>
    /// </summary>
    public sealed class CopterDynamics : ISimVehicle
    {
        private readonly CopterConfig _cfg;
        private readonly WindModel _wind;

        // Текущая команда (обновляется ApplyControl).
        private ControlCommand _cmd;

        // Внутреннее состояние spool-up: текущая нормированная тяга каждого из 4 моторов (0..1).
        private double _mThrust0, _mThrust1, _mThrust2, _mThrust3;

        // Удерживаемый yaw (рад), когда TargetYawDeg = NaN.
        private double _yawHold;

        // =====================================================================
        // ISimVehicle properties
        // =====================================================================

        public VehicleType Vehicle => VehicleType.Copter;
        public int MotorCount => 4;
        public bool SupportsFwMode => false;
        public double MassKg => _cfg.MassKg;
        public double BatteryCapacityMah => _cfg.BatteryCapacityMah;
        public double BatteryNominalV => _cfg.BatteryNominalV;

        // =====================================================================
        // Constructor
        // =====================================================================

        /// <summary>
        /// Создать физику квадрокоптера.
        /// </summary>
        /// <param name="wind">Модель ветра. null → безветрие.</param>
        /// <param name="config">Параметры ВС. null → <see cref="CopterConfig.CreateDefault"/>.</param>
        public CopterDynamics(WindModel wind = null, CopterConfig? config = null)
        {
            _wind = wind ?? new WindModel();
            _cfg = config ?? CopterConfig.CreateDefault();
        }

        // =====================================================================
        // Lifecycle
        // =====================================================================

        public void Reset(SimState state)
        {
            _cmd = new ControlCommand { Mode = ControlMode.Idle };
            _mThrust0 = _mThrust1 = _mThrust2 = _mThrust3 = 0.0;
            _yawHold = state.Attitude.Yaw;
        }

        public void ApplyControl(in ControlCommand command) => _cmd = command;

        // =====================================================================
        // Step
        // =====================================================================

        public void Step(double dt, SimState state)
        {
            if (dt <= 0.0 || state == null) return;

            using (state.Write())
            {
                if (!state.Armed || _cmd.Mode == ControlMode.Idle)
                {
                    IdleOrFreefall(dt, state);
                    return;
                }
                FlightStep(dt, state);
            }
        }

        // =====================================================================
        // Idle / Free-fall (disarmed)
        // =====================================================================

        private void IdleOrFreefall(double dt, SimState state)
        {
            // PWM всех моторов в idle.
            _mThrust0 = _mThrust1 = _mThrust2 = _mThrust3 = 0.0;
            state.ServoPwm[0] = state.ServoPwm[1] = state.ServoPwm[2] = state.ServoPwm[3] = 1000;
            for (int i = 4; i < state.ServoPwm.Length; i++) state.ServoPwm[i] = 1500;
            state.Velocity.ThrottlePercent = 0;

            // Если disarm в воздухе — свободное падение (пункт скоупа #10).
            if (state.Position.AltRelative > 0.1)
            {
                _wind.GetWindNed(out double wN, out double wE, out double wD);
                double vRelN = state.Velocity.Vn - wN;
                double vRelE = state.Velocity.Ve - wE;
                double vRelD = state.Velocity.Vd - wD;

                double aN = -_cfg.DragCoef * vRelN / _cfg.MassKg;
                double aE = -_cfg.DragCoef * vRelE / _cfg.MassKg;
                double aD = -_cfg.DragCoef * vRelD / _cfg.MassKg + Atmosphere.G;

                double newVn = state.Velocity.Vn + aN * dt;
                double newVe = state.Velocity.Ve + aE * dt;
                double newVd = state.Velocity.Vd + aD * dt;

                double lat = state.Position.Lat;
                double lon = state.Position.Lon;
                Navigator.AdvanceLatLon(ref lat, ref lon, newVn * dt, newVe * dt);
                double newAltAmsl = state.Position.AltAmsl - newVd * dt;
                double newAltRel = newAltAmsl - state.Home.AltAmsl;

                if (newAltRel < 0)
                {
                    newAltRel = 0;
                    newAltAmsl = state.Home.AltAmsl;
                    newVn = 0; newVe = 0; newVd = 0;
                }

                state.Position.Lat = lat;
                state.Position.Lon = lon;
                state.Position.AltAmsl = newAltAmsl;
                state.Position.AltRelative = newAltRel;
                state.Velocity.Vn = newVn;
                state.Velocity.Ve = newVe;
                state.Velocity.Vd = newVd;
            }

            UpdateLandedState(state);
            DrainBattery(dt, state, _cfg.IdleCurrentA);
        }

        // =====================================================================
        // Flight step (armed + non-idle)
        // =====================================================================

        private void FlightStep(double dt, SimState state)
        {
            // ---- Snapshot текущего состояния ----
            double lat = state.Position.Lat;
            double lon = state.Position.Lon;
            double altRel = state.Position.AltRelative;
            double altAmsl = state.Position.AltAmsl;
            double roll = state.Attitude.Roll;
            double pitch = state.Attitude.Pitch;
            double yaw = state.Attitude.Yaw;
            double vn = state.Velocity.Vn;
            double ve = state.Velocity.Ve;
            double vd = state.Velocity.Vd;

            // ---- Цели по режиму ----
            double targetVn = 0.0, targetVe = 0.0;
            double targetAltRel = altRel;
            double targetYawDeg = _cmd.TargetYawDeg;

            switch (_cmd.Mode)
            {
                case ControlMode.Takeoff:
                    targetAltRel = double.IsNaN(_cmd.TargetAltRelative)
                        ? altRel + 5.0
                        : _cmd.TargetAltRelative;
                    break;

                case ControlMode.Landing:
                    // Плавное снижение; ground clamp остановит при alt_rel < 0.
                    targetAltRel = -1.0;
                    break;

                default: // Multirotor и любой "навигационный" режим для Copter
                    if (!double.IsNaN(_cmd.TargetAltRelative))
                        targetAltRel = _cmd.TargetAltRelative;

                    if (_cmd.HasPositionTarget)
                    {
                        double distM = Navigator.DistanceM(lat, lon, _cmd.TargetLat, _cmd.TargetLon);
                        double bearingDeg = Navigator.BearingDeg(lat, lon, _cmd.TargetLat, _cmd.TargetLon);

                        double maxSpeed = _cmd.TargetSpeedMs > 0
                            ? Math.Min(_cmd.TargetSpeedMs, _cfg.MaxHorizontalSpeedMs)
                            : _cfg.MaxHorizontalSpeedMs;

                        // Плавный подход: slow down когда близко.
                        double approachSpeed = Math.Min(maxSpeed, distM * 0.5);

                        double bearingRad = bearingDeg * Navigator.DegToRad;
                        targetVn = approachSpeed * Math.Cos(bearingRad);
                        targetVe = approachSpeed * Math.Sin(bearingRad);
                    }
                    break;
            }

            // ---- Alt controller: alt_err → target_climb → target_vd ----
            double altErr = targetAltRel - altRel;
            double targetClimb = Math.Clamp(altErr * _cfg.K_Alt,
                -_cfg.MaxDescentRateMs, _cfg.MaxClimbRateMs);
            double targetVd = -targetClimb;

            // ---- Velocity err → desired horizontal accel (world) ----
            double aNNeed = (targetVn - vn) * _cfg.K_Vel;
            double aENeed = (targetVe - ve) * _cfg.K_Vel;

            // Clamp magnitude к max-accel (tilt-limited).
            double maxTiltRad = _cfg.MaxTiltDeg * Navigator.DegToRad;
            double maxAccel = Atmosphere.G * Math.Tan(maxTiltRad);
            double aMag = Math.Sqrt(aNNeed * aNNeed + aENeed * aENeed);
            if (aMag > maxAccel && aMag > 1e-6)
            {
                double scale = maxAccel / aMag;
                aNNeed *= scale;
                aENeed *= scale;
            }

            // ---- Desired tilt в body frame (через yaw rotation) ----
            double cosPsi = Math.Cos(yaw);
            double sinPsi = Math.Sin(yaw);
            double tx = aNNeed / Atmosphere.G;  // "pitch-like" world (North)
            double ty = aENeed / Atmosphere.G;  // "roll-like" world (East)
            double targetPitch = -(tx * cosPsi + ty * sinPsi);
            double targetRoll = -tx * sinPsi + ty * cosPsi;
            targetRoll = Math.Clamp(targetRoll, -maxTiltRad, maxTiltRad);
            targetPitch = Math.Clamp(targetPitch, -maxTiltRad, maxTiltRad);

            // ---- Attitude first-order lag ----
            double attAlpha = 1.0 - Math.Exp(-dt / _cfg.AttitudeTauSec);
            double newRoll = roll + (targetRoll - roll) * attAlpha;
            double newPitch = pitch + (targetPitch - pitch) * attAlpha;

            // ---- Yaw controller ----
            double yawRate;
            if (double.IsNaN(targetYawDeg))
            {
                // Удерживать _yawHold
                double errDeg = Navigator.AngleDiffDeg(
                    yaw * Navigator.RadToDeg,
                    _yawHold * Navigator.RadToDeg);
                yawRate = errDeg * Navigator.DegToRad * _cfg.K_Yaw;
            }
            else
            {
                double errDeg = Navigator.AngleDiffDeg(
                    yaw * Navigator.RadToDeg, targetYawDeg);
                yawRate = errDeg * Navigator.DegToRad * _cfg.K_Yaw;
            }
            double maxYawRate = _cfg.MaxYawRateDegPerSec * Navigator.DegToRad;
            yawRate = Math.Clamp(yawRate, -maxYawRate, maxYawRate);

            double newYaw = yaw + yawRate * dt;
            // Нормализация в [-π, π]
            while (newYaw > Math.PI) newYaw -= 2 * Math.PI;
            while (newYaw < -Math.PI) newYaw += 2 * Math.PI;

            // Если был явный target — обновляем yaw_hold.
            if (!double.IsNaN(targetYawDeg)) _yawHold = newYaw;

            // ---- Thrust command ----
            // a_d_target от P-регулятора Vd
            double aDNeed = (targetVd - vd) * _cfg.K_Vd;
            // Net a_d = -T·cosφ·cosθ/m + g = aDNeed  →  T = m(g - aDNeed)/(cosφ·cosθ)
            double cosRoll = Math.Cos(newRoll);
            double cosPitch = Math.Cos(newPitch);
            double cosRP = cosRoll * cosPitch;
            if (cosRP < 0.1) cosRP = 0.1; // защита от вырожденного tilt ≈90°

            double tCmd = _cfg.MassKg * (Atmosphere.G - aDNeed) / cosRP;
            double tMaxCmd = _cfg.MaxThrustN * _cmd.ThrottleMax;
            tCmd = Math.Clamp(tCmd, 0.0, tMaxCmd);

            double throttleCmd = tCmd / _cfg.MaxThrustN; // 0..1

            // ---- Per-motor mixer (X-frame, упрощённый) ----
            double rCmd = Math.Clamp((targetRoll - roll) / maxTiltRad, -1, 1);
            double pCmd = Math.Clamp((targetPitch - pitch) / maxTiltRad, -1, 1);
            double yCmd = Math.Clamp(yawRate / maxYawRate, -1, 1);
            const double MixScale = 0.10; // ±10% variation от throttle по attitude error

            // X-frame: M1=FR-CCW, M2=RL-CCW, M3=FL-CW, M4=RR-CW
            double t0 = Math.Clamp(throttleCmd + (-rCmd + pCmd + yCmd) * MixScale, 0, 1);
            double t1 = Math.Clamp(throttleCmd + (+rCmd - pCmd + yCmd) * MixScale, 0, 1);
            double t2 = Math.Clamp(throttleCmd + (+rCmd + pCmd - yCmd) * MixScale, 0, 1);
            double t3 = Math.Clamp(throttleCmd + (-rCmd - pCmd - yCmd) * MixScale, 0, 1);

            // Motor failure injection.
            int failed = state.Failures.MotorFailureIndex;
            if (failed == 0) t0 = 0;
            else if (failed == 1) t1 = 0;
            else if (failed == 2) t2 = 0;
            else if (failed == 3) t3 = 0;

            // Spool-up lag.
            double motorAlpha = 1.0 - Math.Exp(-dt / _cfg.MotorTauSec);
            _mThrust0 += (t0 - _mThrust0) * motorAlpha;
            _mThrust1 += (t1 - _mThrust1) * motorAlpha;
            _mThrust2 += (t2 - _mThrust2) * motorAlpha;
            _mThrust3 += (t3 - _mThrust3) * motorAlpha;

            // PWM выходы.
            state.ServoPwm[0] = (ushort)Math.Round(1000 + _mThrust0 * 1000);
            state.ServoPwm[1] = (ushort)Math.Round(1000 + _mThrust1 * 1000);
            state.ServoPwm[2] = (ushort)Math.Round(1000 + _mThrust2 * 1000);
            state.ServoPwm[3] = (ushort)Math.Round(1000 + _mThrust3 * 1000);
            for (int i = 4; i < state.ServoPwm.Length; i++) state.ServoPwm[i] = 1500;

            // Фактическая тяга = сумма × max_thrust / 4.
            double tActual = (_mThrust0 + _mThrust1 + _mThrust2 + _mThrust3) / 4.0 * _cfg.MaxThrustN;

            // ---- Forces → acceleration в NED (полная DCM) ----
            double sinYaw = Math.Sin(newYaw);
            double cosYawN = Math.Cos(newYaw);
            double sinRoll = Math.Sin(newRoll);
            double sinPitchN = Math.Sin(newPitch);

            double r02 = cosRoll * sinPitchN * cosYawN + sinRoll * sinYaw;
            double r12 = cosRoll * sinPitchN * sinYaw - sinRoll * cosYawN;
            double r22 = cosRoll * cosPitch;

            double aThrustN = -tActual / _cfg.MassKg * r02;
            double aThrustE = -tActual / _cfg.MassKg * r12;
            double aThrustD = -tActual / _cfg.MassKg * r22;

            // Wind + drag.
            _wind.GetWindNed(out double wN, out double wE, out double wD);
            double vRelN = vn - wN;
            double vRelE = ve - wE;
            double vRelD = vd - wD;
            double aDragN = -_cfg.DragCoef * vRelN / _cfg.MassKg;
            double aDragE = -_cfg.DragCoef * vRelE / _cfg.MassKg;
            double aDragD = -_cfg.DragCoef * vRelD / _cfg.MassKg;

            double aN = aThrustN + aDragN;
            double aE = aThrustE + aDragE;
            double aD = aThrustD + aDragD + Atmosphere.G;

            // ---- Integrate velocity + position ----
            double newVn = vn + aN * dt;
            double newVe = ve + aE * dt;
            double newVd = vd + aD * dt;

            Navigator.AdvanceLatLon(ref lat, ref lon, newVn * dt, newVe * dt);
            double newAltAmsl = altAmsl - newVd * dt;  // climb rate = -Vd
            double newAltRel = newAltAmsl - state.Home.AltAmsl;

            // ---- Ground clamp ----
            if (newAltRel < 0.0)
            {
                newAltRel = 0.0;
                newAltAmsl = state.Home.AltAmsl;
                if (newVd > 0) newVd = 0;
                // На земле — затухание горизонтальной скорости (friction).
                newVn *= 0.5; newVe *= 0.5;
            }

            // ---- Write back ----
            state.Position.Lat = lat;
            state.Position.Lon = lon;
            state.Position.AltAmsl = newAltAmsl;
            state.Position.AltRelative = newAltRel;

            state.Attitude.Roll = newRoll;
            state.Attitude.Pitch = newPitch;
            state.Attitude.Yaw = newYaw;
            state.Attitude.RollSpeed = (newRoll - roll) / dt;
            state.Attitude.PitchSpeed = (newPitch - pitch) / dt;
            state.Attitude.YawSpeed = yawRate;

            state.Velocity.Vn = newVn;
            state.Velocity.Ve = newVe;
            state.Velocity.Vd = newVd;
            state.Velocity.AirSpeed = Math.Sqrt(vRelN * vRelN + vRelE * vRelE);
            state.Velocity.ThrottlePercent = (ushort)Math.Clamp(Math.Round(throttleCmd * 100), 0, 100);

            // ---- Landed state + battery ----
            UpdateLandedState(state);

            double currentA = _cfg.IdleCurrentA + throttleCmd * _cfg.MaxExtraCurrentA;
            if (state.Failures.MotorFailureIndex >= 0) currentA *= 0.75;
            DrainBattery(dt, state, currentA);
        }

        // =====================================================================
        // Helpers
        // =====================================================================

        private static void UpdateLandedState(SimState state)
        {
            if (!state.Armed)
            {
                state.LandedState = LandedStateKind.OnGround;
                return;
            }

            double alt = state.Position.AltRelative;
            double vd = state.Velocity.Vd;

            if (alt < 0.3)
                state.LandedState = LandedStateKind.OnGround;
            else if (vd < -0.5 && alt < 3.0)
                state.LandedState = LandedStateKind.Takeoff;
            else if (vd > 1.0 && alt < 5.0)
                state.LandedState = LandedStateKind.Landing;
            else
                state.LandedState = LandedStateKind.InAir;
        }

        private void DrainBattery(double dt, SimState state, double currentA)
        {
            state.Battery.CurrentA = currentA;
            // mAh = A · (dt_sec) · 1000 / 3600
            state.Battery.ConsumedMah += currentA * dt * (1000.0 / 3600.0);

            double pct = 100.0 * (1.0 - state.Battery.ConsumedMah / _cfg.BatteryCapacityMah);
            state.Battery.Percent = Math.Max(0.0, pct);

            double frac = LipoCurveFraction(state.Battery.Percent);
            double vSag = currentA * _cfg.BatteryInternalR;
            state.Battery.VoltageV = Math.Max(0.0, _cfg.BatteryNominalV * frac - vSag);
        }

        /// <summary>
        /// Кусочно-линейная LiPo кривая (доля от напряжения полного заряда).
        /// 100 % → 1.00;  20 % → 0.86 (плато);  0 % → 0.786 (cutoff).
        /// Для 4S: 16.8 → 14.45 → 13.2 В. Для 6S: 25.2 → 21.67 → 19.8 В.
        /// </summary>
        private static double LipoCurveFraction(double percent)
        {
            if (percent >= 90.0) return 1.00 - (100.0 - percent) / 10.0 * 0.04;   // 1.00 → 0.96
            if (percent >= 20.0) return 0.96 - (90.0 - percent) / 70.0 * 0.10;    // 0.96 → 0.86
            if (percent >= 10.0) return 0.86 - (20.0 - percent) / 10.0 * 0.01;    // 0.86 → 0.85
            if (percent >= 0.0) return 0.85 - (10.0 - percent) / 10.0 * 0.064;    // 0.85 → 0.786
            return 0.786;
        }
    }
}