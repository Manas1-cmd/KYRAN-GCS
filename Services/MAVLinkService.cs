using SimpleDroneGCS.Models;
using SimpleDroneGCS.Views;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO.Ports;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Threading;
using static GMap.NET.Entity.OpenStreetMapRouteEntity;
using static MAVLink;
using System.Net;
using System.Net.Sockets;

using static SimpleDroneGCS.Helpers.Loc;

namespace SimpleDroneGCS.Services
{
    
    public class MAVLinkService
    {
        private SerialPort _serialPort;
        
        private System.Net.Sockets.UdpClient _udpClient;
        private System.Net.IPEndPoint _remoteEndPoint;
        private bool _isUdpMode;
        private CancellationTokenSource _udpCts;
        private bool _udpWaitingForFirstPacket; 
        private bool _heartbeatReceived = false; 
        
        private CancellationTokenSource _cts;
        private MavlinkParse _parser;
        private byte _packetSequence = 0;
        private DispatcherTimer _heartbeatTimer;
        private DispatcherTimer _telemetryRequestTimer;
        private DateTime _connectionStartTime = DateTime.MinValue;
        
        private List<WaypointItem> _plannedMission = null;
        public bool HasPlannedMission => _plannedMission != null && _plannedMission.Count > 0;
        public int PlannedMissionCount => _plannedMission?.Count ?? 0;

        private List<WaypointItem> _activeMission = null;
        public bool HasActiveMission => _activeMission != null && _activeMission.Count > 0;
        public List<WaypointItem> ActiveMission => _activeMission;

        private List<MAVLink.mavlink_mission_item_int_t> _missionItemsToUpload;
        private TaskCompletionSource<bool> _missionUploadTcs;
        private int _missionUploadExpectedSeq = -1;
        private DateTime _missionUploadStartTime;

        private List<MAVLink.mavlink_mission_item_int_t> _downloadedMissionItems;
        private TaskCompletionSource<List<MAVLink.mavlink_mission_item_int_t>> _missionDownloadTcs;
        private int _missionDownloadExpectedCount = 0;
        private bool _isDownloading = false;

        public void SetActiveMission(List<WaypointItem> mission)
        {
            _activeMission = mission != null ? new List<WaypointItem>(mission) : null;
            System.Diagnostics.Debug.WriteLine($"✅ Активная миссия установлена: {_activeMission?.Count ?? 0} точек");
        }

        public bool IsConnected { get; private set; }
        public Telemetry CurrentTelemetry { get; private set; }
        public DroneStatus DroneStatus { get; private set; }

        public double? HomeLat { get; private set; }
        public double? HomeLon { get; private set; }
        public double? HomeAlt { get; private set; }
        public bool HasHomePosition => HomeLat.HasValue && HomeLon.HasValue;

        public event EventHandler<Telemetry> TelemetryUpdated;
        public event EventHandler<string> ConnectionStatusChanged;
        public event EventHandler<string> ErrorOccurred;
        public event EventHandler<bool> ConnectionStatusChanged_Bool;
        public event EventHandler TelemetryReceived;
        public event EventHandler<string> MessageReceived; 
        public event Action<string> OnStatusTextReceived;
        public event EventHandler<int> MissionProgressUpdated;
        
        public event Action<byte, byte, byte[], float, float, float> OnMagCalProgress;
        
        public event Action<byte, byte, float, float, float, float> OnMagCalReport;
        
        public int CurrentMissionSeq { get; private set; } = 0;

        public long TotalBytesReceived { get; private set; }
        public long TotalPacketsReceived { get; private set; }
        public long TotalPacketsSent { get; private set; }
        public long PacketErrors { get; private set; }

        private const byte GCS_SYSTEM_ID = 255;
        private const byte GCS_COMPONENT_ID = 190;
        private const int HEARTBEAT_INTERVAL_MS = 1000;
        private const int TELEMETRY_REQUEST_INTERVAL_MS = 5000;

        public MAVLinkService()
        {
            CurrentTelemetry = new Telemetry();
            DroneStatus = new DroneStatus();
            _parser = new MavlinkParse();
        }

        public bool ConnectUDP(string localIp, int localPort)
        {
            try
            {
                Disconnect();

                var localEndPoint = new IPEndPoint(IPAddress.Parse(localIp), localPort);
                _udpClient = new UdpClient(localEndPoint);
                _isUdpMode = true;
                _udpWaitingForFirstPacket = true; 
                IsConnected = true;
                _connectionStartTime = DateTime.Now;
                DroneStatus.IsConnected = true;
                DroneStatus.ConnectionPort = $"UDP:{localIp}:{localPort}";

                _udpCts = new CancellationTokenSource();
                Task.Run(() => ReadLoopUDP(_udpCts.Token));

                StartHeartbeatTimer();
                StartTelemetryRequestTimer();

                StartConnectionTimeoutCheck();

                ConnectionStatusChanged?.Invoke(this, Get("Status_UdpWaiting"));

                Debug.WriteLine($"[UDP] ✅ Server mode: listening on {localIp}:{localPort}");
                return true;
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[UDP] ❌ Error: {ex.Message}");
                ErrorOccurred?.Invoke(this, $"{Get("Msg_UdpError")}: {ex.Message}");
                return false;
            }
        }

        public bool ConnectUDP(string localIp, int localPort, string hostIp, int hostPort)
        {
            try
            {
                Disconnect();

                var localEndPoint = new IPEndPoint(IPAddress.Parse(localIp), localPort);
                _udpClient = new UdpClient(localEndPoint);
                _remoteEndPoint = new IPEndPoint(IPAddress.Parse(hostIp), hostPort);
                _isUdpMode = true;
                _udpWaitingForFirstPacket = false; 
                IsConnected = true;
                _connectionStartTime = DateTime.Now;
                DroneStatus.IsConnected = true;
                DroneStatus.ConnectionPort = $"UDP:{hostIp}:{hostPort}";

                _udpCts = new CancellationTokenSource();
                Task.Run(() => ReadLoopUDP(_udpCts.Token));

                StartHeartbeatTimer();
                StartTelemetryRequestTimer();

                StartConnectionTimeoutCheck();

                ConnectionStatusChanged?.Invoke(this, Get("Status_ConnectedUdp"));

                Debug.WriteLine($"[UDP] ✅ Client mode: {hostIp}:{hostPort}, local: {localIp}:{localPort}");
                return true;
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[UDP] ❌ Error: {ex.Message}");
                ErrorOccurred?.Invoke(this, $"{Get("Msg_UdpError")}: {ex.Message}");
                return false;
            }
        }

        private void StartConnectionTimeoutCheck()
        {
            Task.Delay(10000).ContinueWith(_ =>
            {
                if (IsConnected && DroneStatus.LastHeartbeat == DateTime.MinValue)
                {
                    System.Windows.Application.Current?.Dispatcher?.Invoke(() =>
                    {
                        Debug.WriteLine("[UDP] ⚠️ Нет ответа от дрона за 10 секунд");
                        ErrorOccurred?.Invoke(this, Get("Msg_NoResponse"));
                    });
                }
            });
        }

        private async Task ReadLoopUDP(CancellationToken ct)
        {
            Debug.WriteLine("[UDP] ReadLoop started");

            while (!ct.IsCancellationRequested && _udpClient != null)
            {
                try
                {
                    var result = await _udpClient.ReceiveAsync(ct);

                    _remoteEndPoint = result.RemoteEndPoint;

                    if (_udpWaitingForFirstPacket)
                    {
                        _udpWaitingForFirstPacket = false;
                        Debug.WriteLine($"[UDP] ✅ Получен первый пакет от {result.RemoteEndPoint}");
                        System.Windows.Application.Current?.Dispatcher?.Invoke(() =>
                        {
                            ConnectionStatusChanged?.Invoke(this, Get("Status_ConnectedUdp"));
                        });
                    }

                    TotalBytesReceived += result.Buffer.Length;

                    using (var ms = new System.IO.MemoryStream(result.Buffer))
                    {
                        while (ms.Position < ms.Length)
                        {
                            try
                            {
                                var msg = _parser.ReadPacket(ms);
                                if (msg != null)
                                {
                                    TotalPacketsReceived++;
                                    DroneStatus.PacketsReceived++;
                                    ProcessMessage(msg);
                                }
                            }
                            catch (System.IO.EndOfStreamException)
                            {
                                break;
                            }
                        }
                    }
                }
                catch (OperationCanceledException) { break; }
                catch (ObjectDisposedException) { break; }
                catch (Exception ex)
                {
                    Debug.WriteLine($"[UDP] Read error: {ex.Message}");
                }
            }

            Debug.WriteLine("[UDP] ReadLoop stopped");
        }

