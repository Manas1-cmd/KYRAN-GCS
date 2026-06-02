using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;  // для Application.Current.TryFindResource (локализация)
using SimpleDroneGCS.Simulator.Control;
using SimpleDroneGCS.Simulator.Core;
using SimpleDroneGCS.Simulator.Failures;
using SimpleDroneGCS.Simulator.Mavlink;
using SimpleDroneGCS.Simulator.Physics;

namespace SimpleDroneGCS.Simulator
{
    /// <summary>
    /// Оркестратор симулятора. Владеет всеми компонентами, связывает события,
    /// реализует общий Tick. UI работает только с этим классом.
    /// </summary>
    public sealed class SimulatedDrone : IDisposable
    {
        // =====================================================================
        // Default HOME (Алматы, АО «Стандарт Қапал Құрылыс»).
        // =====================================================================

        public const double DefaultHomeLat = 43.222;
        public const double DefaultHomeLon = 76.851;
        public const double DefaultHomeAltAmsl = 850.0;

        // =====================================================================
        // Компоненты
        // =====================================================================

        private readonly object _lifecycleLock = new();
        private readonly ConcurrentQueue<string> _logQueue = new();

        private readonly SimState _state;
        private readonly SimClock _clock;
        private readonly WindModel _wind;
        private readonly MavlinkOutbound _outbound;
        private readonly MavlinkInbound _inbound;
        private readonly MavlinkBridge _bridge;
        private readonly MissionExecutor _executor;
        private readonly FlightController _fc;
        private readonly FailureInjector _injector;

        private ISimVehicle _vehicle;
        private VehicleType _vehicleType;

        // Последняя команда от FlightController — для диагностики UI.
        // Хранит TargetSpeedMs текущего тика, чтобы HUD мог показать актуальную
        // целевую скорость (а не статический _cruiseSpeedMs из миссии).
        private double _lastTargetSpeedMs = double.NaN;
        private readonly object _lastCmdLock = new();

        // Auto-disarm после посадки.
        // MissionCompleted event (финиш LAND) взводит таймер в 2 сек (соответствует
        // ArduPilot LAND_DISARMDELAY=2 по умолчанию). Когда таймер истекает — вызываем
        // _fc.Disarm(force=false). На земле (AltRelative≈0) Disarm сработает сразу.
        // NaN = таймер неактивен (не запущен или уже сработал).
        private double _autoDisarmTimerSec = double.NaN;

        // Mission upload state (full list от MISSION_COUNT).
        private MissionItem[] _missionUploadBuffer;
        private int _missionUploadExpected;

        // Mission partial upload state (от MISSION_WRITE_PARTIAL_LIST).
        // При PARTIAL мы собираем items для диапазона [start..end] и после
        // полного сбора передаём в Executor.ReplaceRange.
        private MissionItem[] _missionPartialBuffer;
        private ushort _missionPartialStart;
        private ushort _missionPartialEnd;
        private int _missionPartialReceived;

        // Parameters (простой словарь).
        private readonly Dictionary<string, float> _params = new();
        private readonly List<string> _paramOrder = new();

        private bool _running;

        // =====================================================================
        // Events для UI
        // =====================================================================

        /// <summary>Новый снимок состояния (вызывается после каждого тика).</summary>
        public event EventHandler<SimState> StateChanged;

        /// <summary>Строка в лог UI.</summary>
        public event EventHandler<string> LogMessage;

        /// <summary>GCS подключилась/переподключилась.</summary>
        public event EventHandler GcsConnected;

        /// <summary>GCS отключилась (таймаут).</summary>
        public event EventHandler GcsDisconnected;

        // =====================================================================
        // Публичные свойства
        // =====================================================================

        public bool IsRunning { get { lock (_lifecycleLock) return _running; } }
        public bool IsGcsConnected => _bridge.IsGcsConnected;
        public VehicleType VehicleType => _vehicleType;
        public SimState State => _state;
        public WindModel Wind => _wind;

        // ---- Диагностика ----

        /// <summary>Текущий <see cref="ControlMode"/> в физике.</summary>
        public ControlMode CurrentControlMode => _vehicle?.CurrentMode ?? ControlMode.Idle;

        /// <summary>Engagement лифт-моторов (0..1).</summary>
        public double LiftEngagement => _vehicle?.LiftEngagement ?? 1.0;

        /// <summary>Engagement pusher-мотора (0..1).</summary>
        public double PusherEngagement => _vehicle?.PusherEngagement ?? 0.0;

        /// <summary>Текущая команда миссии (null если нет).</summary>
        public MissionCommand? CurrentMissionCommand => _executor.CurrentCommand;

