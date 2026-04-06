using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Net;
using System.Net.Sockets;
using System.Threading;

namespace SimpleDroneGCS.Simulator
{
    /// <summary>
    /// Core simulator: UDP server on :14551, sends telemetry to GCS on :14550.
    /// MAVLinkService is NOT touched — simulator is fully independent.
    /// </summary>
    public class SimulatedDrone : IDisposable
    {
        // ─── Events ───────────────────────────────────────────────────────────────
        public event Action<string>? LogMessage;
        public event Action? StateChanged;

        // ─── Public state ─────────────────────────────────────────────────────────
        public bool IsRunning { get; private set; }
        public bool IsGcsConnected { get; private set; }
        public SimPhysics Physics => _physics;

        // ─── Private ──────────────────────────────────────────────────────────────
        private readonly SimPhysics _physics;
        private readonly ConcurrentQueue<SimCommand> _commandQueue = new();
        private readonly object _sendLock = new();

        private UdpClient? _udp;
        private IPEndPoint _gcsEndPoint;
        private Thread? _receiveThread;
        private Timer? _physicsTimer;
        private Timer? _telemetryTimer;
        private Timer? _heartbeatTimer;
        private volatile bool _running;
        private int _physicsRunning;

        // Mission upload state (receive thread only)
        private int _uploadCount;
        private int _expectedSeq;
        private List<SimWaypoint> _uploadBuffer = new();
        private bool _uploadInProgress;

        // Partial update state
        private bool _partialInProgress;
        private int _partialStartIdx;
        private int _partialEndIdx;

        private const int SimPort = 14551;
        private const int GcsPort = 14550;

        public SimulatedDrone(double homeLat = 43.238949, double homeLon = 76.889709,
                               double homeAltMsl = 682.0)
        {
            _physics = new SimPhysics(homeLat, homeLon, homeAltMsl);
            _gcsEndPoint = new IPEndPoint(IPAddress.Loopback, GcsPort);
            _physics.StatusTextEvent += text =>
            {
                Send(SimMAVLink.StatusText(6, text));
                Log($"[STATUS] {text}");
                StateChanged?.Invoke();
            };
        }

        // ─── Lifecycle ────────────────────────────────────────────────────────────

        public void Start()
        {
            if (_running) return;
            _running = true;
            IsRunning = true;

            _udp = new UdpClient(SimPort);
            Log($"[UDP] Сервер запущен на :{SimPort}");

            _receiveThread = new Thread(ReceiveLoop) { IsBackground = true, Name = "SimReceive" };
            _receiveThread.Start();

            _physicsTimer = new Timer(PhysicsTick, null, 0, 50);
            _telemetryTimer = new Timer(TelemetryTick, null, 200, 100);
            _heartbeatTimer = new Timer(HeartbeatTick, null, 500, 1000);
        }

        public void Stop()
        {
            if (!_running) return;
            _running = false;
            IsRunning = false;
            IsGcsConnected = false;

            _physicsTimer?.Dispose();
            _telemetryTimer?.Dispose();
            _heartbeatTimer?.Dispose();

            try { _udp?.Close(); } catch { }
            _udp = null;

            Log("[UDP] Сервер остановлен");
            StateChanged?.Invoke();
        }

        public void Dispose() => Stop();

        // ─── Timer callbacks ──────────────────────────────────────────────────────

        private void PhysicsTick(object? _)
        {
            if (Interlocked.CompareExchange(ref _physicsRunning, 1, 0) != 0) return;
            try { _physics.Tick(0.05, _commandQueue); }
            finally { Interlocked.Exchange(ref _physicsRunning, 0); }
        }