        public bool Connect(string portName, int baudRate)
        {
            try
            {
                if (IsConnected)
                {
                    Disconnect();
                }

                _serialPort = new SerialPort(portName, baudRate)
                {
                    Parity = Parity.None,
                    StopBits = StopBits.One,
                    DataBits = 8,
                    Handshake = Handshake.None,
                    ReadTimeout = 500,
                    WriteTimeout = 500,
                    DtrEnable = true,
                    RtsEnable = true
                };

                _serialPort.Open();

                if (!_serialPort.IsOpen)
                {
                    ErrorOccurred?.Invoke(this, Get("Msg_ComPortFailed"));
                    return false;
                }

                Thread.Sleep(1000);

                _cts = new CancellationTokenSource();
                Task.Run(() => ReadLoop(_cts.Token));

                IsConnected = true;
                _connectionStartTime = DateTime.Now; 
                DroneStatus.IsConnected = true;
                DroneStatus.ConnectionPort = portName;
                DroneStatus.BaudRate = baudRate;

                ConnectionStatusChanged?.Invoke(this, Get("Connected"));
                ConnectionStatusChanged_Bool?.Invoke(this, true);

                System.Diagnostics.Debug.WriteLine($"[MAVLink] ✅ Порт {portName} открыт на {baudRate} baud");

                StartHeartbeatTimer();

                StartTelemetryRequestTimer();

                RequestDataStreams();

                return true;
            }
            catch (UnauthorizedAccessException)
            {
                ErrorOccurred?.Invoke(this, Get("Msg_ComPortBusy"));
                return false;
            }
            catch (System.IO.IOException ex)
            {
                ErrorOccurred?.Invoke(this, $"{Get("Msg_PortError")}: {ex.Message}");
                return false;
            }
            catch (Exception ex)
            {
                ErrorOccurred?.Invoke(this, Fmt("Msg_ErrorConnection", ex.Message));
                return false;
            }
        }

        public void Disconnect()
        {
            IsConnected = false;
            _isUdpMode = false;
            _udpWaitingForFirstPacket = false;
            _heartbeatReceived = false;
            _connectionStartTime = DateTime.MinValue;
            DroneStatus.IsConnected = false;
            DroneStatus.LastHeartbeat = DateTime.MinValue;

            _heartbeatTimer?.Stop();
            _telemetryRequestTimer?.Stop();
            _cts?.Cancel();
            _udpCts?.Cancel();

            try { _serialPort?.Close(); _serialPort?.Dispose(); _serialPort = null; } catch { }
            try { _udpClient?.Close(); _udpClient?.Dispose(); _udpClient = null; } catch { }

            _remoteEndPoint = null;

            ConnectionStatusChanged?.Invoke(this, Get("Disconnected"));
            ConnectionStatusChanged_Bool?.Invoke(this, false);

            Debug.WriteLine("[MAVLink] Отключено");
        }

        public TimeSpan GetConnectionTime()
        {
            if (!IsConnected || _connectionStartTime == DateTime.MinValue)
                return TimeSpan.Zero;

            return DateTime.Now - _connectionStartTime;
        }

        public static string[] GetAvailablePorts()
        {
            try
            {
                return SerialPort.GetPortNames().OrderBy(p => p).ToArray();
            }
            catch
            {
                return new string[0];
            }
        }

        private void StartHeartbeatTimer()
        {
            _heartbeatTimer = new System.Windows.Threading.DispatcherTimer
            {
                Interval = TimeSpan.FromMilliseconds(HEARTBEAT_INTERVAL_MS)
            };
            _heartbeatTimer.Tick += (s, e) => SendHeartbeat();
            _heartbeatTimer.Start();
        }

        private void StartTelemetryRequestTimer()
        {
            _telemetryRequestTimer = new System.Windows.Threading.DispatcherTimer
            {
                Interval = TimeSpan.FromMilliseconds(TELEMETRY_REQUEST_INTERVAL_MS)
            };
            _telemetryRequestTimer.Tick += (s, e) => RequestDataStreams();
            _telemetryRequestTimer.Start();
        }

        private byte _vehicleMavType = (byte)MAVLink.MAV_TYPE.QUADROTOR; 

        private void SendHeartbeat()
        {
            if (!IsConnected)
                return;

            if (!_isUdpMode && (_serialPort == null || !_serialPort.IsOpen))
                return;

            if (_isUdpMode && _remoteEndPoint == null)
            {
                
                return;
            }

            try
            {
                var heartbeat = new MAVLink.mavlink_heartbeat_t
                {
                    type = _vehicleMavType,
                    autopilot = (byte)MAVLink.MAV_AUTOPILOT.INVALID,
                    base_mode = 0,
                    custom_mode = 0,
                    system_status = (byte)MAVLink.MAV_STATE.ACTIVE,
                    mavlink_version = 3
                };
                SendMessage(heartbeat, MAVLink.MAVLINK_MSG_ID.HEARTBEAT);
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[MAVLink] Ошибка HEARTBEAT: {ex.Message}");
            }
        }

        public void SetVehicleType(byte mavType)
        {
            _vehicleMavType = mavType;
        }

        private void RequestDataStreams()
        {
            if (!IsConnected || DroneStatus.SystemId == 0)
                return;

            try
            {
                
                RequestDataStream(MAVLink.MAV_DATA_STREAM.ALL, 10, true);
                RequestDataStream(MAVLink.MAV_DATA_STREAM.POSITION, 5, true);
                RequestDataStream(MAVLink.MAV_DATA_STREAM.EXTRA1, 10, true);
                RequestDataStream(MAVLink.MAV_DATA_STREAM.EXTRA2, 10, true);
                RequestDataStream(MAVLink.MAV_DATA_STREAM.EXTRA3, 2, true);
                RequestDataStream(MAVLink.MAV_DATA_STREAM.RAW_SENSORS, 2, true);
                RequestDataStream(MAVLink.MAV_DATA_STREAM.EXTENDED_STATUS, 2, true);

                System.Diagnostics.Debug.WriteLine("[MAVLink] Запрошены потоки телеметрии");
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[MAVLink] Ошибка запроса телеметрии: {ex.Message}");
            }
        }

        private void RequestDataStream(MAVLink.MAV_DATA_STREAM streamId, ushort rate, bool enable)
        {
            var request = new MAVLink.mavlink_request_data_stream_t
            {
                target_system = (byte)DroneStatus.SystemId,
                target_component = (byte)DroneStatus.ComponentId,
                req_stream_id = (byte)streamId,
                req_message_rate = rate,
                start_stop = (byte)(enable ? 1 : 0)
            };

            SendMessage(request, MAVLink.MAVLINK_MSG_ID.REQUEST_DATA_STREAM);
        }

        private async Task ReadLoop(CancellationToken token)
        {
            byte[] buffer = new byte[4096];
            List<byte> dataBuffer = new List<byte>();

            System.Diagnostics.Debug.WriteLine("[MAVLink] ReadLoop started");

            while (!token.IsCancellationRequested && IsConnected)
            {
                try
                {
                    if (_serialPort?.BytesToRead > 0)
                    {
                        int count = _serialPort.Read(buffer, 0, Math.Min(buffer.Length, _serialPort.BytesToRead));
                        TotalBytesReceived += count;

                        for (int i = 0; i < count; i++)
                        {
                            dataBuffer.Add(buffer[i]);
                        }

                        while (dataBuffer.Count > 0)
                        {
                            bool packetFound = false;

                            for (int i = 0; i < dataBuffer.Count; i++)
                            {
                                if (dataBuffer[i] == 0xFD || dataBuffer[i] == 0xFE)
                                {
                                    int minPacketSize = (dataBuffer[i] == 0xFD) ? 12 : 8;

                                    if (dataBuffer.Count - i >= minPacketSize)
                                    {
                                        try
                                        {
                                            byte[] packetData = dataBuffer.Skip(i).ToArray();

                                            using (var ms = new System.IO.MemoryStream(packetData))
                                            {
                                                var msg = _parser.ReadPacket(ms);

                                                if (msg != null)
                                                {
                                                    ProcessMessage(msg);

                                                    int bytesToRemove = (int)ms.Position;
                                                    dataBuffer.RemoveRange(0, i + bytesToRemove);

                                                    packetFound = true;
                                                    TotalPacketsReceived++;
                                                    DroneStatus.PacketsReceived++;
                                                    break;
                                                }
                                            }
                                        }
                                        catch (System.IO.EndOfStreamException)
                                        {
                                            break;
                                        }
                                        catch (Exception ex)
                                        {
                                            System.Diagnostics.Debug.WriteLine($"[MAVLink] Parse error: {ex.Message}");
                                            dataBuffer.RemoveAt(i);
                                            PacketErrors++;
                                            break;
                                        }
                                    }
                                    else
                                    {
                                        break;
                                    }
                                }
                            }

                            if (!packetFound)
                            {
                                if (dataBuffer.Count > 1024)
                                {
                                    dataBuffer.RemoveRange(0, dataBuffer.Count - 512);
                                }
                                break;
                            }
                        }
                    }
                    else
                    {
                        await Task.Delay(10, token);
                    }
                }
                catch (OperationCanceledException)
                {
                    break;
                }
                catch (Exception ex)
                {
                    System.Diagnostics.Debug.WriteLine($"[MAVLink] ReadLoop error: {ex.Message}");
                    await Task.Delay(100, token);
                }
            }

            System.Diagnostics.Debug.WriteLine("[MAVLink] ReadLoop stopped");
        }

        private void ProcessHomePosition(MAVLink.MAVLinkMessage msg)
        {
            var home = (MAVLink.mavlink_home_position_t)msg.data;
            HomeLat = home.latitude / 1e7;
            HomeLon = home.longitude / 1e7;
            HomeAlt = home.altitude / 1000.0; 

            System.Diagnostics.Debug.WriteLine($"[MAVLink] HOME: {HomeLat:F6}, {HomeLon:F6}, Alt: {HomeAlt:F1}m");
        }

