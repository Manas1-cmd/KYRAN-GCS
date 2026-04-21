using System;
using SimpleDroneGCS.Simulator.Core;

namespace SimpleDroneGCS.Simulator.Control
{
    // =========================================================================
    // MAVLink команды миссии (MAV_CMD)
    // =========================================================================

    /// <summary>
    /// MAVLink команды миссии. Коды совпадают с реальными MAV_CMD ArduPilot.
    /// </summary>
    public enum MissionCommand : ushort
    {
        Waypoint = 16,
        LoiterUnlim = 17,
        LoiterTurns = 18,
        LoiterTime = 19,
        ReturnToLaunch = 20,
        Land = 21,
        Takeoff = 22,
        VtolTakeoff = 84,
        VtolLand = 85,
        NavDelay = 93,
        DoChangeSpeed = 178,
        DoVtolTransition = 3000,
    }

    // =========================================================================
    // Элемент миссии
    // =========================================================================

    /// <summary>
    /// Один пункт миссии. Соответствует MAVLink MISSION_ITEM_INT.
    /// Mutable struct — MavlinkInbound строит поля по одному при приёме пакета.
    /// </summary>
    public struct MissionItem
    {
        public ushort Seq;
        public MissionCommand Command;
        public float Param1;
        public float Param2;
        public float Param3;
        public float Param4;
        /// <summary>Широта, градусы.</summary>
        public double Lat;
        /// <summary>Долгота, градусы.</summary>
        public double Lon;
        /// <summary>Высота над HOME, м (при frame = GLOBAL_RELATIVE_ALT).</summary>
        public double AltRelative;
        /// <summary>MAV_FRAME. 3 = GLOBAL_RELATIVE_ALT (стандарт ArduPilot).</summary>
        public byte Frame;
        /// <summary>Если false — остановиться на этом WP до внешней команды.</summary>
        public bool Autocontinue;
    }

    // =========================================================================
    // Состояние executor'а
    // =========================================================================

    public enum MissionExecState : byte
    {
        /// <summary>Миссия не запущена.</summary>
        Idle = 0,
        /// <summary>Идёт к текущему WP.</summary>
        Navigating = 1,
        /// <summary>Удерживает точку (LOITER_* или autocontinue=false).</summary>
        Loitering = 2,
        /// <summary>Ожидание (NAV_DELAY).</summary>
        Delaying = 3,
        /// <summary>Переход MC↔FW в процессе.</summary>
        Transitioning = 4,
        /// <summary>Миссия завершена (последний WP достигнут).</summary>
        Completed = 5,
    }

    // =========================================================================
    // Executor
    // =========================================================================

    /// <summary>
    /// Исполнитель миссии. На каждом тике возвращает <see cref="ControlCommand"/>
    /// для <see cref="ISimVehicle"/>. Обрабатывает NAV-команды ArduPilot,
    /// LOITER, DELAY, CHANGE_SPEED, VTOL-переходы, RTL.
    /// </summary>
    public sealed class MissionExecutor
    {
        private readonly object _lock = new();

        private MissionItem[] _items = Array.Empty<MissionItem>();
        private int _currentIndex;
        private MissionExecState _state = MissionExecState.Idle;

        private double _loiterElapsedSec;
        private double _cruiseSpeedMs;   // обновляется DO_CHANGE_SPEED
        private bool _rtlActive;

        // Для VTOL: текущий рабочий режим (меняется через TRANSITION_FW/MC).
        private ControlMode _currentRegime = ControlMode.Multirotor;

        // ---- События ----

        /// <summary>Достигнута точка (seq указывается в аргументе).</summary>
        public event EventHandler<ushort> MissionItemReached;

        /// <summary>Сменился текущий WP (как после SET_CURRENT или AdvanceTo).</summary>
        public event EventHandler<ushort> CurrentItemChanged;

        /// <summary>Миссия полностью пройдена.</summary>
        public event EventHandler MissionCompleted;

        // ---- Публичные свойства ----

        public int ItemCount { get { lock (_lock) return _items.Length; } }

