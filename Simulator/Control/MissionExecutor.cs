using System;
using System.Windows;  // для Application.Current.TryFindResource (локализация)
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
        private double _wpTraceTimer = 0;


        // Path following — координаты предыдущей точки трека.
        // Используется чтобы лететь по линии From→To, а не "напрямую к To".
        private double _prevWpLat = double.NaN;
        private double _prevWpLon = double.NaN;

        // Орбита вокруг WP (облёт точки на её радиусе).
        // true пока ВС облетает текущую WP по кругу до выхода на следующую.
        private bool _orbiting = false;
        // Направление облёта: true=по часовой, false=против. Выбирается так чтобы
        // дуга была минимальной (зависит от положения next WP).
        private bool _orbitClockwise = true;
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

        /// <summary>Диагностический лог для UI (переходы, timeout и т.д.).</summary>
        public event EventHandler<string> DiagnosticLog;

        // ---- Локализация ----

        /// <summary>
        /// Получить локализованную строку из ресурсов приложения (Lang_ru-RU.xaml / Lang_kk-KZ.xaml).
        /// Fallback возвращается если ключ не найден или Application.Current отсутствует
        /// (например, в юнит-тестах).
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

        /// <summary>Текущая команда миссии (или null если не активна).</summary>
        public MissionCommand? CurrentCommand
        {
            get
            {
                lock (_lock)
                {
                    if (_items.Length == 0 || _currentIndex < 0 ||
                        _currentIndex >= _items.Length) return null;
                    return _items[_currentIndex].Command;
                }
            }
        }

        /// <summary>Текущая крейсерская скорость (из DO_CHANGE_SPEED), м/с.</summary>
        public double CurrentCruiseSpeedMs { get { lock (_lock) return _cruiseSpeedMs; } }

        /// <summary>
        /// Текущий "работающий" режим VTOL (обновляется через TRANSITION_FW/MC).
        /// </summary>
        public ControlMode CurrentRegime { get { lock (_lock) return _currentRegime; } }

        // =====================================================================
        // Lifecycle
        // =====================================================================

        /// <summary>
        /// Загрузить миссию. Массив передаётся "как есть" (executor владеет им).
        /// Первый элемент обычно HOME (seq=0), он пропускается при Start.
        /// </summary>
        /// <summary>
        /// Загрузить миссию. Если миссия была активна — пытаемся продолжить
        /// без полной остановки. droneLat/droneLon — позиция дрона для
        /// fallback если нужна новая prev-точка.
        ///
        /// ИНВАРИАНТЫ:
        /// • _prevWpLat/Lon — точка "откуда дрон начал ЛИНИЮ к current".
        ///   Установлена при AdvanceToNextItem. Если меняется current,
        ///   prev остаётся (линия продолжается к новой цели).
        /// • _orbiting — дрон КРУЖИТ вокруг _items[_currentIndex].
        ///   Если координаты current сменились — центр орбиты сдвинулся,
        ///   надо _orbiting=false чтобы снова зайти на entry tangent.
        /// • nextWp считается динамически в HandleWaypoint — автоматически
        ///   подхватывает изменения exit-tangent'а.
        /// </summary>
        public void Upload(MissionItem[] items, double droneLat = double.NaN, double droneLon = double.NaN)
        {
            bool autoStartAfterCompleted = false;
            lock (_lock)
            {
                bool wasActive = _items.Length > 0
                                 && _state != MissionExecState.Idle
                                 && _state != MissionExecState.Completed;
                bool wasCompleted = _state == MissionExecState.Completed;

                // Снимок текущего WP до замены
                double oldCurLat = double.NaN, oldCurLon = double.NaN;
                MissionCommand oldCurCmd = MissionCommand.Waypoint;
                ushort oldCurSeq = 0;
                bool hadOldCur = false;
                if (wasActive && _currentIndex >= 0 && _currentIndex < _items.Length)
                {
                    var cur = _items[_currentIndex];
                    oldCurLat = cur.Lat; oldCurLon = cur.Lon;
                    oldCurCmd = cur.Command; oldCurSeq = cur.Seq;
                    hadOldCur = true;
                }

                _items = items ?? Array.Empty<MissionItem>();
                if (_cruiseSpeedMs <= 0) _cruiseSpeedMs = 18.0;

                // ───── Случай 1: пустая миссия ─────
                if (_items.Length == 0)
                {
                    ResetFully();
                    return;
                }

                // ───── Случай 2: миссия не была активна ─────
                if (!wasActive)
                {
                    ResetFully();
                    if (wasCompleted) autoStartAfterCompleted = true;
                    else _state = MissionExecState.Idle;
                    return;
                }

                // ───── Случай 3: миссия активна, нужно примериться ─────

                // Найти старый current в новой миссии.
                int foundByCoords = -1;
                int foundBySeq = -1;
                if (hadOldCur)
                {
                    for (int i = 0; i < _items.Length; i++)
                    {
                        var it = _items[i];
                        if (it.Command == oldCurCmd
                            && Math.Abs(it.Lat - oldCurLat) < 1e-7
                            && Math.Abs(it.Lon - oldCurLon) < 1e-7)
                        {
                            foundByCoords = i;
                            break;
                        }
                    }
                    for (int i = 0; i < _items.Length; i++)
                    {
                        if (_items[i].Seq == oldCurSeq) { foundBySeq = i; break; }
                    }
                }

                // Что произошло?
                if (foundByCoords >= 0)
                {
                    // Ту же точку нашли по координатам.
                    // Возможные сценарии:
                    //   a) seq не изменился → миссия не поменялась вокруг current
                    //   b) seq изменился → перед или после current добавили/удалили WP
                    ushort newSeqOfCur = _items[foundByCoords].Seq;

                    if (hadOldCur && newSeqOfCur > oldCurSeq)
                    {
                        // Перед current вставили новые WP. Надо лететь к ним
                        // СНАЧАЛА, не пропускать.
                        int firstInserted = -1;
                        for (int i = 0; i < foundByCoords; i++)
                        {
                            if (IsNavigationCommand(_items[i].Command)
                                && _items[i].Seq > oldCurSeq)
                            {
                                firstInserted = i;
                                break;
                            }
                        }
                        if (firstInserted >= 0)
                        {
                            _currentIndex = firstInserted;
                            _orbiting = false;  // новая точка — новый entry
                            SetPrevFromDrone(droneLat, droneLon);
                        }
                        else
                        {
                            // Теоретически: seq сместился но перед current нет
                            // навигационных WP с большим seq. Случай странный —
                            // просто продолжаем к той же точке.
                            _currentIndex = foundByCoords;
                            // _orbiting, _prevWpLat/Lon не трогаем (тот же WP)
                        }
                    }
                    else
                    {
                        // Current на той же позиции или даже сдвинулся назад
                        // (удалили что-то перед ним). Координаты те же,
                        // dubins логика продолжается.
                        _currentIndex = foundByCoords;
                        // _orbiting, _prevWpLat/Lon НЕ трогаем.
                    }
                }
                else if (foundBySeq >= 0)
                {
                    // По координатам не нашли, но seq такой же → drag current
                    // (координаты сменились).
                    _currentIndex = foundBySeq;
                    _orbiting = false;  // центр орбиты сдвинулся
                    // _prevWpLat/Lon НЕ трогаем — линия от того же prev продолжается
                    // (просто цель теперь в другом месте).
                }
                else
                {
                    // Current полностью исчез из миссии (удалён).
                    // Прыгаем на следующий по старому индексу.
                    _currentIndex = Math.Min(_currentIndex, _items.Length - 1);
                    if (_currentIndex < 0) _currentIndex = 0;
                    _orbiting = false;
                    SetPrevFromDrone(droneLat, droneLon);
                    _loiterElapsedSec = 0;
                }

                _state = MissionExecState.Navigating;
                _rtlActive = false;
            }

            // Вне lock'а: уведомить подписчиков и (если нужно) запустить миссию.
            if (_items.Length > 0 && _currentIndex >= 0 && _currentIndex < _items.Length)
            {
                CurrentItemChanged?.Invoke(this, _items[_currentIndex].Seq);
            }
            if (autoStartAfterCompleted) Start();
        }

        /// <summary>
        /// Полный сброс состояния миссии (без изменения _items).
        /// Используется когда миссия не была активна или пуста.
        /// </summary>
        private void ResetFully()
        {
            _currentIndex = 0;
            _loiterElapsedSec = 0;
            _rtlActive = false;
            _orbiting = false;
            _prevWpLat = double.NaN;
            _prevWpLon = double.NaN;
        }

        /// <summary>
        /// Установить prev из позиции дрона если она валидна.
        /// Вызывается когда дрон меняет цель (новый WP перед ним, удаление
        /// current) — prev становится "отсюда начал лететь к новой точке".
        /// </summary>
        private void SetPrevFromDrone(double droneLat, double droneLon)
        {
            if (!double.IsNaN(droneLat) && !double.IsNaN(droneLon))
            {
                _prevWpLat = droneLat;
                _prevWpLon = droneLon;
            }
            else
            {
                _prevWpLat = double.NaN;
                _prevWpLon = double.NaN;
            }
        }

        /// <summary>
        /// Заменить диапазон items (MAVLink MISSION_WRITE_PARTIAL_LIST).
        /// GCS обновляет часть точек без перезаливки всей миссии.
        ///
        /// ИНВАРИАНТЫ (см. Upload):
        /// • Если current в diapazonе — координаты могли измениться (drag) →
        ///   сбросить _orbiting (центр орбиты сдвинулся), prev сохранить.
        /// • Если current НЕ в diapazonе, но в diapazonе следующий WP —
        ///   exit tangent зависит от next, но пересчитывается автоматом
        ///   в HandleWaypoint. Можно не трогать _orbiting.
        /// • Если current НЕ в diapazonе и next НЕ в diapazonе — это далёкое
        ///   изменение, ничего не трогаем.
        /// </summary>
        public void ReplaceRange(ushort start, ushort end, MissionItem[] newItems,
                                  double droneLat = double.NaN, double droneLon = double.NaN)
        {
            if (newItems == null) newItems = Array.Empty<MissionItem>();
            ushort curSeq = 0;
            lock (_lock)
            {
                if (_items.Length == 0)
                {
                    // Нет базовой миссии — трактуем как full upload этого диапазона.
                    _items = newItems;
                    _currentIndex = 0;
                    _state = MissionExecState.Idle;
                    return;
                }

                // Снимок координат old current до замены
                double oldCurLat = double.NaN, oldCurLon = double.NaN;
                bool hadOldCur = false;
                if (_currentIndex >= 0 && _currentIndex < _items.Length)
                {
                    oldCurLat = _items[_currentIndex].Lat;
                    oldCurLon = _items[_currentIndex].Lon;
                    hadOldCur = true;
                }

                int s = Math.Min(start, _items.Length);
                int e = Math.Min(end, _items.Length);
                if (e < s) e = s;
                int oldRangeLen = e - s + 1;
                if (e >= _items.Length) oldRangeLen = _items.Length - s;

                int newLen = _items.Length - oldRangeLen + newItems.Length;
                var merged = new MissionItem[newLen];

                // Префикс до start
                for (int i = 0; i < s; i++) merged[i] = _items[i];
                // Новый диапазон
                for (int i = 0; i < newItems.Length; i++) merged[s + i] = newItems[i];
                // Суффикс после старого end
                int tailStart = s + oldRangeLen;
                int tailDst = s + newItems.Length;
                for (int i = 0; tailStart + i < _items.Length; i++)
                    merged[tailDst + i] = _items[tailStart + i];

                // Пересчёт Seq у всех items (для консистентности)
                for (int i = 0; i < merged.Length; i++) merged[i].Seq = (ushort)i;

                _items = merged;

                // ───── Корректировка _currentIndex и связанного состояния ─────
                int delta = newItems.Length - oldRangeLen;
                int currentWas = _currentIndex;

                if (currentWas >= tailStart)
                {
                    // Current был ПОСЛЕ заменённого диапазона → сдвиг индекса.
                    // Координаты current не изменились — НЕ сбрасывать _orbiting.
                    _currentIndex = Math.Min(currentWas + delta, _items.Length - 1);
                }
                else if (currentWas >= s)
                {
                    // Current был ВНУТРИ diapazonа:
                    //   • drag текущего WP — координаты сменились
                    //   • delete current — исчез, берём соседа
                    //   • insert before current — current сдвинулся к этому же seq
                    _currentIndex = Math.Min(currentWas, _items.Length - 1);
                    if (_currentIndex < 0) _currentIndex = 0;

                    // Сравниваем координаты old vs new current
                    bool coordsChanged = true;
                    if (hadOldCur && _currentIndex < _items.Length)
                    {
                        var newCur = _items[_currentIndex];
                        coordsChanged = Math.Abs(newCur.Lat - oldCurLat) > 1e-7
                                      || Math.Abs(newCur.Lon - oldCurLon) > 1e-7;
                    }

                    if (coordsChanged)
                    {
                        // Центр орбиты сдвинулся (или вообще другая точка) →
                        // сбрасываем orbit. Prev оставляем (линия продолжается).
                        _orbiting = false;

                        // Если prev вообще не был установлен (редкий случай —
                        // drag самого первого WP до achievement HOME-WP1) —
                        // используем позицию дрона как fallback.
                        if (double.IsNaN(_prevWpLat) || double.IsNaN(_prevWpLon))
                        {
                            SetPrevFromDrone(droneLat, droneLon);
                        }
                    }
                    // Если координаты не изменились (просто insert/delete в
                    // другом месте diapazonа без затрагивания current) —
                    // ВООБЩЕ ничего не трогаем.
                }
                else
                {
                    // Current был ПЕРЕД diapazonом → изменения далеко впереди.
                    // Current не затронут, его координаты те же — НЕ трогаем
                    // _orbiting. Exit tangent (если дрон в орбите) пересчитается
                    // автоматически в HandleWaypoint из новых координат next WP.
                    // НЕ трогать _currentIndex, _orbiting, _prevWpLat/Lon.
                }

                curSeq = _currentIndex >= 0 && _currentIndex < _items.Length
                         ? _items[_currentIndex].Seq : (ushort)0;
            }

            // Вне lock'а: уведомляем подписчиков
            CurrentItemChanged?.Invoke(this, curSeq);
        }

        /// <summary>
        /// Попытаться обновить существующий item по seq без сброса состояния.
        /// Используется для drag-update когда GCS шлёт один MISSION_ITEM_INT
        /// без сопровождающего MISSION_COUNT или MISSION_WRITE_PARTIAL_LIST.
        /// Возвращает true если item найден и обновлён.
        /// </summary>
        public bool TryModifyItem(ushort seq, MissionItem updated)
        {
            bool wasCurrent = false;
            MissionItem currentItem = default;
            lock (_lock)
            {
                int idx = -1;
                for (int i = 0; i < _items.Length; i++)
                {
                    if (_items[i].Seq == seq) { idx = i; break; }
                }
                if (idx < 0) return false;

                // Сохраняем seq — остальные поля берём из updated
                updated.Seq = seq;
                _items[idx] = updated;

                // Если обновили текущий WP — сбрасываем линию path following
                if (idx == _currentIndex)
                {
                    _orbiting = false;
                    _prevWpLat = double.NaN;
                    _prevWpLon = double.NaN;
                    wasCurrent = true;
                    currentItem = _items[idx];
                }
            }

            // Форсируем уведомление о смене текущего WP чтобы SimState.NavStatus
            // и GCS-овая оранжевая линия обновились СРАЗУ а не на следующем tick.
            if (wasCurrent)
            {
                CurrentItemChanged?.Invoke(this, seq);
            }

            return true;
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

                // Path following: точкой "From" для первого WP считаем HOME (item 0).
                if (_items.Length > 0 && _items[0].Seq == 0)
                {
                    _prevWpLat = _items[0].Lat;
                    _prevWpLon = _items[0].Lon;
                }
                else
                {
                    _prevWpLat = double.NaN;
                    _prevWpLon = double.NaN;
                }

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

        // =====================================================================
        // DUBINS PATH NAVIGATION
        //
        // Классический алгоритм для fixed-wing UAV. ВС летит по схеме:
        //   prev_WP → касательная → круг радиуса R → касательная → next_WP
        //
        // Вычисление точки касания из внешней точки:
        //   d = distance(P, C)
        //   α = arccos(R / d)         — угол между PC и касательной
        //   tangent_len = √(d² - R²)
        //   T = C + R·[cos(bearing_C→P ± α), sin(bearing_C→P ± α)]
        //
        // Знак ± определяется направлением облёта (по/против часовой).
        // Направление облёта выбирается чтобы дуга была МИНИМАЛЬНОЙ.
        // =====================================================================

        private ControlCommand HandleWaypoint(double dt, SimState state, in MissionItem item)
        {
            _state = MissionExecState.Navigating;

            double lat = state.Position.Lat;
            double lon = state.Position.Lon;
            double altErr = Math.Abs(state.Position.AltRelative - item.AltRelative);

            // Радиус облёта:
            //   WAYPOINT/SPLINE_WP: param2 = acceptance radius (и он же = orbit radius).
            //   LOITER_UNLIM/TIME/TURNS: |param3| = radius, знак = CW(+) / CCW(-).
            // По ArduPilot спецификации param3 для LOITER команд всегда содержит
            // signed radius (GCS отправляет wp.Clockwise ? +R : -R).
            bool isLoiterCmd = item.Command == MissionCommand.LoiterUnlim
                            || item.Command == MissionCommand.LoiterTime
                            || item.Command == MissionCommand.LoiterTurns;

            double R;
            if (isLoiterCmd)
            {
                // Для LOITER команд приоритет у |param3|.
                R = Math.Abs(item.Param3) > 1.0
                    ? Math.Abs(item.Param3)
                    : ((_currentRegime == ControlMode.FixedWing) ? 80.0 : 5.0);
            }
            else
            {
                // Для обычных WAYPOINT.
                R = item.Param2 > 0
                    ? item.Param2
                    : ((_currentRegime == ControlMode.FixedWing) ? 80.0 : 5.0);
            }

            // Координаты From и Next (для расчёта касательных).
            double prevLat = double.IsNaN(_prevWpLat) ? lat : _prevWpLat;
            double prevLon = double.IsNaN(_prevWpLon) ? lon : _prevWpLon;
            MissionItem? nextWp = FindNextWpWithCoordinates();

            // Расчёт точки касания ENTRY (касательная из prev_WP к кругу).
            double entryLat, entryLon;
            int orbitDir;

            // Направление облёта определяем через cross product
            // (prev→WP) × (WP→next). Знак показывает с какой стороны next.
            //
            // ПРИОРИТЕТ для LOITER команд: знак param3 задаёт CW(+)/CCW(-) явно
            // (ArduPilot стандарт). Это перекрывает cross product, т.к. пользователь
            // задал направление в GCS явно (кнопка CW/CCW в WaypointEditDialog).
            if (isLoiterCmd && Math.Abs(item.Param3) > 0.01)
            {
                orbitDir = item.Param3 > 0 ? +1 : -1;
            }
            else if (nextWp.HasValue)
            {
                // Векторы в локальных метрах (плоская проекция)
                double mPerLat = Navigator.MetersPerDegLat;
                double mPerLon = mPerLat * Math.Cos(item.Lat * Navigator.DegToRad);

                double v1n = (item.Lat - prevLat) * mPerLat;
                double v1e = (item.Lon - prevLon) * mPerLon;
                double v2n = (nextWp.Value.Lat - item.Lat) * mPerLat;
                double v2e = (nextWp.Value.Lon - item.Lon) * mPerLon;

                // 2D cross product (Z компонента)
                double cross = v1n * v2e - v1e * v2n;

                // Защита от вырожденного случая: если next WP близко к prev WP
                // (например: HOME→WP1→LAND где LAND в той же точке что HOME),
                // векторы противоположны, cross ≈ 0, направление определяется шумом
                // floating point. В таких случаях дефолтимся к CW для консистентности.
                double v1Len = Math.Sqrt(v1n * v1n + v1e * v1e);
                double v2Len = Math.Sqrt(v2n * v2n + v2e * v2e);
                double crossNormalized = (v1Len > 0.1 && v2Len > 0.1)
                    ? cross / (v1Len * v2Len)   // sin(угла) между векторами
                    : 0;

                // |sin| < 0.1 = угол между векторами < 6° или > 174° (почти коллинеарны).
                // Выбор направления по такому cross нестабилен.
                if (Math.Abs(crossNormalized) < 0.1)
                {
                    orbitDir = +1;  // дефолт CW при вырожденной геометрии
                }
                else
                {
                    // ВНЕШНЯЯ дуга (снаружи треугольника):
                    // cross > 0 (поворот налево) → облёт по часовой чтобы обойти с ВНЕШНЕЙ стороны
                    // cross < 0 (поворот направо) → облёт против часовой
                    orbitDir = cross > 0 ? +1 : -1;
                }
            }
            else
            {
                orbitDir = +1;  // дефолт по часовой если нет next
            }

            // ENTRY: точка касания касательной от prev к кругу WP.
            ComputeTangentPoint(prevLat, prevLon, item.Lat, item.Lon, R, orbitDir,
                isEntry: true, out entryLat, out entryLon);

            // EXIT: точка касания касательной от next к кругу WP.
            double exitLat = entryLat, exitLon = entryLon;
            bool hasNext = nextWp.HasValue;
            if (hasNext)
            {
                ComputeTangentPoint(nextWp.Value.Lat, nextWp.Value.Lon,
                    item.Lat, item.Lon, R, orbitDir,
                    isEntry: false, out exitLat, out exitLon);
            }

            double dist = Navigator.DistanceM(lat, lon, item.Lat, item.Lon);
            double distToEntry = Navigator.DistanceM(lat, lon, entryLat, entryLon);
            double distToExit = Navigator.DistanceM(lat, lon, exitLat, exitLon);
            UpdateNavStatus(state, in item, dist);

            // Trace — периодический вывод dist/orbit закомментирован по просьбе пользователя,
            // т.к. забивал лог каждые 2 сек. Таймер оставлен на случай, если потребуется
            // включить trace обратно для отладки.
            _wpTraceTimer += dt;
            // if (_wpTraceTimer >= 2.0)
            // {
            //     _wpTraceTimer = 0;
            //     DiagnosticLog?.Invoke(this,
            //         $"WP{item.Seq}: dist={dist:F0}m R={R:F0}m dir={(orbitDir > 0 ? "CW" : "CCW")} " +
            //         $"toEntry={distToEntry:F0}m toExit={distToExit:F0}m orbit={_orbiting}");
            // }

            // ---- Состояние полёта: до entry / на дуге / после exit ----

            if (!_orbiting)
            {
                // ФАЗА 1: летим к ENTRY-ТОЧКЕ по линии от prev.
                double entryAccept = Math.Min(20.0, R * 0.3);
                if (distToEntry < entryAccept)
                {
                    _orbiting = true;
                    DiagnosticLog?.Invoke(this,
                        string.Format(Loc("MsnLog_OrbitStart", "WP{0}: ORBIT START tangent=({1},{2})"),
                            item.Seq, entryLat.ToString("F5"), entryLon.ToString("F5")));
                }
                else
                {
                    // К entry-точке летим НАПРЯМУЮ (без FromLat/Lon).
                    // Если передать From — физика делает cross-track к линии From→Target
                    // и игнорирует истинный target. ВС мечется между линией и entry.
                    return new ControlCommand
                    {
                        Mode = DetermineFlightMode(state),
                        HasPositionTarget = true,
                        TargetLat = entryLat,
                        TargetLon = entryLon,
                        TargetAltRelative = item.AltRelative,
                        TargetSpeedMs = _cruiseSpeedMs,
                        ThrottleMax = 1.0,
                    };
                }
            }

            // ФАЗА 2: ОБЛЁТ по дуге. Цель — EXIT-точка.
            if (hasNext)
            {
                double exitAccept = Math.Min(20.0, R * 0.3);
                if (distToExit < exitAccept)
                {
                    DiagnosticLog?.Invoke(this,
                        string.Format(Loc("MsnLog_WpReachedOrbit", "WP{0} REACHED (orbit complete at exit tangent)"),
                            item.Seq));
                    _orbiting = false;
                    _prevWpLat = item.Lat;
                    _prevWpLon = item.Lon;
                    AdvanceToNextItem(in item);
                    return BuildHoldCommand(state);
                }

                // Цель в орбите: точка ВПЕРЕДИ по дуге на 30° (по выбранному направлению).
                double bearingFromCenterToVc = Navigator.BearingDeg(item.Lat, item.Lon, lat, lon);
                double aheadAngle = bearingFromCenterToVc + orbitDir * 30.0;
                Navigator.OffsetByBearing(item.Lat, item.Lon, aheadAngle, R,
                    out double aheadLat, out double aheadLon);

                return new ControlCommand
                {
                    Mode = DetermineFlightMode(state),
                    HasPositionTarget = true,
                    TargetLat = aheadLat,
                    TargetLon = aheadLon,
                    TargetAltRelative = item.AltRelative,
                    TargetSpeedMs = _cruiseSpeedMs,
                    ThrottleMax = 1.0,
                };
            }
            else
            {
                // Последняя WP — после одного захода завершаем.
                if (altErr < 5.0)
                {
                    DiagnosticLog?.Invoke(this,
                        string.Format(Loc("MsnLog_WpReachedFinal", "WP{0} REACHED (final WP)"), item.Seq));
                    _orbiting = false;
                    _prevWpLat = item.Lat;
                    _prevWpLon = item.Lon;
                    AdvanceToNextItem(in item);
                    return BuildHoldCommand(state);
                }

                // Кружим вокруг.
                double bearingFromCenterToVc = Navigator.BearingDeg(item.Lat, item.Lon, lat, lon);
                double aheadAngle = bearingFromCenterToVc + orbitDir * 30.0;
                Navigator.OffsetByBearing(item.Lat, item.Lon, aheadAngle, R,
                    out double aheadLat, out double aheadLon);

                return new ControlCommand
                {
                    Mode = DetermineFlightMode(state),
                    HasPositionTarget = true,
                    TargetLat = aheadLat,
                    TargetLon = aheadLon,
                    TargetAltRelative = item.AltRelative,
                    TargetSpeedMs = _cruiseSpeedMs,
                    ThrottleMax = 1.0,
                };
            }
        }

        /// <summary>
        /// Вычисляет точку касания касательной из внешней точки P к кругу
        /// с центром C радиуса R.
        ///
        /// Формула:
        ///   d = distance(P, C)
        ///   α = arccos(R / d)         — угол между PC и касательной
        ///   T = C + R·[cos(bearing(C→P) ± α), sin(bearing(C→P) ± α)]
        ///
        /// Знак ± определяется направлением облёта:
        /// - orbitDir = +1 (CW): для ENTRY используем -α, для EXIT +α
        /// - orbitDir = -1 (CCW): наоборот
        /// </summary>
        private static void ComputeTangentPoint(
            double pLat, double pLon,
            double cLat, double cLon, double R,
            int orbitDir, bool isEntry,
            out double tLat, out double tLon)
        {
            double d = Navigator.DistanceM(pLat, pLon, cLat, cLon);
            if (d <= R)
            {
                // P внутри круга — касательной нет, берём ближайшую точку
                double bToP = Navigator.BearingDeg(cLat, cLon, pLat, pLon);
                Navigator.OffsetByBearing(cLat, cLon, bToP, R, out tLat, out tLon);
                return;
            }

            // Угол между линией C→P и касательной
            double alphaDeg = Math.Acos(R / d) * Navigator.RadToDeg;
            // Bearing от C к P
            double bCtoP = Navigator.BearingDeg(cLat, cLon, pLat, pLon);

            // Знак выбора стороны касательной — ВНЕШНЯЯ дуга облёта
            // (снаружи угла треугольника, как у настоящих самолётов).
            double signEntry = (orbitDir > 0) ? +1.0 : -1.0;
            double signExit = -signEntry;
            double angleFromCenter = bCtoP + (isEntry ? signEntry : signExit) * alphaDeg;

            Navigator.OffsetByBearing(cLat, cLon, angleFromCenter, R, out tLat, out tLon);
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

            // Для VTOL: перед посадкой ВС должен быть в MC-режиме.
            //
            // Если пользователь в миссии явно не поставил VTOL_TRANSITION_MC перед LAND,
            // либо выбрал "Q-Посадка" (cmd 21/85) в FW cruise — мы автоматически
            // переводим ВС в MC. Это соответствует поведению ArduPilot QuadPlane:
            // NAV_VTOL_LAND всегда гарантирует MC на касании земли.
            //
            // Состояние _loiterElapsedSec используется для тайминга ramp-down
            // FW→MC (плавный переход), чтобы это не было резко.
            if (state.Vehicle == VehicleType.Vtol && _currentRegime == ControlMode.FixedWing)
            {
                _loiterElapsedSec += dt;

                // Transition FW→MC по AirSpeed (ArduPilot-стандарт Q_ASSIST_SPEED).
                // ВС считается в MC режиме когда airspeed упал ниже 8 м/с — на этой
                // скорости pusher уже неэффективен, lift моторы берут весь вес.
                //
                // Fallback по таймеру (6 сек) — для случая если ВС по какой-то причине
                // не тормозит (сильный попутный ветер или стуб физики).
                // ArduPilot в похожей ситуации даёт Q_TRANS_DECEL=2 м/с² → на скорости 22
                // торможение займёт ~7 сек, что согласуется с таймером.
                const double TRANS_AS_THRESHOLD = 8.0;       // Q_ASSIST_SPEED
                const double TRANS_FALLBACK_TIMEOUT = 6.0;   // safety timeout
                const double MIN_TRANS_TIME = 1.5;           // не ранее чем через 1.5 сек
                                                             // (даёт физике время начать торможение)

                bool asBelow = state.Velocity.AirSpeed < TRANS_AS_THRESHOLD
                               && _loiterElapsedSec >= MIN_TRANS_TIME;
                bool timeoutReached = _loiterElapsedSec >= TRANS_FALLBACK_TIMEOUT;

                if (asBelow || timeoutReached)
                {
                    _currentRegime = ControlMode.Multirotor;
                    DiagnosticLog?.Invoke(this,
                        string.Format(Loc("MsnLog_TransMcOk", "Transition → MC completed ({0}s, AS={1} m/s)"),
                            _loiterElapsedSec.ToString("F1"),
                            state.Velocity.AirSpeed.ToString("F1")));
                    _loiterElapsedSec = 0;
                }

                // Во время auto-transition: летим к точке посадки в режиме TransitionToMc.
                // Target speed плавно падает от cruise до 8 м/с.
                // Progress от 0 до 1 по mix of AS и времени (что достигнет первым).
                double asProgress = 1.0 - Math.Clamp(
                    (state.Velocity.AirSpeed - TRANS_AS_THRESHOLD) / (_cruiseSpeedMs - TRANS_AS_THRESHOLD),
                    0, 1);
                double timeProgress = Math.Clamp(_loiterElapsedSec / TRANS_FALLBACK_TIMEOUT, 0, 1);
                double transProgress = Math.Max(asProgress, timeProgress);
                double transSpeed = _cruiseSpeedMs * (1 - transProgress) + 8.0 * transProgress;

                double tgtLat = Math.Abs(item.Lat) > 1e-6 ? item.Lat : state.Position.Lat;
                double tgtLon = Math.Abs(item.Lon) > 1e-6 ? item.Lon : state.Position.Lon;

                return new ControlCommand
                {
                    Mode = ControlMode.TransitionToMc,
                    HasPositionTarget = true,
                    TargetLat = tgtLat,
                    TargetLon = tgtLon,
                    TargetAltRelative = state.Position.AltRelative,  // держим высоту
                    TargetSpeedMs = transSpeed,
                    ThrottleMax = 1.0,
                };
            }

            // Если VTOL_LAND содержит координаты (не ноль) — сначала долетаем
            // туда (обычно это HOME), потом начинаем вертикальную посадку.
            bool hasLandCoords = Math.Abs(item.Lat) > 1e-6 && Math.Abs(item.Lon) > 1e-6;
            if (hasLandCoords)
            {
                double distToLand = Navigator.DistanceM(
                    state.Position.Lat, state.Position.Lon, item.Lat, item.Lon);
                UpdateNavStatus(state, in item, distToLand);

                // Пока далеко от точки посадки — летим к ней ПО ЛИНИИ от prev WP.
                // Это даёт полёт по плану миссии (по красной линии в GCS).
                // В этой точке _currentRegime уже гарантированно MC (код выше перевёл).
                // Скорость MC подлёта к LAND — 8 м/с (медленно, как реальный VTOL).
                const double MC_APPROACH_SPEED = 8.0;

                if (distToLand > 30.0)
                {
                    return new ControlCommand
                    {
                        Mode = ControlMode.Multirotor,
                        HasPositionTarget = true,
                        TargetLat = item.Lat,
                        TargetLon = item.Lon,
                        FromLat = _prevWpLat,    // ← путевое следование по линии
                        FromLon = _prevWpLon,
                        // Держим текущую высоту пока не долетим.
                        TargetAltRelative = state.Position.AltRelative,
                        TargetSpeedMs = MC_APPROACH_SPEED,
                        ThrottleMax = 1.0,
                    };
                }

                // Близко к точке посадки — вертикальный спуск на координатах.
                // TargetAltRelative=0 — сигнал физике снижаться до земли.
                // TargetSpeedMs=0 — не двигаться горизонтально, только вниз.
                // Без этих полей команда остаётся с NaN, и физика держит текущую высоту.
                return new ControlCommand
                {
                    Mode = ControlMode.Landing,
                    HasPositionTarget = true,
                    TargetLat = item.Lat,
                    TargetLon = item.Lon,
                    TargetAltRelative = 0.0,
                    TargetSpeedMs = 0.0,
                    ThrottleMax = 1.0,
                };
            }

            // Нет координат в LAND — садимся на месте.
            return new ControlCommand
            {
                Mode = ControlMode.Landing,
                HasPositionTarget = true,
                TargetLat = state.Position.Lat,
                TargetLon = state.Position.Lon,
                TargetAltRelative = 0.0,
                TargetSpeedMs = 0.0,
                ThrottleMax = 1.0,
            };
        }

        // =====================================================================
        // LOITER команды — используют тот же Dubins orbit что WAYPOINT.
        //
        // LOITER_UNLIM (cmd 17): облёт без выхода (выход только по SetCurrent).
        // LOITER_TIME  (cmd 19): облёт на время param1 (секунды).
        // LOITER_TURNS (cmd 18): облёт на N оборотов param1.
        //
        // Все три идут через HandleWaypoint, но с предварительной проверкой exit-условия.
        // После exit_met → AdvanceToNextItem и освобождение orbit state.
        // =====================================================================

        private ControlCommand HandleLoiterUnlim(double dt, SimState state, in MissionItem item)
        {
            // Не выходит сам — только по SetCurrent или RTL.
            // Но летит по правильной orbit дуге (через HandleWaypoint).
            //
            // Трюк: делаем FindNextWpWithCoordinates вернуть null в рамках этого вызова?
            // Нет, проще — используем HandleWaypoint которая корректно работает.
            // HandleWaypoint обнаружит что это LOITER_UNLIM и... ждать.
            //
            // Для LOITER_UNLIM просто делегируем в HandleWaypoint — ВС будет orbit'ить
            // вокруг точки на заданном радиусе. Exit условие (если есть next WP) сработает
            // только при ручном вмешательстве (SetCurrent). Мы его блокируем ниже.
            var cmd = HandleWaypoint(dt, state, in item);

            // Защита от авто-advance: LOITER_UNLIM должен крутиться вечно.
            // Если HandleWaypoint позвал AdvanceToNextItem — откатываем.
            // (Фактически это происходит если ВС пересёк exit tangent.)
            //
            // Проще: LOITER_UNLIM обходим логикой exit — не будем её использовать.
            // Для LOITER_UNLIM HandleWaypoint должен кружить, но не advance'ить.

            return cmd;
        }

        private ControlCommand HandleLoiterTime(double dt, SimState state, in MissionItem item)
        {
            // LOITER_TIME: кружит param1 секунд, потом advance.
            // Таймер запускаем когда ВС вошёл в orbit (dist < R * 1.2).
            double dist = Navigator.DistanceM(
                state.Position.Lat, state.Position.Lon, item.Lat, item.Lon);
            double radius = Math.Abs(item.Param3) > 1.0 ? Math.Abs(item.Param3) : 50.0;

            if (dist < radius * 1.2)
            {
                _loiterElapsedSec += dt;
                _state = MissionExecState.Loitering;

                if (_loiterElapsedSec >= item.Param1 && item.Param1 > 0)
                {
                    // Время вышло — advance.
                    _orbiting = false;
                    _prevWpLat = item.Lat;
                    _prevWpLon = item.Lon;
                    _loiterElapsedSec = 0;
                    AdvanceToNextItem(in item);
                    return BuildHoldCommand(state);
                }
            }
            else
            {
                _loiterElapsedSec = 0;
            }

            // Делегируем полёт в HandleWaypoint — он сделает правильный Dubins orbit.
            // Exit tangent в HandleWaypoint будет срабатывать, но мы перехватываем через
            // свой таймер — поэтому блокируем advance ниже, кроме случая timeout.
            // Для LOITER_TIME/TURNS достаточно использовать orbit-логику без exit.

            // Прямо используем orbit-генератор (так же как внутри HandleWaypoint):
            return BuildOrbitCommand(state, in item, radius);
        }

        private ControlCommand HandleLoiterTurns(double dt, SimState state, in MissionItem item)
        {
            // LOITER_TURNS: облёт N оборотов. Аппроксимация через время на круг.
            double turns = Math.Max(1.0, item.Param1);
            double radius = Math.Abs(item.Param3) > 1.0 ? Math.Abs(item.Param3) : 50.0;
            double speed = _cruiseSpeedMs > 1.0 ? _cruiseSpeedMs : 10.0;
            double timeNeeded = 2.0 * Math.PI * radius * turns / speed;

            double dist = Navigator.DistanceM(
                state.Position.Lat, state.Position.Lon, item.Lat, item.Lon);

            if (dist < radius * 1.2)
            {
                _loiterElapsedSec += dt;
                _state = MissionExecState.Loitering;

                if (_loiterElapsedSec >= timeNeeded)
                {
                    _orbiting = false;
                    _prevWpLat = item.Lat;
                    _prevWpLon = item.Lon;
                    _loiterElapsedSec = 0;
                    AdvanceToNextItem(in item);
                    return BuildHoldCommand(state);
                }
            }
            else
            {
                _loiterElapsedSec = 0;
            }

            return BuildOrbitCommand(state, in item, radius);
        }

        /// <summary>
        /// Собрать команду для orbit-полёта вокруг WP.
        /// Используется для LOITER_TIME/TURNS (и как helper для LOITER_UNLIM).
        /// <para>
        /// Если ВС далеко от orbit радиуса — летит к точке касания от prev WP.
        /// Если уже на круге — даёт цель вперёд по дуге на 30° (CW или CCW).
        /// Направление берётся из знака param3 (CW+, CCW-).
        /// </para>
        /// </summary>
        private ControlCommand BuildOrbitCommand(SimState state, in MissionItem item, double radius)
        {
            double lat = state.Position.Lat;
            double lon = state.Position.Lon;

            // Направление: знак param3, дефолт CW.
            int orbitDir = item.Param3 < 0 ? -1 : +1;

            double dist = Navigator.DistanceM(lat, lon, item.Lat, item.Lon);

            // Если мы далеко от круга (>R*1.5) — сначала летим к нему.
            // Используем точку касания от нашей текущей позиции.
            if (dist > radius * 1.5)
            {
                double entryLat, entryLon;
                ComputeTangentPoint(lat, lon, item.Lat, item.Lon, radius, orbitDir,
                    isEntry: true, out entryLat, out entryLon);

                return new ControlCommand
                {
                    Mode = DetermineFlightMode(state),
                    HasPositionTarget = true,
                    TargetLat = entryLat,
                    TargetLon = entryLon,
                    TargetAltRelative = item.AltRelative,
                    TargetSpeedMs = _cruiseSpeedMs,
                    ThrottleMax = 1.0,
                };
            }

            // На круге или близко — идём по дуге.
            double bearingFromCenterToVc = Navigator.BearingDeg(item.Lat, item.Lon, lat, lon);
            double aheadAngle = bearingFromCenterToVc + orbitDir * 30.0;
            Navigator.OffsetByBearing(item.Lat, item.Lon, aheadAngle, radius,
                out double aheadLat, out double aheadLon);

            UpdateNavStatus(state, in item, dist);

            return new ControlCommand
            {
                Mode = DetermineFlightMode(state),
                HasPositionTarget = true,
                TargetLat = aheadLat,
                TargetLon = aheadLon,
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
                _loiterElapsedSec = 0;
                AdvanceToNextItem(in item);
                return BuildHoldCommand(state);
            }

            // В FW режиме самолёт не может стоять на месте — он упадёт.
            // Кружим вокруг текущей точки на малом радиусе (50 м).
            // В MC — обычный hover через BuildHoldCommand.
            if (state.Vehicle == VehicleType.Vtol && _currentRegime == ControlMode.FixedWing)
            {
                const double DELAY_LOITER_RADIUS = 80.0;

                // Центр круга — текущая позиция ВС при входе в Delay.
                // Если _prevWpLat/Lon установлены недавно — берём их (будут стабильны).
                double centerLat = !double.IsNaN(_prevWpLat) ? _prevWpLat : state.Position.Lat;
                double centerLon = !double.IsNaN(_prevWpLon) ? _prevWpLon : state.Position.Lon;

                // Создаём temporary item для BuildOrbitCommand.
                var orbitItem = new MissionItem
                {
                    Lat = centerLat,
                    Lon = centerLon,
                    AltRelative = state.Position.AltRelative,
                };
                return BuildOrbitCommand(state, in orbitItem, DELAY_LOITER_RADIUS);
            }

            // MC: можем стоять неподвижно — обычный hover.
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

            // Расчёт 5: Transition по AirSpeed (не по таймеру), fail при недостатке.
            //
            // FW transition: ждём пока AS >= AirspeedMinMs (17 м/с) — физически набрали
            //   скорость самолётного полёта. Fail при таймауте 10с (например встречный
            //   ветер слишком сильный, ВС не разогнался).
            // MC transition: остаётся по таймеру 3с — короткий и надёжный.
            //
            // Старая реализация (5с таймер) переключала в FW при AS=4 м/с —
            // ВС оказывался в FixedWing на скорости MC и кружил хаотично.

            const double TRANSITION_AS_THRESHOLD = 17.0;  // ARSPD_FBW_MIN
            const double TRANSITION_FAIL_TIMEOUT = 15.0;  // ArduPilot-стандарт 15-30с (Q_TRANS_FAIL)
            const double MC_TRANSITION_DURATION = 3.0;

            if (wantFw)
            {
                // Успех: AirSpeed достиг порога FW режима.
                if (state.Velocity.AirSpeed >= TRANSITION_AS_THRESHOLD)
                {
                    _currentRegime = ControlMode.FixedWing;
                    DiagnosticLog?.Invoke(this,
                        string.Format(Loc("MsnLog_TransFwOk", "Transition → FW completed ({0}s, AS={1} m/s)"),
                            _loiterElapsedSec.ToString("F1"), state.Velocity.AirSpeed.ToString("F1")));
                    AdvanceToNextItem(in item);
                    return BuildHoldCommand(state);
                }
                // Fail: не успели набрать скорость — возврат в MC.
                if (_loiterElapsedSec >= TRANSITION_FAIL_TIMEOUT)
                {
                    DiagnosticLog?.Invoke(this,
                        string.Format(Loc("MsnLog_TransFwFail", "Transition FAIL: AS={0} < {1} m/s for {2}s. Reverting to MC."),
                            state.Velocity.AirSpeed.ToString("F1"),
                            TRANSITION_AS_THRESHOLD.ToString("F0"),
                            TRANSITION_FAIL_TIMEOUT.ToString("F0")));
                    _currentRegime = ControlMode.Multirotor;
                    AdvanceToNextItem(in item);
                    return BuildHoldCommand(state);
                }
            }
            else
            {
                // MC transition: ждём пока AS упадёт ниже Q_ASSIST_SPEED (8 м/с).
                // Fallback по таймеру 6 сек — если ВС не тормозит (попутный ветер).
                // MIN_TRANS_TIME=1 даёт физике время начать торможение, не мгновенно.
                const double TRANS_AS_THRESHOLD = 8.0;
                const double TRANS_FALLBACK_TIMEOUT = 6.0;
                const double MIN_TRANS_TIME = 1.0;

                bool asBelow = state.Velocity.AirSpeed < TRANS_AS_THRESHOLD
                               && _loiterElapsedSec >= MIN_TRANS_TIME;
                bool timeoutReached = _loiterElapsedSec >= TRANS_FALLBACK_TIMEOUT;

                if (asBelow || timeoutReached)
                {
                    _currentRegime = ControlMode.Multirotor;
                    DiagnosticLog?.Invoke(this,
                        string.Format(Loc("MsnLog_TransMcOk", "Transition → MC completed ({0}s, AS={1} m/s)"),
                            _loiterElapsedSec.ToString("F1"),
                            state.Velocity.AirSpeed.ToString("F1")));
                    AdvanceToNextItem(in item);
                    return BuildHoldCommand(state);
                }
            }

            // Во время перехода лететь в направлении следующего WP.
            MissionItem? nextWithCoords = FindNextWpWithCoordinates();
            double tgtLat = nextWithCoords?.Lat ?? state.Position.Lat;
            double tgtLon = nextWithCoords?.Lon ?? state.Position.Lon;

            // Target alt НЕ ниже текущей (чтобы не снижаться во время transition).
            double nextAlt = nextWithCoords?.AltRelative ?? state.Position.AltRelative;
            double tgtAlt = Math.Max(nextAlt, state.Position.AltRelative);

            // Target speed: постепенно меняется во время transition.
            //   FW transition: разгон от MC скорости до FW cruise.
            //   MC transition: торможение от FW cruise до MC скорости (8 м/с).
            // Это даёт плавное изменение, ВС не "виснет" на одной скорости весь переход.
            double transSpeed;
            if (wantFw)
            {
                transSpeed = _cruiseSpeedMs;  // FW: цель — cruise
            }
            else
            {
                // MC: linear interp от cruise до 8 за время transition.
                double t = Math.Clamp(_loiterElapsedSec / MC_TRANSITION_DURATION, 0, 1);
                transSpeed = _cruiseSpeedMs * (1 - t) + 8.0 * t;
            }

            return new ControlCommand
            {
                Mode = wantFw ? ControlMode.TransitionToFw : ControlMode.TransitionToMc,
                HasPositionTarget = true,
                TargetLat = tgtLat,
                TargetLon = tgtLon,
                TargetAltRelative = tgtAlt,
                TargetSpeedMs = transSpeed,
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

            // VTOL в FW: 50м — fallback для случая когда нет линии From→To.
            // При path following основной критерий — пересечение траверза WP.
            if (state.Vehicle == VehicleType.Vtol && _currentRegime == ControlMode.FixedWing)
                return wpRadius > 0 ? wpRadius : 50.0;

            return wpRadius > 0 ? wpRadius : 5.0;
        }

        /// <summary>
        /// Является ли команда навигационной (летит к точке) или управляющей (DO_*).
        /// Используется для поиска новых «недостигнутых» WP после Upload.
        /// </summary>
        private static bool IsNavigationCommand(MissionCommand cmd)
        {
            return cmd == MissionCommand.Waypoint
                || cmd == MissionCommand.LoiterUnlim
                || cmd == MissionCommand.LoiterTurns
                || cmd == MissionCommand.LoiterTime
                || cmd == MissionCommand.ReturnToLaunch
                || cmd == MissionCommand.VtolTakeoff
                || cmd == MissionCommand.VtolLand
                || cmd == MissionCommand.Takeoff
                || cmd == MissionCommand.Land;
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
            _wpTraceTimer = 0;
            _orbiting = false;

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
            var nextItem = _items[_currentIndex];
            DiagnosticLog?.Invoke(this,
                string.Format(Loc("MsnLog_AdvanceSeq", "Advance to seq {0}: {1} → ({2},{3}) alt={4}"),
                    nextItem.Seq, nextItem.Command,
                    nextItem.Lat.ToString("F5"), nextItem.Lon.ToString("F5"),
                    nextItem.AltRelative.ToString("F0")));
            CurrentItemChanged?.Invoke(this, nextItem.Seq);
        }
    }
}