        /// <summary>Состояние executor'а.</summary>
        public MissionExecState MissionState => _executor.State;

        /// <summary>Текущий seq миссии.</summary>
        public ushort CurrentMissionSeq => _executor.CurrentSeq;

        /// <summary>Режим VTOL (MC/FW — что сейчас основной).</summary>
        public ControlMode VtolRegime => _executor.CurrentRegime;

        /// <summary>Крейсерская скорость от DO_CHANGE_SPEED.</summary>
        public double CruiseSpeedMs => _executor.CurrentCruiseSpeedMs;

        /// <summary>
        /// Фактическая целевая скорость текущего тика (TargetSpeedMs из ControlCommand).
        /// Меняется в зависимости от фазы полёта:
        ///   FW cruise: cruise speed (18 м/с)
        ///   TRANS_MC:  плавно падает 18 → 8 м/с
        ///   MC navigation к LAND: 8 м/с
        ///   На точке LAND: 0 м/с (висит)
        /// NaN если не задана (Idle, hover без явной команды).
        /// </summary>
        public double ActiveTargetSpeedMs
        {
            get { lock (_lastCmdLock) return _lastTargetSpeedMs; }
        }

        /// <summary>Масштаб времени (пауза = 0, x1, x2, x5).</summary>
        public double TimeScale
        {
            get => _clock.TimeScale;
            set => _clock.TimeScale = value;
        }

        // =====================================================================
        // Constructor
        // =====================================================================

        public SimulatedDrone(VehicleType initialVehicle = VehicleType.Vtol,
            int listenPort = 14551)
        {
            _vehicleType = initialVehicle;
            _wind = new WindModel();

            _state = SimState.CreateDefault(initialVehicle,
                DefaultHomeLat, DefaultHomeLon, DefaultHomeAltAmsl);

            _vehicle = CreateVehicle(initialVehicle);
            _vehicle.Reset(_state);

            _clock = new SimClock();
            _outbound = new MavlinkOutbound(sysId: 1, compId: 1);
            _inbound = new MavlinkInbound();
            _bridge = new MavlinkBridge(listenPort, _outbound, _inbound);
            _executor = new MissionExecutor();
            _fc = new FlightController(_state, _executor);
            _injector = new FailureInjector();

            InitDefaultParams(initialVehicle);
            WireEvents();
        }

        private ISimVehicle CreateVehicle(VehicleType type) => type switch
        {
            VehicleType.Vtol => new VtolDynamics(_wind),
            VehicleType.Copter => new CopterDynamics(_wind),
            _ => throw new ArgumentOutOfRangeException(nameof(type)),
        };

        // =====================================================================
        // Lifecycle
        // =====================================================================

        /// <summary>Запустить симулятор.</summary>
        public void Start()
        {
            lock (_lifecycleLock)
            {
                if (_running) return;

                _vehicle.Reset(_state);
                _bridge.Start();
                _autoDisarmTimerSec = double.NaN;  // сброс таймера при старте новой сессии
                _clock.Start(OnSimTick);
                _running = true;
            }
            Log(Loc("SimLog_Started", "Simulator started"));
        }

        /// <summary>Остановить.</summary>
        public void Stop()
        {
            lock (_lifecycleLock)
            {
                if (!_running) return;
                _running = false;
            }

            try { _clock.Stop(); } catch { }
            try { _bridge.Stop(); } catch { }

            Log(Loc("SimLog_Stopped", "Simulator stopped"));
        }

        public void Pause(bool pause) => TimeScale = pause ? 0.0 : 1.0;

        public void Dispose()
        {
            Stop();
            try { _clock.Dispose(); } catch { }
            try { _bridge.Dispose(); } catch { }
        }

        // =====================================================================
        // Public API для UI
        // =====================================================================

        /// <summary>Сменить тип ВС. Пересоздаёт Vehicle и сбрасывает state.</summary>
        public void SetVehicleType(VehicleType type)
        {
            if (_vehicleType == type) return;

            bool wasRunning = IsRunning;
            if (wasRunning) Stop();

            _vehicleType = type;
            _vehicle = CreateVehicle(type);
            _state.ResetToDefaults(type,
                DefaultHomeLat, DefaultHomeLon, DefaultHomeAltAmsl);
            _vehicle.Reset(_state);
            _executor.Clear();
            InitDefaultParams(type);

            Log(string.Format(Loc("SimLog_VehicleChanged", "Vehicle changed to {0}"), type));
            if (wasRunning) Start();
        }