        public MissionExecState State { get { lock (_lock) return _state; } }

        /// <summary>Seq текущего WP (0 если нет).</summary>
        public ushort CurrentSeq
        {
            get
            {
                lock (_lock)
                {
                    if (_items.Length == 0 || _currentIndex >= _items.Length) return 0;
                    return _items[_currentIndex].Seq;
                }
            }
        }

        public bool IsActive
        {
            get { lock (_lock) return _state != MissionExecState.Idle && _state != MissionExecState.Completed; }
        }

        public bool IsRtlActive { get { lock (_lock) return _rtlActive; } }

        // =====================================================================
        // Lifecycle
        // =====================================================================

        /// <summary>
        /// Загрузить миссию. Массив передаётся "как есть" (executor владеет им).
        /// Первый элемент обычно HOME (seq=0), он пропускается при Start.
        /// </summary>
        public void Upload(MissionItem[] items)
        {
            lock (_lock)
            {
                _items = items ?? Array.Empty<MissionItem>();
                _currentIndex = 0;
                _state = MissionExecState.Idle;
                _loiterElapsedSec = 0;
                _rtlActive = false;
            }
        }

        /// <summary>Очистить миссию.</summary>
        public void Clear()
        {
            lock (_lock)
            {
                _items = Array.Empty<MissionItem>();
                _currentIndex = 0;
                _state = MissionExecState.Idle;
                _loiterElapsedSec = 0;
                _rtlActive = false;
            }
        }

        /// <summary>Получить копию миссии (для MISSION_REQUEST_LIST download).</summary>
        public MissionItem[] Download()
        {
            lock (_lock)
            {
                var copy = new MissionItem[_items.Length];
                Array.Copy(_items, copy, _items.Length);
                return copy;
            }
        }

        /// <summary>Запустить прохождение миссии с первого пользовательского WP.</summary>
        public void Start()
        {
            ushort seqChanged = 0;
            bool fire = false;
            lock (_lock)
            {
                if (_items.Length == 0) return;

                // Пропускаем HOME (seq=0), если есть.
                _currentIndex = 0;
                for (int i = 0; i < _items.Length; i++)
                {
                    if (_items[i].Seq > 0) { _currentIndex = i; break; }
                }
                _state = MissionExecState.Navigating;
                _rtlActive = false;
                _loiterElapsedSec = 0;
                seqChanged = _items[_currentIndex].Seq;
                fire = true;
            }
            if (fire) CurrentItemChanged?.Invoke(this, seqChanged);
        }

        /// <summary>Остановить миссию (не очищает данные).</summary>
        public void Stop()
        {
            lock (_lock)
            {
                _state = MissionExecState.Idle;
                _rtlActive = false;
                _loiterElapsedSec = 0;
            }
        }

        /// <summary>
        /// Установить крейсерскую скорость извне (DO_CHANGE_SPEED через COMMAND_LONG).
        /// Влияет на все последующие WP до следующего DO_CHANGE_SPEED.
        /// </summary>
        public void SetCruiseSpeed(double speedMs)
        {
            lock (_lock)
            {
                if (speedMs > 0) _cruiseSpeedMs = speedMs;
            }
        }

        /// <summary>
        /// Запросить переход VTOL (DO_VTOL_TRANSITION через COMMAND_LONG).
        /// При AUTO-миссии переход может быть и через MissionItem, но GCS часто
        /// шлёт command напрямую.
        /// </summary>
        /// <param name="wantFw">true = перейти в FW, false = в MC.</param>
        public void RequestVtolTransition(bool wantFw)
        {
            lock (_lock)
            {
                _currentRegime = wantFw ? ControlMode.FixedWing : ControlMode.Multirotor;
            }
        }

        /// <summary>Перейти к WP с указанным seq (MISSION_SET_CURRENT).</summary>
        public void SetCurrent(ushort seq)
        {
            bool fire = false;
            lock (_lock)
            {
                for (int i = 0; i < _items.Length; i++)
                {
                    if (_items[i].Seq == seq)
                    {
                        _currentIndex = i;
                        _state = MissionExecState.Navigating;
                        _loiterElapsedSec = 0;
                        _rtlActive = false;
                        fire = true;
                        break;
                    }
                }
            }
            if (fire) CurrentItemChanged?.Invoke(this, seq);
        }

