using System;
using SimpleDroneGCS.Simulator.Control;
using SimpleDroneGCS.Simulator.Core;

namespace SimpleDroneGCS.Simulator.Physics
{
    // =========================================================================
    // Конфигурация
    // =========================================================================

    /// <summary>
    /// Параметры VTOL QuadPlane. Дефолты — под 34-кг ВС с размахом 4 м.
    /// </summary>
    public struct VtolConfig
    {
        // ---- Масса и батарея ----
        public double MassKg;
        public double BatteryCapacityMah;
        public double BatteryNominalV;
        public double BatteryInternalR;

        // ---- Двигатели ----
        /// <summary>Суммарная максимальная тяга 4 лифт-моторов, Н.</summary>
        public double MaxLiftThrustN;
        /// <summary>Максимальная тяга pusher-мотора (M5), Н.</summary>
        public double MaxPusherThrustN;

        // ---- Аэродинамика ----
        public double WingAreaM2;
        public double WingAspectRatio;
        /// <summary>C_L α (доналёто 2π·AR/(AR+2)), /рад.</summary>
        public double CL_Alpha;
        public double StallAngleRad;
        public double CD_0;
        /// <summary>Efficiency фактор Oswald для induced drag.</summary>
        public double OswaldEff;
        /// <summary>Базовый drag всего планера (не крыла), кг/м.</summary>
        public double BaseDragCoef;

        // ---- Скорости переходов ----
        public double VStallMs;
        public double VTransitionMs;
        public double VMinTransitionMs;
        public double VCruiseMs;

        // ---- Лимиты ----
        public double MaxTiltDegMc;
        public double MaxPitchDegFw;
        public double MaxRollDegFw;
        public double MaxClimbRateMs;
        public double MaxDescentRateMs;
        public double MaxHorizontalSpeedMs;
        public double MaxYawRateDegPerSec;

        // ---- Временные константы ----
        public double AttitudeTauSec;
        public double MotorTauSec;
        /// <summary>Сглаживание переключений MC↔FW, сек.</summary>
        public double EngagementRampTimeSec;

        // ---- Токи ----
        public double IdleCurrentA;
        public double MaxMcExtraCurrentA;
        public double MaxFwExtraCurrentA;

        // ---- Регуляторы ----
        public double K_Alt;
        public double K_Vd;
        public double K_Vel;
        public double K_Yaw;
        public double K_FwPitch;
        public double K_FwBank;
        public double K_FwAirspeed;

        /// <summary>Дефолты для 34-кг VTOL, размах 4 м, 12S 22 А·ч, cruise 22 м/с.</summary>
        public static VtolConfig CreateDefault() => new VtolConfig
        {
            MassKg = 34.0,
            MaxLiftThrustN = 500.0,          // T/W ≈ 1.5
            MaxPusherThrustN = 150.0,
            BatteryCapacityMah = 44000,      // 12S 22 А·ч
            BatteryNominalV = 50.4,          // 12S полный
            BatteryInternalR = 0.015,

            WingAreaM2 = 1.6,                // размах 4 × хорда 0.4
            WingAspectRatio = 10.0,
            CL_Alpha = 5.5,                  // 2π·AR/(AR+2) ≈ 5.24 → округл.
            StallAngleRad = 15.0 * Math.PI / 180.0,
            CD_0 = 0.03,
            OswaldEff = 0.8,
            BaseDragCoef = 2.0,

            VStallMs = 13.0,
            VTransitionMs = 17.0,
            VMinTransitionMs = 10.0,
            VCruiseMs = 22.0,

            MaxTiltDegMc = 30,
            MaxPitchDegFw = 20,
            MaxRollDegFw = 45,
            MaxClimbRateMs = 5.0,
            MaxDescentRateMs = 3.0,
            MaxHorizontalSpeedMs = 10.0,
            MaxYawRateDegPerSec = 90,

            AttitudeTauSec = 0.30,           // тяжёлый ВС отвечает медленнее
            MotorTauSec = 0.12,
            EngagementRampTimeSec = 3.0,

            IdleCurrentA = 3.0,
            MaxMcExtraCurrentA = 120.0,
            MaxFwExtraCurrentA = 40.0,

            K_Alt = 0.5,
            K_Vd = 2.0,
            K_Vel = 1.0,
            K_Yaw = 1.5,
            K_FwPitch = 0.06,
            K_FwBank = 0.04,
            K_FwAirspeed = 0.15,
        };
    }