        /// <summary>Сместить HOME на указанные координаты.</summary>
        public void SetHome(double lat, double lon, double altAmsl)
        {
            using (_state.Write())
            {
                _state.Home.Lat = lat;
                _state.Home.Lon = lon;
                _state.Home.AltAmsl = altAmsl;
                _state.Home.Set = true;
                _state.Position.Lat = lat;
                _state.Position.Lon = lon;
                _state.Position.AltAmsl = altAmsl;
                _state.Position.AltRelative = 0;
            }
            _vehicle.Reset(_state);
            Log(string.Format(Loc("SimLog_HomeAuto", "HOME: {0}, {1}, {2} m"), lat.ToString("F6"), lon.ToString("F6"), altAmsl.ToString("F0")));
        }

        // ---- Failures (прокидываем в injector) ----

        public void InjectGpsLoss() => _injector.InjectGpsLoss(_state);
        public void ClearGpsLoss() => _injector.ClearGpsLoss(_state);
        public void InjectRcFailsafe() => _injector.InjectRcFailsafe(_state);
        public void ClearRcFailsafe() => _injector.ClearRcFailsafe(_state);
        public void InjectBatteryLow() => _injector.InjectBatteryLow(_state);
        public void InjectBatteryCritical() => _injector.InjectBatteryCritical(_state);
        public void ClearBatteryFailure() => _injector.ClearBatteryFailure(_state);
        public void InjectMotorFailure(int idx) => _injector.InjectMotorFailure(_state, idx);
        public void ClearMotorFailure() => _injector.ClearMotorFailure(_state);
        public void InjectCompassError() => _injector.InjectCompassError(_state);
        public void ClearCompassError() => _injector.ClearCompassError(_state);
        public void InjectEkfDivergence() => _injector.InjectEkfDivergence(_state);
        public void ClearEkfDivergence() => _injector.ClearEkfDivergence(_state);
        public void ClearAllFailures() => _injector.ClearAll(_state);

        /// <summary>Задать ветер.</summary>
        public void SetWind(double directionDeg, double speedMs, double verticalMs = 0.0)
        {
            _wind.Set(directionDeg, speedMs, verticalMs);
            Log(string.Format(Loc("SimLog_WindSet", "Wind: {0}° {1} m/s"), directionDeg.ToString("F0"), speedMs.ToString("F1")));
        }

        // =====================================================================
        // Event wiring
        // =====================================================================

        private void WireEvents()
        {
            // ---- Bridge ----
            _bridge.GcsConnected += (s, ep) =>
            {
                Log(string.Format(Loc("SimLog_GcsConn", "GCS connected: {0}"), ep));
                GcsConnected?.Invoke(this, EventArgs.Empty);
            };
            _bridge.GcsDisconnected += (s, e) =>
            {
                Log(Loc("SimLog_GcsDisc", "GCS disconnected (timeout)"));
                GcsDisconnected?.Invoke(this, EventArgs.Empty);
            };

            // ---- Inbound: commands ----
            _inbound.ArmCommand += OnArmCommand;
            _inbound.SetModeCommand += OnSetModeCommand;
            _inbound.RtlCommand += OnRtlCommand;
            _inbound.LandCommand += OnLandCommand;
            _inbound.RebootCommand += OnRebootCommand;
            _inbound.MotorTestCommand += OnMotorTestCommand;
            _inbound.SetServoCommand += OnSetServoCommand;
            _inbound.CalibrationCommand += OnCalibrationCommand;
            _inbound.AutopilotCapabilitiesRequested += OnAutopilotCapsRequested;
            _inbound.VtolTransitionCommand += OnVtolTransitionCommand;
            _inbound.ChangeSpeedCommand += OnChangeSpeedCommand;
            _inbound.SetHomeCommand += OnSetHomeCommand;
            _inbound.MissionStartCommand += OnMissionStartCommand;
            _inbound.UnhandledCommand += OnUnhandledCommand;

            // ---- Inbound: params ----
            _inbound.ParamRequestList += OnParamRequestList;
            _inbound.ParamRequestRead += OnParamRequestRead;
            _inbound.ParamSet += OnParamSet;

            // ---- Inbound: missions ----
            _inbound.MissionRequestList += OnMissionRequestList;
            _inbound.MissionRequestInt += OnMissionRequestInt;
            _inbound.MissionCountStart += OnMissionCountStart;
            _inbound.MissionItemInt += OnMissionItemInt;
            _inbound.MissionAckReceived += OnMissionAckFromGcs;
            _inbound.MissionClearAll += OnMissionClearAll;
            _inbound.MissionSetCurrent += OnMissionSetCurrent;
            _inbound.MissionWritePartial += OnMissionWritePartial;

            // ---- Inbound: guided ----
            _inbound.GuidedTargetInt += OnGuidedTarget;

            // ---- FlightController ----
            _fc.ModeChanged += (s, mode) =>
            {
                var snap = _state.Snapshot();
                _bridge.SendImmediate(_outbound.BuildHeartbeat(snap));
            };
            _fc.ArmStateChanged += (s, armed) =>
            {
                var snap = _state.Snapshot();
                _bridge.SendImmediate(_outbound.BuildHeartbeat(snap));
            };
            _fc.StatusText += (s, text) =>
            {
                _bridge.SendImmediate(_outbound.BuildStatusText(text, severity: 6));
                Log(text);
            };

            // ---- Mission executor ----
            _executor.MissionItemReached += (s, seq) =>
            {
                _bridge.SendImmediate(_outbound.BuildMissionItemReached(seq));
                Log(string.Format(Loc("SimLog_WpReachedShort", "WP reached: {0}"), seq));
            };
            _executor.CurrentItemChanged += (s, seq) =>
            {
                _bridge.SendImmediate(_outbound.BuildMissionCurrent(seq));
            };
            _executor.MissionCompleted += (s, e) =>
            {
                _bridge.SendImmediate(_outbound.BuildStatusText("Mission complete", severity: 6));
                Log(Loc("SimLog_MissionComplete", "Mission complete"));

                // Auto-disarm через 2 сек (ArduPilot LAND_DISARMDELAY).
                // Работает только если миссия завершилась посадкой (ВС на земле).
                // Проверка AltRelative в tick-обработчике — если ВС в воздухе,
                // таймер всё равно ничего не сделает (Disarm откажется).
                _autoDisarmTimerSec = 2.0;
            };

            _executor.DiagnosticLog += (s, msg) => Log(Loc("Sim_LogPrefixMission", "[MSN]") + " " + msg);

            // ---- Failure injector ----
            _injector.RequestAutoRtl += (s, e) => _fc.TriggerRtl();
            _injector.RequestAutoLand += (s, e) => _fc.TriggerLand();
            _injector.StatusText += (s, text) =>
            {
                _bridge.SendImmediate(_outbound.BuildStatusText(text, severity: 4));
                Log(text);
            };
        }