        private void TelemetryTick(object? _)
        {
            // Send telemetry always — GCS needs it even before we know its port
            if (!_running) return;

            var p = _physics;
            Send(SimMAVLink.GlobalPositionInt(p.Lat, p.Lon, p.AltMsl, p.AltRel, p.Speed, p.Heading));
            Send(SimMAVLink.Attitude(p.Roll, p.Pitch, (float)(p.Heading * Math.PI / 180.0)));
            Send(SimMAVLink.GpsRawInt(p.Lat, p.Lon, p.AltMsl, p.Speed, p.Heading, p.GpsFixType, p.SatCount));
            Send(SimMAVLink.SysStatus(p.BattPct, p.Voltage));
            Send(SimMAVLink.BatteryStatus(p.BattPct, p.Voltage, 5.0));
            Send(SimMAVLink.VfrHud(p.Speed, p.AltRel, p.ClimbRate, p.Heading,
                p.Armed ? (int)Math.Clamp(p.Speed / p.CruiseSpeed * 60 + 20, 0, 100) : 0));

            if (p.State == SimState.Mission && p.CurrentWpIndex >= 0)
            {
                Send(SimMAVLink.MissionCurrent((ushort)p.CurrentWpIndex));
                // NAV_CONTROLLER_OUTPUT — target heading для отображения в GCS
                Send(SimMAVLink.NavControllerOutput(
                    navBearing: p.NavBearing,
                    targetBearing: p.NavBearing,
                    wpDist: 0));
            }

            StateChanged?.Invoke();
        }

        private void HeartbeatTick(object? _)
        {
            if (!_running) return;
            Send(SimMAVLink.Heartbeat(_physics.Armed, _physics.FlightMode));
        }

        // ─── UDP receive ──────────────────────────────────────────────────────────

        private void ReceiveLoop()
        {
            var remoteEP = new IPEndPoint(IPAddress.Any, 0);
            while (_running)
            {
                try
                {
                    byte[] data = _udp!.Receive(ref remoteEP);
                    _gcsEndPoint = remoteEP;

                    var cmd = SimMAVLink.ParsePacket(data, data.Length);

                    // Any packet from GCS = it's connected; send telemetry burst immediately
                    if (!IsGcsConnected)
                    {
                        IsGcsConnected = true;
                        Log($"[GCS] Подключён: {remoteEP}");
                        var p = _physics;
                        Send(SimMAVLink.SysStatus(p.BattPct, p.Voltage));
                        Send(SimMAVLink.BatteryStatus(p.BattPct, p.Voltage, 5.0));
                        Send(SimMAVLink.GpsRawInt(p.Lat, p.Lon, p.AltMsl,
                             p.Speed, p.Heading, p.GpsFixType, p.SatCount));
                        StateChanged?.Invoke();
                    }

                    if (cmd == null) continue;
                    HandlePacket(cmd);
                }
                catch (SocketException) when (!_running) { break; }
                catch (ObjectDisposedException) { break; }
                catch (Exception ex) { Log($"[ERR] Receive: {ex.Message}"); }
            }
        }

        private void HandlePacket(SimCommand cmd)
        {
            switch (cmd.Type)
            {
                case SimCommandType.GcsHeartbeat:
                    break; // already handled in ReceiveLoop

                case SimCommandType.MissionCount:
                    BeginMissionUpload(cmd.MissionCount);
                    break;

                case SimCommandType.MissionWritePartialList:
                    // Partial update: запрашиваем только изменённые WP
                    _partialStartIdx = cmd.WpSeq;
                    _partialEndIdx = cmd.MissionCount;
                    _expectedSeq = cmd.WpSeq;
                    _partialInProgress = true;
                    Log($"[MISSION] Partial update seq={_partialStartIdx}..{_partialEndIdx}");
                    Send(SimMAVLink.MissionRequestInt((ushort)_partialStartIdx));
                    break;

                case SimCommandType.MissionItemInt when _partialInProgress:
                    ReceivePartialMissionItem(cmd.Waypoint!);
                    break;

                case SimCommandType.MissionItemInt when _uploadInProgress:
                    ReceiveMissionItem(cmd.Waypoint!);
                    break;

                case SimCommandType.MissionClearAll:
                    _commandQueue.Enqueue(cmd);
                    Log("[MISSION] Миссия очищена");
                    break;

                case SimCommandType.Arm:
                    Send(SimMAVLink.CommandAck(400, 0));
                    _commandQueue.Enqueue(cmd);
                    Log("[CMD] ARM");
                    break;

                case SimCommandType.Disarm:
                    Send(SimMAVLink.CommandAck(400, 0));
                    _commandQueue.Enqueue(cmd);
                    Log("[CMD] DISARM");
                    break;

                case SimCommandType.SetMode:
                    _commandQueue.Enqueue(cmd);
                    Log($"[CMD] SET_MODE → {cmd.ModeName}");
                    break;

                case SimCommandType.MissionStart:
                    Send(SimMAVLink.CommandAck(300, 0));
                    _commandQueue.Enqueue(cmd);
                    Log("[CMD] MISSION_START");
                    break;

                case SimCommandType.SetCurrentWaypoint:
                    _commandQueue.Enqueue(cmd);
                    Log($"[CMD] SET_CURRENT_WP → {cmd.WpSeq}");
                    break;
            }
        }