        private void ProcessMessage(MAVLink.MAVLinkMessage msg)
        {
            try
            {
                CurrentTelemetry.LastUpdate = DateTime.Now;

                switch ((MAVLink.MAVLINK_MSG_ID)msg.msgid)
                {
                    case MAVLink.MAVLINK_MSG_ID.HEARTBEAT:
                        ProcessHeartbeat(msg);
                        break;

                    case MAVLink.MAVLINK_MSG_ID.ATTITUDE:
                        ProcessAttitude(msg);
                        break;

                    case MAVLink.MAVLINK_MSG_ID.GLOBAL_POSITION_INT:
                        ProcessGlobalPosition(msg);
                        break;

                    case MAVLink.MAVLINK_MSG_ID.GPS_RAW_INT:
                        ProcessGpsRaw(msg);
                        break;

                    case MAVLink.MAVLINK_MSG_ID.VFR_HUD:
                        ProcessVfrHud(msg);
                        break;

                    case MAVLink.MAVLINK_MSG_ID.SYS_STATUS:
                        ProcessSysStatus(msg);
                        break;

                    case MAVLink.MAVLINK_MSG_ID.MISSION_CURRENT:
                        ProcessMissionCurrent(msg);
                        break;

                    case MAVLink.MAVLINK_MSG_ID.STATUSTEXT:
                        ProcessStatusText(msg);
                        break;

                    case MAVLink.MAVLINK_MSG_ID.COMMAND_ACK:
                        ProcessCommandAck(msg);
                        break;

                    case MAVLink.MAVLINK_MSG_ID.RC_CHANNELS:
                        ProcessRcChannels(msg);
                        break;

                    case MAVLink.MAVLINK_MSG_ID.RAW_IMU:
                        ProcessRawImu(msg);
                        break;

                    case MAVLink.MAVLINK_MSG_ID.SCALED_PRESSURE:
                        ProcessScaledPressure(msg);
                        break;

                    case MAVLink.MAVLINK_MSG_ID.BATTERY_STATUS:
                        ProcessBatteryStatus(msg);
                        break;

                    case MAVLink.MAVLINK_MSG_ID.HOME_POSITION:
                        ProcessHomePosition(msg);
                        break;

                    case MAVLink.MAVLINK_MSG_ID.SERVO_OUTPUT_RAW:
                        ProcessServoOutput(msg);
                        break;

                    case MAVLink.MAVLINK_MSG_ID.MISSION_REQUEST_INT:
                        ProcessMissionRequestInt(msg);
                        break;

                    case MAVLink.MAVLINK_MSG_ID.MISSION_REQUEST:
                        ProcessMissionRequest(msg);
                        break;

                    case MAVLink.MAVLINK_MSG_ID.MISSION_ACK:
                        ProcessMissionAck(msg);
                        break;

                    case MAVLink.MAVLINK_MSG_ID.MISSION_COUNT:
                        ProcessMissionCount(msg);
                        break;

                    case MAVLink.MAVLINK_MSG_ID.MISSION_ITEM_INT:
                        ProcessMissionItemInt(msg);
                        break;

                    case (MAVLink.MAVLINK_MSG_ID)191: 
                        ProcessMagCalProgress(msg);
                        break;

                    case (MAVLink.MAVLINK_MSG_ID)192: 
                        ProcessMagCalReport(msg);
                        break;
                }

                TelemetryUpdated?.Invoke(this, CurrentTelemetry);
                TelemetryReceived?.Invoke(this, EventArgs.Empty);
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[MAVLink] Process message error: {ex.Message}");
            }
        }

        private void ProcessHeartbeat(MAVLink.MAVLinkMessage msg)
        {
            var heartbeat = (MAVLink.mavlink_heartbeat_t)msg.data;

            CurrentTelemetry.Armed = (heartbeat.base_mode & (byte)MAVLink.MAV_MODE_FLAG.SAFETY_ARMED) != 0;
            CurrentTelemetry.BaseMode = heartbeat.base_mode;
            CurrentTelemetry.CustomMode = heartbeat.custom_mode;
            CurrentTelemetry.SystemStatus = heartbeat.system_status;

            DroneStatus.SystemId = msg.sysid;
            DroneStatus.ComponentId = msg.compid;
            DroneStatus.Autopilot = heartbeat.autopilot;
            DroneStatus.Type = heartbeat.type;
            DroneStatus.LastHeartbeat = DateTime.Now;

            if (!_heartbeatReceived)
            {
                _heartbeatReceived = true;
                ConnectionStatusChanged?.Invoke(this, Get("Connected"));
                ConnectionStatusChanged_Bool?.Invoke(this, true);
                Debug.WriteLine("[MAVLink] ✅ Первый HEARTBEAT получен - дрон подключен!");
            }

            CurrentTelemetry.FlightMode = GetFlightModeName(heartbeat.custom_mode);
        }

        private void ProcessAttitude(MAVLink.MAVLinkMessage msg)
        {
            var attitude = (MAVLink.mavlink_attitude_t)msg.data;
            CurrentTelemetry.Roll = attitude.roll * (180.0 / Math.PI);
            CurrentTelemetry.Pitch = attitude.pitch * (180.0 / Math.PI);
            CurrentTelemetry.Yaw = attitude.yaw * (180.0 / Math.PI);
        }

        private void ProcessGlobalPosition(MAVLink.MAVLinkMessage msg)
        {
            var pos = (MAVLink.mavlink_global_position_int_t)msg.data;
            CurrentTelemetry.Latitude = pos.lat / 1e7;
            CurrentTelemetry.Longitude = pos.lon / 1e7;
            CurrentTelemetry.Altitude = pos.alt / 1000.0; 
            CurrentTelemetry.RelativeAltitude = pos.relative_alt / 1000.0; 
            CurrentTelemetry.Speed = Math.Sqrt(pos.vx * pos.vx + pos.vy * pos.vy) / 100.0;
            CurrentTelemetry.ClimbRate = -pos.vz / 100.0;
        }

        private void ProcessGpsRaw(MAVLink.MAVLinkMessage msg)
        {
            var gps = (MAVLink.mavlink_gps_raw_int_t)msg.data;
            CurrentTelemetry.SatellitesVisible = gps.satellites_visible;
            CurrentTelemetry.GpsFixType = gps.fix_type;

            if (CurrentTelemetry.Latitude == 0)
                CurrentTelemetry.Latitude = gps.lat / 1e7;
            if (CurrentTelemetry.Longitude == 0)
                CurrentTelemetry.Longitude = gps.lon / 1e7;

            CurrentTelemetry.GpsAltitude = gps.alt / 1000.0;
        }

        private void ProcessVfrHud(MAVLink.MAVLinkMessage msg)
        {
            var hud = (MAVLink.mavlink_vfr_hud_t)msg.data;
            CurrentTelemetry.Airspeed = hud.airspeed;
            CurrentTelemetry.Speed = hud.groundspeed;
            CurrentTelemetry.Altitude = hud.alt;
            CurrentTelemetry.ClimbRate = hud.climb;
            CurrentTelemetry.Heading = hud.heading;
            CurrentTelemetry.Throttle = hud.throttle;
        }

        private void ProcessSysStatus(MAVLink.MAVLinkMessage msg)
        {
            var sys = (MAVLink.mavlink_sys_status_t)msg.data;
            CurrentTelemetry.BatteryVoltage = sys.voltage_battery / 1000.0;
            CurrentTelemetry.BatteryCurrent = sys.current_battery / 100.0;
            CurrentTelemetry.BatteryPercent = sys.battery_remaining;
        }

        private void ProcessMissionCurrent(MAVLink.MAVLinkMessage msg)
        {
            var mission = (MAVLink.mavlink_mission_current_t)msg.data;
            CurrentTelemetry.CurrentWaypoint = mission.seq;
            CurrentMissionSeq = mission.seq;
            MissionProgressUpdated?.Invoke(this, (int)mission.seq);
        }

        private void ProcessMagCalProgress(MAVLink.MAVLinkMessage msg)
        {
            try
            {
                var progress = (MAVLink.mavlink_mag_cal_progress_t)msg.data;
                OnMagCalProgress?.Invoke(
                    progress.compass_id,
                    progress.completion_pct,
                    progress.completion_mask,   
                    progress.direction_x,
                    progress.direction_y,
                    progress.direction_z
                );
                Debug.WriteLine($"[MAG_CAL] Progress: {progress.completion_pct}% compass#{progress.compass_id}");
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[MAG_CAL] Progress parse error: {ex.Message}");
            }
        }

        private void ProcessMagCalReport(MAVLink.MAVLinkMessage msg)
        {
            try
            {
                var report = (MAVLink.mavlink_mag_cal_report_t)msg.data;
                OnMagCalReport?.Invoke(
                    report.compass_id,
                    report.cal_status,   
                    report.fitness,
                    report.ofs_x,
                    report.ofs_y,
                    report.ofs_z
                );
                Debug.WriteLine($"[MAG_CAL] Report: compass#{report.compass_id} status={report.cal_status} fitness={report.fitness:F1}");
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[MAG_CAL] Report parse error: {ex.Message}");
            }
        }