        // =====================================================================
        // Sim tick
        // =====================================================================

        private void OnSimTick(SimTickArgs args)
        {
            try
            {
                double dt = args.Dt;

                var cmd = _fc.Update(dt);

                // Сохраняем TargetSpeedMs для диагностики в HUD.
                lock (_lastCmdLock) _lastTargetSpeedMs = cmd.TargetSpeedMs;

                _vehicle.ApplyControl(cmd);
                _vehicle.Step(dt, _state);
                _injector.Tick(dt, _state);
                _bridge.Tick(_state);

                // Auto-disarm таймер: если MissionCompleted взвёл таймер,
                // отсчитываем его и по истечении → _fc.Disarm. Только один раз.
                if (!double.IsNaN(_autoDisarmTimerSec))
                {
                    _autoDisarmTimerSec -= dt;
                    if (_autoDisarmTimerSec <= 0)
                    {
                        _autoDisarmTimerSec = double.NaN;
                        // ВС на земле — Disarm сработает штатно.
                        // Если по какой-то причине ВС в воздухе — Disarm откажется
                        // (сам FlightController проверяет AltRelative>0.5).
                        _fc.Disarm(force: false);
                    }
                }

                // Событие для UI — раз в несколько тиков достаточно (10 Hz).
                if ((args.TickIndex % 5) == 0)
                    StateChanged?.Invoke(this, _state.Snapshot());
            }
            catch (Exception ex)
            {
                Log(string.Format(Loc("SimLog_TickError", "Tick error: {0}"), ex.Message));
            }
        }

        // =====================================================================
        // Command handlers
        // =====================================================================

        private void OnArmCommand(object s, ArmCommandArgs a)
        {
            bool ok;
            if (a.Arm)
            {
                ok = _fc.TryArm(a.Force, out string _);
            }
            else
            {
                _fc.Disarm(a.Force);
                ok = true;
            }
            byte result = ok ? (byte)0 : (byte)4; // ACCEPTED / FAILED
            _bridge.SendImmediate(_outbound.BuildCommandAck(400, result));
        }

        private void OnSetModeCommand(object s, uint mode)
        {
            _fc.SetMode(mode);
            _bridge.SendImmediate(_outbound.BuildCommandAck(176, 0));
        }