        /// <summary>Запустить RTL (отдельно от WP ReturnToLaunch).</summary>
        public void TriggerRtl()
        {
            lock (_lock)
            {
                _rtlActive = true;
                _state = MissionExecState.Navigating;
            }
        }

        // =====================================================================
        // Update — основной тик
        // =====================================================================

        /// <summary>
        /// Обработать тик миссии. Возвращает команду для <see cref="ISimVehicle"/>.
        /// </summary>
        public ControlCommand Update(double dt, SimState state)
        {
            if (state == null) return default;

            lock (_lock)
            {
                // Idle / нет миссии → держать позицию.
                if (_state == MissionExecState.Idle
                    || _state == MissionExecState.Completed
                    || _items.Length == 0)
                {
                    return BuildHoldCommand(state);
                }

                // RTL имеет приоритет над миссией.
                if (_rtlActive) return HandleRtl(dt, state);

                // Страховка индекса.
                if (_currentIndex < 0 || _currentIndex >= _items.Length)
                {
                    _state = MissionExecState.Completed;
                    MissionCompleted?.Invoke(this, EventArgs.Empty);
                    return BuildHoldCommand(state);
                }

                var item = _items[_currentIndex];
                UpdateMissionSeqInState(state, item.Seq);

                return item.Command switch
                {
                    MissionCommand.Waypoint => HandleWaypoint(dt, state, in item),
                    MissionCommand.Takeoff or MissionCommand.VtolTakeoff => HandleTakeoff(dt, state, in item),
                    MissionCommand.Land or MissionCommand.VtolLand => HandleLand(dt, state, in item),
                    MissionCommand.LoiterUnlim => HandleLoiterUnlim(dt, state, in item),
                    MissionCommand.LoiterTime => HandleLoiterTime(dt, state, in item),
                    MissionCommand.LoiterTurns => HandleLoiterTurns(dt, state, in item),
                    MissionCommand.NavDelay => HandleDelay(dt, state, in item),
                    MissionCommand.DoChangeSpeed => HandleChangeSpeed(state, in item),
                    MissionCommand.DoVtolTransition => HandleVtolTransition(dt, state, in item),
                    MissionCommand.ReturnToLaunch => HandleRtlItem(dt, state, in item),
                    _ => HandleSkip(in item), // неизвестные команды пропускаем
                };
            }
        }

        // =====================================================================
        // Handlers
        // =====================================================================

        private ControlCommand HandleWaypoint(double dt, SimState state, in MissionItem item)
        {
            _state = MissionExecState.Navigating;

            double dist = Navigator.DistanceM(
                state.Position.Lat, state.Position.Lon, item.Lat, item.Lon);
            double altErr = Math.Abs(state.Position.AltRelative - item.AltRelative);
            double acceptRadius = GetAcceptanceRadius(state, in item);

            UpdateNavStatus(state, in item, dist);

            if (dist < acceptRadius && altErr < 5.0)
            {
                AdvanceToNextItem(in item);
                return BuildHoldCommand(state);
            }

            return new ControlCommand
            {
                Mode = DetermineFlightMode(state),
                HasPositionTarget = true,
                TargetLat = item.Lat,
                TargetLon = item.Lon,
                TargetAltRelative = item.AltRelative,
                TargetSpeedMs = _cruiseSpeedMs,
                ThrottleMax = 1.0,
            };
        }

        private ControlCommand HandleTakeoff(double dt, SimState state, in MissionItem item)
        {
            _state = MissionExecState.Navigating;

            double altErr = item.AltRelative - state.Position.AltRelative;

            // Условие достижения: в пределах 2 м от target alt И вертикальная скорость мала.
            if (Math.Abs(altErr) < 2.0 && Math.Abs(state.Velocity.Vd) < 1.0)
            {
                AdvanceToNextItem(in item);
                return BuildHoldCommand(state);
            }

            return new ControlCommand
            {
                Mode = ControlMode.Takeoff,
                TargetAltRelative = item.AltRelative,
                ThrottleMax = 1.0,
            };
        }

