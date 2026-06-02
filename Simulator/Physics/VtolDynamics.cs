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
            // --- Mass & battery ---
            public double MassKg = 34.0;
            public double BatteryCapacityMah = 44000;
            public double BatteryNominalV = 50.4;

            // --- Target cruise speeds per mode ---
            public double HorizMcSpeedMs = 10.0;      // MC waypoint
            public double HorizFwSpeedMs = 20.0;      // FW waypoint (cruise)
            public double MaxClimbRateMs = 3.0;       // скорость подъёма (CopterDynamics-style)
            public double MaxDescentRateMs = 2.0;     // скорость снижения (меньше для безопасности)

            // --- Inertia (§1, §11 справочника) ---
            public double MaxAccelMcMs2 = 2.5;        // hover → 10 м/с за ~4 сек (подтверждено)
            public double MaxAccelFwMs2 = 2.3;        // Расчёт 11 + полевой тест: ROC max=2.6 м/с.
                                                      // Транзишн 0→17 м/с за ~9с (был 10.4с при 2.0).
                                                      // Полевой тест показал AS=16.3 за 10с — на грани, +15% запас.
                                                      // Расчёт 8: T_max=78Н, запас 2.8x над cruise drag.
            public double MaxVerticalAccelMs2 = 2.0;  // лимит вертикального ускорения
            public double K_Vel = 3.0;                // P-gain velocity controller, τ=0.33с
            public double K_Alt = 1.0;                // P-gain alt→climb, τ=1с
            public double K_Vd = 2.0;                 // P-gain vd→accel, τ=0.5с

            // --- Bank-to-turn в FW (§4) ---
            public double MaxBankDeg = 35.0;          // лимит крена, типичный QuadPlane (подтверждено)
            public double MaxBankRateDegPerSec = 30.0;
            public double MaxPitchDeg = 20.0;         // ArduPilot PTCH_LIM_MAX_DEG

            // --- MC attitude ---
            public double YawRateMcMaxDegPerSec = 90.0;  // мультикоптер поворачивается быстро
            public double MaxTiltMcDeg = 25.0;           // visual tilt в MC при движении

            // --- Dynamics lag (§11) ---
            public double AttitudeTauSec = 0.25;      // PX4 SIH, Pixhawk-class
            public double MotorTauSec = 0.30;         // large VTOL spool-up
            public double HoverThrottle = 0.50;       // ArduPilot MOT_THST_HOVER для T/W≈2

            // --- Drag ---
            public double DragCoef = 1.4;             // Н·с/м, линейный drag (34 кг с крылом).
                                                      // Расчёт 2: совпадает с квадратичной моделью при V=20.
                                                      // Расчёт 6: с MaxAccelFw=2.0 → V_terminal=49 м/с, t(0→17)=10.4с.

            // --- Transition corridor (§12) ---
            public double TransitionFwDurationSec = 5.0;  // Q_TRANSITION_MS в секундах
            public double TransitionMcDurationSec = 3.0;
            public double AirspeedMinMs = 17.0;           // ARSPD_FBW_MIN
            public double AirspeedStallMs = 12.0;         // Расчёт 1: реалистично для 34кг при S=2.5м², CL_max=1.51.
                                                          // Старое 14 даёт stall в повороте 35° при cruise 20.
            public double TransitionFailTimeoutSec = 15.0; // Q_TRANS_FAIL — ArduPilot-стандарт 15-30с.
                                                           // Полевой тест: AS=16.3 за 10с — нужно больше времени.

            // --- Battery currents ---
            public double IdleCurrentA = 1.0;
            public double McCurrentA = 80.0;
            public double FwCurrentA = 25.0;
        }

        private readonly VtolConfig _cfg;
        private readonly WindModel _wind;

        private ControlCommand _cmd;

        // Engagement state (transitions)
        private double _liftEngagement = 1.0;
        private double _pusherEngagement = 0.0;
        private double _transitionProgressSec = 0.0;
        private ControlMode _lastMode = ControlMode.Idle;

        // Attitude state — НЕ визуал, реально влияет на динамику.
        // bank → turn rate в FW (§4), pitch → climb angle, yaw → направление velocity в FW.
        private double _yawRad = 0.0;
        private double _bankRad = 0.0;
        private double _pitchRad = 0.0;

        // Motor spool-up lag (§11) — actual thrust engagement с задержкой MotorTauSec.
        // Target engagement из UpdateEngagement(), actual через first-order lag.
        private double _liftThrustActual = 0.0;
        private double _pusherThrustActual = 0.0;

        // Transition corridor monitoring (§12).
        // Timer считает секунды с момента входа в TransitionToFw. При timeout без
        // достижения AirspeedMinMs — флаг failed, эмитится STATUSTEXT, режим форсится обратно в MC.
        private double _transitionTimerSec = 0.0;
        private bool _transitionFailed = false;

        // Атмосферный buffet — малые колебания attitude от турбулентности.
        // Симулируются как сумма 3-х синусоид разных частот (упрощённая Dryden-модель).
        // НЕ влияет на физику движения — добавляется только в state.Attitude перед выводом.
        // Время накапливается в тиках; RNG-фаза каждой синусоиды рандомится при init,
        // чтобы все ВС в одном полёте не дрожали синхронно.
        private double _buffetTimeSec = 0.0;
        private readonly double _buffetRollPhase1;
        private readonly double _buffetRollPhase2;
        private readonly double _buffetRollPhase3;
        private readonly double _buffetPitchPhase1;
        private readonly double _buffetPitchPhase2;
        private readonly double _buffetPitchPhase3;

        // --- Events ---

        /// <summary>
        /// STATUSTEXT для GCS/лога. Эмитится при transition fail, stall warning и т.д.
        /// Подписаться в SimulatedDrone/SimulatorWindow и прокинуть через MAVLink.
        /// </summary>
        public event EventHandler<string> StatusText;

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

            // Случайные начальные фазы buffet — чтобы все 3 синусоиды начали с разного места.
            // Это даёт "органичный" паттерн колебаний, а не идеально-симметричные волны.
            var rng = new Random();
            _buffetRollPhase1 = rng.NextDouble() * 2 * Math.PI;
            _buffetRollPhase2 = rng.NextDouble() * 2 * Math.PI;
            _buffetRollPhase3 = rng.NextDouble() * 2 * Math.PI;
            _buffetPitchPhase1 = rng.NextDouble() * 2 * Math.PI;
            _buffetPitchPhase2 = rng.NextDouble() * 2 * Math.PI;
            _buffetPitchPhase3 = rng.NextDouble() * 2 * Math.PI;
        }

        public void ApplyControl(in ControlCommand cmd) => _cmd = cmd;

        public void Reset(SimState state)
        {
            _cmd = new ControlCommand { Mode = ControlMode.Idle };
            _liftEngagement = 1.0;
            _pusherEngagement = 0.0;
            _transitionProgressSec = 0.0;
            _lastMode = ControlMode.Idle;

            // Сохраняем текущий yaw при ресете — не сбрасываем в 0.
            _yawRad = state?.Attitude.Yaw ?? 0.0;
            _bankRad = 0.0;
            _pitchRad = 0.0;

            // На земле лифт-моторы считаем "прогретыми" (spool-up уже прошёл).
            _liftThrustActual = 1.0;
            _pusherThrustActual = 0.0;

            _transitionTimerSec = 0.0;
            _transitionFailed = false;
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
                // motorsActive=false → actual thrust затухает к 0 с MotorTauSec.
                UpdateMotorLag(dt, state, motorsActive: false);
                DrainBattery(dt, state, _cfg.IdleCurrentA);
                return;
            }

            // Смена режима — сбрасываем таймеры transition
            if (_cmd.Mode != _lastMode)
            {
                _transitionProgressSec = 0;
                // Transition-fail timer стартует при входе в TransitionToFw,
                // flag fail сбрасывается при выходе из этого режима.
                if (_cmd.Mode == ControlMode.TransitionToFw)
                {
                    _transitionTimerSec = 0;
                    _transitionFailed = false;
                }
                else if (_lastMode == ControlMode.TransitionToFw)
                {
                    _transitionFailed = false;
                }
                _lastMode = _cmd.Mode;
            }

            CheckTransitionCorridor(dt, state);
            UpdateEngagement(dt);
            UpdateMotorLag(dt, state, motorsActive: true);
            MoveVehicle(dt, state);

            // Battery current от ACTUAL engagement (c lag), не от target —
            // потребление реалистично при spool-up/spool-down.
            double currentA = _cfg.IdleCurrentA
                + _liftThrustActual * _cfg.McCurrentA
                + _pusherThrustActual * _cfg.FwCurrentA;
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

                // Все моторы выключены при disarm — PWM = 1000 (idle, 0% throttle).
                // SERVO_OUTPUT_RAW (cmd 36) → GCS отображает Multirotor 0%, Pusher 0%.
                for (int i = 0; i < state.ServoPwm.Length; i++)
                    state.ServoPwm[i] = 1000;

                // DO_SET_SERVO override (motor test на земле).
                for (int i = 0; i < state.ServoPwm.Length; i++)
                    if (state.ServoOverride[i] != 0)
                        state.ServoPwm[i] = state.ServoOverride[i];
            }
            _liftEngagement = 1.0;
            _pusherEngagement = 0.0;
        }

        private void UpdateEngagement(double dt)
        {
            switch (_cmd.Mode)
            {
                case ControlMode.FixedWing:
                    if (_transitionFailed)
                    {
                        // FW stall detected (см. CheckTransitionCorridor):
                        // lift моторы выстреливают чтобы спасти ВС (экстренный переход к MC).
                        // Это срабатывает при pusher failure в FW cruise или при падении AirSpeed.
                        _liftEngagement = 1.0;
                        _pusherEngagement = 0.0;
                    }
                    else
                    {
                        _liftEngagement = 0.0;
                        _pusherEngagement = 1.0;
                    }
                    break;

                case ControlMode.TransitionToFw:
                    if (_transitionFailed)
                    {
                        // Корридор провален (§12) — физика как MC, ВС спасается.
                        // STATUSTEXT уже был эмитнут в CheckTransitionCorridor.
                        _liftEngagement = 1.0;
                        _pusherEngagement = 0.0;
                        break;
                    }
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

        /// <summary>
        /// First-order lag для spool-up/spool-down моторов (§11 справочника).
        /// <para>
        /// Target engagement (<see cref="_liftEngagement"/>, <see cref="_pusherEngagement"/>)
        /// вычисляется в <see cref="UpdateEngagement"/> из режима полёта. Actual thrust
        /// (<see cref="_liftThrustActual"/>, <see cref="_pusherThrustActual"/>) догоняет
        /// target с задержкой <see cref="VtolConfig.MotorTauSec"/>.
        /// </para>
        /// <para>
        /// Формула (§11): α = 1 − exp(−dt/τ); actual[k] = actual[k-1] + α · (target − actual[k-1])
        /// </para>
        /// <para>
        /// Если <paramref name="motorsActive"/> = false (disarmed/idle), target = 0 —
        /// моторы затухают к нулю за ~3·τ секунд.
        /// </para>
        /// </summary>
        private void UpdateMotorLag(double dt, SimState state, bool motorsActive)
        {
            double liftTarget = motorsActive ? _liftEngagement : 0.0;
            double pusherTarget = motorsActive ? _pusherEngagement : 0.0;

            // === MOTOR FAILURE INJECTION ===
            // Failure моторов влияет на тягу, симулируя реальный отказ.
            //
            // VTOL компоновка: M1-M4 = lift (мультиротор), M5 = pusher (толкающий).
            //
            // Lift motor failure (M1-M4):
            //   - 1 из 4 → -25% подъёмной силы (оставшиеся 3 не могут компенсировать полностью)
            //   - ArduPilot в реальности пытается компенсировать через yaw-балансировку,
            //     но у quadcopter нет избыточных моторов → общий запас тяги падает.
            //   - ВС начинает терять высоту если уже был близко к hover ceiling.
            //
            // Pusher motor failure (M5):
            //   - 100% потеря forward thrust в FW
            //   - ВС не сможет держать скорость в FW → будет терять её
            //   - Physics: transition corridor monitor заметит AS < threshold → failed
            //     state → force MC (автоматический emergency transition)
            int failedMotor = state.Failures.MotorFailureIndex;
            if (failedMotor >= 0 && motorsActive)
            {
                if (failedMotor >= 0 && failedMotor <= 3)
                {
                    // Lift motor M1..M4 failed → -25% подъёмной тяги
                    liftTarget *= 0.75;
                }
                else if (failedMotor == 4)
                {
                    // Pusher M5 failed → 0 forward thrust
                    pusherTarget = 0.0;
                }
            }

            // Защита от деления на ноль при τ≈0.
            double tau = Math.Max(0.01, _cfg.MotorTauSec);
            double alpha = 1.0 - Math.Exp(-dt / tau);

            _liftThrustActual += (liftTarget - _liftThrustActual) * alpha;
            _pusherThrustActual += (pusherTarget - _pusherThrustActual) * alpha;

            // Clamp для численной устойчивости (защита от накопления float-ошибок).
            _liftThrustActual = Math.Clamp(_liftThrustActual, 0.0, 1.0);
            _pusherThrustActual = Math.Clamp(_pusherThrustActual, 0.0, 1.0);
        }

        /// <summary>
        /// Мониторинг transition corridor (§12 справочника).
        /// <para>
        /// При входе в <see cref="ControlMode.TransitionToFw"/> запускается таймер
        /// <see cref="_transitionTimerSec"/>. Если за <see cref="VtolConfig.TransitionFailTimeoutSec"/>
        /// ВС не достиг <see cref="VtolConfig.AirspeedMinMs"/> — transition считается failed.
        /// </para>
        /// <para>
        /// При fail эмитится <see cref="StatusText"/> один раз, и
        /// <see cref="UpdateEngagement"/> начинает держать ВС в MC-режиме (физика спасает ВС).
        /// Решение о миссии (abort/reroute) — ответственность верхнего уровня.
        /// </para>
        /// <para>
        /// Успешный exit: airspeed набран → дальнейший UpdateEngagement завершит переход
        /// через <see cref="_transitionProgressSec"/>.
        /// </para>
        /// </summary>
        private void CheckTransitionCorridor(double dt, SimState state)
        {
            // Check 1: FW cruise с критически низкой airspeed (pusher fail/stall).
            // В FW ВС держится на AirSpeed ≥ AirspeedStallMs. Если скорость упала
            // ниже stall (12 м/с) — крылья не создают подъёмной силы, ВС падает.
            // Сигнализируем: _transitionFailed→true, физика перейдёт в MC-спасение.
            if (_cmd.Mode == ControlMode.FixedWing && !_transitionFailed)
            {
                if (state.Velocity.AirSpeed < _cfg.AirspeedStallMs)
                {
                    _transitionFailed = true;

                    string stallMsg = string.Format(
                        System.Globalization.CultureInfo.InvariantCulture,
                        "FW stall: airspeed {0:F1} < {1:F0} m/s. Reverting to MC.",
                        state.Velocity.AirSpeed,
                        _cfg.AirspeedStallMs);

                    StatusText?.Invoke(this, stallMsg);
                    return;
                }
            }

            // Check 2: Только в режиме TransitionToFw мониторим корридор.
            if (_cmd.Mode != ControlMode.TransitionToFw)
            {
                return;
            }

            _transitionTimerSec += dt;

            // Уже зафейлено — не эмитим повторно.
            if (_transitionFailed) return;

            // Успех: airspeed набран → transition завершится естественно.
            if (state.Velocity.AirSpeed >= _cfg.AirspeedMinMs)
            {
                return;
            }

            // Timeout без достижения airspeed → fail.
            if (_transitionTimerSec >= _cfg.TransitionFailTimeoutSec)
            {
                _transitionFailed = true;

                string msg = string.Format(
                    System.Globalization.CultureInfo.InvariantCulture,
                    "Transition failed: airspeed {0:F1} < {1:F0} m/s for {2:F0}s. Reverting to MC.",
                    state.Velocity.AirSpeed,
                    _cfg.AirspeedMinMs,
                    _cfg.TransitionFailTimeoutSec);

                StatusText?.Invoke(this, msg);
            }
        }

        // =====================================================================
        // Главное — движение ВС. Без физики.
        // =====================================================================

        /// <summary>
        /// Физика движения VTOL — честный P-каскад, инерция, ветер сносит, bank-to-turn.
        /// <para>
        /// Архитектура (formulas из SIMULATOR_FORMULAS.md):
        /// <list type="number">
        ///   <item>Target velocity NED из режима и команды (mode-specific speed limits)</item>
        ///   <item>P-каскад по высоте: alt_err → climb_target (§11, K_Alt)</item>
        ///   <item>P-каскад по vertical speed: vd_err → vertical accel, clamp MaxVerticalAccelMs2 (§11, K_Vd)</item>
        ///   <item>P-каскад по горизонтали: vel_err → horizontal accel, clamp по MaxAccel*Ms2 (§1, §11)</item>
        ///   <item>Drag от air velocity (velocity - wind) — §1, ветер реально сносит ВС</item>
        ///   <item>Bank-to-turn в FW: ω_yaw = g·tan(φ)/V (§4), bank как real state с MaxBankRateDegPerSec</item>
        ///   <item>Attitude lag: bank/pitch/yaw догоняют target с AttitudeTauSec (§11)</item>
        ///   <item>Euler интегрирование position += velocity·dt (§13, достаточно для 50 Гц)</item>
        ///   <item>Ground clamp: на земле скорость умножается на 0.5 (ground friction hack, как в CopterDynamics)</item>
        /// </list>
        /// </para>
        /// </summary>
        /// <summary>
        /// Физика движения VTOL — честный P-каскад, инерция, ветер сносит, bank-to-turn.
        /// <para>
        /// Архитектура (formulas из SIMULATOR_FORMULAS.md):
        /// <list type="number">
        ///   <item>Target velocity NED: target (lat/lon) → local NED delta (§14), далее скорость вдоль вектора</item>
        ///   <item>P-каскад по высоте: alt_err → climb_target (§11, K_Alt)</item>
        ///   <item>P-каскад по vertical speed: vd_err → vertical accel, clamp MaxVerticalAccelMs2 (§11, K_Vd)</item>
        ///   <item>P-каскад по горизонтали: vel_err → horizontal accel, clamp по MaxAccel*Ms2 (§1, §11)</item>
        ///   <item>Drag от air velocity (velocity - wind) — §1, ветер реально сносит</item>
        ///   <item>Bank-to-turn в FW: ω_yaw = g·tan(φ)/V (§4)</item>
        ///   <item>Attitude lag: bank/pitch/yaw догоняют target с AttitudeTauSec (§11)</item>
        ///   <item>Position integration: lat/lon через геодезическую плоскую аппроксимацию (§14)</item>
        ///   <item>Ground clamp: altRel ≥ 0, horizontal velocity * 0.5 на земле</item>
        /// </list>
        /// </para>
        /// </summary>
        private void MoveVehicle(double dt, SimState state)
        {
            // =========================================================================
            // 0. Snapshot текущего состояния (читаем в том же потоке что writer — OK без lock)
            // =========================================================================
            double curLat = state.Position.Lat;
            double curLon = state.Position.Lon;
            double altRel = state.Position.AltRelative;
            double altAmsl = state.Position.AltAmsl;

            double vN = state.Velocity.Vn;
            double vE = state.Velocity.Ve;
            double vD = state.Velocity.Vd;

            // Wind NED — WindModel возвращает вектор (куда дует), см. WindModel.GetWindNed
            _wind.GetWindNed(out double windN, out double windE, out double windD);

            const double EARTH_R = 6371000.0;  // средний радиус Земли, м (§14)

            // =========================================================================
            // 1. P-каскад alt → target climb rate (§11)
            // =========================================================================
            double targetAltRel = double.IsNaN(_cmd.TargetAltRelative)
                ? altRel
                : _cmd.TargetAltRelative;
            double altError = targetAltRel - altRel;
            double targetVd = -_cfg.K_Alt * altError;  // NED: vd<0 = подъём

            // Clamp по MaxClimb/MaxDescent
            targetVd = Math.Clamp(targetVd, -_cfg.MaxClimbRateMs, _cfg.MaxDescentRateMs);

            // Двухступенчатый спуск при посадке (ArduPilot Q_LAND_FINAL_ALT).
            // Выше 6 м — обычная скорость до Q_LAND_SPEED (3 м/с).
            // Ниже 6 м — замедленная до Q_LAND_FINAL_SPEED (1.5 м/с).
            //
            // Это имитирует поведение реального QuadPlane: мягкий финальный метр,
            // точная высота принятия решения об аварийном прерывании.
            if (_cmd.Mode == ControlMode.Landing && targetVd > 0)
            {
                // ControlMode.Landing = режим посадки. targetVd > 0 = спускаемся (NED).
                const double LAND_FINAL_ALT = 6.0;       // высота перехода на замедление
                const double LAND_SPEED = 3.0;           // обычная скорость посадки [м/с]
                const double LAND_FINAL_SPEED = 1.5;     // финальная скорость посадки [м/с]

                double maxDescentRate = altRel < LAND_FINAL_ALT
                    ? LAND_FINAL_SPEED
                    : LAND_SPEED;

                if (targetVd > maxDescentRate)
                    targetVd = maxDescentRate;
            }

            // =========================================================================
            // 2. P-каскад vd → vertical accel (§11)
            // =========================================================================
            double vdError = targetVd - vD;
            double aD = _cfg.K_Vd * vdError;
            aD = Math.Clamp(aD, -_cfg.MaxVerticalAccelMs2, _cfg.MaxVerticalAccelMs2);

            // Motor failure физическое влияние на вертикальную динамику.
            //
            // _liftThrustActual ∈ [0,1] уже учитывает failure degradation
            // (см. UpdateMotorLag: при Motor1-4 fail → liftTarget × 0.75).
            //
            // Формула баланса сил в MC:
            //   Thrust = liftThrustActual × MaxThrust (обычно 2×вес для маневрирования)
            //   Weight = mass × g
            //   Net vertical accel = Thrust/mass - g = liftThrust × 2g - g = (2×lift - 1) × g
            //
            // При lift=1.0 (норма): net_up = +g (может лететь вверх с 1g)
            // При lift=0.75 (один мотор fail): net_up = (2×0.75-1)×g = 0.5g (слабее)
            // При lift=0.5 (два мотора fail): net_up = 0 (может только висеть)
            // При lift=0.25: net_up = -0.5g (падает)
            //
            // Поэтому максимальное подъёмное (отрицательное NED) ускорение ограничено
            // доступной лифт-тягой. Гравитация работает независимо от моторов.
            //
            // Применяем ТОЛЬКО в MC-режимах (где лифт = основной двигатель высоты).
            // В FW лифт даёт крылья, не моторы.
            if (_cmd.Mode == ControlMode.Multirotor
                || _cmd.Mode == ControlMode.TransitionToMc
                || _cmd.Mode == ControlMode.Takeoff
                || _cmd.Mode == ControlMode.Landing)
            {
                // Максимально доступное подъёмное ускорение исходя из реальной тяги.
                // Используем 2.5 как запас тяги (thrust-to-weight ratio для VTOL ≈ 2.0-2.5).
                const double MAX_LIFT_G = 2.5;
                double availableUpAccel = (_liftThrustActual * MAX_LIFT_G - 1.0) * Atmosphere.G;

                // aD (NED): отрицательный = вверх. Гравитация (+g) всегда добавляется.
                // Если aD <0 (хотим вверх) — проверяем что моторы это могут.
                // Минимальное aD = -availableUpAccel (максимум подъёма).
                // Если availableUpAccel < 0 (тяги меньше веса) — ВС падает.
                if (aD < -availableUpAccel)
                    aD = -availableUpAccel;  // не может подняться быстрее чем позволяют моторы
            }

            // =========================================================================
            // 3. Горизонтальная динамика — РАЗНАЯ для MC и FW (Расчёт 6, Правка #2-#3)
            //
            // MC (multicopter): тяга в произвольном направлении. P-каскад по NED-вектору
            //   к target lat/lon. Clamp общего magnitude ускорения. Drag по NED.
            //
            // FW (fixed-wing): тяга ВДОЛЬ heading (pusher толкает вперёд), поворот —
            //   через bank (§4). Scalar speed controller: target airspeed → форвард-
            //   ускорение. Yaw rate из bank-angle двигает heading (секция 5),
            //   вектор velocity следует за heading (Правка velocity→heading в секции 4).
            //
            // Drag по NED air-velocity применяется в обоих режимах (§1).
            //
            // Без FW/MC разделения P-каскад по вектору пытался резко развернуть velocity
            // в повороте, и весь MaxAccelFwMs2 уходил на торможение вместо разгона.
            // =========================================================================

            double vAirN = vN - windN;
            double vAirE = vE - windE;
            double dragPerMass = _cfg.DragCoef / _cfg.MassKg;
            double aN, aE;

            if (IsFwMode(_cmd.Mode))
            {
                // --- FW: thrust along heading, scalar airspeed controller ---
                double cosYaw = Math.Cos(_yawRad);
                double sinYaw = Math.Sin(_yawRad);

                // Проекция air velocity на heading (signed forward speed).
                double vAlongHeading = vAirN * cosYaw + vAirE * sinYaw;

                // Target airspeed: команда (CHANGE_SPEED) или default.
                double targetAirspeed = _cmd.TargetSpeedMs > 0.01
                    ? _cmd.TargetSpeedMs
                    : _cfg.HorizFwSpeedMs;

                // Scalar P-каскад по airspeed (не по вектору!).
                double aThrust = _cfg.K_Vel * (targetAirspeed - vAlongHeading);
                aThrust = Math.Clamp(aThrust, -_cfg.MaxAccelFwMs2, _cfg.MaxAccelFwMs2);

                // Pusher failure: если pusher мотор отказал (UpdateMotorLag уже
                // загнал _pusherThrustActual к 0), тяга вперёд недоступна.
                // ВС может только тормозить (drag + отрицательные ускорения от целей),
                // но не разгоняться. Это приведёт к падению AirSpeed → TransitionCorridor
                // monitor детектирует → эмитит Transition FAIL → physics спасает ВС
                // переводом в MC.
                if (aThrust > 0 && _pusherThrustActual < 0.01)
                {
                    aThrust = 0;  // pusher не тянет — ускорение вперёд невозможно
                }

                // Тяга проецируется на NED вдоль heading.
                double aThrustN = aThrust * cosYaw;
                double aThrustE = aThrust * sinYaw;

                // Drag по NED air-velocity — физика среды.
                aN = aThrustN - dragPerMass * vAirN;
                aE = aThrustE - dragPerMass * vAirE;
            }
            else
            {
                // --- MC: vector velocity controller к lat/lon target ---
                //
                // TargetSpeedMs семантика:
                //   > 0.01     → лети с этой скоростью к точке
                //   == 0.0     → ОСТАНОВИТЬСЯ (для VTOL_LAND) — target velocity = 0
                //   NaN или <0 → default HorizMcSpeedMs (10 м/с)
                //
                // Без различия 0 vs NaN: при посадке ВС думал что нужно лететь 10 м/с
                // к HOME, проскакивал точку, болтался — не мог зависнуть.
                bool targetSpeedExplicit = !double.IsNaN(_cmd.TargetSpeedMs) && _cmd.TargetSpeedMs >= 0;
                double targetHorizSpeed;
                if (targetSpeedExplicit)
                {
                    targetHorizSpeed = _cmd.TargetSpeedMs;  // включая 0
                }
                else
                {
                    targetHorizSpeed = _cfg.HorizMcSpeedMs;  // default
                }

                double targetVn = 0.0, targetVe = 0.0;
                if (_cmd.HasPositionTarget &&
                    !double.IsNaN(_cmd.TargetLat) && !double.IsNaN(_cmd.TargetLon)
                    && targetHorizSpeed > 0.01)  // если speed=0, target velocity тоже 0
                {
                    double dLatDeg = _cmd.TargetLat - curLat;
                    double dLonDeg = _cmd.TargetLon - curLon;
                    double cosCurLat = Math.Cos(curLat * Math.PI / 180.0);
                    double dN = dLatDeg * (Math.PI / 180.0) * EARTH_R;
                    double dE = dLonDeg * (Math.PI / 180.0) * EARTH_R * cosCurLat;
                    double horizDist = Math.Sqrt(dN * dN + dE * dE);
                    if (horizDist > 1e-3)
                    {
                        targetVn = (dN / horizDist) * targetHorizSpeed;
                        targetVe = (dE / horizDist) * targetHorizSpeed;
                    }
                }

                // P-каскад по вектору. При targetVn/Ve=0 P-controller гасит текущую velocity.
                double aCmdN = _cfg.K_Vel * (targetVn - vN);
                double aCmdE = _cfg.K_Vel * (targetVe - vE);

                // Clamp командного ускорения по magnitude.
                double aCmdMag = Math.Sqrt(aCmdN * aCmdN + aCmdE * aCmdE);
                if (aCmdMag > _cfg.MaxAccelMcMs2)
                {
                    double scale = _cfg.MaxAccelMcMs2 / aCmdMag;
                    aCmdN *= scale;
                    aCmdE *= scale;
                }

                // Drag по NED air-velocity.
                aN = aCmdN - dragPerMass * vAirN;
                aE = aCmdE - dragPerMass * vAirE;
            }

            // =========================================================================
            // 4. Velocity integration (Euler, §13)
            //
            // Для FW: после интегрирования ПРИНУДИТЕЛЬНО выравниваем вектор velocity
            // по heading (yaw). В реальном самолёте velocity всегда ≈ направление носа
            // (крыло работает, боковое скольжение минимально).
            //
            // Без этого: при быстром yaw-rate velocity отстаёт, vAlongHeading падает,
            // scalar controller добавляет тяги вдоль heading — но velocity идёт
            // в другую сторону, drag тормозит весь вектор. ВС замедляется в повороте.
            //
            // MC оставляем как есть — мультикоптер реально может лететь боком.
            // =========================================================================
            double newVn = vN + aN * dt;
            double newVe = vE + aE * dt;
            double newVd = vD + aD * dt;

            if (IsFwMode(_cmd.Mode))
            {
                // Сохраняем magnitude горизонтальной скорости, перепроецируем на heading.
                double horizMag = Math.Sqrt(newVn * newVn + newVe * newVe);
                if (horizMag > 0.5)
                {
                    newVn = horizMag * Math.Cos(_yawRad);
                    newVe = horizMag * Math.Sin(_yawRad);
                }
            }

            // =========================================================================
            // 5. Attitude targets + Bank-to-turn в FW (§4)
            // =========================================================================
            double horizSpeed = Math.Sqrt(newVn * newVn + newVe * newVe);
            double targetBankRad = 0.0;
            double targetPitchRad = 0.0;
            double yawRatePerSec = 0.0;

            if (IsFwMode(_cmd.Mode) && horizSpeed > 1.0)
            {
                // FW — поворот через крен. Target heading из target lat/lon.
                double targetYawRad = _yawRad;  // default: держим текущий курс
                if (_cmd.HasPositionTarget &&
                    !double.IsNaN(_cmd.TargetLat) && !double.IsNaN(_cmd.TargetLon))
                {
                    double dLat = _cmd.TargetLat - curLat;
                    double dLon = _cmd.TargetLon - curLon;
                    double cosCurLatY = Math.Cos(curLat * Math.PI / 180.0);
                    double dNy = dLat * (Math.PI / 180.0) * EARTH_R;
                    double dEy = dLon * (Math.PI / 180.0) * EARTH_R * cosCurLatY;
                    if (dNy * dNy + dEy * dEy > 1e-6)
                    {
                        targetYawRad = Math.Atan2(dEy, dNy);
                    }
                }
                double yawErr = WrapAngleRad(targetYawRad - _yawRad);

                // Bank target с atan-насыщением (Расчёт 6, Правка #3).
                // При yawErr=10° → bank=11°, при 30°=23°, при 60°=30°, ≥90°→max.
                // Без рывков, в orbit (yawErr ≈ 30° постоянно) даёт стабильный умеренный крен.
                double yawErrDeg = yawErr * 180.0 / Math.PI;
                double bankCmdDeg = _cfg.MaxBankDeg * (2.0 / Math.PI) * Math.Atan(yawErrDeg / 25.0);
                targetBankRad = bankCmdDeg * Math.PI / 180.0;

                // Bank-to-turn (§4): ω = g·tan(φ)/V с floor по скорости (Правка #4).
                // При V<10 формула даёт нереалистично большой yaw rate (хаос на низких V).
                // Min 10 м/с обеспечивает устойчивость каскада yaw → bank → attitude.
                double vEff = Math.Max(horizSpeed, 10.0);
                yawRatePerSec = Atmosphere.G * Math.Tan(_bankRad) / vEff;

                // Pitch = flight path angle = atan2(-vd, horizontal)
                targetPitchRad = Math.Atan2(-newVd, Math.Max(horizSpeed, 1.0));
                targetPitchRad = Math.Clamp(targetPitchRad,
                                             -DegToRad(_cfg.MaxPitchDeg),
                                             +DegToRad(_cfg.MaxPitchDeg));
            }
            else
            {
                // MC: bank не участвует в повороте, yaw напрямую
                targetBankRad = 0.0;
                targetPitchRad = 0.0;

                double maxYawRate = DegToRad(_cfg.YawRateMcMaxDegPerSec);

                if (!double.IsNaN(_cmd.TargetYawDeg))
                {
                    // Явное yaw command
                    double targetYawMc = _cmd.TargetYawDeg * Math.PI / 180.0;
                    double yawErrMc = WrapAngleRad(targetYawMc - _yawRad);
                    yawRatePerSec = Math.Clamp(yawErrMc * 2.0, -maxYawRate, +maxYawRate);
                }
                else if (horizSpeed > 0.5)
                {
                    // Автоматически смотрим по направлению движения
                    double targetYawMc = Math.Atan2(newVe, newVn);
                    double yawErrMc = WrapAngleRad(targetYawMc - _yawRad);
                    yawRatePerSec = Math.Clamp(yawErrMc * 2.0, -maxYawRate, +maxYawRate);
                }
            }

            // =========================================================================
            // 6. Attitude lag (§11)
            // =========================================================================
            double attTau = Math.Max(0.01, _cfg.AttitudeTauSec);
            double attAlpha = 1.0 - Math.Exp(-dt / attTau);

            // Bank rate limit (§11)
            double maxBankRateRad = DegToRad(_cfg.MaxBankRateDegPerSec);
            double bankDelta = (targetBankRad - _bankRad) * attAlpha;
            bankDelta = Math.Clamp(bankDelta, -maxBankRateRad * dt, +maxBankRateRad * dt);
            _bankRad += bankDelta;

            _pitchRad += (targetPitchRad - _pitchRad) * attAlpha;
            _yawRad = WrapAngleRad(_yawRad + yawRatePerSec * dt);

            // =========================================================================
            // 7. Position integration в геодезические (§14, плоская аппроксимация)
            // =========================================================================
            double deltaN = newVn * dt;
            double deltaE = newVe * dt;

            double newLat = curLat + (deltaN / EARTH_R) * (180.0 / Math.PI);
            double cosLatForLon = Math.Cos(curLat * Math.PI / 180.0);
            if (Math.Abs(cosLatForLon) < 1e-9) cosLatForLon = 1e-9;  // защита у полюсов
            double newLon = curLon + (deltaE / (EARTH_R * cosLatForLon)) * (180.0 / Math.PI);

            double newAltRel = altRel - newVd * dt;   // NED: vd<0 = alt растёт
            double newAltAmsl = altAmsl - newVd * dt;

            // =========================================================================
            // 8. Ground clamp — не падаем ниже 0, trim horizontal (ground friction)
            // =========================================================================
            if (newAltRel < 0.0)
            {
                double groundDelta = 0.0 - newAltRel;
                newAltRel = 0.0;
                newAltAmsl += groundDelta;
                newVd = 0.0;
                newVn *= 0.5;
                newVe *= 0.5;
            }

            // =========================================================================
            // 9. HUD throttle (до Write-lock чтобы не держать lock на вычислениях)
            // =========================================================================
            double throttlePct;
            if (IsFwMode(_cmd.Mode))
            {
                throttlePct = _pusherThrustActual;
            }
            else
            {
                // В MC — baseline HoverThrottle + коррекция от vertical accel
                // aD отрицательный = подъём = throttle выше hover
                throttlePct = _cfg.HoverThrottle - aD * 0.05;
                throttlePct = Math.Clamp(throttlePct, 0.0, 1.0);
            }

            double airSpeed = Math.Sqrt(vAirN * vAirN + vAirE * vAirE);

            // Атмосферный buffet — малые колебания attitude для реалистичности.
            // НЕ влияет на физику (ранее использованный _bankRad/_pitchRad сохраняется как
            // база для следующего тика). Применяется только к значениям записываемым в state
            // — т.е. к тому что видит GCS на AttitudeIndicator.
            _buffetTimeSec += dt;
            var (rollBuffet, pitchBuffet) = ComputeBuffet(airSpeed, _cmd.Mode, _bankRad);

            // =========================================================================
            // 10. Запись в SimState под Write-lock (обязательно по контракту SimState)
            // =========================================================================
            using (state.Write())
            {
                state.Position.Lat = newLat;
                state.Position.Lon = newLon;
                state.Position.AltAmsl = newAltAmsl;
                state.Position.AltRelative = newAltRel;

                state.Velocity.Vn = newVn;
                state.Velocity.Ve = newVe;
                state.Velocity.Vd = newVd;
                state.Velocity.AirSpeed = airSpeed;
                // GroundSpeed — readonly computed, не присваиваем
                state.Velocity.ThrottlePercent = (ushort)(throttlePct * 100);

                state.Attitude.Roll = _bankRad + rollBuffet;
                state.Attitude.Pitch = _pitchRad + pitchBuffet;
                state.Attitude.Yaw = _yawRad;

                // PWM моторов для SERVO_OUTPUT_RAW → GCS телеметрия.
                //
                // VTOL раскладка:
                //   Каналы 1-4 (индекс 0-3): lift-моторы (мультиротор).
                //     PWM = 1000 + lift_thrust_actual × throttle × 1000
                //   Канал 5 (индекс 4):      pusher-мотор.
                //     PWM = 1000 + pusher_thrust_actual × 1000
                //   Каналы 6-8 (5-7):        свободные servo (1500 нейтрально).
                //
                // Формула в GCS: percent = (PWM - 1000) / 10  → 0..100%
                //
                // В hover/MC: lift=1.0, pusher=0 → каналы 1-4 показывают throttle (50% hover),
                //                                 канал 5 = 0%.
                // В FW cruise: lift=0, pusher=1 → каналы 1-4 = 0%, канал 5 = 100% (или меньше при cruise).
                // В transition: оба плавно меняются.
                double liftPct = _liftThrustActual * throttlePct;  // делим throttle между моторами
                double pusherPct = _pusherThrustActual;            // pusher работает на полную (или 0)

                ushort liftPwm = (ushort)Math.Round(1000 + Math.Clamp(liftPct, 0, 1) * 1000);
                ushort pusherPwm = (ushort)Math.Round(1000 + Math.Clamp(pusherPct, 0, 1) * 1000);

                state.ServoPwm[0] = liftPwm;   // Motor 1 (lift)
                state.ServoPwm[1] = liftPwm;   // Motor 2 (lift)
                state.ServoPwm[2] = liftPwm;   // Motor 3 (lift)
                state.ServoPwm[3] = liftPwm;   // Motor 4 (lift)
                state.ServoPwm[4] = pusherPwm; // Motor 5 (pusher)
                for (int i = 5; i < state.ServoPwm.Length; i++)
                    state.ServoPwm[i] = 1500;  // нейтраль для свободных каналов
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

        /// <summary>Режим полёта считается FW если pusher активен.</summary>
        private static bool IsFwMode(ControlMode mode)
        {
            return mode == ControlMode.FixedWing || mode == ControlMode.TransitionToFw;
        }

        /// <summary>
        /// Атмосферный buffet — малые колебания attitude от турбулентности.
        /// <para>
        /// Возвращает (rollOffsetRad, pitchOffsetRad) которые нужно добавить к
        /// базовому attitude ВС. НЕ влияет на физику движения — только на отображение.
        /// </para>
        /// <para>
        /// Модель: сумма 3 синусоид на разных частотах (упрощённая Dryden-модель).
        /// Амплитуда зависит от режима полёта и скорости:
        ///   Hover MC:     ±0.3° roll, ±0.2° pitch
        ///   FW cruise:    ±1.0° roll, ±0.7° pitch
        ///   FW поворот:   ±1.5° roll, ±1.0° pitch (турбулентно в повороте)
        ///   Transition:   ±2.0° roll, ±1.5° pitch (нестабильный режим)
        /// </para>
        /// </summary>
        private (double rollOffset, double pitchOffset) ComputeBuffet(double airSpeed, ControlMode mode, double bankRad)
        {
            // Амплитуды в градусах → радианы.
            double rollAmpDeg, pitchAmpDeg;

            switch (mode)
            {
                case ControlMode.FixedWing:
                    // Больше buffet при более высокой скорости (квадратично, как real airbuffet).
                    // Базово 1° при V=20, растёт до 1.5° при V=25+.
                    double speedFactor = Math.Clamp(airSpeed / 20.0, 0.5, 1.5);
                    rollAmpDeg = 1.0 * speedFactor;
                    pitchAmpDeg = 0.7 * speedFactor;

                    // В повороте (bank > 10°) турбулентность больше — асимметричный поток.
                    double bankDeg = Math.Abs(bankRad) * 180.0 / Math.PI;
                    if (bankDeg > 10)
                    {
                        double turnFactor = 1.0 + (bankDeg - 10) / 50.0;  // +50% при bank=35°
                        rollAmpDeg *= turnFactor;
                        pitchAmpDeg *= turnFactor;
                    }
                    break;

                case ControlMode.TransitionToFw:
                case ControlMode.TransitionToMc:
                    // Transition — самый нестабильный режим.
                    rollAmpDeg = 2.0;
                    pitchAmpDeg = 1.5;
                    break;

                case ControlMode.Landing:
                case ControlMode.Takeoff:
                    // Близко к земле — ground effect add-noise (визуально).
                    rollAmpDeg = 0.5;
                    pitchAmpDeg = 0.3;
                    break;

                default:  // Multirotor hover, Idle
                    // Multirotor очень стабилен — минимальные колебания.
                    rollAmpDeg = 0.3;
                    pitchAmpDeg = 0.2;
                    break;
            }

            // На земле (Idle) — нет buffet вообще, ВС стоит ровно.
            if (mode == ControlMode.Idle)
                return (0, 0);

            // Сумма 3-х синусоид с разными частотами (Hz): 0.3, 0.7, 1.4.
            // Низкие частоты = большое качание, высокие = мелкая дрожь.
            // Амплитуды 0.5, 0.3, 0.2 → в сумме = 1.0 (нормированная сумма).
            double t = _buffetTimeSec;
            const double f1 = 0.3, f2 = 0.7, f3 = 1.4;
            const double a1 = 0.5, a2 = 0.3, a3 = 0.2;

            double rollNorm =
                a1 * Math.Sin(2 * Math.PI * f1 * t + _buffetRollPhase1) +
                a2 * Math.Sin(2 * Math.PI * f2 * t + _buffetRollPhase2) +
                a3 * Math.Sin(2 * Math.PI * f3 * t + _buffetRollPhase3);

            double pitchNorm =
                a1 * Math.Sin(2 * Math.PI * f1 * t + _buffetPitchPhase1) +
                a2 * Math.Sin(2 * Math.PI * f2 * t + _buffetPitchPhase2) +
                a3 * Math.Sin(2 * Math.PI * f3 * t + _buffetPitchPhase3);

            double rollOffset = rollAmpDeg * rollNorm * Math.PI / 180.0;
            double pitchOffset = pitchAmpDeg * pitchNorm * Math.PI / 180.0;

            return (rollOffset, pitchOffset);
        }

        private static double DegToRad(double deg) => deg * Math.PI / 180.0;

        /// <summary>Нормализация угла в диапазон [-π, +π].</summary>
        private static double WrapAngleRad(double angleRad)
        {
            while (angleRad > Math.PI) angleRad -= 2.0 * Math.PI;
            while (angleRad < -Math.PI) angleRad += 2.0 * Math.PI;
            return angleRad;
        }
    }


}