using System;
using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Threading;
using SimpleDroneGCS.Simulator.Core;

namespace SimpleDroneGCS.Simulator.Mavlink
{
    /// <summary>
    /// UDP-мост между симулятором и GCS.
    /// <para>
    /// Слушает <see cref="ListenPort"/>. Первый входящий пакет фиксирует endpoint
    /// GCS (discovery). Rate-limited scheduler выдаёт периодические пакеты
    /// (HEARTBEAT 1 Hz, ATTITUDE 10 Hz, ...) из <see cref="Tick"/>.
    /// </para>
    /// <para>
    /// Не-периодические пакеты (COMMAND_ACK, STATUSTEXT, MISSION_ITEM_INT и т. п.)
    /// отправляются через <see cref="SendImmediate"/> из обработчиков inbound-событий.
    /// </para>
    /// </summary>
    public sealed class MavlinkBridge : IDisposable
    {
        // =====================================================================
        // Расписание периодической телеметрии
        // =====================================================================

        private enum PeriodicMsg
        {
            Heartbeat,
            SysStatus,
            SystemTime,
            Attitude,
            GlobalPositionInt,
            GpsRawInt,
            VfrHud,
            ServoOutputRaw,
            BatteryStatus,
            NavControllerOutput,
            HomePosition,
            ExtendedSysState,
            Vibration,
            Wind,
            EkfStatusReport,
            EstimatorStatus,
            RadioStatus,
        }

        private struct Schedule
        {
            public PeriodicMsg Msg;
            public int PeriodMs;
            public long NextSendMs;
        }

        // =====================================================================
        // Поля
        // =====================================================================

        /// <summary>Порт, на котором слушает симулятор.</summary>
        public int ListenPort { get; }

        /// <summary>Таймаут бездействия GCS, после которого считаем "disconnected", мс.</summary>
        public const int GcsTimeoutMs = 3000;

        private readonly MavlinkOutbound _outbound;
        private readonly MavlinkInbound _inbound;

        private readonly object _lifecycleLock = new();
        private readonly object _endpointLock = new();
        private readonly object _sendLock = new();

        private UdpClient _udp;
        private CancellationTokenSource _cts;
        private Thread _recvThread;
        private bool _running;

        private IPEndPoint _gcsEndpoint;
        private long _lastHeartbeatRxMs;
        private bool _gcsConnected;

        private readonly Stopwatch _wall = Stopwatch.StartNew();
        private Schedule[] _schedules;

        // =====================================================================
        // События
        // =====================================================================

        /// <summary>GCS обнаружена/переподключилась. Передаётся её endpoint.</summary>
        public event EventHandler<IPEndPoint> GcsConnected;

        /// <summary>Нет HEARTBEAT от GCS более <see cref="GcsTimeoutMs"/>.</summary>
        public event EventHandler GcsDisconnected;

        // =====================================================================
        // Свойства
        // =====================================================================

        /// <summary>Доступ к inbound-парсеру (подписка на события команд).</summary>
        public MavlinkInbound Inbound => _inbound;

        /// <summary>Доступ к outbound-builder'у (для SendImmediate).</summary>
        public MavlinkOutbound Outbound => _outbound;

        /// <summary>Bridge запущен.</summary>
        public bool IsRunning { get { lock (_lifecycleLock) return _running; } }

        /// <summary>GCS считается подключённой (шлёт HEARTBEAT'ы).</summary>
        public bool IsGcsConnected { get { lock (_endpointLock) return _gcsConnected; } }

        // =====================================================================
        // Constructor
        // =====================================================================

        public MavlinkBridge(int listenPort = 14551,
            MavlinkOutbound outbound = null,
            MavlinkInbound inbound = null)
        {
            ListenPort = listenPort;
            _outbound = outbound ?? new MavlinkOutbound();
            _inbound = inbound ?? new MavlinkInbound();
            InitSchedules();
        }