        private ControlCommand HandleLand(double dt, SimState state, in MissionItem item)
        {
            _state = MissionExecState.Navigating;

            // Приземление: alt_rel ≈ 0 И почти нулевая скорость.
            if (state.Position.AltRelative < 0.3 && Math.Abs(state.Velocity.Vd) < 0.3)
            {
                AdvanceToNextItem(in item);
                _state = MissionExecState.Completed;
                MissionCompleted?.Invoke(this, EventArgs.Empty);
                return BuildHoldCommand(state);
            }

            return new ControlCommand
            {
                Mode = ControlMode.Landing,
                ThrottleMax = 1.0,
            };
        }

        private ControlCommand HandleLoiterUnlim(double dt, SimState state, in MissionItem item)
        {
            _state = MissionExecState.Loitering;

            // Держим точку. Выход — только по SetCurrent() или TriggerRtl().
            double dist = Navigator.DistanceM(
                state.Position.Lat, state.Position.Lon, item.Lat, item.Lon);
            UpdateNavStatus(state, in item, dist);

            return new ControlCommand
            {
                Mode = DetermineFlightMode(state),
                HasPositionTarget = true,
                TargetLat = item.Lat,
                TargetLon = item.Lon,
                TargetAltRelative = item.AltRelative,
                TargetSpeedMs = _cruiseSpeedMs,
                ThrottleMax = 1.0,
            };
        }

        private ControlCommand HandleLoiterTime(double dt, SimState state, in MissionItem item)
        {
            double dist = Navigator.DistanceM(
                state.Position.Lat, state.Position.Lon, item.Lat, item.Lon);
            double acceptRadius = GetAcceptanceRadius(state, in item);
            UpdateNavStatus(state, in item, dist);

            if (dist < acceptRadius)
            {
                _state = MissionExecState.Loitering;
                _loiterElapsedSec += dt;

                if (_loiterElapsedSec >= item.Param1)  // p1 = секунды
                {
                    AdvanceToNextItem(in item);
                    return BuildHoldCommand(state);
                }
            }
            else
            {
                _state = MissionExecState.Navigating;
                _loiterElapsedSec = 0;
            }

            return new ControlCommand
            {
                Mode = DetermineFlightMode(state),
                HasPositionTarget = true,
                TargetLat = item.Lat,
                TargetLon = item.Lon,
                TargetAltRelative = item.AltRelative,
                TargetSpeedMs = _cruiseSpeedMs,
                ThrottleMax = 1.0,
            };
        }

        private ControlCommand HandleLoiterTurns(double dt, SimState state, in MissionItem item)
        {
            // Аппроксимация: T = 2π·R·N / V.
            double turns = Math.Max(1.0, item.Param1);
            double radius = item.Param3 > 1.0 ? item.Param3 : 50.0;
            double speed = _cruiseSpeedMs > 1.0 ? _cruiseSpeedMs : 10.0;
            double timeNeeded = 2.0 * Math.PI * radius * turns / speed;

            double dist = Navigator.DistanceM(
                state.Position.Lat, state.Position.Lon, item.Lat, item.Lon);
            double acceptRadius = GetAcceptanceRadius(state, in item);
            UpdateNavStatus(state, in item, dist);

            if (dist < acceptRadius)
            {
                _state = MissionExecState.Loitering;
                _loiterElapsedSec += dt;

                if (_loiterElapsedSec >= timeNeeded)
                {
                    AdvanceToNextItem(in item);
                    return BuildHoldCommand(state);
                }
            }
            else
            {
                _state = MissionExecState.Navigating;
                _loiterElapsedSec = 0;
            }

            return new ControlCommand
            {
                Mode = DetermineFlightMode(state),
                HasPositionTarget = true,
                TargetLat = item.Lat,
                TargetLon = item.Lon,
                TargetAltRelative = item.AltRelative,
                TargetSpeedMs = _cruiseSpeedMs,
                ThrottleMax = 1.0,
            };
        }