        private void OnRtlCommand(object s, EventArgs e)
        {
            _fc.TriggerRtl();
            _bridge.SendImmediate(_outbound.BuildCommandAck(20, 0));
        }

        private void OnLandCommand(object s, EventArgs e)
        {
            _fc.TriggerLand();
            _bridge.SendImmediate(_outbound.BuildCommandAck(21, 0));
        }

        private void OnRebootCommand(object s, EventArgs e)
        {
            _bridge.SendImmediate(_outbound.BuildCommandAck(246, 0));
            _bridge.SendImmediate(_outbound.BuildStatusText("Rebooting..."));
            Log(Loc("SimLog_RebootReq", "Reboot requested"));

            Task.Run(async () =>
            {
                await Task.Delay(500);
                Stop();
                await Task.Delay(2000);
                Start();
                _bridge.SendImmediate(_outbound.BuildStatusText("Booted"));
            });
        }

        private void OnMotorTestCommand(object s, MotorTestArgs a)
        {
            // В MVP не меняем физику — только эмитим STATUSTEXT.
            Log(string.Format(Loc("SimLog_MotorTest", "MotorTest: M{0} throttle={1}% {2}s"), a.MotorIndex, a.Throttle.ToString("F0"), a.DurationSec.ToString("F1")));
            _bridge.SendImmediate(_outbound.BuildStatusText(
                $"Motor test M{a.MotorIndex}", severity: 6));
            _bridge.SendImmediate(_outbound.BuildCommandAck(209, 0));
        }

        private void OnSetServoCommand(object s, SetServoArgs a)
        {
            int idx = a.ServoIndex - 1;
            if (idx < 0 || idx >= 16)
            {
                _bridge.SendImmediate(_outbound.BuildCommandAck(183, 3));
                return;
            }

            ushort pwm = a.PwmUs;
            using (_state.Write())
            {
                _state.ServoOverride[idx] = pwm <= 1000 ? (ushort)0 : pwm;
            }

            Log(string.Format(Loc("SimLog_SetServo", "DO_SET_SERVO: SERVO{0} PWM={1}"), a.ServoIndex, pwm));
            _bridge.SendImmediate(_outbound.BuildStatusText(
                $"Servo {a.ServoIndex} PWM={pwm}", severity: 6));
            _bridge.SendImmediate(_outbound.BuildCommandAck(183, 0));
        }

        private void OnCalibrationCommand(object s, CalibrationArgs a)
        {
            _bridge.SendImmediate(_outbound.BuildCommandAck(241, 0)); // ACCEPTED
            _bridge.SendImmediate(_outbound.BuildStatusText(
                "Calibration started", severity: 6));
            Log(Loc("SimLog_CalibStart", "Calibration started"));

            // Имитируем задержку реального FC.
            Task.Run(async () =>
            {
                await Task.Delay(1500);
                _bridge.SendImmediate(_outbound.BuildStatusText(
                    "Calibration complete", severity: 6));
                Log(Loc("SimLog_CalibDone", "Calibration complete"));
            });
        }

        private void OnAutopilotCapsRequested(object s, EventArgs e)
        {
            _bridge.SendImmediate(_outbound.BuildAutopilotVersion());
            _bridge.SendImmediate(_outbound.BuildCommandAck(520, 0));
        }

        private void OnVtolTransitionCommand(object s, byte transitionState)
        {
            // 3 = MAV_VTOL_STATE_MC, 4 = MAV_VTOL_STATE_FW.
            bool wantFw = transitionState >= 4;
            _executor.RequestVtolTransition(wantFw);
            _bridge.SendImmediate(_outbound.BuildCommandAck(300, 0)); // ACCEPTED
            Log(wantFw ? "VTOL transition → FixedWing" : "VTOL transition → Multirotor");
        }

        private void OnChangeSpeedCommand(object s, float speedMs)
        {
            _executor.SetCruiseSpeed(speedMs);
            _bridge.SendImmediate(_outbound.BuildCommandAck(178, 0));
            Log(string.Format(Loc("SimLog_SpeedSet", "Speed set: {0} m/s"), speedMs.ToString("F1")));
        }

        private void OnSetHomeCommand(object s, SetHomeArgs a)
        {
            var snap = _state.Snapshot();
            double lat, lon, altAmsl;
            if (a.UseCurrent)
            {
                lat = snap.Position.Lat;
                lon = snap.Position.Lon;
                altAmsl = snap.Position.AltAmsl;
            }
            else
            {
                lat = a.Lat; lon = a.Lon; altAmsl = a.AltAmsl;
            }

            using (_state.Write())
            {
                _state.Home.Set = true;
                _state.Home.Lat = lat;
                _state.Home.Lon = lon;
                _state.Home.AltAmsl = altAmsl;
            }
            _bridge.SendImmediate(_outbound.BuildCommandAck(179, 0));
            _bridge.SendImmediate(_outbound.BuildHomePosition(_state.Snapshot()));
            Log(string.Format(Loc("SimLog_HomeSet", "HOME set: {0}, {1}, {2} m"), lat.ToString("F6"), lon.ToString("F6"), altAmsl.ToString("F0")));
        }