        private void ProcessStatusText(MAVLink.MAVLinkMessage msg)
        {
            var status = (MAVLink.mavlink_statustext_t)msg.data;
            string text = System.Text.Encoding.ASCII.GetString(status.text).TrimEnd('\0');

            System.Diagnostics.Debug.WriteLine($"[DRONE] {text}");

            MessageReceived?.Invoke(this, text);

            OnStatusTextReceived?.Invoke(text);
        }

        private void ProcessCommandAck(MAVLink.MAVLinkMessage msg)
        {
            var ack = (MAVLink.mavlink_command_ack_t)msg.data;

            string commandName = ((MAVLink.MAV_CMD)ack.command).ToString();
            string resultName = ((MAVLink.MAV_RESULT)ack.result).ToString();

            System.Diagnostics.Debug.WriteLine($"[COMMAND_ACK] {commandName} → {resultName}");

            if (ack.command == (ushort)MAVLink.MAV_CMD.COMPONENT_ARM_DISARM)
            {
                if (ack.result == (byte)MAVLink.MAV_RESULT.ACCEPTED)
                {
                    System.Diagnostics.Debug.WriteLine("✅ ARM команда ПРИНЯТА!");
                }
                else if (ack.result == (byte)MAVLink.MAV_RESULT.TEMPORARILY_REJECTED)
                {
                    System.Diagnostics.Debug.WriteLine("⚠️ ARM ВРЕМЕННО ОТКЛОНЕНА - проверьте:");
                    System.Diagnostics.Debug.WriteLine("   • GPS Fix (нужен 3D)");
                    System.Diagnostics.Debug.WriteLine("   • Калибровка завершена");
                    System.Diagnostics.Debug.WriteLine("   • Режим полета подходит");
                }
                else if (ack.result == (byte)MAVLink.MAV_RESULT.DENIED)
                {
                    System.Diagnostics.Debug.WriteLine("❌ ARM ОТКЛОНЕНА - серьезные проблемы безопасности");
                }
                else
                {
                    System.Diagnostics.Debug.WriteLine($"❌ ARM ОТКЛОНЕНА: {resultName} (код {ack.result})");
                }
            }
        }

        private void ProcessServoOutput(MAVLink.MAVLinkMessage msg)
        {
            var servo = (MAVLink.mavlink_servo_output_raw_t)msg.data;

            CurrentTelemetry.Motor1Percent = PwmToPercent(servo.servo5_raw);
            CurrentTelemetry.Motor2Percent = PwmToPercent(servo.servo6_raw);
            CurrentTelemetry.Motor3Percent = PwmToPercent(servo.servo7_raw);
            CurrentTelemetry.Motor4Percent = PwmToPercent(servo.servo8_raw);
            CurrentTelemetry.PusherPercent = PwmToPercent(servo.servo3_raw);
        }

        private int PwmToPercent(ushort pwm)
        {
            if (pwm <= 1000) return 0;
            if (pwm >= 2000) return 100;
            return (pwm - 1000) / 10;
        }

        private void ProcessRcChannels(MAVLink.MAVLinkMessage msg)
        {
            
        }

        private void ProcessRawImu(MAVLink.MAVLinkMessage msg)
        {
            
        }

        private void ProcessScaledPressure(MAVLink.MAVLinkMessage msg)
        {
            
        }

        private void ProcessBatteryStatus(MAVLink.MAVLinkMessage msg)
        {
            var battery = (MAVLink.mavlink_battery_status_t)msg.data;

            if (battery.voltages.Length > 0 && battery.voltages[0] != ushort.MaxValue)
            {
                CurrentTelemetry.BatteryVoltage = battery.voltages[0] / 1000.0;
            }

            CurrentTelemetry.BatteryCurrent = battery.current_battery / 100.0;
            CurrentTelemetry.BatteryPercent = battery.battery_remaining;
        }

        private string GetFlightModeName(uint customMode)
        {
            
            byte mavType = DroneStatus.Type;
            bool isCopter = (mavType == 2 || mavType == 3 || mavType == 4 || 
                            mavType == 13 || mavType == 14 || mavType == 15);
            
            if (mavType == 0)
                isCopter = VehicleManager.Instance.CurrentVehicleType == VehicleType.Copter;

            if (isCopter)
            {
                
                switch (customMode)
                {
                    case 0: return "STABILIZE";
                    case 1: return "ACRO";
                    case 2: return "ALT_HOLD";
                    case 3: return "AUTO";
                    case 4: return "GUIDED";
                    case 5: return "LOITER";
                    case 6: return "RTL";
                    case 7: return "CIRCLE";
                    case 9: return "LAND";
                    case 11: return "DRIFT";
                    case 13: return "SPORT";
                    case 14: return "FLIP";
                    case 15: return "AUTOTUNE";
                    case 16: return "POSHOLD";
                    case 17: return "BRAKE";
                    case 18: return "THROW";
                    case 19: return "AVOID_ADSB";
                    case 20: return "GUIDED_NOGPS";
                    case 21: return "SMART_RTL";
                    default: return $"MODE_{customMode}";
                }
            }
            else 
            {
                
                switch (customMode)
                {
                    case 0: return "MANUAL";
                    case 1: return "CIRCLE";
                    case 2: return "STABILIZE";
                    case 3: return "TRAINING";
                    case 4: return "ACRO";
                    case 5: return "FBWA";
                    case 6: return "FBWB";
                    case 7: return "CRUISE";
                    case 8: return "AUTOTUNE";
                    case 10: return "AUTO";
                    case 11: return "RTL";
                    case 12: return "LOITER";
                    case 13: return "TAKEOFF";
                    case 15: return "GUIDED";
                    case 17: return "QSTABILIZE";
                    case 18: return "QHOVER";
                    case 19: return "QLOITER";
                    case 20: return "QLAND";
                    case 21: return "QRTL";
                    case 22: return "QAUTOTUNE";
                    case 23: return "QACRO";
                    case 25: return "THERMAL";
                    default: return $"MODE_{customMode}";
                }
            }
        }

        public void SendArm()
        {
            SetArm(true);
        }

        public void SendDisarm()
        {
            SetArm(false);
        }

        public void SetArm(bool arm, bool force = false)
        {
            if (!IsConnected)
            {
                ErrorOccurred?.Invoke(this, Get("Msg_DroneNotConnected"));
                return;
            }

            var cmd = new MAVLink.mavlink_command_long_t
            {
                target_system = (byte)DroneStatus.SystemId,
                target_component = (byte)DroneStatus.ComponentId,
                command = (ushort)MAVLink.MAV_CMD.COMPONENT_ARM_DISARM,
                confirmation = 0,
                param1 = arm ? 1 : 0,
                param2 = force ? 21196 : 0,  
                param3 = 0,
                param4 = 0,
                param5 = 0,
                param6 = 0,
                param7 = 0
            };

            SendMessage(cmd, MAVLink.MAVLINK_MSG_ID.COMMAND_LONG);

            System.Diagnostics.Debug.WriteLine($"[MAVLink] {(arm ? "ARM" : "DISARM")}{(force ? " (FORCE)" : "")} отправлено");
        }

        public void ForceArm()
        {
            if (!IsConnected)
            {
                ErrorOccurred?.Invoke(this, Get("Msg_DroneNotConnected"));
                return;
            }

            var cmd = new MAVLink.mavlink_command_long_t
            {
                target_system = (byte)DroneStatus.SystemId,
                target_component = (byte)DroneStatus.ComponentId,
                command = (ushort)MAVLink.MAV_CMD.COMPONENT_ARM_DISARM,
                confirmation = 0,
                param1 = 1, 
                param2 = 21196, 
                param3 = 0,
                param4 = 0,
                param5 = 0,
                param6 = 0,
                param7 = 0
            };

            SendMessage(cmd, MAVLink.MAVLINK_MSG_ID.COMMAND_LONG);

            System.Diagnostics.Debug.WriteLine("[MAVLink] Принудительное ARM отправлено");
        }

        public void Takeoff(double altitude)
        {
            if (!IsConnected)
            {
                ErrorOccurred?.Invoke(this, Get("Msg_DroneNotConnected"));
                return;
            }

            if (!CurrentTelemetry.Armed)
            {
                ErrorOccurred?.Invoke(this, Get("Msg_DroneNotArmed"));
                return;
            }

            var cmd = new MAVLink.mavlink_command_long_t
            {
                target_system = (byte)DroneStatus.SystemId,
                target_component = (byte)DroneStatus.ComponentId,
                command = (ushort)MAVLink.MAV_CMD.TAKEOFF,
                confirmation = 0,
                param1 = 0, 
                param2 = 0, 
                param3 = 0, 
                param4 = 0, 
                param5 = 0, 
                param6 = 0, 
                param7 = (float)altitude 
            };

            SendMessage(cmd, MAVLink.MAVLINK_MSG_ID.COMMAND_LONG);

            System.Diagnostics.Debug.WriteLine($"[MAVLink] Взлёт на {altitude}м отправлен");
        }