        private ControlCommand HandleDelay(double dt, SimState state, in MissionItem item)
        {
            _state = MissionExecState.Delaying;
            _loiterElapsedSec += dt;

            if (_loiterElapsedSec >= item.Param1)
            {
                AdvanceToNextItem(in item);
                return BuildHoldCommand(state);
            }
            return BuildHoldCommand(state);
        }

        private ControlCommand HandleChangeSpeed(SimState state, in MissionItem item)
        {
            // p1 = speed type (0=airspeed, 1=groundspeed).
            // p2 = новая скорость м/с.
            if (item.Param2 > 0) _cruiseSpeedMs = item.Param2;

            // Команда мгновенная — сразу advance.
            AdvanceToNextItem(in item);
            return BuildHoldCommand(state);
        }

        private ControlCommand HandleVtolTransition(double dt, SimState state, in MissionItem item)
        {
            // p1 = 3 (MC) или 4 (FW).
            bool wantFw = item.Param1 >= 4;

            _state = MissionExecState.Transitioning;
            _loiterElapsedSec += dt;

            // Условие завершения: airspeed criterion ИЛИ timeout 15 с.
            bool airspeedOk = wantFw
                ? state.Velocity.AirSpeed > 17.0
                : state.Velocity.AirSpeed < 10.0;
            bool timeout = _loiterElapsedSec > 20.0;

            if (airspeedOk || timeout)
            {
                _currentRegime = wantFw ? ControlMode.FixedWing : ControlMode.Multirotor;
                AdvanceToNextItem(in item);
                return BuildHoldCommand(state);
            }

            // Во время перехода лететь в направлении следующего WP (чтобы pusher
            // разгонял ВС по курсу, а не в никуда).
            MissionItem? nextWithCoords = FindNextWpWithCoordinates();
            double tgtLat = nextWithCoords?.Lat ?? state.Position.Lat;
            double tgtLon = nextWithCoords?.Lon ?? state.Position.Lon;
            double tgtAlt = nextWithCoords?.AltRelative ?? state.Position.AltRelative;

            return new ControlCommand
            {
                Mode = wantFw ? ControlMode.TransitionToFw : ControlMode.TransitionToMc,
                HasPositionTarget = true,
                TargetLat = tgtLat,
                TargetLon = tgtLon,
                TargetAltRelative = tgtAlt,
                TargetSpeedMs = _cruiseSpeedMs,
                ThrottleMax = 1.0,
            };
        }

        /// <summary>
        /// Найти следующий элемент миссии после текущего, у которого есть
        /// валидные координаты (lat/lon != 0). Пропускает DO_*-команды.
        /// </summary>
        private MissionItem? FindNextWpWithCoordinates()
        {
            for (int i = _currentIndex + 1; i < _items.Length; i++)
            {
                var it = _items[i];
                if (Math.Abs(it.Lat) > 1e-6 && Math.Abs(it.Lon) > 1e-6)
                    return it;
            }
            return null;
        }

        private ControlCommand HandleRtlItem(double dt, SimState state, in MissionItem item)
        {
            _rtlActive = true;
            return HandleRtl(dt, state);
        }