        private void OnMissionStartCommand(object s, EventArgs e)
        {
            if (_executor.ItemCount == 0)
            {
                _bridge.SendImmediate(_outbound.BuildCommandAck(300, 4)); // FAILED
                Log(Loc("SimLog_MissionStartNone", "MISSION_START: no mission"));
                return;
            }
            if (_executor.State == MissionExecState.Idle ||
                _executor.State == MissionExecState.Completed)
            {
                _executor.Start();
            }
            _bridge.SendImmediate(_outbound.BuildCommandAck(300, 0));
            Log(Loc("SimLog_MissionStarted", "Mission started (via MISSION_START)"));
        }

        private void OnUnhandledCommand(object s, ushort cmd)
        {
            _bridge.SendImmediate(_outbound.BuildCommandAck(cmd, 3)); // UNSUPPORTED
            Log(string.Format(Loc("SimLog_UnsupportedCmd", "Unsupported command: {0}"), cmd));
        }

        // =====================================================================
        // Param handlers
        // =====================================================================

        private void OnParamRequestList(object s, EventArgs e)
        {
            ushort total;
            List<string> ids;
            lock (_params)
            {
                total = (ushort)_paramOrder.Count;
                ids = new List<string>(_paramOrder);
            }
            for (int i = 0; i < ids.Count; i++)
            {
                string id = ids[i];
                float val;
                lock (_params) val = _params[id];
                _bridge.SendImmediate(_outbound.BuildParamValue(
                    id, val, type: 9 /* REAL32 */, total, (ushort)i));
            }
            Log(string.Format(Loc("SimLog_ParamListSent", "Param list sent: {0}"), total));
        }

        private void OnParamRequestRead(object s, string paramId)
        {
            float val;
            int index;
            int total;
            lock (_params)
            {
                total = _paramOrder.Count;
                index = _paramOrder.IndexOf(paramId);
                if (index < 0 || !_params.TryGetValue(paramId, out val)) return;
            }
            _bridge.SendImmediate(_outbound.BuildParamValue(
                paramId, val, 9, (ushort)total, (ushort)index));
        }

        private void OnParamSet(object s, ParamSetArgs a)
        {
            int index;
            int total;
            lock (_params)
            {
                if (!_params.ContainsKey(a.ParamId))
                {
                    _params[a.ParamId] = a.Value;
                    _paramOrder.Add(a.ParamId);
                }
                else
                {
                    _params[a.ParamId] = a.Value;
                }
                total = _paramOrder.Count;
                index = _paramOrder.IndexOf(a.ParamId);
            }
            _bridge.SendImmediate(_outbound.BuildParamValue(
                a.ParamId, a.Value, 9, (ushort)total, (ushort)index));
            Log(string.Format(Loc("SimLog_ParamSet", "Param set: {0} = {1}"), a.ParamId, a.Value));
        }

        // =====================================================================
        // Mission handlers
        // =====================================================================

        private void OnMissionRequestList(object s, EventArgs e)
        {
            var items = _executor.Download();
            _bridge.SendImmediate(_outbound.BuildMissionCount((ushort)items.Length));
        }

        private void OnMissionRequestInt(object s, ushort seq)
        {
            var items = _executor.Download();
            if (seq >= items.Length) return;
            _bridge.SendImmediate(_outbound.BuildMissionItemInt(items[seq]));
        }

        /// <summary>
        /// GCS шлёт MISSION_COUNT: запускает full upload.
        /// Правильный ArduPilot протокол: дрон отвечает MISSION_REQUEST_INT(0)
        /// — запрашивает первый item. GCS шлёт MISSION_ITEM_INT(0), дрон
        /// запрашивает (1) и т.д. до count. В конце — MISSION_ACK(accepted).
        /// </summary>
        private void OnMissionCountStart(object s, ushort count)
        {
            _missionPartialBuffer = null;  // cancel partial если был

            if (count == 0)
            {
                _missionUploadBuffer = null;
                _missionUploadExpected = 0;
                _bridge.SendImmediate(_outbound.BuildMissionAck(0));
                return;
            }

            _missionUploadBuffer = new MissionItem[count];
            _missionUploadExpected = 0;
            Log(string.Format(Loc("SimLog_MissionUploadBegin", "Mission upload: {0} items"), count));

            // Запрашиваем первый item у GCS.
            _bridge.SendImmediate(_outbound.BuildMissionRequestInt(0));
        }