        public void Land()
        {
            if (!IsConnected)
            {
                ErrorOccurred?.Invoke(this, Get("Msg_DroneNotConnected"));
                return;
            }

            var cmd = new MAVLink.mavlink_command_long_t
            {
                target_system = (byte)DroneStatus.SystemId,
                target_component = (byte)DroneStatus.ComponentId,
                command = (ushort)MAVLink.MAV_CMD.LAND,
                confirmation = 0,
                param1 = 0, 
                param2 = 0, 
                param3 = 0, 
                param4 = 0, 
                param5 = 0, 
                param6 = 0, 
                param7 = 0 
            };

            SendMessage(cmd, MAVLink.MAVLINK_MSG_ID.COMMAND_LONG);

            System.Diagnostics.Debug.WriteLine("[MAVLink] Посадка отправлена");
        }

        public void ReturnToLaunch()
        {
            if (!IsConnected) return;

            var vehicleType = VehicleManager.Instance.CurrentVehicleType;
            uint rtlMode = (vehicleType == VehicleType.Copter) ? 6u : 11u;

            SetMode(rtlMode);

            System.Diagnostics.Debug.WriteLine($"[MAVLink] RTL режим установлен (mode={rtlMode})");
        }

        public void SetMode(uint mode)
        {
            if (!IsConnected)
            {
                ErrorOccurred?.Invoke(this, Get("Msg_DroneNotConnected"));
                return;
            }

            var setMode = new MAVLink.mavlink_set_mode_t
            {
                target_system = (byte)DroneStatus.SystemId,
                base_mode = (byte)MAVLink.MAV_MODE_FLAG.CUSTOM_MODE_ENABLED,
                custom_mode = mode
            };

            SendMessage(setMode, MAVLink.MAVLINK_MSG_ID.SET_MODE);

            System.Diagnostics.Debug.WriteLine($"[MAVLink] Режим {GetFlightModeName(mode)} установлен");
        }

        public void GoTo(double latitude, double longitude, double altitude)
        {
            if (!IsConnected)
            {
                ErrorOccurred?.Invoke(this, Get("Msg_DroneNotConnected"));
                return;
            }

            SetMode(4);

            var posTarget = new MAVLink.mavlink_set_position_target_global_int_t
            {
                target_system = (byte)DroneStatus.SystemId,
                target_component = (byte)DroneStatus.ComponentId,
                coordinate_frame = (byte)MAVLink.MAV_FRAME.GLOBAL_RELATIVE_ALT_INT,
                type_mask = 0b0000111111111000, 
                lat_int = (int)(latitude * 1e7),
                lon_int = (int)(longitude * 1e7),
                alt = (float)altitude
            };

            SendMessage(posTarget, MAVLink.MAVLINK_MSG_ID.SET_POSITION_TARGET_GLOBAL_INT);

            System.Diagnostics.Debug.WriteLine($"[MAVLink] Полёт в точку: {latitude}, {longitude}, {altitude}м");
        }

        public void Stop()
        {
            if (!IsConnected) return;

            SetMode(17);
        }

        public void SetFlightMode(string modeName)
        {
            if (!IsConnected) return;

            var vehicleType = VehicleManager.Instance.CurrentVehicleType;

            var copterModeMap = new Dictionary<string, uint>
            {
                { "STABILIZE", 0 },
                { "ACRO", 1 },
                { "ALT_HOLD", 2 },
                { "AUTO", 3 },
                { "GUIDED", 4 },
                { "LOITER", 5 },
                { "RTL", 6 },
                { "CIRCLE", 7 },
                { "LAND", 9 },
                { "DRIFT", 11 },
                { "SPORT", 13 },
                { "FLIP", 14 },
                { "AUTOTUNE", 15 },
                { "POSHOLD", 16 },
                { "BRAKE", 17 },
                { "THROW", 18 },
                { "GUIDED_NOGPS", 20 },
                { "SMART_RTL", 21 },
                { "FLOWHOLD", 22 },
                { "FOLLOW", 23 },
                { "ZIGZAG", 24 }
            };

            var planeModeMap = new Dictionary<string, uint>
            {
                { "MANUAL", 0 },
                { "CIRCLE", 1 },
                { "STABILIZE", 2 },
                { "TRAINING", 3 },
                { "ACRO", 4 },
                { "FBWA", 5 },
                { "FBWB", 6 },
                { "CRUISE", 7 },
                { "AUTOTUNE", 8 },
                { "AUTO", 10 },
                { "RTL", 11 },
                { "LOITER", 12 },
                { "TAKEOFF", 13 },
                { "GUIDED", 15 },
                { "QSTABILIZE", 17 },
                { "QHOVER", 18 },
                { "QLOITER", 19 },
                { "QLAND", 20 },
                { "QRTL", 21 },
                { "QAUTOTUNE", 22 },
                { "QACRO", 23 },
                { "THERMAL", 24 }
            };

            var modeMap = (vehicleType == VehicleType.Copter) ? copterModeMap : planeModeMap;

            if (!modeMap.ContainsKey(modeName))
            {
                System.Diagnostics.Debug.WriteLine($"⚠️ Неизвестный режим: {modeName}");
                return;
            }

            uint customMode = modeMap[modeName];

            SetMode(customMode);

            System.Diagnostics.Debug.WriteLine($"[MAVLink] {vehicleType} режим {modeName} (custom_mode={customMode}) отправлен");
        }

        public void SendCommandLong(ushort command,
            float param1 = 0, float param2 = 0, float param3 = 0,
            float param4 = 0, float param5 = 0, float param6 = 0, float param7 = 0)
        {
            if (!IsConnected) return;

            try
            {
                var msg = new MAVLink.mavlink_command_long_t
                {
                    target_system = (byte)DroneStatus.SystemId,
                    target_component = (byte)DroneStatus.ComponentId,
                    command = command,
                    confirmation = 0,
                    param1 = param1,
                    param2 = param2,
                    param3 = param3,
                    param4 = param4,
                    param5 = param5,
                    param6 = param6,
                    param7 = param7
                };

                SendMessage(msg, MAVLink.MAVLINK_MSG_ID.COMMAND_LONG);

                Debug.WriteLine($"[MAVLink] CMD_LONG: {command} p1={param1} p2={param2} p3={param3}");
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[MAVLink] SendCommandLong error: {ex.Message}");
            }
        }

        public void SendPreflightCalibration(bool gyro = false, bool barometer = false,
                                     bool accelerometer = false, bool compassMot = false,
                                     bool radioTrim = false)
        {
            if (!IsConnected) return;

            try
            {
                var msg = new MAVLink.mavlink_command_long_t
                {
                    target_system = (byte)DroneStatus.SystemId,        
                    target_component = (byte)DroneStatus.ComponentId,  
                    command = (ushort)MAVLink.MAV_CMD.PREFLIGHT_CALIBRATION,
                    confirmation = 0,
                    param1 = gyro ? 1 : 0,           
                    param2 = 0,                      
                    param3 = barometer ? 1 : 0,      
                    param4 = radioTrim ? 4 : 0,      
                    param5 = accelerometer ? 1 : 0,  
                    param6 = compassMot ? 1 : 0,     
                    param7 = 0
                };

                SendMessage(msg, MAVLink.MAVLINK_MSG_ID.COMMAND_LONG);

                string calibName = gyro ? "Gyro" :
                                  barometer ? "Barometer/Airspeed" :
                                  accelerometer ? "Accelerometer" :
                                  compassMot ? "CompassMot" :
                                  radioTrim ? "Radio Trim" : "Unknown";

                System.Diagnostics.Debug.WriteLine($"[MAVLink] Preflight calibration started: {calibName}");

                OnStatusTextReceived?.Invoke(Fmt("Msg_CalibStarted", calibName));
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[MAVLink] Calibration error: {ex.Message}");
            }
        }

        public void SendTakeoff(double altitude)
        {
            Takeoff(altitude);
        }

        public void SendLand()
        {
            Land();
        }

        public void SendRTL()
        {
            ReturnToLaunch();
        }

        public void SetParameter(string paramName, float value)
        {
            if (!IsConnected)
            {
                ErrorOccurred?.Invoke(this, Get("Msg_DroneNotConnected"));
                return;
            }

            byte[] paramIdBytes = new byte[16];
            byte[] nameBytes = System.Text.Encoding.ASCII.GetBytes(paramName);
            Array.Copy(nameBytes, paramIdBytes, Math.Min(nameBytes.Length, 16));

            var paramSet = new MAVLink.mavlink_param_set_t
            {
                target_system = (byte)DroneStatus.SystemId,
                target_component = (byte)DroneStatus.ComponentId,
                param_id = paramIdBytes,
                param_value = value,
                param_type = (byte)MAVLink.MAV_PARAM_TYPE.REAL32
            };

            SendMessage(paramSet, MAVLink.MAVLINK_MSG_ID.PARAM_SET);

            System.Diagnostics.Debug.WriteLine($"[MAVLink] PARAM_SET: {paramName} = {value}");
        }

        public void SetVTOLAutoTransition(bool autoTransition)
        {
            float qOptions = autoTransition ? 128f : 0f;
            SetParameter("Q_OPTIONS", qOptions);

            System.Diagnostics.Debug.WriteLine($"[MAVLink] VTOL AutoTransition: {(autoTransition ? "ON" : "OFF")}");
        }