        private ControlCommand HandleRtl(double dt, SimState state)
        {
            const double RtlAltitude = 30.0;

            double distToHome = Navigator.DistanceM(
                state.Position.Lat, state.Position.Lon,
                state.Home.Lat, state.Home.Lon);

            // Шаг 1: набор высоты на месте, если низко.
            if (state.Position.AltRelative < RtlAltitude - 2.0 && distToHome > 10.0)
            {
                return new ControlCommand
                {
                    Mode = DetermineFlightMode(state),
                    HasPositionTarget = true,
                    TargetLat = state.Position.Lat,
                    TargetLon = state.Position.Lon,
                    TargetAltRelative = RtlAltitude,
                    ThrottleMax = 1.0,
                };
            }

            // Шаг 2: полёт к HOME.
            if (distToHome > 5.0)
            {
                return new ControlCommand
                {
                    Mode = DetermineFlightMode(state),
                    HasPositionTarget = true,
                    TargetLat = state.Home.Lat,
                    TargetLon = state.Home.Lon,
                    TargetAltRelative = RtlAltitude,
                    TargetSpeedMs = _cruiseSpeedMs,
                    ThrottleMax = 1.0,
                };
            }

            // Шаг 3: посадка.
            if (state.Position.AltRelative > 0.3)
            {
                return new ControlCommand
                {
                    Mode = ControlMode.Landing,
                    ThrottleMax = 1.0,
                };
            }

            // Приземлились.
            _rtlActive = false;
            _state = MissionExecState.Completed;
            MissionCompleted?.Invoke(this, EventArgs.Empty);
            return BuildHoldCommand(state);
        }

        private ControlCommand HandleSkip(in MissionItem item)
        {
            // Неизвестная/неподдерживаемая команда — пропускаем.
            AdvanceToNextItem(in item);
            return default;
        }

        // =====================================================================
        // Helpers
        // =====================================================================

        private ControlCommand BuildHoldCommand(SimState state)
        {
            return new ControlCommand
            {
                Mode = DetermineFlightMode(state),
                HasPositionTarget = true,
                TargetLat = state.Position.Lat,
                TargetLon = state.Position.Lon,
                TargetAltRelative = state.Position.AltRelative,
                ThrottleMax = 1.0,
            };
        }

        /// <summary>
        /// Определить flight mode: для Copter всегда MC, для VTOL — по последнему
        /// завершённому TRANSITION.
        /// </summary>
        private ControlMode DetermineFlightMode(SimState state)
        {
            if (state.Vehicle == VehicleType.Copter) return ControlMode.Multirotor;
            return _currentRegime;
        }

        /// <summary>
        /// Радиус принятия точки. Для VTOL в FW — не меньше 100 м (память проекта).
        /// Для MC — 5 м или <c>param2</c>.
        /// </summary>
        private double GetAcceptanceRadius(SimState state, in MissionItem item)
        {
            double wpRadius = item.Param2;

            if (state.Vehicle == VehicleType.Vtol && _currentRegime == ControlMode.FixedWing)
                return Math.Max(100.0, wpRadius);

            return wpRadius > 0 ? wpRadius : 5.0;
        }

        private static void UpdateNavStatus(SimState state, in MissionItem item, double distM)
        {
            double bearing = Navigator.BearingDeg(
                state.Position.Lat, state.Position.Lon, item.Lat, item.Lon);
            double altErr = item.AltRelative - state.Position.AltRelative;

            using (state.Write())
            {
                state.NavStatus.NavBearingDeg = bearing;
                state.NavStatus.TargetBearingDeg = bearing;
                state.NavStatus.WpDistance = distM;
                state.NavStatus.AltError = altErr;
                state.NavStatus.AspdError = 0.0;
                state.NavStatus.XtrackError = 0.0; // TODO: между prev и curr WP
            }
        }

        private static void UpdateMissionSeqInState(SimState state, ushort seq)
        {
            using (state.Write())
                state.CurrentMissionSeq = seq;
        }

        /// <summary>
        /// Перейти к следующему элементу. Эмитит <see cref="MissionItemReached"/>
        /// и (если есть следующий) <see cref="CurrentItemChanged"/>.
        /// </summary>
        private void AdvanceToNextItem(in MissionItem reached)
        {
            _loiterElapsedSec = 0;

            MissionItemReached?.Invoke(this, reached.Seq);

            // Autocontinue = false → встаём в Loiter.
            if (!reached.Autocontinue)
            {
                _state = MissionExecState.Loitering;
                return;
            }

            _currentIndex++;

            if (_currentIndex >= _items.Length)
            {
                _state = MissionExecState.Completed;
                MissionCompleted?.Invoke(this, EventArgs.Empty);
                return;
            }

            _state = MissionExecState.Navigating;
            CurrentItemChanged?.Invoke(this, _items[_currentIndex].Seq);
        }
    }
}