    // =========================================================================
    // VtolDynamics
    // =========================================================================

    /// <summary>
    /// Физическая модель VTOL QuadPlane.
    /// <para>
    /// Всегда активна аэродинамика крыла (Lift от плотности воздуха · площадь · C_L).
    /// Всегда возможны лифт-моторы (M1..M4) и pusher (M5). Их вклад регулируется
    /// двумя коэффициентами engagement (0..1), которые плавно меняются по
    /// <see cref="ControlMode"/> и airspeed.
    /// </para>
    /// <para>
    /// Переходы MC↔FW через blend weights:
    ///   — TransitionToFw: pusher сразу = 1; lift_engagement = 1 − (v−v_stall)/(v_trans−v_stall)
    ///   — TransitionToMc: pusher сразу = 0; lift_engagement = 1 − (v−v_stall)/(v_trans−v_stall)
    /// </para>
    /// </summary>
    public sealed class VtolDynamics : ISimVehicle
    {
        private readonly VtolConfig _cfg;
        private readonly WindModel _wind;

        private ControlCommand _cmd;

        // Motor state (spool-up lag).
        private double _mLift0, _mLift1, _mLift2, _mLift3;
        private double _mPusher;

        // Mode engagement (плавный blend).
        private double _liftEngagement = 1.0;    // 1 = лифт-моторы активны, 0 = FW
        private double _pusherEngagement = 0.0;  // 1 = pusher активен, 0 = MC

        // Yaw hold.
        private double _yawHold;

        // ---- ISimVehicle ----

        public VehicleType Vehicle => VehicleType.Vtol;
        public int MotorCount => 5;
        public bool SupportsFwMode => true;
        public double MassKg => _cfg.MassKg;
        public double BatteryCapacityMah => _cfg.BatteryCapacityMah;
        public double BatteryNominalV => _cfg.BatteryNominalV;

        /// <summary>
        /// Создать VTOL-физику.
        /// </summary>
        /// <param name="wind">Ветер (null → безветрие).</param>
        /// <param name="config">Параметры (null → <see cref="VtolConfig.CreateDefault"/>).</param>
        public VtolDynamics(WindModel wind = null, VtolConfig? config = null)
        {
            _wind = wind ?? new WindModel();
            _cfg = config ?? VtolConfig.CreateDefault();
        }

        public void Reset(SimState state)
        {
            _cmd = new ControlCommand { Mode = ControlMode.Idle };
            _mLift0 = _mLift1 = _mLift2 = _mLift3 = 0.0;
            _mPusher = 0.0;
            _liftEngagement = 1.0;
            _pusherEngagement = 0.0;
            _yawHold = state.Attitude.Yaw;
        }

        public void ApplyControl(in ControlCommand command) => _cmd = command;

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
        // Idle / Free-fall
        // =====================================================================