        private void InitSchedules()
        {
            _schedules = new[]
            {
                new Schedule { Msg = PeriodicMsg.Heartbeat,           PeriodMs = 1000 },
                new Schedule { Msg = PeriodicMsg.SysStatus,           PeriodMs = 500 },
                new Schedule { Msg = PeriodicMsg.SystemTime,          PeriodMs = 1000 },
                new Schedule { Msg = PeriodicMsg.Attitude,            PeriodMs = 100 },
                new Schedule { Msg = PeriodicMsg.GlobalPositionInt,   PeriodMs = 200 },
                new Schedule { Msg = PeriodicMsg.GpsRawInt,           PeriodMs = 200 },
                new Schedule { Msg = PeriodicMsg.VfrHud,              PeriodMs = 100 },
                new Schedule { Msg = PeriodicMsg.ServoOutputRaw,      PeriodMs = 100 },
                new Schedule { Msg = PeriodicMsg.BatteryStatus,       PeriodMs = 1000 },
                new Schedule { Msg = PeriodicMsg.NavControllerOutput, PeriodMs = 200 },
                new Schedule { Msg = PeriodicMsg.HomePosition,        PeriodMs = 5000 },
                new Schedule { Msg = PeriodicMsg.ExtendedSysState,    PeriodMs = 1000 },
                new Schedule { Msg = PeriodicMsg.Vibration,           PeriodMs = 1000 },
                new Schedule { Msg = PeriodicMsg.Wind,                PeriodMs = 1000 },
                new Schedule { Msg = PeriodicMsg.EkfStatusReport,     PeriodMs = 1000 },
                new Schedule { Msg = PeriodicMsg.EstimatorStatus,     PeriodMs = 1000 },
                new Schedule { Msg = PeriodicMsg.RadioStatus,         PeriodMs = 1000 },
            };
        }

        // =====================================================================
        // Lifecycle
        // =====================================================================

        /// <summary>
        /// Запустить сокет и receive-поток. Идемпотентно при повторном старте —
        /// бросит исключение.
        /// </summary>
        public void Start()
        {
            lock (_lifecycleLock)
            {
                if (_running)
                    throw new InvalidOperationException("MavlinkBridge уже запущен.");

                _udp = new UdpClient(ListenPort);
                _udp.Client.ReceiveTimeout = 500; // для корректного выхода из цикла
                _cts = new CancellationTokenSource();
                _running = true;

                _recvThread = new Thread(ReceiveLoop)
                {
                    IsBackground = true,
                    Name = "MavlinkBridge.Receive",
                };
                _recvThread.Start(_cts.Token);

                // Инициализируем расписание от текущего времени.
                long now = _wall.ElapsedMilliseconds;
                for (int i = 0; i < _schedules.Length; i++)
                    _schedules[i].NextSendMs = now + _schedules[i].PeriodMs;
            }
        }

        /// <summary>Остановить. Безопасно повторно.</summary>
        public void Stop()
        {
            UdpClient udp;
            CancellationTokenSource cts;
            Thread thr;

            lock (_lifecycleLock)
            {
                if (!_running) return;
                udp = _udp;
                cts = _cts;
                thr = _recvThread;
                _udp = null;
                _cts = null;
                _recvThread = null;
                _running = false;
            }

            try { cts?.Cancel(); } catch { }
            try { udp?.Close(); } catch { }

            if (thr != null && thr != Thread.CurrentThread)
            {
                try { thr.Join(2000); } catch { }
            }

            try { udp?.Dispose(); } catch { }
            try { cts?.Dispose(); } catch { }

            lock (_endpointLock)
            {
                _gcsEndpoint = null;
                _gcsConnected = false;
            }
        }

        public void Dispose() => Stop();

        // =====================================================================
        // Receive loop
        // =====================================================================

        private void ReceiveLoop(object state)
        {
            var token = (CancellationToken)state;
            var anyEp = new IPEndPoint(IPAddress.Any, 0);

            while (!token.IsCancellationRequested)
            {
                byte[] data;
                IPEndPoint from;
                try
                {
                    // Блокирующий receive с таймаутом (ReceiveTimeout = 500 мс).
                    data = _udp.Receive(ref anyEp);
                    from = anyEp;
                }
                catch (SocketException se) when (se.SocketErrorCode == SocketError.TimedOut)
                {
                    // Нормально: периодически проверяем cancellation + таймаут GCS.
                    CheckGcsTimeout();
                    continue;
                }
                catch (ObjectDisposedException) { break; }
                catch (SocketException) { break; }
                catch { continue; }

                // Endpoint discovery / tracking.
                UpdateGcsEndpoint(from);

                // Парсим.
                try { _inbound.Feed(data, 0, data.Length); }
                catch { /* плохой пакет — игнор, не валим поток */ }

                CheckGcsTimeout();
            }
        }