        private void OnMissionItemInt(object s, MissionItem item)
        {
            Log(string.Format("[ITEM] seq={0} cmd={1} lat={2:F6} lon={3:F6} alt={4:F1}",
                item.Seq, item.Command, item.Lat, item.Lon, item.AltRelative));

            // Partial upload приоритет
            if (_missionPartialBuffer != null)
            {
                HandlePartialItem(item);
                return;
            }

            // Full upload в процессе
            if (_missionUploadBuffer != null)
            {
                if (item.Seq >= _missionUploadBuffer.Length) return;

                _missionUploadBuffer[item.Seq] = item;
                _missionUploadExpected = item.Seq + 1;

                if (_missionUploadExpected >= _missionUploadBuffer.Length)
                {
                    // Передаём текущую позицию дрона — используется executor'ом
                    // как prev point если добавили WP перед текущим.
                    _executor.Upload(_missionUploadBuffer,
                        _state.Position.Lat, _state.Position.Lon);
                    _bridge.SendImmediate(_outbound.BuildMissionAck(0)); // ACCEPTED
                    Log(string.Format(Loc("SimLog_MissionUploadedDone", "Mission uploaded: {0} items"), _missionUploadBuffer.Length));
                    _missionUploadBuffer = null;
                }
                else
                {
                    _bridge.SendImmediate(_outbound.BuildMissionRequestInt((ushort)_missionUploadExpected));
                }
                return;
            }

            // Одиночный ITEM без контекста — как реальный ArduPilot: игнорируем.
            Log(string.Format("[ITEM] seq={0} ignored (no upload context)", item.Seq));
        }

        /// <summary>
        /// MISSION_WRITE_PARTIAL_LIST (msg 38) — частичное обновление диапазона
        /// [start..end] без полной перезаливки. Это основной путь для drag в полёте.
        /// Эмулирует поведение реального ArduPilot.
        ///
        /// Протокол: получив PARTIAL, дрон запрашивает item(start), получает,
        /// запрашивает (start+1) и т.д. до end включительно. Затем MISSION_ACK.
        /// Executor.ReplaceRange сливает новые items со старыми без сброса _currentIndex.
        /// </summary>
        private void OnMissionWritePartial(object s, (ushort start, ushort end) range)
        {
            // Отменяем full upload если он был в процессе — partial его перебивает.
            _missionUploadBuffer = null;

            int count = range.end - range.start + 1;
            Log(string.Format("[PARTIAL] Request range [{0}..{1}] = {2} items",
                range.start, range.end, count));

            if (count <= 0 || count > 2048)
            {
                Log("[PARTIAL] Invalid range, rejecting");
                _bridge.SendImmediate(_outbound.BuildMissionAck(3)); // MAV_MISSION_INVALID
                return;
            }

            _missionPartialBuffer = new MissionItem[count];
            _missionPartialStart = range.start;
            _missionPartialEnd = range.end;
            _missionPartialReceived = 0;

            // Запрашиваем первый item в диапазоне
            _bridge.SendImmediate(_outbound.BuildMissionRequestInt(range.start));
        }

        private void HandlePartialItem(MissionItem item)
        {
            int bufIdx = item.Seq - _missionPartialStart;
            if (bufIdx < 0 || bufIdx >= _missionPartialBuffer.Length)
            {
                Log(string.Format("[PARTIAL] item seq={0} out of range [{1}..{2}]",
                    item.Seq, _missionPartialStart, _missionPartialEnd));
                return;
            }

            _missionPartialBuffer[bufIdx] = item;
            _missionPartialReceived++;

            ushort expectedNext = (ushort)(item.Seq + 1);
            if (_missionPartialReceived >= _missionPartialBuffer.Length)
            {
                _executor.ReplaceRange(_missionPartialStart, _missionPartialEnd,
                    _missionPartialBuffer,
                    _state.Position.Lat, _state.Position.Lon);
                _bridge.SendImmediate(_outbound.BuildMissionAck(0));
                Log(string.Format("[PARTIAL] ✓ Applied [{0}..{1}]",
                    _missionPartialStart, _missionPartialEnd));
                _missionPartialBuffer = null;
            }
            else
            {
                _bridge.SendImmediate(_outbound.BuildMissionRequestInt(expectedNext));
            }
        }