        private void IdleOrFreefall(double dt, SimState state)
        {
            _mLift0 = _mLift1 = _mLift2 = _mLift3 = 0.0;
            _mPusher = 0.0;
            for (int i = 0; i < 5; i++) state.ServoPwm[i] = 1000;
            for (int i = 5; i < state.ServoPwm.Length; i++) state.ServoPwm[i] = 1500;
            state.Velocity.ThrottlePercent = 0;

            // Disarm в воздухе → свободное падение (пункт #10).
            if (state.Position.AltRelative > 0.1)
            {
                _wind.GetWindNed(out double wN, out double wE, out double wD);
                double vRelN = state.Velocity.Vn - wN;
                double vRelE = state.Velocity.Ve - wE;
                double vRelD = state.Velocity.Vd - wD;
                double vMag = Math.Sqrt(vRelN * vRelN + vRelE * vRelE + vRelD * vRelD);

                double rho = Atmosphere.AirDensity(state.Position.AltAmsl);
                double qbar = 0.5 * rho * vMag * vMag;
                double aDragMag = (vMag > 0.1) ? qbar * _cfg.BaseDragCoef / _cfg.MassKg : 0;

                double aN = (vMag > 0.1) ? -aDragMag * vRelN / vMag : 0;
                double aE = (vMag > 0.1) ? -aDragMag * vRelE / vMag : 0;
                double aD = ((vMag > 0.1) ? -aDragMag * vRelD / vMag : 0) + Atmosphere.G;

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
                    newVn = newVe = newVd = 0;
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
        // Flight step
        // =====================================================================

        private void FlightStep(double dt, SimState state)
        {
            // ---- Читаем состояние ----
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

            // ---- Airspeed относительно ветра ----
            _wind.GetWindNed(out double wN, out double wE, out double wD);
            double vRelN = vn - wN;
            double vRelE = ve - wE;
            double vRelD = vd - wD;
            double airspeedHoriz = Math.Sqrt(vRelN * vRelN + vRelE * vRelE);
            double airspeedTotal = Math.Sqrt(vRelN * vRelN + vRelE * vRelE + vRelD * vRelD);

            // ---- Цели по режиму ----
            double targetAltRel = altRel;
            double targetVn = 0, targetVe = 0;
            double targetYawDeg = _cmd.TargetYawDeg;

            switch (_cmd.Mode)
            {
                case ControlMode.Takeoff:
                    targetAltRel = double.IsNaN(_cmd.TargetAltRelative)
                        ? altRel + 10.0
                        : _cmd.TargetAltRelative;
                    break;
                case ControlMode.Landing:
                    targetAltRel = -1.0;
                    break;
                default:
                    if (!double.IsNaN(_cmd.TargetAltRelative))
                        targetAltRel = _cmd.TargetAltRelative;
                    if (_cmd.HasPositionTarget)
                    {
                        double distM = Navigator.DistanceM(lat, lon,
                            _cmd.TargetLat, _cmd.TargetLon);
                        double bearingDeg = Navigator.BearingDeg(lat, lon,
                            _cmd.TargetLat, _cmd.TargetLon);
                        double maxSpeed = _cmd.TargetSpeedMs > 0
                            ? _cmd.TargetSpeedMs : _cfg.MaxHorizontalSpeedMs;
                        double approach = Math.Min(maxSpeed, distM * 0.5);
                        double bRad = bearingDeg * Navigator.DegToRad;
                        targetVn = approach * Math.Cos(bRad);
                        targetVe = approach * Math.Sin(bRad);
                    }
                    break;
            }

            // ---- Engagement (plenum lift vs pusher) ----
            double liftTarget = 1.0, pusherTarget = 0.0;
            switch (_cmd.Mode)
            {
                case ControlMode.FixedWing:
                    pusherTarget = 1;
                    // Safety net: если airspeed ниже stall-скорости — не убираем
                    // лифт-моторы (крыло пока не создаёт достаточную подъёмную силу).
                    // Выше VTransition — лифт-моторы выключаем полностью.
                    double tFw = (airspeedHoriz - _cfg.VStallMs)
                        / Math.Max(0.1, _cfg.VTransitionMs - _cfg.VStallMs);
                    liftTarget = 1.0 - Math.Clamp(tFw, 0, 1);
                    break;

                case ControlMode.TransitionToFw:
                    pusherTarget = 1;
                    // Лифт-моторы убираются по мере роста airspeed.
                    double t1 = (airspeedHoriz - _cfg.VStallMs)
                        / Math.Max(0.1, _cfg.VTransitionMs - _cfg.VStallMs);
                    liftTarget = 1.0 - Math.Clamp(t1, 0, 1);
                    break;

                case ControlMode.TransitionToMc:
                    pusherTarget = 0;
                    // Лифт-моторы нарастают по мере падения airspeed.
                    double t2 = (airspeedHoriz - _cfg.VStallMs)
                        / Math.Max(0.1, _cfg.VTransitionMs - _cfg.VStallMs);
                    liftTarget = 1.0 - Math.Clamp(t2, 0, 1);
                    break;

                default: // Multirotor, Takeoff, Landing
                    liftTarget = 1; pusherTarget = 0; break;
            }
            double engStep = dt / _cfg.EngagementRampTimeSec;
            _liftEngagement = MoveToward(_liftEngagement, liftTarget, engStep);
            _pusherEngagement = MoveToward(_pusherEngagement, pusherTarget, engStep);

            // ---- MC controller ----
            ComputeMcController(state, targetAltRel, targetVn, targetVe,
                out double mcRoll, out double mcPitch, out double mcYawRate, out double mcThrottle);

            // ---- FW controller ----
            ComputeFwController(state, targetAltRel, airspeedHoriz,
                out double fwRoll, out double fwPitch, out double fwYawRate, out double fwThrottle);

            // ---- Blend attitude targets ----
            double totalEng = _liftEngagement + _pusherEngagement;
            if (totalEng < 0.01) totalEng = 1.0; // safety
            double targetRoll = (mcRoll * _liftEngagement + fwRoll * _pusherEngagement) / totalEng;
            double targetPitch = (mcPitch * _liftEngagement + fwPitch * _pusherEngagement) / totalEng;
            double yawRate = (mcYawRate * _liftEngagement + fwYawRate * _pusherEngagement) / totalEng;

            double maxTiltRad = Math.Max(_cfg.MaxTiltDegMc, _cfg.MaxRollDegFw) * Navigator.DegToRad;
            targetRoll = Math.Clamp(targetRoll, -maxTiltRad, maxTiltRad);
            targetPitch = Math.Clamp(targetPitch, -maxTiltRad, maxTiltRad);

            // ---- Attitude first-order lag ----
            double attAlpha = 1.0 - Math.Exp(-dt / _cfg.AttitudeTauSec);
            double newRoll = roll + (targetRoll - roll) * attAlpha;
            double newPitch = pitch + (targetPitch - pitch) * attAlpha;

            double newYaw = yaw + yawRate * dt;
            while (newYaw > Math.PI) newYaw -= 2 * Math.PI;
            while (newYaw < -Math.PI) newYaw += 2 * Math.PI;
            if (!double.IsNaN(targetYawDeg)) _yawHold = newYaw;

            // ---- Motor outputs ----
            double liftThrottle = Math.Clamp(mcThrottle * _liftEngagement, 0, 1);
            double pusherThrottle = Math.Clamp(fwThrottle * _pusherEngagement, 0, 1);

            // X-mixer for lift
            double rCmd = Math.Clamp((targetRoll - roll)
                / Math.Max(0.01, _cfg.MaxTiltDegMc * Navigator.DegToRad), -1, 1);
            double pCmd = Math.Clamp((targetPitch - pitch)
                / Math.Max(0.01, _cfg.MaxTiltDegMc * Navigator.DegToRad), -1, 1);
            double yCmd = Math.Clamp(yawRate
                / (_cfg.MaxYawRateDegPerSec * Navigator.DegToRad), -1, 1);
            const double MixScale = 0.10;

            double t0 = Math.Clamp(liftThrottle + (-rCmd + pCmd + yCmd) * MixScale, 0, 1);
            double ta = Math.Clamp(liftThrottle + (+rCmd - pCmd + yCmd) * MixScale, 0, 1);
            double tb = Math.Clamp(liftThrottle + (+rCmd + pCmd - yCmd) * MixScale, 0, 1);
            double tc = Math.Clamp(liftThrottle + (-rCmd - pCmd - yCmd) * MixScale, 0, 1);

            int failed = state.Failures.MotorFailureIndex;
            if (failed == 0) t0 = 0;
            else if (failed == 1) ta = 0;
            else if (failed == 2) tb = 0;
            else if (failed == 3) tc = 0;
            bool pusherFailed = failed == 4;

            double motorAlpha = 1.0 - Math.Exp(-dt / _cfg.MotorTauSec);
            _mLift0 += (t0 - _mLift0) * motorAlpha;
            _mLift1 += (ta - _mLift1) * motorAlpha;
            _mLift2 += (tb - _mLift2) * motorAlpha;
            _mLift3 += (tc - _mLift3) * motorAlpha;
            _mPusher += ((pusherFailed ? 0 : pusherThrottle) - _mPusher) * motorAlpha;

            // PWM: M1..M4 на SERVO1..4, pusher на SERVO5.
            state.ServoPwm[0] = (ushort)Math.Round(1000 + _mLift0 * 1000);
            state.ServoPwm[1] = (ushort)Math.Round(1000 + _mLift1 * 1000);
            state.ServoPwm[2] = (ushort)Math.Round(1000 + _mLift2 * 1000);
            state.ServoPwm[3] = (ushort)Math.Round(1000 + _mLift3 * 1000);
            state.ServoPwm[4] = (ushort)Math.Round(1000 + _mPusher * 1000);

            // Аэроповерхности (для визуализации в GCS, физически не влияют).
            int aileron = 1500 + (int)Math.Round((targetRoll - roll) * 500);
            int elevator = 1500 + (int)Math.Round((targetPitch - pitch) * 500);
            int rudder = 1500 + (int)Math.Round(yawRate * 150);
            state.ServoPwm[5] = (ushort)Math.Clamp(elevator, 1000, 2000);
            state.ServoPwm[6] = (ushort)Math.Clamp(aileron, 1000, 2000);
            state.ServoPwm[7] = (ushort)Math.Clamp(3000 - aileron, 1000, 2000);  // зеркально
            state.ServoPwm[8] = (ushort)Math.Clamp(rudder, 1000, 2000);
            for (int i = 9; i < state.ServoPwm.Length; i++) state.ServoPwm[i] = 1500;

            // ---- Aero и тяга ----
            double tLiftActual = (_mLift0 + _mLift1 + _mLift2 + _mLift3) / 4.0
                * _cfg.MaxLiftThrustN;
            double tPushActual = _mPusher * _cfg.MaxPusherThrustN;

            double rho = Atmosphere.AirDensity(altAmsl);
            double qbar = 0.5 * rho * airspeedTotal * airspeedTotal;

            // Angle of attack (упрощённо: pitch − flight_path_angle).
            double flightPathAngle = airspeedTotal > 0.5
                ? Math.Asin(Math.Clamp(-vRelD / airspeedTotal, -1, 1))
                : 0;
            double alpha = newPitch - flightPathAngle;

            // C_L: линейный до stall, потом резкий спад.
            double cL;
            double absAlpha = Math.Abs(alpha);
            if (absAlpha < _cfg.StallAngleRad)
            {
                cL = _cfg.CL_Alpha * alpha;
            }
            else
            {
                double clPeak = _cfg.CL_Alpha * _cfg.StallAngleRad;
                double reduction = Math.Clamp(
                    1.0 - (absAlpha - _cfg.StallAngleRad) * 5.0, 0, 1);
                cL = clPeak * Math.Sign(alpha) * reduction;
            }

            double cD = _cfg.CD_0 + (cL * cL) / (Math.PI * _cfg.WingAspectRatio * _cfg.OswaldEff);

            double liftMag = qbar * _cfg.WingAreaM2 * cL;
            double dragMag = qbar * _cfg.WingAreaM2 * cD;

            // DCM body → NED (ZYX).
            double cPsi = Math.Cos(newYaw);
            double sPsi = Math.Sin(newYaw);
            double cTh = Math.Cos(newPitch);
            double sTh = Math.Sin(newPitch);
            double cPh = Math.Cos(newRoll);
            double sPh = Math.Sin(newRoll);

            // Ось body +X в NED.
            double bxN = cTh * cPsi;
            double bxE = cTh * sPsi;
            double bxD = -sTh;

            // Ось body −Z в NED (вверх body).
            double bmzN = -(cPh * sTh * cPsi + sPh * sPsi);
            double bmzE = -(cPh * sTh * sPsi - sPh * cPsi);
            double bmzD = -(cPh * cTh);

            // Единичный вектор движения (для drag).
            double ainv = (airspeedTotal > 0.1) ? 1.0 / airspeedTotal : 0;
            double mvN = vRelN * ainv;
            double mvE = vRelE * ainv;
            double mvD = vRelD * ainv;

            // Forces / mass.
            double invM = 1.0 / _cfg.MassKg;
            double aN = tLiftActual * invM * bmzN + tPushActual * invM * bxN
                      + liftMag * invM * bmzN + dragMag * invM * (-mvN)
                      + qbar * _cfg.BaseDragCoef * invM * (-mvN);
            double aE = tLiftActual * invM * bmzE + tPushActual * invM * bxE
                      + liftMag * invM * bmzE + dragMag * invM * (-mvE)
                      + qbar * _cfg.BaseDragCoef * invM * (-mvE);
            double aD = tLiftActual * invM * bmzD + tPushActual * invM * bxD
                      + liftMag * invM * bmzD + dragMag * invM * (-mvD)
                      + qbar * _cfg.BaseDragCoef * invM * (-mvD)
                      + Atmosphere.G;

            // ---- Integrate velocity & position ----
            double newVn = vn + aN * dt;
            double newVe = ve + aE * dt;
            double newVd = vd + aD * dt;

            Navigator.AdvanceLatLon(ref lat, ref lon, newVn * dt, newVe * dt);
            double newAltAmsl = altAmsl - newVd * dt;
            double newAltRel = newAltAmsl - state.Home.AltAmsl;

            if (newAltRel < 0)
            {
                newAltRel = 0;
                newAltAmsl = state.Home.AltAmsl;
                if (newVd > 0) newVd = 0;
                newVn *= 0.5; newVe *= 0.5;
            }

            // ---- Write state ----
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
            state.Velocity.AirSpeed = airspeedHoriz;
            double displayThrottle = liftThrottle * _liftEngagement
                                   + pusherThrottle * _pusherEngagement;
            state.Velocity.ThrottlePercent = (ushort)Math.Clamp(
                Math.Round(displayThrottle * 100), 0, 100);

            // Wind в state (для MAVLink WIND message).
            state.Wind.DirectionDeg = _wind.DirectionDeg;
            state.Wind.SpeedMs = _wind.SpeedMs;
            state.Wind.SpeedZMs = _wind.VerticalMs;

            UpdateLandedState(state);

            // ---- Battery ----
            double currentA = _cfg.IdleCurrentA
                + liftThrottle * _liftEngagement * _cfg.MaxMcExtraCurrentA
                + pusherThrottle * _pusherEngagement * _cfg.MaxFwExtraCurrentA;
            if (state.Failures.MotorFailureIndex >= 0) currentA *= 0.8;
            DrainBattery(dt, state, currentA);
        }

        // =====================================================================
        // Controllers
        // =====================================================================

        private void ComputeMcController(SimState state,
            double targetAltRel, double targetVn, double targetVe,
            out double targetRoll, out double targetPitch,
            out double yawRate, out double throttle)
        {
            double altErr = targetAltRel - state.Position.AltRelative;
            double targetClimb = Math.Clamp(altErr * _cfg.K_Alt,
                -_cfg.MaxDescentRateMs, _cfg.MaxClimbRateMs);
            double targetVd = -targetClimb;

            double aN = (targetVn - state.Velocity.Vn) * _cfg.K_Vel;
            double aE = (targetVe - state.Velocity.Ve) * _cfg.K_Vel;
            double maxTiltRad = _cfg.MaxTiltDegMc * Navigator.DegToRad;
            double maxAccel = Atmosphere.G * Math.Tan(maxTiltRad);
            double aMag = Math.Sqrt(aN * aN + aE * aE);
            if (aMag > maxAccel && aMag > 1e-6)
            {
                double s = maxAccel / aMag;
                aN *= s; aE *= s;
            }

            double cPsi = Math.Cos(state.Attitude.Yaw);
            double sPsi = Math.Sin(state.Attitude.Yaw);
            double tx = aN / Atmosphere.G;
            double ty = aE / Atmosphere.G;
            targetPitch = -(tx * cPsi + ty * sPsi);
            targetRoll = -tx * sPsi + ty * cPsi;
            targetRoll = Math.Clamp(targetRoll, -maxTiltRad, maxTiltRad);
            targetPitch = Math.Clamp(targetPitch, -maxTiltRad, maxTiltRad);

            double yawTargetDeg = double.IsNaN(_cmd.TargetYawDeg)
                ? _yawHold * Navigator.RadToDeg
                : _cmd.TargetYawDeg;
            double errDeg = Navigator.AngleDiffDeg(
                state.Attitude.Yaw * Navigator.RadToDeg, yawTargetDeg);
            yawRate = errDeg * Navigator.DegToRad * _cfg.K_Yaw;
            double maxYawRate = _cfg.MaxYawRateDegPerSec * Navigator.DegToRad;
            yawRate = Math.Clamp(yawRate, -maxYawRate, maxYawRate);

            double aDNeed = (targetVd - state.Velocity.Vd) * _cfg.K_Vd;
            double cR = Math.Cos(targetRoll);
            double cP = Math.Cos(targetPitch);
            double cRP = Math.Max(cR * cP, 0.1);
            double tNeed = _cfg.MassKg * (Atmosphere.G - aDNeed) / cRP;
            throttle = Math.Clamp(tNeed / _cfg.MaxLiftThrustN, 0, _cmd.ThrottleMax);
        }

        private void ComputeFwController(SimState state,
            double targetAltRel, double airspeedHoriz,
            out double targetRoll, out double targetPitch,
            out double yawRate, out double throttle)
        {
            // Pitch от alt error.
            double altErr = targetAltRel - state.Position.AltRelative;
            double maxPitchRad = _cfg.MaxPitchDegFw * Navigator.DegToRad;
            targetPitch = Math.Clamp(altErr * _cfg.K_FwPitch, -maxPitchRad, maxPitchRad);

            // Bank от heading error (координированный поворот).
            double headingErrDeg = 0;
            if (_cmd.HasPositionTarget)
            {
                double bearing = Navigator.BearingDeg(
                    state.Position.Lat, state.Position.Lon,
                    _cmd.TargetLat, _cmd.TargetLon);
                headingErrDeg = Navigator.AngleDiffDeg(
                    state.Attitude.Yaw * Navigator.RadToDeg, bearing);
            }
            else if (!double.IsNaN(_cmd.TargetYawDeg))
            {
                headingErrDeg = Navigator.AngleDiffDeg(
                    state.Attitude.Yaw * Navigator.RadToDeg, _cmd.TargetYawDeg);
            }
            double maxBankRad = _cfg.MaxRollDegFw * Navigator.DegToRad;
            targetRoll = Math.Clamp(headingErrDeg * _cfg.K_FwBank, -maxBankRad, maxBankRad);

            // Yaw rate из угла крена (coordinated turn): ω = g·tan(φ)/V.
            double vEff = Math.Max(airspeedHoriz, 5.0);
            yawRate = Atmosphere.G * Math.Tan(targetRoll) / vEff;

            // Pusher throttle: airspeed controller.
            double targetAirspeed = _cmd.TargetAirspeedMs > 0
                ? _cmd.TargetAirspeedMs : _cfg.VCruiseMs;
            double spdErr = targetAirspeed - airspeedHoriz;
            throttle = Math.Clamp(0.5 + spdErr * _cfg.K_FwAirspeed, 0, _cmd.ThrottleMax);
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
            state.Battery.ConsumedMah += currentA * dt * (1000.0 / 3600.0);
            double pct = 100.0 * (1.0 - state.Battery.ConsumedMah / _cfg.BatteryCapacityMah);
            state.Battery.Percent = Math.Max(0.0, pct);
            double frac = LipoCurveFraction(state.Battery.Percent);
            double vSag = currentA * _cfg.BatteryInternalR;
            state.Battery.VoltageV = Math.Max(0.0, _cfg.BatteryNominalV * frac - vSag);
        }

        private static double LipoCurveFraction(double percent)
        {
            if (percent >= 90.0) return 1.00 - (100.0 - percent) / 10.0 * 0.04;
            if (percent >= 20.0) return 0.96 - (90.0 - percent) / 70.0 * 0.10;
            if (percent >= 10.0) return 0.86 - (20.0 - percent) / 10.0 * 0.01;
            if (percent >= 0.0) return 0.85 - (10.0 - percent) / 10.0 * 0.064;
            return 0.786;
        }

        private static double MoveToward(double current, double target, double maxDelta)
        {
            double diff = target - current;
            if (Math.Abs(diff) <= maxDelta) return target;
            return current + Math.Sign(diff) * maxDelta;
        }
    }
}