        public void SendSetHome(bool useCurrentLocation, double lat = 0, double lon = 0, double alt = 0)
        {
            if (!IsConnected)
            {
                ErrorOccurred?.Invoke(this, Get("Msg_DroneNotConnected"));
                return;
            }

            var cmd = new MAVLink.mavlink_command_long_t
            {
                target_system = (byte)DroneStatus.SystemId,
                target_component = (byte)DroneStatus.ComponentId,
                command = (ushort)MAVLink.MAV_CMD.DO_SET_HOME,
                confirmation = 0,
                param1 = useCurrentLocation ? 1 : 0, 
                param2 = 0,
                param3 = 0,
                param4 = 0,
                param5 = useCurrentLocation ? 0 : (float)lat,
                param6 = useCurrentLocation ? 0 : (float)lon,
                param7 = useCurrentLocation ? 0 : (float)alt
            };

            SendMessage(cmd, MAVLink.MAVLINK_MSG_ID.COMMAND_LONG);

            System.Diagnostics.Debug.WriteLine($"[MAVLink] SET_HOME отправлен: useCurrentLocation={useCurrentLocation}");
        }

        public void SavePlannedMission(List<WaypointItem> waypoints)
        {
            if (waypoints == null || waypoints.Count == 0)
            {
                _plannedMission = null;
                System.Diagnostics.Debug.WriteLine("⚠️ Миссия очищена (нет точек)");
                return;
            }

            _plannedMission = new List<WaypointItem>(waypoints);
            System.Diagnostics.Debug.WriteLine($"💾 Миссия сохранена: {_plannedMission.Count} точек");
        }

        public List<WaypointItem> GetPlannedMission()
        {
            return _plannedMission;
        }

        public async Task<bool> UploadPlannedMission()
        {
            if (_plannedMission == null || _plannedMission.Count == 0)
            {
                ErrorOccurred?.Invoke(this, Get("Msg_NoSavedMission"));
                return false;
            }

            return await UploadMission(_plannedMission);
        }

        public async Task<bool> UploadMission(List<WaypointItem> waypoints)
        {
            if (!IsConnected)
            {
                ErrorOccurred?.Invoke(this, Get("Msg_DroneNotConnected"));
                return false;
            }

            if (waypoints == null || waypoints.Count == 0)
            {
                ErrorOccurred?.Invoke(this, Get("Msg_NoPointsToUpload"));
                return false;
            }

            System.Diagnostics.Debug.WriteLine($"📤 Начало загрузки миссии: {waypoints.Count} точек");

            try
            {
                
                _missionItemsToUpload = new List<MAVLink.mavlink_mission_item_int_t>();

                _missionItemsToUpload.Add(CreateHomeWaypoint(waypoints[0]));

                for (int i = 0; i < waypoints.Count; i++)
                {
                    _missionItemsToUpload.Add(ConvertToMissionItem(waypoints[i], i + 1));
                }

                ClearMission();
                await Task.Delay(300);

                _missionUploadTcs = new TaskCompletionSource<bool>();
                _missionUploadExpectedSeq = 0;
                _missionUploadStartTime = DateTime.Now;

                var missionCount = new MAVLink.mavlink_mission_count_t
                {
                    target_system = (byte)DroneStatus.SystemId,
                    target_component = (byte)DroneStatus.ComponentId,
                    count = (ushort)_missionItemsToUpload.Count,
                    mission_type = 0
                };

                SendMessage(missionCount, MAVLink.MAVLINK_MSG_ID.MISSION_COUNT);
                System.Diagnostics.Debug.WriteLine($"📊 MISSION_COUNT: {_missionItemsToUpload.Count}");

                var timeoutTask = Task.Delay(15000);
                var completedTask = await Task.WhenAny(_missionUploadTcs.Task, timeoutTask);

                if (completedTask == timeoutTask)
                {
                    
                    System.Diagnostics.Debug.WriteLine("⚠️ Mission handshake timeout, fallback to sequential send");
                    await FallbackSequentialUpload();
                    return true;
                }

                bool result = _missionUploadTcs.Task.Result;
                if (result)
                {
                    System.Diagnostics.Debug.WriteLine("✅ Миссия загружена (handshake OK)");
                }
                else
                {
                    System.Diagnostics.Debug.WriteLine("❌ Миссия отклонена FC");
                    ErrorOccurred?.Invoke(this, Get("Msg_FcRejectedMission"));
                }

                return result;
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"❌ Ошибка: {ex.Message}");
                ErrorOccurred?.Invoke(this, Fmt("Msg_MissionUploadErrorFmt", ex.Message));
                return false;
            }
            finally
            {
                _missionItemsToUpload = null;
            }
        }

        private async Task FallbackSequentialUpload()
        {
            if (_missionItemsToUpload == null) return;

            for (int i = 0; i < _missionItemsToUpload.Count; i++)
            {
                SendMissionItem(_missionItemsToUpload[i]);
                MissionProgressUpdated?.Invoke(this, (int)((i + 1) * 100.0 / _missionItemsToUpload.Count));
                System.Diagnostics.Debug.WriteLine($"📍 seq={i}: fallback send");
                await Task.Delay(200);
            }
        }

        private void ProcessMissionRequestInt(MAVLink.MAVLinkMessage msg)
        {
            var req = (MAVLink.mavlink_mission_request_int_t)msg.data;
            SendRequestedMissionItem(req.seq);
        }

        private void ProcessMissionRequest(MAVLink.MAVLinkMessage msg)
        {
            var req = (MAVLink.mavlink_mission_request_t)msg.data;
            SendRequestedMissionItem(req.seq);
        }

        private void SendRequestedMissionItem(ushort seq)
        {
            if (_missionItemsToUpload == null || seq >= _missionItemsToUpload.Count)
            {
                System.Diagnostics.Debug.WriteLine($"⚠️ FC запросил seq={seq}, но items={_missionItemsToUpload?.Count ?? 0}");
                return;
            }

            var item = _missionItemsToUpload[seq];
            SendMissionItem(item);

            int progress = (int)((seq + 1) * 100.0 / _missionItemsToUpload.Count);
            MissionProgressUpdated?.Invoke(this, progress);

            System.Diagnostics.Debug.WriteLine($"📍 MISSION_ITEM_INT seq={seq}/{_missionItemsToUpload.Count - 1} ({progress}%)");
        }

        private void ProcessMissionAck(MAVLink.MAVLinkMessage msg)
        {
            var ack = (MAVLink.mavlink_mission_ack_t)msg.data;
            var result = (MAVLink.MAV_MISSION_RESULT)ack.type;

            System.Diagnostics.Debug.WriteLine($"📩 MISSION_ACK: {result}");

            if (_missionUploadTcs != null && !_missionUploadTcs.Task.IsCompleted)
            {
                _missionUploadTcs.TrySetResult(result == MAVLink.MAV_MISSION_RESULT.MAV_MISSION_ACCEPTED);
            }
        }

        public async Task<List<MAVLink.mavlink_mission_item_int_t>> DownloadMission(int timeoutMs = 10000)
        {
            if (!IsConnected) return null;

            try
            {
                _isDownloading = true;
                _downloadedMissionItems = new List<MAVLink.mavlink_mission_item_int_t>();
                _missionDownloadTcs = new TaskCompletionSource<List<MAVLink.mavlink_mission_item_int_t>>();
                _missionDownloadExpectedCount = 0;

                var request = new MAVLink.mavlink_mission_request_list_t
                {
                    target_system = (byte)DroneStatus.SystemId,
                    target_component = (byte)DroneStatus.ComponentId,
                    mission_type = 0 
                };
                SendMessage(request, MAVLink.MAVLINK_MSG_ID.MISSION_REQUEST_LIST);
                System.Diagnostics.Debug.WriteLine("[DOWNLOAD] Отправлен MISSION_REQUEST_LIST");

                var timeoutTask = Task.Delay(timeoutMs);
                var completedTask = await Task.WhenAny(_missionDownloadTcs.Task, timeoutTask);

                _isDownloading = false;

                if (completedTask == timeoutTask)
                {
                    System.Diagnostics.Debug.WriteLine("[DOWNLOAD] ❌ Таймаут");
                    _missionDownloadTcs.TrySetResult(null);
                    return null;
                }

                return await _missionDownloadTcs.Task;
            }
            catch (Exception ex)
            {
                _isDownloading = false;
                System.Diagnostics.Debug.WriteLine($"[DOWNLOAD] Ошибка: {ex.Message}");
                return null;
            }
        }

        private void ProcessMissionCount(MAVLink.MAVLinkMessage msg)
        {
            if (!_isDownloading) return;

            var count = (MAVLink.mavlink_mission_count_t)msg.data;
            _missionDownloadExpectedCount = count.count;
            _downloadedMissionItems = new List<MAVLink.mavlink_mission_item_int_t>();

            System.Diagnostics.Debug.WriteLine($"[DOWNLOAD] MISSION_COUNT: {count.count} элементов");

            if (count.count == 0)
            {
                
                SendMissionAck(MAVLink.MAV_MISSION_RESULT.MAV_MISSION_ACCEPTED);
                _missionDownloadTcs?.TrySetResult(_downloadedMissionItems);
                return;
            }

            RequestMissionItem(0);
        }