        private void OnMissionAckFromGcs(object s, EventArgs e)
        {
            // GCS подтвердила завершение download'а — нам ничего делать не надо.
        }

        private void OnMissionClearAll(object s, EventArgs e)
        {
            _executor.Clear();
            _bridge.SendImmediate(_outbound.BuildMissionAck(0));
            Log(Loc("SimLog_MissionClearedDone", "Mission cleared"));
        }

        private void OnMissionSetCurrent(object s, ushort seq)
        {
            _executor.SetCurrent(seq);
            Log(string.Format(Loc("SimLog_MissionSetCurrent", "Mission set current: {0}"), seq));
        }

        // =====================================================================
        // Guided
        // =====================================================================

        private void OnGuidedTarget(object s, GuidedTargetArgs a)
        {
            _fc.SetGuidedTarget(a.Lat, a.Lon, a.AltRelative);
            var vehicle = _state.Snapshot().Vehicle;
            uint guidedMode = vehicle == VehicleType.Vtol
                ? PlaneMode.Guided : CopterMode.Guided;
            _fc.SetMode(guidedMode);
        }

        // =====================================================================
        // Default params
        // =====================================================================

        private void InitDefaultParams(VehicleType vehicleType)
        {
            lock (_params)
            {
                _params.Clear();
                _paramOrder.Clear();

                void Add(string id, float v)
                {
                    _params[id] = v;
                    _paramOrder.Add(id);
                }

                // Servo functions — важно для GCS (motor mapping).
                if (vehicleType == VehicleType.Vtol)
                {
                    // Motors 1..4 = Motor1..Motor4 для QuadPlane лифт-моторов.
                    Add("SERVO1_FUNCTION", 33);
                    Add("SERVO2_FUNCTION", 34);
                    Add("SERVO3_FUNCTION", 35);
                    Add("SERVO4_FUNCTION", 36);
                    // SERVO5 = Throttle (pusher motor).
                    Add("SERVO5_FUNCTION", 70);
                    // Аэроповерхности.
                    Add("SERVO6_FUNCTION", 19); // Elevator
                    Add("SERVO7_FUNCTION", 4);  // Aileron
                    Add("SERVO8_FUNCTION", 4);  // Aileron
                    Add("SERVO9_FUNCTION", 21); // Rudder
                    Add("Q_ASSIST_SPEED", 13);
                    Add("Q_ENABLE", 1);
                    Add("FRAME_CLASS", 7); // QuadPlane
                    Add("AIRSPEED_CRUISE", 22);
                }
                else
                {
                    Add("SERVO1_FUNCTION", 33);
                    Add("SERVO2_FUNCTION", 34);
                    Add("SERVO3_FUNCTION", 35);
                    Add("SERVO4_FUNCTION", 36);
                    Add("FRAME_CLASS", 1); // Quad
                    Add("FRAME_TYPE", 1);  // X
                    Add("WPNAV_SPEED", 500);
                    Add("WPNAV_ACCEPT_RADIUS", 2);
                }

                // Общие.
                Add("ARMING_CHECK", 1);
                Add("BATT_CAPACITY",
                    vehicleType == VehicleType.Vtol ? 44000 : 5200);
                Add("FS_THR_ENABLE", 1);
                Add("FS_BATT_ENABLE", 2);
                Add("SYSID_THISMAV", 1);
                Add("SYSID_MYGCS", 255);
            }
        }

        // =====================================================================
        // Log
        // =====================================================================

        /// <summary>
        /// Получить локализованную строку из ресурсов приложения.
        /// Ресурсы загружены в App.xaml как MergedDictionary (Lang_ru-RU.xaml / Lang_kk-KZ.xaml).
        /// Если ключ не найден — возвращает fallback (для защиты от падения при отсутствии ресурса).
        /// Thread-safe: TryFindResource использует UI Dispatcher автоматически.
        /// </summary>
        private static string Loc(string key, string fallback = null)
        {
            try
            {
                var app = Application.Current;
                if (app != null)
                {
                    var res = app.TryFindResource(key);
                    if (res is string s) return s;
                }
            }
            catch { /* no-op */ }
            return fallback ?? key;
        }

        private void Log(string msg)
        {
            string stamped = $"[{DateTime.Now:HH:mm:ss}] {msg}";
            _logQueue.Enqueue(stamped);
            LogMessage?.Invoke(this, stamped);
        }

        /// <summary>Забрать накопленные сообщения (для UI при старте).</summary>
        public IReadOnlyList<string> DrainLog()
        {
            var list = new List<string>();
            while (_logQueue.TryDequeue(out var msg)) list.Add(msg);
            return list;
        }
    }
}