        // ─── Mission upload handshake ─────────────────────────────────────────────

        private void BeginMissionUpload(int count)
        {
            if (count <= 0) { Send(SimMAVLink.MissionAck(0)); return; }
            _uploadCount = count;
            _expectedSeq = 0;
            _uploadBuffer = new List<SimWaypoint>(count);
            _uploadInProgress = true;
            _partialInProgress = false; // сбрасываем partial если был активен
            _physics.UploadInProgress = true;
            Log($"[MISSION] Начало загрузки: {count} WP");
            Send(SimMAVLink.MissionRequestInt(0));
        }

        private void ReceiveMissionItem(SimWaypoint wp)
        {
            if (wp.Seq != _expectedSeq)
            {
                Send(SimMAVLink.MissionRequestInt((ushort)_expectedSeq));
                return;
            }
            _uploadBuffer.Add(wp);
            _expectedSeq++;

            if (_expectedSeq < _uploadCount)
            {
                Send(SimMAVLink.MissionRequestInt((ushort)_expectedSeq));
            }
            else
            {
                _uploadInProgress = false;
                Send(SimMAVLink.MissionAck(0));
                _physics.SetMission(_uploadBuffer);
                _physics.UploadInProgress = false;
                Log($"[MISSION] Загрузка завершена: {_uploadBuffer.Count} WP принято");
                StateChanged?.Invoke();
            }
        }

        // ─── Send ──────────────────────────────────────────────────────────────────

        private void ReceivePartialMissionItem(SimWaypoint wp)
        {
            // Обновляем только конкретный WP — без остановки миссии
            _physics.UpdateWaypointAt(wp.Seq, wp);

            if (wp.Seq < _partialEndIdx)
            {
                Send(SimMAVLink.MissionRequestInt((ushort)(wp.Seq + 1)));
            }
            else
            {
                _partialInProgress = false;
                Send(SimMAVLink.MissionAck(0));
                Log($"[MISSION] Partial update done: seq={_partialStartIdx}..{_partialEndIdx}");
                StateChanged?.Invoke();
            }
        }

        private void Send(byte[] data)
        {
            if (_udp == null || !_running) return;
            try
            {
                lock (_sendLock)
                    _udp.Send(data, data.Length, _gcsEndPoint);
            }
            catch (SocketException ex) when (_running)
            {
                // 10054 = WSAECONNRESET — GCS not listening yet, suppress
                if (ex.ErrorCode != 10054)
                    Log($"[ERR] Send: {ex.Message}");
            }
        }

        private void Log(string msg) =>
            LogMessage?.Invoke($"{DateTime.Now:HH:mm:ss}  {msg}");

        // ─── Configuration ────────────────────────────────────────────────────────

        public void SetCruiseSpeed(double mps) => _physics.CruiseSpeed = mps;

        public void SetScenario(string scenario)
        {
            _physics.ScenarioGpsLoss = scenario == "Потеря GPS";
            _physics.ScenarioBattDrain = scenario == "Разряд батареи";
            _physics.ScenarioRcFailsafe = scenario == "RC Failsafe";
        }
    }
}