        private void ProcessMissionItemInt(MAVLink.MAVLinkMessage msg)
        {
            if (!_isDownloading) return;

            var item = (MAVLink.mavlink_mission_item_int_t)msg.data;
            _downloadedMissionItems.Add(item);

            int received = _downloadedMissionItems.Count;
            int progress = (int)((double)received / _missionDownloadExpectedCount * 100);
            MissionProgressUpdated?.Invoke(this, progress);

            System.Diagnostics.Debug.WriteLine($"[DOWNLOAD] MISSION_ITEM_INT seq={item.seq} cmd={item.command} ({received}/{_missionDownloadExpectedCount})");

            if (received >= _missionDownloadExpectedCount)
            {
                
                SendMissionAck(MAVLink.MAV_MISSION_RESULT.MAV_MISSION_ACCEPTED);
                _missionDownloadTcs?.TrySetResult(_downloadedMissionItems);
                System.Diagnostics.Debug.WriteLine($"[DOWNLOAD] ✅ Миссия скачана: {received} элементов");
            }
            else
            {
                
                RequestMissionItem((ushort)received);
            }
        }

        private void RequestMissionItem(ushort seq)
        {
            var request = new MAVLink.mavlink_mission_request_int_t
            {
                target_system = (byte)DroneStatus.SystemId,
                target_component = (byte)DroneStatus.ComponentId,
                seq = seq,
                mission_type = 0
            };
            SendMessage(request, MAVLink.MAVLINK_MSG_ID.MISSION_REQUEST_INT);
        }

        private void SendMissionAck(MAVLink.MAV_MISSION_RESULT result)
        {
            var ack = new MAVLink.mavlink_mission_ack_t
            {
                target_system = (byte)DroneStatus.SystemId,
                target_component = (byte)DroneStatus.ComponentId,
                type = (byte)result,
                mission_type = 0
            };
            SendMessage(ack, MAVLink.MAVLINK_MSG_ID.MISSION_ACK);
        }        
        