        /// <summary>
        /// Запомнить/обновить endpoint GCS. При смене endpoint — эмитит GcsConnected.
        /// </summary>
        private void UpdateGcsEndpoint(IPEndPoint from)
        {
            bool emitConnected = false;
            IPEndPoint toReport = null;

            lock (_endpointLock)
            {
                _lastHeartbeatRxMs = _wall.ElapsedMilliseconds;

                if (_gcsEndpoint == null ||
                    !_gcsEndpoint.Address.Equals(from.Address) ||
                    _gcsEndpoint.Port != from.Port)
                {
                    _gcsEndpoint = new IPEndPoint(from.Address, from.Port);
                    if (!_gcsConnected)
                    {
                        _gcsConnected = true;
                        emitConnected = true;
                        toReport = _gcsEndpoint;
                    }
                    else
                    {
                        // Endpoint сменился, но считаем ещё connected — просто
                        // перенаправим. Re-emit чтобы UI показал новый IP.
                        emitConnected = true;
                        toReport = _gcsEndpoint;
                    }
                }
                else if (!_gcsConnected)
                {
                    _gcsConnected = true;
                    emitConnected = true;
                    toReport = _gcsEndpoint;
                }
            }

            if (emitConnected) GcsConnected?.Invoke(this, toReport);
        }

        /// <summary>Проверить, не пропал ли GCS (нет HB > timeout).</summary>
        private void CheckGcsTimeout()
        {
            bool emitDisconnected = false;
            lock (_endpointLock)
            {
                if (_gcsConnected &&
                    _wall.ElapsedMilliseconds - _lastHeartbeatRxMs > GcsTimeoutMs)
                {
                    _gcsConnected = false;
                    emitDisconnected = true;
                }
            }
            if (emitDisconnected) GcsDisconnected?.Invoke(this, EventArgs.Empty);
        }

        // =====================================================================
        // Send
        // =====================================================================

        /// <summary>
        /// Немедленно отправить пакет в текущий endpoint GCS.
        /// Если endpoint ещё не обнаружен — тихо игнорируется.
        /// </summary>
        public void SendImmediate(byte[] packet)
        {
            if (packet == null || packet.Length == 0) return;

            IPEndPoint ep;
            lock (_endpointLock) { ep = _gcsEndpoint; }
            if (ep == null) return;

            UdpClient udp;
            lock (_lifecycleLock) { udp = _udp; if (!_running) return; }
            if (udp == null) return;

            try
            {
                lock (_sendLock) udp.Send(packet, packet.Length, ep);
            }
            catch { /* закрытие сокета во время отправки — игнор */ }
        }

        // =====================================================================
        // Tick — периодическая телеметрия
        // =====================================================================

        /// <summary>
        /// Вызывается из SimClock. Проходит по расписанию и отправляет пакеты,
        /// у которых подошло время. Если GCS не обнаружена — пакеты не строятся
        /// (экономит CPU).
        /// </summary>
        public void Tick(SimState state)
        {
            if (state == null) return;

            IPEndPoint ep;
            lock (_endpointLock) { ep = _gcsEndpoint; }
            if (ep == null) return;

            long now = _wall.ElapsedMilliseconds;
            SimState snap = null; // ленивое снятие

            for (int i = 0; i < _schedules.Length; i++)
            {
                if (now < _schedules[i].NextSendMs) continue;
                _schedules[i].NextSendMs = now + _schedules[i].PeriodMs;

                snap ??= state.Snapshot();
                byte[] pkt = BuildPeriodic(_schedules[i].Msg, snap);
                if (pkt != null) SendImmediate(pkt);
            }
        }

        private byte[] BuildPeriodic(PeriodicMsg msg, SimState s) => msg switch
        {
            PeriodicMsg.Heartbeat => _outbound.BuildHeartbeat(s),
            PeriodicMsg.SysStatus => _outbound.BuildSysStatus(s),
            PeriodicMsg.SystemTime => _outbound.BuildSystemTime(),
            PeriodicMsg.Attitude => _outbound.BuildAttitude(s),
            PeriodicMsg.GlobalPositionInt => _outbound.BuildGlobalPositionInt(s),
            PeriodicMsg.GpsRawInt => _outbound.BuildGpsRawInt(s),
            PeriodicMsg.VfrHud => _outbound.BuildVfrHud(s),
            PeriodicMsg.ServoOutputRaw => _outbound.BuildServoOutputRaw(s),
            PeriodicMsg.BatteryStatus => _outbound.BuildBatteryStatus(s),
            PeriodicMsg.NavControllerOutput => _outbound.BuildNavControllerOutput(s),
            PeriodicMsg.HomePosition => _outbound.BuildHomePosition(s),
            PeriodicMsg.ExtendedSysState => _outbound.BuildExtendedSysState(s),
            PeriodicMsg.Vibration => _outbound.BuildVibration(s),
            PeriodicMsg.Wind => _outbound.BuildWind(s),
            PeriodicMsg.EkfStatusReport => _outbound.BuildEkfStatusReport(s),
            PeriodicMsg.EstimatorStatus => _outbound.BuildEstimatorStatus(s),
            PeriodicMsg.RadioStatus => _outbound.BuildRadioStatus(s),
            _ => null,
        };
    }
}