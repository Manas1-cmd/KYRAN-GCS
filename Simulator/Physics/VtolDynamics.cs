using System;
using SimpleDroneGCS.Simulator.Control;
using SimpleDroneGCS.Simulator.Core;

namespace SimpleDroneGCS.Simulator.Physics
{
    /// <summary>
    /// VTOL физика — МАКСИМАЛЬНО ПРОСТАЯ.
    /// Никаких сил, никакой aerodynamics. Просто двигаем ВС куда сказали.
    /// Нужна работающая симуляция для тестирования GCS, а не реалистичная физика.
    /// </summary>
    public sealed class VtolDynamics : ISimVehicle
    {
        public sealed class VtolConfig
        {
            public double MassKg = 34.0;
            public double BatteryCapacityMah = 44000;
            public double BatteryNominalV = 50.4;

            // Параметры движения (не физика, просто скорости)
            public double VerticalSpeedMs = 3.0;      // скорость подъёма/посадки
            public double HorizMcSpeedMs = 10.0;      // MC waypoint
            public double HorizFwSpeedMs = 20.0;      // FW waypoint (cruise)
            public double TurnRateDegPerSec = 90.0;   // физический максимум; L1 ограничивает реальный turn через bank

            // Engagement по времени
            public double TransitionFwDurationSec = 5.0;
            public double TransitionMcDurationSec = 3.0;

            // Батарея
            public double IdleCurrentA = 1.0;
            public double McCurrentA = 80.0;          // в MC-режимах
            public double FwCurrentA = 25.0;          // в FW-режимах
        }

        private readonly VtolConfig _cfg;
        private readonly WindModel _wind;

        private ControlCommand _cmd;

        // Engagement state
        private double _liftEngagement = 1.0;
        private double _pusherEngagement = 0.0;
        private double _transitionProgressSec = 0.0;
        private ControlMode _lastMode = ControlMode.Idle;

        // Yaw hold
        private double _yawRad = 0.0;

        // =====================================================================
        // ISimVehicle
        // =====================================================================

        public VehicleType Vehicle => VehicleType.Vtol;
        public int MotorCount => 5;
        public bool SupportsFwMode => true;
        public double MassKg => _cfg.MassKg;
        public double BatteryCapacityMah => _cfg.BatteryCapacityMah;
        public double BatteryNominalV => _cfg.BatteryNominalV;

        public ControlMode CurrentMode => _cmd.Mode;
        public double LiftEngagement => _liftEngagement;
        public double PusherEngagement => _pusherEngagement;

        public VtolDynamics(WindModel wind, VtolConfig cfg = null)
        {
            _wind = wind ?? throw new ArgumentNullException(nameof(wind));
            _cfg = cfg ?? new VtolConfig();
            _cmd = new ControlCommand { Mode = ControlMode.Idle };
        }

        public void ApplyControl(in ControlCommand cmd) => _cmd = cmd;

        public void Reset(SimState state)
        {
            _cmd = new ControlCommand { Mode = ControlMode.Idle };
            _liftEngagement = 1.0;
            _pusherEngagement = 0.0;
            _transitionProgressSec = 0.0;
            _lastMode = ControlMode.Idle;
            _yawRad = 0.0;
        }

        // =====================================================================
        // Step
        // =====================================================================

        public void Step(double dt, SimState state)
        {
            // На земле и не armed — сидим
            if (!state.Armed || _cmd.Mode == ControlMode.Idle)
            {
                GroundStep(state);
                DrainBattery(dt, state, _cfg.IdleCurrentA);
                return;
            }

            // Смена режима — сбрасываем таймер transition
            if (_cmd.Mode != _lastMode)
            {
                _transitionProgressSec = 0;
                _lastMode = _cmd.Mode;
            }

            UpdateEngagement(dt);
            MoveVehicle(dt, state);

            double currentA = _cfg.IdleCurrentA
                + _liftEngagement * _cfg.McCurrentA
                + _pusherEngagement * _cfg.FwCurrentA;
            DrainBattery(dt, state, currentA);
        }

        private void GroundStep(SimState state)
        {
            using (state.Write())
            {
                state.Velocity.Vn = 0;
                state.Velocity.Ve = 0;
                state.Velocity.Vd = 0;
                state.Velocity.AirSpeed = 0;
                state.Velocity.ThrottlePercent = 0;
                state.Position.AltRelative = 0;
                if (state.Home.Set)
                {
                    state.Position.Lat = state.Home.Lat;
                    state.Position.Lon = state.Home.Lon;
                    state.Position.AltAmsl = state.Home.AltAmsl;
                }
                state.LandedState = LandedStateKind.OnGround;
            }
            _liftEngagement = 1.0;
            _pusherEngagement = 0.0;
        }