        public async Task<bool> ModifyWaypointInFlight(WaypointItem wp, int missionSeq)
        {
            if (!IsConnected) return false;

            try
            {
                
                var item = ConvertToMissionItem(wp, missionSeq);
                _missionItemsToUpload = new List<MAVLink.mavlink_mission_item_int_t> { item };
                _missionUploadTcs = new TaskCompletionSource<bool>();

                var partial = new MAVLink.mavlink_mission_write_partial_list_t
                {
                    target_system = (byte)DroneStatus.SystemId,
                    target_component = (byte)DroneStatus.ComponentId,
                    start_index = (short)missionSeq,
                    end_index = (short)missionSeq,
                    mission_type = 0
                };

                SendMessage(partial, MAVLink.MAVLINK_MSG_ID.MISSION_WRITE_PARTIAL_LIST);
                System.Diagnostics.Debug.WriteLine($"✏️ MISSION_WRITE_PARTIAL seq={missionSeq}");

                var timeoutTask = Task.Delay(5000);
                var completedTask = await Task.WhenAny(_missionUploadTcs.Task, timeoutTask);

                if (completedTask == timeoutTask)
                {
                    
                    SendMissionItem(item);
                    System.Diagnostics.Debug.WriteLine("⚠️ Partial write timeout, sent item directly");
                    return true;
                }

                return _missionUploadTcs.Task.Result;
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"❌ ModifyWaypoint error: {ex.Message}");
                return false;
            }
            finally
            {
                _missionItemsToUpload = null;
            }
        }

        public async Task<bool> ReuploadMissionInFlight(List<WaypointItem> waypoints, int resumeFromSeq = -1)
        {
            if (!IsConnected) return false;

            bool uploaded = await UploadMission(waypoints);
            if (!uploaded) return false;

            if (resumeFromSeq >= 0)
            {
                await Task.Delay(300);
                SetCurrentWaypoint((ushort)resumeFromSeq);
            }

            return true;
        }

        private MAVLink.mavlink_mission_item_int_t CreateHomeWaypoint(WaypointItem firstWaypoint)
        {
            return new MAVLink.mavlink_mission_item_int_t
            {
                target_system = (byte)DroneStatus.SystemId,
                target_component = (byte)DroneStatus.ComponentId,
                seq = 0,
                frame = (byte)MAVLink.MAV_FRAME.GLOBAL_RELATIVE_ALT,
                command = (ushort)MAVLink.MAV_CMD.WAYPOINT,
                current = 1, 
                autocontinue = 1,
                param1 = 0,
                param2 = 0,
                param3 = 0,
                param4 = 0,
                x = (int)(firstWaypoint.Latitude * 1e7),
                y = (int)(firstWaypoint.Longitude * 1e7),
                z = (float)firstWaypoint.Altitude,
                mission_type = 0
            };
        }

        private MAVLink.mavlink_mission_item_int_t ConvertToMissionItem(WaypointItem wp, int sequence)
        {
            ushort mavCmd;
            
            if (!wp.AutoNext)
            {
                mavCmd = (ushort)MAVLink.MAV_CMD.LOITER_UNLIM;  
            }
            
            else if (wp.LoiterTurns > 0)
            {
                mavCmd = 18;  
            }
            
            else
            {
                mavCmd = ConvertCommandTypeToMAVCmd(wp.CommandType);
            }

            float param1 = 0;
            float param2 = 0;
            float param3 = 0;
            float param4 = 0;

            float signedRadius = wp.Clockwise ? (float)Math.Abs(wp.Radius) : -(float)Math.Abs(wp.Radius);

            if (mavCmd == (ushort)MAVLink.MAV_CMD.LOITER_UNLIM)
            {
                param3 = signedRadius;  
            }
            
            else if (mavCmd == 18)
            {
                param1 = (float)wp.LoiterTurns;  
                param3 = signedRadius;           
            }
            else
            {
                
                switch (wp.CommandType)
                {
                    case "VTOL_TRANSITION_FW":
                        param1 = 4;  
                        break;
                    case "VTOL_TRANSITION_MC":
                        param1 = 3;  
                        break;
                    case "DELAY":
                        param1 = (float)wp.Delay;  
                        break;
                    case "LOITER_TIME":
                        param1 = (float)wp.Delay;  
                        param3 = signedRadius;     
                        break;
                    case "LOITER_TURNS":
                        param1 = (float)wp.LoiterTurns;  
                        param3 = signedRadius;           
                        break;
                    case "LOITER_UNLIM":
                        param3 = signedRadius;  
                        break;
                    case "WAYPOINT":
                        param1 = (float)wp.Delay;  
                        param2 = 0;  
                        break;
                    case "VTOL_TAKEOFF":
                    case "VTOL_LAND":
                        
                        break;
                    default:
                        param1 = (float)wp.Delay;
                        break;
                }
            }

            return new MAVLink.mavlink_mission_item_int_t
            {
                target_system = (byte)DroneStatus.SystemId,
                target_component = (byte)DroneStatus.ComponentId,
                seq = (ushort)sequence,
                frame = (byte)MAVLink.MAV_FRAME.GLOBAL_RELATIVE_ALT,
                command = mavCmd,
                current = 0, 
                autocontinue = (byte)(wp.AutoNext ? 1 : 0), 
                param1 = param1,
                param2 = param2,
                param3 = param3,
                param4 = param4,
                x = (int)(wp.Latitude * 1e7),
                y = (int)(wp.Longitude * 1e7),
                z = (float)wp.Altitude,
                mission_type = 0
            };
        }

        private ushort ConvertCommandTypeToMAVCmd(string commandType)
        {
            switch (commandType)
            {
                case "WAYPOINT": return (ushort)MAVLink.MAV_CMD.WAYPOINT;               
                case "LOITER_UNLIM": return (ushort)MAVLink.MAV_CMD.LOITER_UNLIM;       
                case "LOITER_TURNS": return 18;                                          
                case "LOITER_TIME": return (ushort)MAVLink.MAV_CMD.LOITER_TIME;         
                case "RETURN_TO_LAUNCH": return (ushort)MAVLink.MAV_CMD.RETURN_TO_LAUNCH; 
                case "LAND": return (ushort)MAVLink.MAV_CMD.LAND;                       
                case "TAKEOFF": return (ushort)MAVLink.MAV_CMD.TAKEOFF;                 
                case "VTOL_TAKEOFF": return 84;                                          
                case "VTOL_LAND": return 85;                                             
                case "VTOL_TRANSITION_FW": return 3000;                                  
                case "VTOL_TRANSITION_MC": return 3000;                                  
                case "DELAY": return (ushort)MAVLink.MAV_CMD.DELAY;                     
                case "CHANGE_SPEED": return (ushort)MAVLink.MAV_CMD.DO_CHANGE_SPEED;    
                case "SET_HOME": return (ushort)MAVLink.MAV_CMD.DO_SET_HOME;            
                default: return (ushort)MAVLink.MAV_CMD.WAYPOINT;
            }
        }

        private void SendMissionItem(MAVLink.mavlink_mission_item_int_t item)
        {
            SendMessage(item, MAVLink.MAVLINK_MSG_ID.MISSION_ITEM_INT);
        }

        public void StartMission()
        {
            if (!IsConnected) return;

            uint autoMode = 3; 
            try
            {
                var vehicleType = VehicleManager.Instance.CurrentVehicleType;
                if (vehicleType == VehicleType.QuadPlane)
                {
                    autoMode = 10; 
                }
            }
            catch { }

            SetMode(autoMode);

            var cmd = new MAVLink.mavlink_command_long_t
            {
                target_system = (byte)DroneStatus.SystemId,
                target_component = (byte)DroneStatus.ComponentId,
                command = (ushort)MAVLink.MAV_CMD.MISSION_START,
                confirmation = 0,
                param1 = 0, 
                param2 = 0, 
                param3 = 0,
                param4 = 0,
                param5 = 0,
                param6 = 0,
                param7 = 0
            };

            SendMessage(cmd, MAVLink.MAVLINK_MSG_ID.COMMAND_LONG);

            System.Diagnostics.Debug.WriteLine($"[MAVLink] Миссия запущена (AUTO={autoMode} + MISSION_START)");
        }

        public void PauseMission()
        {
            if (!IsConnected) return;

            SetMode(5); 

            System.Diagnostics.Debug.WriteLine("[MAVLink] Миссия приостановлена");
        }

        public async Task<bool> UploadVtolMission(
            WaypointItem home,
            WaypointItem startCircle,
            List<WaypointItem> waypoints,
            WaypointItem landingCircle,
            double takeoffAltitude,
            double landAltitude)
        {
            if (!IsConnected)
            {
                ErrorOccurred?.Invoke(this, Get("Msg_DroneNotConnected"));
                return false;
            }

            try
            {
                var items = new List<MAVLink.mavlink_mission_item_int_t>();
                int seq = 0;

                items.Add(new MAVLink.mavlink_mission_item_int_t
                {
                    target_system = (byte)DroneStatus.SystemId,
                    target_component = (byte)DroneStatus.ComponentId,
                    seq = (ushort)seq++,
                    frame = (byte)MAVLink.MAV_FRAME.GLOBAL_RELATIVE_ALT,
                    command = (ushort)MAVLink.MAV_CMD.WAYPOINT,
                    current = 1,
                    autocontinue = 1,
                    x = (int)(home.Latitude * 1e7),
                    y = (int)(home.Longitude * 1e7),
                    z = 0,
                    mission_type = 0
                });

                items.Add(new MAVLink.mavlink_mission_item_int_t
                {
                    target_system = (byte)DroneStatus.SystemId,
                    target_component = (byte)DroneStatus.ComponentId,
                    seq = (ushort)seq++,
                    frame = (byte)MAVLink.MAV_FRAME.GLOBAL_RELATIVE_ALT,
                    command = 84, 
                    current = 0,
                    autocontinue = 1,
                    param1 = 0,
                    param2 = 1, 
                    x = (int)(home.Latitude * 1e7),
                    y = (int)(home.Longitude * 1e7),
                    z = (float)takeoffAltitude,
                    mission_type = 0
                });

                items.Add(new MAVLink.mavlink_mission_item_int_t
                {
                    target_system = (byte)DroneStatus.SystemId,
                    target_component = (byte)DroneStatus.ComponentId,
                    seq = (ushort)seq++,
                    frame = (byte)MAVLink.MAV_FRAME.GLOBAL_RELATIVE_ALT,
                    command = 3000, 
                    current = 0,
                    autocontinue = 1,
                    param1 = 4, 
                    x = 0, y = 0, z = 0,
                    mission_type = 0
                });

                if (startCircle != null)
                    items.Add(CreateLoiterItem(startCircle, seq++));

                foreach (var wp in waypoints)
                    items.Add(CreateLoiterItem(wp, seq++));

                if (landingCircle != null)
                    items.Add(CreateLoiterItem(landingCircle, seq++));

                items.Add(new MAVLink.mavlink_mission_item_int_t
                {
                    target_system = (byte)DroneStatus.SystemId,
                    target_component = (byte)DroneStatus.ComponentId,
                    seq = (ushort)seq++,
                    frame = (byte)MAVLink.MAV_FRAME.GLOBAL_RELATIVE_ALT,
                    command = 3000,
                    current = 0,
                    autocontinue = 1,
                    param1 = 3, 
                    x = 0, y = 0, z = 0,
                    mission_type = 0
                });

                items.Add(new MAVLink.mavlink_mission_item_int_t
                {
                    target_system = (byte)DroneStatus.SystemId,
                    target_component = (byte)DroneStatus.ComponentId,
                    seq = (ushort)seq++,
                    frame = (byte)MAVLink.MAV_FRAME.GLOBAL_RELATIVE_ALT,
                    command = 85, 
                    current = 0,
                    autocontinue = 0,
                    param1 = 0,
                    x = (int)(home.Latitude * 1e7),
                    y = (int)(home.Longitude * 1e7),
                    z = 0,
                    mission_type = 0
                });

                ClearMission();
                await Task.Delay(500);

                var missionCount = new MAVLink.mavlink_mission_count_t
                {
                    target_system = (byte)DroneStatus.SystemId,
                    target_component = (byte)DroneStatus.ComponentId,
                    count = (ushort)items.Count,
                    mission_type = 0
                };
                SendMessage(missionCount, MAVLink.MAVLINK_MSG_ID.MISSION_COUNT);
                await Task.Delay(500);

                for (int i = 0; i < items.Count; i++)
                {
                    SendMissionItem(items[i]);
                    System.Diagnostics.Debug.WriteLine($"[VTOL] seq={i}: cmd={items[i].command}");
                    await Task.Delay(200);
                }

                System.Diagnostics.Debug.WriteLine($"✅ VTOL миссия: {items.Count} элементов");
                return true;
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"❌ VTOL ошибка: {ex.Message}");
                ErrorOccurred?.Invoke(this, Fmt("Msg_VtolMissionError", ex.Message));
                return false;
            }
        }

        private MAVLink.mavlink_mission_item_int_t CreateLoiterItem(WaypointItem wp, int sequence)
        {
            ushort command;
            float param1 = 0;

            if (!wp.AutoNext)
            {
                command = 17; 
                param1 = 0;
            }
            else if (wp.LoiterTurns > 0)
            {
                command = 18; 
                param1 = wp.LoiterTurns;
            }
            else
            {
                command = 16; 
                param1 = (float)wp.Delay;
            }

            float signedRadius = wp.Clockwise ? (float)Math.Abs(wp.Radius) : -(float)Math.Abs(wp.Radius);

            return new MAVLink.mavlink_mission_item_int_t
            {
                target_system = (byte)DroneStatus.SystemId,
                target_component = (byte)DroneStatus.ComponentId,
                seq = (ushort)sequence,
                frame = (byte)MAVLink.MAV_FRAME.GLOBAL_RELATIVE_ALT,
                command = command,
                current = 0,
                autocontinue = (byte)(wp.AutoNext ? 1 : 0),
                param1 = param1,
                param2 = 0,
                param3 = signedRadius,
                param4 = 0,
                x = (int)(wp.Latitude * 1e7),
                y = (int)(wp.Longitude * 1e7),
                z = (float)wp.Altitude,
                mission_type = 0
            };
        }

        public void SetCurrentWaypoint(ushort seq)
        {
            if (!IsConnected) return;

            var setCurrent = new MAVLink.mavlink_mission_set_current_t
            {
                target_system = (byte)DroneStatus.SystemId,
                target_component = (byte)DroneStatus.ComponentId,
                seq = seq
            };

            SendMessage(setCurrent, MAVLink.MAVLINK_MSG_ID.MISSION_SET_CURRENT);

            System.Diagnostics.Debug.WriteLine($"[MAVLink] Установлена точка миссии: {seq}");
        }

        public void ClearMission()
        {
            if (!IsConnected) return;

            var clearAll = new MAVLink.mavlink_mission_clear_all_t
            {
                target_system = (byte)DroneStatus.SystemId,
                target_component = (byte)DroneStatus.ComponentId,
                mission_type = 0
            };

            SendMessage(clearAll, MAVLink.MAVLINK_MSG_ID.MISSION_CLEAR_ALL);

            System.Diagnostics.Debug.WriteLine("[MAVLink] Миссия очищена");
        }

        private void SendMessage(object message, MAVLink.MAVLINK_MSG_ID messageId)
        {
            try
            {
                byte[] packet = _parser.GenerateMAVLinkPacket20(
                    messageId, message, false,
                    GCS_SYSTEM_ID, GCS_COMPONENT_ID, _packetSequence++);

                if (packet == null || packet.Length == 0) return;

                if (_isUdpMode && _udpClient != null)
                {
                    if (_remoteEndPoint != null)
                    {
                        _udpClient.Send(packet, packet.Length, _remoteEndPoint);
                        TotalPacketsSent++;
                        DroneStatus.PacketsSent++;
                    }
                    
                }
                
                else if (_serialPort != null && _serialPort.IsOpen)
                {
                    _serialPort.Write(packet, 0, packet.Length);
                    TotalPacketsSent++;
                    DroneStatus.PacketsSent++;
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[MAVLink] Send error: {ex.Message}");
            }
        }

        public string GetStatistics()
        {
            return $"{Get("Stats_Received")}: {TotalBytesReceived} B, " +
                   $"{Get("Stats_Packets")}: {TotalPacketsReceived}, " +
                   $"{Get("Stats_Sent")}: {TotalPacketsSent}, " +
                   $"{Get("Stats_Errors")}: {PacketErrors}";
        }

        public void ResetStatistics()
        {
            TotalBytesReceived = 0;
            TotalPacketsReceived = 0;
            TotalPacketsSent = 0;
            PacketErrors = 0;
            DroneStatus.PacketsReceived = 0;
            DroneStatus.PacketsSent = 0;
            DroneStatus.PacketErrors = 0;
        }

    }
}