        private void UpdateEngagement(double dt)
        {
            switch (_cmd.Mode)
            {
                case ControlMode.FixedWing:
                    _liftEngagement = 0.0;
                    _pusherEngagement = 1.0;
                    break;

                case ControlMode.TransitionToFw:
                    _transitionProgressSec += dt;
                    double tFw = Math.Clamp(
                        _transitionProgressSec / _cfg.TransitionFwDurationSec, 0, 1);
                    _liftEngagement = 1.0 - tFw;
                    _pusherEngagement = tFw;
                    break;

                case ControlMode.TransitionToMc:
                    _transitionProgressSec += dt;
                    double tMc = Math.Clamp(
                        _transitionProgressSec / _cfg.TransitionMcDurationSec, 0, 1);
                    _liftEngagement = tMc;
                    _pusherEngagement = 1.0 - tMc;
                    break;

                default:  // Multirotor, Takeoff, Landing, Idle
                    _liftEngagement = 1.0;
                    _pusherEngagement = 0.0;
                    break;
            }
        }

        // =====================================================================
        // Главное — движение ВС. Без физики.
        // =====================================================================

        private void MoveVehicle(double dt, SimState state)
        {
            double lat = state.Position.Lat;
            double lon = state.Position.Lon;
            double altRel = state.Position.AltRelative;
            double altAmsl = state.Position.AltAmsl;

            // Расчёт целей движения в зависимости от режима
            double targetAlt;
            double targetVn = 0, targetVe = 0;
            // Если L1 (или другой контроллер) прислал TargetYawDeg — используем его.
            // Иначе пусть switch case вычислит сам.
            double targetYawDeg = !double.IsNaN(_cmd.TargetYawDeg) ? _cmd.TargetYawDeg : double.NaN;

            // Скорость зависит от режима
            bool isFw = _cmd.Mode == ControlMode.FixedWing
                     || _cmd.Mode == ControlMode.TransitionToFw;
            double cruiseSpeed = isFw
                ? (_cmd.TargetSpeedMs > 0 ? _cmd.TargetSpeedMs : _cfg.HorizFwSpeedMs)
                : (_cmd.TargetSpeedMs > 0 ? _cmd.TargetSpeedMs : _cfg.HorizMcSpeedMs);

            switch (_cmd.Mode)
            {
                case ControlMode.Takeoff:
                    // Вертикально вверх до target alt
                    targetAlt = double.IsNaN(_cmd.TargetAltRelative)
                        ? 10.0 : _cmd.TargetAltRelative;
                    targetVn = 0;
                    targetVe = 0;
                    break;

                case ControlMode.Landing:
                    // Вертикально вниз
                    targetAlt = -1.0;
                    if (_cmd.HasPositionTarget)
                    {
                        // Если есть точка посадки — летим к ней на текущей высоте
                        double dist = Navigator.DistanceM(lat, lon,
                            _cmd.TargetLat, _cmd.TargetLon);
                        if (dist > 5.0)
                        {
                            // Далеко — держим alt, летим горизонтально к точке
                            targetAlt = altRel;
                            double bearing = Navigator.BearingDeg(lat, lon,
                                _cmd.TargetLat, _cmd.TargetLon);
                            if (double.IsNaN(targetYawDeg)) targetYawDeg = bearing;
                            double bRad = bearing * Navigator.DegToRad;
                            double speed = Math.Min(cruiseSpeed, dist * 0.5);
                            targetVn = speed * Math.Cos(bRad);
                            targetVe = speed * Math.Sin(bRad);
                        }
                    }
                    break;

                case ControlMode.TransitionToFw:
                case ControlMode.TransitionToMc:
                    // Держим высоту. Движемся к следующей точке если она есть.
                    targetAlt = double.IsNaN(_cmd.TargetAltRelative)
                        ? altRel : Math.Max(_cmd.TargetAltRelative, altRel);
                    if (_cmd.HasPositionTarget)
                    {
                        double bearing = Navigator.BearingDeg(lat, lon,
                            _cmd.TargetLat, _cmd.TargetLon);
                        if (double.IsNaN(targetYawDeg)) targetYawDeg = bearing;
                        double bRad = bearing * Navigator.DegToRad;
                        // Во время transition скорость ЛИНЕЙНО растёт от MC до FW
                        double progress = _cmd.Mode == ControlMode.TransitionToFw
                            ? _pusherEngagement
                            : _liftEngagement;
                        double speed = _cfg.HorizMcSpeedMs
                            + (_cfg.HorizFwSpeedMs - _cfg.HorizMcSpeedMs) * progress;
                        targetVn = speed * Math.Cos(bRad);
                        targetVe = speed * Math.Sin(bRad);
                    }
                    break;

                case ControlMode.FixedWing:
                case ControlMode.Multirotor:
                default:
                    // Waypoint navigation
                    targetAlt = double.IsNaN(_cmd.TargetAltRelative)
                        ? altRel : _cmd.TargetAltRelative;
                    if (_cmd.HasPositionTarget)
                    {
                        double dist = Navigator.DistanceM(lat, lon,
                            _cmd.TargetLat, _cmd.TargetLon);
                        double bearing;

                        // PATH FOLLOWING: если есть линия From→To, летим ПО ЛИНИИ
                        // с коррекцией на отклонение (cross-track). Это даёт
                        // прямой полёт по плану миссии, не "напрямую к WP".
                        if (!double.IsNaN(_cmd.FromLat) && !double.IsNaN(_cmd.FromLon))
                        {
                            // Bearing вдоль линии From→To (это и есть "правильное" направление).
                            double lineBearing = Navigator.BearingDeg(
                                _cmd.FromLat, _cmd.FromLon,
                                _cmd.TargetLat, _cmd.TargetLon);

                            // Cross-track: насколько ВС отклонился от линии.
                            double xtrack = Navigator.CrossTrackDistanceM(
                                _cmd.FromLat, _cmd.FromLon,
                                _cmd.TargetLat, _cmd.TargetLon,
                                lat, lon);

                            // Коррекция курса: чем больше отклонение, тем сильнее
                            // довернуть к линии. Макс. коррекция 30°.
                            // xtrack положительный = ВС справа → доворачиваем влево (-).
                            double correctionDeg = Math.Clamp(-xtrack * 0.5, -30, 30);
                            bearing = lineBearing + correctionDeg;
                        }
                        else
                        {
                            // Нет линии — летим прямо к точке (старая логика).
                            bearing = Navigator.BearingDeg(lat, lon,
                                _cmd.TargetLat, _cmd.TargetLon);
                        }

                        if (double.IsNaN(targetYawDeg)) targetYawDeg = bearing;
                        double speed = dist < 1.0 ? 0 : Math.Min(cruiseSpeed, dist * 0.5);
                        // Минимум cruise при path following (чтобы не тормозил у каждой WP)
                        if (!double.IsNaN(_cmd.FromLat))
                            speed = cruiseSpeed;

                        // КЛЮЧЕВОЙ МОМЕНТ: в FW-режиме скорость идёт ПО YAW
                        // (с инерцией поворота), а не по мгновенному bearing.
                        // Это даёт реальную дугу разворота как у самолёта.
                        bool isFwForVelocity = _cmd.Mode == ControlMode.FixedWing
                                            || _cmd.Mode == ControlMode.TransitionToFw;
                        if (isFwForVelocity)
                        {
                            // Yaw повернётся к bearing постепенно (TurnRateDegPerSec).
                            // А Vn/Ve берём от ТЕКУЩЕГО yaw, не от bearing.
                            // Так ВС летит дугой пока поворачивается.
                            double yawForVel = _yawRad;
                            targetVn = speed * Math.Cos(yawForVel);
                            targetVe = speed * Math.Sin(yawForVel);
                        }
                        else
                        {
                            // MC-режим — летит прямо к цели (мультикоптер может).
                            double bRad = bearing * Navigator.DegToRad;
                            targetVn = speed * Math.Cos(bRad);
                            targetVe = speed * Math.Sin(bRad);
                        }
                    }
                    break;
            }

            // ---- Применяем движение ----

            // Вертикальная скорость: плавно к target alt
            double altErr = targetAlt - altRel;
            double targetVd;
            if (altErr > 1.0)
                targetVd = -_cfg.VerticalSpeedMs;  // вверх
            else if (altErr < -1.0)
                targetVd = _cfg.VerticalSpeedMs;   // вниз
            else
                targetVd = -altErr;                // мягкая корректировка

            // Интегрируем Vd → alt
            double newVd = targetVd;
            double newAltRel = altRel - newVd * dt;
            double newAltAmsl = altAmsl - newVd * dt;

            // Ground clamp
            if (newAltRel <= 0)
            {
                newAltRel = 0;
                newVd = 0;
                newAltAmsl = state.Home.Set ? state.Home.AltAmsl : altAmsl;
            }

            // Применяем горизонтальную скорость (прямо, без инерции)
            double newVn = targetVn;
            double newVe = targetVe;

            // Обновляем позицию
            const double earthRadius = 6378137.0;
            double dLat = (newVn * dt) / earthRadius * Navigator.RadToDeg;
            double dLon = (newVe * dt) / (earthRadius * Math.Cos(lat * Navigator.DegToRad))
                          * Navigator.RadToDeg;
            double newLat = lat + dLat;
            double newLon = lon + dLon;

            // ---- Yaw — плавный поворот ----
            double yawDeg = _yawRad * Navigator.RadToDeg;
            if (!double.IsNaN(targetYawDeg))
            {
                double yawErr = Navigator.AngleDiffDeg(yawDeg, targetYawDeg);
                double maxTurn = _cfg.TurnRateDegPerSec * dt;
                double turn = Math.Clamp(yawErr, -maxTurn, maxTurn);
                yawDeg += turn;
            }
            _yawRad = yawDeg * Navigator.DegToRad;
            while (_yawRad > Math.PI) _yawRad -= 2 * Math.PI;
            while (_yawRad < -Math.PI) _yawRad += 2 * Math.PI;

            // ---- Attitude (визуальный — для HUD GCS) ----
            double roll = 0;
            double pitch = 0;
            double horizSpeed = Math.Sqrt(newVn * newVn + newVe * newVe);
            if (isFw && horizSpeed > 5.0)
            {
                // В FW маленький pitch при подъёме/спуске
                if (targetVd < -0.5) pitch = 5 * Navigator.DegToRad;
                else if (targetVd > 0.5) pitch = -5 * Navigator.DegToRad;
                // Bank при повороте
                if (!double.IsNaN(targetYawDeg))
                {
                    double yawErr = Navigator.AngleDiffDeg(yawDeg, targetYawDeg);
                    roll = Math.Clamp(yawErr * 0.3, -25, 25) * Navigator.DegToRad;
                }
            }
            else if (horizSpeed > 2.0)
            {
                // MC — небольшой наклон вперёд при движении
                pitch = -10 * Navigator.DegToRad * Math.Min(1.0, horizSpeed / 10.0);
            }

            // ---- AirSpeed (с учётом ветра) ----
            _wind.GetWindNed(out double wN, out double wE, out double wD);
            double vRelN = newVn - wN;
            double vRelE = newVe - wE;
            double airspeed = Math.Sqrt(vRelN * vRelN + vRelE * vRelE);

            // ---- Запись в state ----
            using (state.Write())
            {
                state.Position.Lat = newLat;
                state.Position.Lon = newLon;
                state.Position.AltRelative = newAltRel;
                state.Position.AltAmsl = newAltAmsl;

                state.Attitude.Roll = roll;
                state.Attitude.Pitch = pitch;
                state.Attitude.Yaw = _yawRad;
                state.Attitude.RollSpeed = 0;
                state.Attitude.PitchSpeed = 0;
                state.Attitude.YawSpeed = 0;

                state.Velocity.Vn = newVn;
                state.Velocity.Ve = newVe;
                state.Velocity.Vd = newVd;
                state.Velocity.AirSpeed = airspeed;

                // Throttle для HUD (процент)
                double throttle = _liftEngagement * 0.7 + _pusherEngagement * 0.8;
                state.Velocity.ThrottlePercent = (ushort)Math.Clamp(
                    Math.Round(throttle * 100), 0, 100);

                state.Wind.DirectionDeg = _wind.DirectionDeg;
                state.Wind.SpeedMs = _wind.SpeedMs;
                state.Wind.SpeedZMs = _wind.VerticalMs;

                // Landed state
                state.LandedState = newAltRel < 0.3 && Math.Abs(newVd) < 0.3
                    ? LandedStateKind.OnGround
                    : LandedStateKind.InAir;
            }
        }

        private void DrainBattery(double dt, SimState state, double currentA)
        {
            double consumedMah = currentA * dt / 3.6;
            using (state.Write())
            {
                state.Battery.ConsumedMah += consumedMah;
                double pct = 100.0 * (1.0 - state.Battery.ConsumedMah / _cfg.BatteryCapacityMah);
                state.Battery.Percent = Math.Clamp(pct, 0, 100);

                double vFull = _cfg.BatteryNominalV;
                double vEmpty = vFull * 0.80;
                state.Battery.VoltageV = vEmpty + (vFull - vEmpty) * (state.Battery.Percent / 100.0);
                state.Battery.CurrentA = currentA;
            }
        }
    }
}