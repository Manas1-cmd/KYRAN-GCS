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

namespace SimpleDroneGCS.Services
{
    /// <summary>
    /// Полноценный сервис MAVLink для управления дроном
    /// Поддержка: телеметрия, команды, миссии, параметры
    /// </summary>
    public class MAVLinkService
    {
        private SerialPort _serialPort;
        // === UDP ПОЛЯ ===
        private System.Net.Sockets.UdpClient _udpClient;
        private System.Net.IPEndPoint _remoteEndPoint;
        private bool _isUdpMode;
        private CancellationTokenSource _udpCts;
        private bool _udpWaitingForFirstPacket; // Флаг ожидания первого пакета
        // ===========================
        private CancellationTokenSource _cts;
        private MavlinkParse _parser;
        private byte _packetSequence = 0;
        private DispatcherTimer _heartbeatTimer;
        private DispatcherTimer _telemetryRequestTimer;
        private DateTime _connectionStartTime = DateTime.MinValue;
        // НОВОЕ: Хранилище запланированной миссии
        private List<WaypointItem> _plannedMission = null;
        public bool HasPlannedMission => _plannedMission != null && _plannedMission.Count > 0;
        public int PlannedMissionCount => _plannedMission?.Count ?? 0;

        // НОВОЕ: Активная миссия (для отображения на FlightDataView)
        private List<WaypointItem> _activeMission = null;
        public bool HasActiveMission => _activeMission != null && _activeMission.Count > 0;
        public List<WaypointItem> ActiveMission => _activeMission;

        // === Mission Upload Protocol (handshake) ===
        private List<MAVLink.mavlink_mission_item_int_t> _missionItemsToUpload;
        private TaskCompletionSource<bool> _missionUploadTcs;
        private int _missionUploadExpectedSeq = -1;
        private DateTime _missionUploadStartTime;

        // === Mission Download Protocol ===
        private List<MAVLink.mavlink_mission_item_int_t> _downloadedMissionItems;
        private TaskCompletionSource<List<MAVLink.mavlink_mission_item_int_t>> _missionDownloadTcs;
        private int _missionDownloadExpectedCount = 0;
        private bool _isDownloading = false;

        // Метод для установки активной миссии
        public void SetActiveMission(List<WaypointItem> mission)
        {
            _activeMission = mission != null ? new List<WaypointItem>(mission) : null;
            System.Diagnostics.Debug.WriteLine($"✅ Активная миссия установлена: {_activeMission?.Count ?? 0} точек");
        }

        // Свойства
        public bool IsConnected { get; private set; }
        public Telemetry CurrentTelemetry { get; private set; }
        public DroneStatus DroneStatus { get; private set; }

        // HOME позиция от дрона
        public double? HomeLat { get; private set; }
        public double? HomeLon { get; private set; }
        public double? HomeAlt { get; private set; }
        public bool HasHomePosition => HomeLat.HasValue && HomeLon.HasValue;

        // События
        public event EventHandler<Telemetry> TelemetryUpdated;
        public event EventHandler<string> ConnectionStatusChanged;
        public event EventHandler<string> ErrorOccurred;
        public event EventHandler<bool> ConnectionStatusChanged_Bool;
        public event EventHandler TelemetryReceived;
        public event EventHandler<string> MessageReceived; // Текстовые сообщения от дрона
        public event Action<string> OnStatusTextReceived;
        public event EventHandler<int> MissionProgressUpdated;

        // Текущая точка миссии (из MISSION_CURRENT)
        public int CurrentMissionSeq { get; private set; } = 0;

        // Статистика
        public long TotalBytesReceived { get; private set; }
        public long TotalPacketsReceived { get; private set; }
        public long TotalPacketsSent { get; private set; }
        public long PacketErrors { get; private set; }

        // Константы
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

        #region UDP CONNECTION

        /// <summary>
        /// UDP подключение (режим сервер - слушаем порт)
        /// </summary>
        public bool ConnectUDP(string localIp, int localPort)
        {
            try
            {
                Disconnect();

                var localEndPoint = new IPEndPoint(IPAddress.Parse(localIp), localPort);
                _udpClient = new UdpClient(localEndPoint);
                _isUdpMode = true;
                _udpWaitingForFirstPacket = true; // Ждём первый пакет
                IsConnected = true;
                _connectionStartTime = DateTime.Now;
                DroneStatus.IsConnected = true;
                DroneStatus.ConnectionPort = $"UDP:{localIp}:{localPort}";

                _udpCts = new CancellationTokenSource();
                Task.Run(() => ReadLoopUDP(_udpCts.Token));

                StartHeartbeatTimer();
                StartTelemetryRequestTimer();

                // Таймаут проверки - если за 10 сек нет ответа
                StartConnectionTimeoutCheck();

                ConnectionStatusChanged?.Invoke(this, "UDP: Ожидание дрона...");
                ConnectionStatusChanged_Bool?.Invoke(this, true);

                Debug.WriteLine($"[UDP] ✅ Server mode: listening on {localIp}:{localPort}");
                return true;
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[UDP] ❌ Error: {ex.Message}");
                ErrorOccurred?.Invoke(this, $"UDP ошибка: {ex.Message}");
                return false;
            }
        }

        /// <summary>
        /// UDP подключение с указанием целевого хоста (режим клиента)
        /// </summary>
        public bool ConnectUDP(string localIp, int localPort, string hostIp, int hostPort)
        {
            try
            {
                Disconnect();

                var localEndPoint = new IPEndPoint(IPAddress.Parse(localIp), localPort);
                _udpClient = new UdpClient(localEndPoint);
                _remoteEndPoint = new IPEndPoint(IPAddress.Parse(hostIp), hostPort);
                _isUdpMode = true;
                _udpWaitingForFirstPacket = false; // Уже знаем куда слать
                IsConnected = true;
                _connectionStartTime = DateTime.Now;
                DroneStatus.IsConnected = true;
                DroneStatus.ConnectionPort = $"UDP:{hostIp}:{hostPort}";

                _udpCts = new CancellationTokenSource();
                Task.Run(() => ReadLoopUDP(_udpCts.Token));

                StartHeartbeatTimer();
                StartTelemetryRequestTimer();

                // Таймаут проверки
                StartConnectionTimeoutCheck();

                ConnectionStatusChanged?.Invoke(this, "Подключено (UDP)");
                ConnectionStatusChanged_Bool?.Invoke(this, true);

                Debug.WriteLine($"[UDP] ✅ Client mode: {hostIp}:{hostPort}, local: {localIp}:{localPort}");
                return true;
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[UDP] ❌ Error: {ex.Message}");
                ErrorOccurred?.Invoke(this, $"UDP ошибка: {ex.Message}");
                return false;
            }
        }

        /// <summary>
        /// Таймаут проверки подключения
        /// </summary>
        private void StartConnectionTimeoutCheck()
        {
            Task.Delay(10000).ContinueWith(_ =>
            {
                if (IsConnected && DroneStatus.LastHeartbeat == DateTime.MinValue)
                {
                    System.Windows.Application.Current?.Dispatcher?.Invoke(() =>
                    {
                        Debug.WriteLine("[UDP] ⚠️ Нет ответа от дрона за 10 секунд");
                        ErrorOccurred?.Invoke(this, "Нет ответа от дрона. Проверьте IP/порт.");
                    });
                }
            });
        }

        /// <summary>
        /// Цикл чтения UDP
        /// </summary>
        private async Task ReadLoopUDP(CancellationToken ct)
        {
            Debug.WriteLine("[UDP] ReadLoop started");

            while (!ct.IsCancellationRequested && _udpClient != null)
            {
                try
                {
                    var result = await _udpClient.ReceiveAsync(ct);

                    // Запоминаем откуда пришёл пакет (для ответов)
                    _remoteEndPoint = result.RemoteEndPoint;

                    // Первый пакет получен - обновляем статус
                    if (_udpWaitingForFirstPacket)
                    {
                        _udpWaitingForFirstPacket = false;
                        Debug.WriteLine($"[UDP] ✅ Получен первый пакет от {result.RemoteEndPoint}");
                        System.Windows.Application.Current?.Dispatcher?.Invoke(() =>
                        {
                            ConnectionStatusChanged?.Invoke(this, "Подключено (UDP)");
                        });
                    }

                    TotalBytesReceived += result.Buffer.Length;

                    // Парсим через MemoryStream (как в Serial)
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

        #endregion

        #region CONNECTION

        /// <summary>
        /// Подключение к дрону
        /// </summary>
        public bool Connect(string portName, int baudRate)
        {
            try
            {
                if (IsConnected)
                {
                    Disconnect();
                }

                // Открываем COM порт
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
                    ErrorOccurred?.Invoke(this, "Не удалось открыть COM порт");
                    return false;
                }

                // Ждём стабилизации порта
                Thread.Sleep(1000);

                // Запускаем чтение
                _cts = new CancellationTokenSource();
                Task.Run(() => ReadLoop(_cts.Token));

                // Устанавливаем статус подключения
                IsConnected = true;
                _connectionStartTime = DateTime.Now; // ЗАПУСКАЕМ СЕКУНДОМЕР
                DroneStatus.IsConnected = true;
                DroneStatus.ConnectionPort = portName;
                DroneStatus.BaudRate = baudRate;

                ConnectionStatusChanged?.Invoke(this, "Подключено");
                ConnectionStatusChanged_Bool?.Invoke(this, true);

                System.Diagnostics.Debug.WriteLine($"[MAVLink] ✅ Порт {portName} открыт на {baudRate} baud");

                // Запускаем отправку HEARTBEAT (каждую секунду)
                StartHeartbeatTimer();

                // Запускаем запрос телеметрии (каждые 5 секунд)
                StartTelemetryRequestTimer();

                // Первый запрос телеметрии сразу
                RequestDataStreams();

                return true;
            }
            catch (UnauthorizedAccessException)
            {
                ErrorOccurred?.Invoke(this, "COM порт занят другим приложением");
                return false;
            }
            catch (System.IO.IOException ex)
            {
                ErrorOccurred?.Invoke(this, $"Ошибка порта: {ex.Message}");
                return false;
            }
            catch (Exception ex)
            {
                ErrorOccurred?.Invoke(this, $"Ошибка подключения: {ex.Message}");
                return false;
            }
        }

        /// <summary>
        /// Отключение от дрона
        /// </summary>
        public void Disconnect()
        {
            IsConnected = false;
            _isUdpMode = false;
            _udpWaitingForFirstPacket = false;
            _connectionStartTime = DateTime.MinValue;
            DroneStatus.IsConnected = false;
            DroneStatus.LastHeartbeat = DateTime.MinValue;

            _heartbeatTimer?.Stop();
            _telemetryRequestTimer?.Stop();
            _cts?.Cancel();
            _udpCts?.Cancel();

            try { _serialPort?.Close(); _serialPort?.Dispose(); _serialPort = null; } catch { }
            try { _udpClient?.Close(); _udpClient?.Dispose(); _udpClient = null; } catch { }

            // Сбрасываем remote endpoint
            _remoteEndPoint = null;

            ConnectionStatusChanged?.Invoke(this, "Отключено");
            ConnectionStatusChanged_Bool?.Invoke(this, false);

            Debug.WriteLine("[MAVLink] Отключено");
        }


        /// <summary>
        /// Получить время с момента подключения
        /// </summary>
        public TimeSpan GetConnectionTime()
        {
            if (!IsConnected || _connectionStartTime == DateTime.MinValue)
                return TimeSpan.Zero;

            return DateTime.Now - _connectionStartTime;
        }

        /// <summary>
        /// Получить список доступных COM портов
        /// </summary>
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

        #endregion

        #region HEARTBEAT & TELEMETRY REQUEST

        /// <summary>
        /// Запуск таймера отправки HEARTBEAT
        /// </summary>
        private void StartHeartbeatTimer()
        {
            _heartbeatTimer = new System.Windows.Threading.DispatcherTimer
            {
                Interval = TimeSpan.FromMilliseconds(HEARTBEAT_INTERVAL_MS)
            };
            _heartbeatTimer.Tick += (s, e) => SendHeartbeat();
            _heartbeatTimer.Start();
        }

        /// <summary>
        /// Запуск таймера запроса телеметрии
        /// </summary>
        private void StartTelemetryRequestTimer()
        {
            _telemetryRequestTimer = new System.Windows.Threading.DispatcherTimer
            {
                Interval = TimeSpan.FromMilliseconds(TELEMETRY_REQUEST_INTERVAL_MS)
            };
            _telemetryRequestTimer.Tick += (s, e) => RequestDataStreams();
            _telemetryRequestTimer.Start();
        }

        /// <summary>
        /// Отправка HEARTBEAT
        /// </summary>
        private byte _vehicleMavType = (byte)MAVLink.MAV_TYPE.QUADROTOR; // По умолчанию квадрокоптер

        private void SendHeartbeat()
        {
            if (!IsConnected)
                return;

            // Для Serial проверяем порт
            if (!_isUdpMode && (_serialPort == null || !_serialPort.IsOpen))
                return;

            // Для UDP в серверном режиме ждём первый пакет от дрона
            if (_isUdpMode && _remoteEndPoint == null)
            {
                // Тихо ждём - не спамим в лог каждую секунду
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


        /// <summary>
        /// Запрос потоков телеметрии от дрона
        /// </summary>
        private void RequestDataStreams()
        {
            if (!IsConnected || DroneStatus.SystemId == 0)
                return;

            try
            {
                // Запрашиваем все основные потоки данных
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

        /// <summary>
        /// Запрос конкретного потока данных
        /// </summary>
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

        #endregion




        #region READ LOOP

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

                        // Добавляем новые байты в буфер
                        for (int i = 0; i < count; i++)
                        {
                            dataBuffer.Add(buffer[i]);
                        }

                        // Пытаемся распарсить все пакеты из буфера
                        while (dataBuffer.Count > 0)
                        {
                            bool packetFound = false;

                            // Ищем начало MAVLink пакета
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

        #endregion

        private void ProcessHomePosition(MAVLink.MAVLinkMessage msg)
        {
            var home = (MAVLink.mavlink_home_position_t)msg.data;
            HomeLat = home.latitude / 1e7;
            HomeLon = home.longitude / 1e7;
            HomeAlt = home.altitude / 1000.0; // MSL в метрах

            System.Diagnostics.Debug.WriteLine($"[MAVLink] HOME: {HomeLat:F6}, {HomeLon:F6}, Alt: {HomeAlt:F1}m");
        }

        #region MESSAGE PROCESSING

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

                    // === Mission Protocol Handshake ===
                    case MAVLink.MAVLINK_MSG_ID.MISSION_REQUEST_INT:
                        ProcessMissionRequestInt(msg);
                        break;

                    case MAVLink.MAVLINK_MSG_ID.MISSION_REQUEST:
                        ProcessMissionRequest(msg);
                        break;

                    case MAVLink.MAVLINK_MSG_ID.MISSION_ACK:
                        ProcessMissionAck(msg);
                        break;

                    // === Mission Download Protocol ===
                    case MAVLink.MAVLINK_MSG_ID.MISSION_COUNT:
                        ProcessMissionCount(msg);
                        break;

                    case MAVLink.MAVLINK_MSG_ID.MISSION_ITEM_INT:
                        ProcessMissionItemInt(msg);
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
            CurrentTelemetry.Altitude = pos.alt / 1000.0; // MSL высота
            CurrentTelemetry.RelativeAltitude = pos.relative_alt / 1000.0; // ОТ HOME - НОВОЕ!
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

        private void ProcessStatusText(MAVLink.MAVLinkMessage msg)
        {
            var status = (MAVLink.mavlink_statustext_t)msg.data;
            string text = System.Text.Encoding.ASCII.GetString(status.text).TrimEnd('\0');

            System.Diagnostics.Debug.WriteLine($"[DRONE] {text}");

            // Отправляем в UI
            MessageReceived?.Invoke(this, text);

            OnStatusTextReceived?.Invoke(text);
        }

        private void ProcessCommandAck(MAVLink.MAVLinkMessage msg)
        {
            var ack = (MAVLink.mavlink_command_ack_t)msg.data;

            string commandName = ((MAVLink.MAV_CMD)ack.command).ToString();
            string resultName = ((MAVLink.MAV_RESULT)ack.result).ToString();

            System.Diagnostics.Debug.WriteLine($"[COMMAND_ACK] {commandName} → {resultName}");

            // Специальная обработка ARM
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

            // QuadPlane: моторы VTOL обычно на servo5-8, pusher на servo3
            // PWM 1000-2000 → 0-100%
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
            // RC channels - можно добавить если нужно
        }

        private void ProcessRawImu(MAVLink.MAVLinkMessage msg)
        {
            // IMU данные - можно добавить если нужно
        }

        private void ProcessScaledPressure(MAVLink.MAVLinkMessage msg)
        {
            // Барометр - можно добавить если нужно
        }

        private void ProcessBatteryStatus(MAVLink.MAVLinkMessage msg)
        {
            var battery = (MAVLink.mavlink_battery_status_t)msg.data;

            // Подробные данные батареи
            if (battery.voltages.Length > 0 && battery.voltages[0] != ushort.MaxValue)
            {
                CurrentTelemetry.BatteryVoltage = battery.voltages[0] / 1000.0;
            }

            CurrentTelemetry.BatteryCurrent = battery.current_battery / 100.0;
            CurrentTelemetry.BatteryPercent = battery.battery_remaining;
        }

        private string GetFlightModeName(uint customMode)
        {
            // КРИТИЧНО: используем РЕАЛЬНЫЙ тип дрона из heartbeat, а не UI-выбор
            // MAV_TYPE: 2=Quad, 3=Coax, 4=Heli, 13=Hexa, 14=Octo → Copter
            // MAV_TYPE: 1=FixedWing, 19-22=VTOL variants → Plane
            byte mavType = DroneStatus.Type;
            bool isCopter = (mavType == 2 || mavType == 3 || mavType == 4 || 
                            mavType == 13 || mavType == 14 || mavType == 15);
            
            // Если heartbeat ещё не пришёл (Type=0), используем UI-выбор как fallback
            if (mavType == 0)
                isCopter = VehicleManager.Instance.CurrentVehicleType == VehicleType.Copter;

            if (isCopter)
            {
                // ArduCopter flight modes
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
            else // Plane / QuadPlane
            {
                // ArduPlane flight modes
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

        #endregion

        #region COMMANDS - ARM/DISARM

        /// <summary>
        /// Вооружить дрон (обёртка для удобства)
        /// </summary>
        public void SendArm()
        {
            SetArm(true);
        }

        /// <summary>
        /// Разоружить дрон (обёртка для удобства)
        /// </summary>
        public void SendDisarm()
        {
            SetArm(false);
        }

        /// <summary>
        /// Вооружить/Разоружить дрон
        /// </summary>
        /// <summary>
        /// Вооружить/Разоружить дрон
        /// </summary>
        public void SetArm(bool arm, bool force = false)
        {
            if (!IsConnected)
            {
                ErrorOccurred?.Invoke(this, "Дрон не подключен");
                return;
            }

            var cmd = new MAVLink.mavlink_command_long_t
            {
                target_system = (byte)DroneStatus.SystemId,
                target_component = (byte)DroneStatus.ComponentId,
                command = (ushort)MAVLink.MAV_CMD.COMPONENT_ARM_DISARM,
                confirmation = 0,
                param1 = arm ? 1 : 0,
                param2 = force ? 21196 : 0,  // 21196 = force
                param3 = 0,
                param4 = 0,
                param5 = 0,
                param6 = 0,
                param7 = 0
            };

            SendMessage(cmd, MAVLink.MAVLINK_MSG_ID.COMMAND_LONG);

            System.Diagnostics.Debug.WriteLine($"[MAVLink] {(arm ? "ARM" : "DISARM")}{(force ? " (FORCE)" : "")} отправлено");
        }

        /// <summary>
        /// Принудительное вооружение (игнорируя проверки безопасности)
        /// </summary>
        public void ForceArm()
        {
            if (!IsConnected)
            {
                ErrorOccurred?.Invoke(this, "Дрон не подключен");
                return;
            }

            var cmd = new MAVLink.mavlink_command_long_t
            {
                target_system = (byte)DroneStatus.SystemId,
                target_component = (byte)DroneStatus.ComponentId,
                command = (ushort)MAVLink.MAV_CMD.COMPONENT_ARM_DISARM,
                confirmation = 0,
                param1 = 1, // ARM
                param2 = 21196, // Magic number для force
                param3 = 0,
                param4 = 0,
                param5 = 0,
                param6 = 0,
                param7 = 0
            };

            SendMessage(cmd, MAVLink.MAVLINK_MSG_ID.COMMAND_LONG);

            System.Diagnostics.Debug.WriteLine("[MAVLink] Принудительное ARM отправлено");
        }

        #endregion

        #region COMMANDS - FLIGHT

        /// <summary>
        /// Взлёт
        /// </summary>
        public void Takeoff(double altitude)
        {
            if (!IsConnected)
            {
                ErrorOccurred?.Invoke(this, "Дрон не подключен");
                return;
            }

            if (!CurrentTelemetry.Armed)
            {
                ErrorOccurred?.Invoke(this, "Дрон не вооружён");
                return;
            }

            var cmd = new MAVLink.mavlink_command_long_t
            {
                target_system = (byte)DroneStatus.SystemId,
                target_component = (byte)DroneStatus.ComponentId,
                command = (ushort)MAVLink.MAV_CMD.TAKEOFF,
                confirmation = 0,
                param1 = 0, // pitch
                param2 = 0, // empty
                param3 = 0, // empty
                param4 = 0, // yaw
                param5 = 0, // latitude
                param6 = 0, // longitude
                param7 = (float)altitude // altitude
            };

            SendMessage(cmd, MAVLink.MAVLINK_MSG_ID.COMMAND_LONG);

            System.Diagnostics.Debug.WriteLine($"[MAVLink] Взлёт на {altitude}м отправлен");
        }

        /// <summary>
        /// Посадка
        /// </summary>
        public void Land()
        {
            if (!IsConnected)
            {
                ErrorOccurred?.Invoke(this, "Дрон не подключен");
                return;
            }

            var cmd = new MAVLink.mavlink_command_long_t
            {
                target_system = (byte)DroneStatus.SystemId,
                target_component = (byte)DroneStatus.ComponentId,
                command = (ushort)MAVLink.MAV_CMD.LAND,
                confirmation = 0,
                param1 = 0, // abort alt
                param2 = 0, // land mode
                param3 = 0, // empty
                param4 = 0, // yaw
                param5 = 0, // latitude
                param6 = 0, // longitude
                param7 = 0 // altitude
            };

            SendMessage(cmd, MAVLink.MAVLINK_MSG_ID.COMMAND_LONG);

            System.Diagnostics.Debug.WriteLine("[MAVLink] Посадка отправлена");
        }

        /// <summary>
        /// Возврат домой (RTL)
        /// </summary>
        public void ReturnToLaunch()
        {
            if (!IsConnected) return;

            // RTL для Copter = 6, для Plane = 11
            var vehicleType = VehicleManager.Instance.CurrentVehicleType;
            uint rtlMode = (vehicleType == VehicleType.Copter) ? 6u : 11u;

            SetMode(rtlMode);

            System.Diagnostics.Debug.WriteLine($"[MAVLink] RTL режим установлен (mode={rtlMode})");
        }

        /// <summary>
        /// Установить режим полёта
        /// </summary>
        public void SetMode(uint mode)
        {
            if (!IsConnected)
            {
                ErrorOccurred?.Invoke(this, "Дрон не подключен");
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

        /// <summary>
        /// Полёт в точку (GUIDED режим)
        /// </summary>
        public void GoTo(double latitude, double longitude, double altitude)
        {
            if (!IsConnected)
            {
                ErrorOccurred?.Invoke(this, "Дрон не подключен");
                return;
            }

            // Сначала переключаем в GUIDED режим
            SetMode(4);

            // Отправляем целевую позицию
            var posTarget = new MAVLink.mavlink_set_position_target_global_int_t
            {
                target_system = (byte)DroneStatus.SystemId,
                target_component = (byte)DroneStatus.ComponentId,
                coordinate_frame = (byte)MAVLink.MAV_FRAME.GLOBAL_RELATIVE_ALT_INT,
                type_mask = 0b0000111111111000, // Только позиция
                lat_int = (int)(latitude * 1e7),
                lon_int = (int)(longitude * 1e7),
                alt = (float)altitude
            };

            SendMessage(posTarget, MAVLink.MAVLINK_MSG_ID.SET_POSITION_TARGET_GLOBAL_INT);

            System.Diagnostics.Debug.WriteLine($"[MAVLink] Полёт в точку: {latitude}, {longitude}, {altitude}м");
        }

        /// <summary>
        /// Остановка (BRAKE режим или LOITER)
        /// </summary>
        public void Stop()
        {
            if (!IsConnected) return;

            // Режим 17 = BRAKE (если поддерживается)
            // Иначе 5 = LOITER
            SetMode(17);
        }

        /// <summary>
        /// Смена режима полета (ИСПРАВЛЕНО для UDP)
        /// </summary>
        public void SetFlightMode(string modeName)
        {
            if (!IsConnected) return;

            // Получаем текущий тип дрона
            var vehicleType = VehicleManager.Instance.CurrentVehicleType;

            // Маппинг для COPTER
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

            // Маппинг для PLANE/QUADPLANE
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

            // Выбираем правильную карту
            var modeMap = (vehicleType == VehicleType.Copter) ? copterModeMap : planeModeMap;

            if (!modeMap.ContainsKey(modeName))
            {
                System.Diagnostics.Debug.WriteLine($"⚠️ Неизвестный режим: {modeName}");
                return;
            }

            uint customMode = modeMap[modeName];

            // Используем универсальный SetMode (работает и для Serial и для UDP)
            SetMode(customMode);

            System.Diagnostics.Debug.WriteLine($"[MAVLink] {vehicleType} режим {modeName} (custom_mode={customMode}) отправлен");
        }

        /// <summary>
        /// Preflight Calibration
        /// </summary>
        /// <summary>
        /// Preflight Calibration
        /// </summary>
        public void SendPreflightCalibration(bool gyro = false, bool barometer = false,
                                     bool accelerometer = false, bool compassMot = false,
                                     bool radioTrim = false)
        {
            if (!IsConnected) return;

            try
            {
                var msg = new MAVLink.mavlink_command_long_t
                {
                    target_system = (byte)DroneStatus.SystemId,        // ✅ ИСПРАВЛЕНО
                    target_component = (byte)DroneStatus.ComponentId,  // ✅ ИСПРАВЛЕНО
                    command = (ushort)MAVLink.MAV_CMD.PREFLIGHT_CALIBRATION,
                    confirmation = 0,
                    param1 = gyro ? 1 : 0,           // Gyro calibration
                    param2 = 0,                      // Magnetometer (unused)
                    param3 = barometer ? 1 : 0,      // Barometer + Airspeed
                    param4 = radioTrim ? 4 : 0,      // Radio trim (4 = trim)
                    param5 = accelerometer ? 1 : 0,  // Accelerometer
                    param6 = compassMot ? 1 : 0,     // CompassMot
                    param7 = 0
                };

                // ✅ ИСПОЛЬЗУЕМ ПРАВИЛЬНЫЙ МЕТОД SendMessage
                SendMessage(msg, MAVLink.MAVLINK_MSG_ID.COMMAND_LONG);

                string calibName = gyro ? "Gyro" :
                                  barometer ? "Barometer/Airspeed" :
                                  accelerometer ? "Accelerometer" :
                                  compassMot ? "CompassMot" :
                                  radioTrim ? "Radio Trim" : "Unknown";

                System.Diagnostics.Debug.WriteLine($"[MAVLink] Preflight calibration started: {calibName}");

                // ✅ СОБЫТИЕ ДЛЯ UI
                OnStatusTextReceived?.Invoke($"Калибровка запущена: {calibName}");
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[MAVLink] Calibration error: {ex.Message}");
            }
        }

        /// <summary>
        /// Взлёт (обёртка для удобства)
        /// </summary>
        public void SendTakeoff(double altitude)
        {
            Takeoff(altitude);
        }

        /// <summary>
        /// Посадка (обёртка для удобства)
        /// </summary>
        public void SendLand()
        {
            Land();
        }

        /// <summary>
        /// Возврат домой (обёртка для удобства)
        /// </summary>
        public void SendRTL()
        {
            ReturnToLaunch();
        }

        #region PARAMETER MANAGEMENT

        /// <summary>
        /// Установка параметра на дроне
        /// </summary>
        public void SetParameter(string paramName, float value)
        {
            if (!IsConnected)
            {
                ErrorOccurred?.Invoke(this, "Дрон не подключен");
                return;
            }

            // Преобразуем имя в 16-байтовый массив
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

        /// <summary>
        /// Установка Q_OPTIONS для VTOL автоперехода
        /// Бит 7 (128) = автопереход в самолёт после NAV_TAKEOFF
        /// </summary>
        public void SetVTOLAutoTransition(bool autoTransition)
        {
            float qOptions = autoTransition ? 128f : 0f;
            SetParameter("Q_OPTIONS", qOptions);

            System.Diagnostics.Debug.WriteLine($"[MAVLink] VTOL AutoTransition: {(autoTransition ? "ON" : "OFF")}");
        }

        #endregion

        /// <summary>
        /// Установка HOME позиции
        /// </summary>
        /// <param name="useCurrentLocation">true = текущая позиция дрона, false = указанные координаты</param>
        public void SendSetHome(bool useCurrentLocation, double lat = 0, double lon = 0, double alt = 0)
        {
            if (!IsConnected)
            {
                ErrorOccurred?.Invoke(this, "Дрон не подключен");
                return;
            }

            var cmd = new MAVLink.mavlink_command_long_t
            {
                target_system = (byte)DroneStatus.SystemId,
                target_component = (byte)DroneStatus.ComponentId,
                command = (ushort)MAVLink.MAV_CMD.DO_SET_HOME,
                confirmation = 0,
                param1 = useCurrentLocation ? 1 : 0, // 1 = текущая позиция, 0 = указанные координаты
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
        #endregion

        #region COMMANDS - MISSION

        /// <summary>
        /// Сохранить миссию для последующей отправки
        /// </summary>
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

        /// <summary>
        /// Получить запланированную миссию
        /// </summary>
        public List<WaypointItem> GetPlannedMission()
        {
            return _plannedMission;
        }


        /// <summary>
        /// Загрузка СОХРАНЁННОЙ миссии в дрон
        /// </summary>
        public async Task<bool> UploadPlannedMission()
        {
            if (_plannedMission == null || _plannedMission.Count == 0)
            {
                ErrorOccurred?.Invoke(this, "Нет сохранённой миссии");
                return false;
            }

            return await UploadMission(_plannedMission);
        }

        /// <summary>
        /// Загрузка миссии в дрон
        /// </summary>
        public async Task<bool> UploadMission(List<WaypointItem> waypoints)
        {
            if (!IsConnected)
            {
                ErrorOccurred?.Invoke(this, "Дрон не подключен");
                return false;
            }

            if (waypoints == null || waypoints.Count == 0)
            {
                ErrorOccurred?.Invoke(this, "Нет точек для загрузки");
                return false;
            }

            System.Diagnostics.Debug.WriteLine($"📤 Начало загрузки миссии: {waypoints.Count} точек");

            try
            {
                // 1. Подготавливаем все mission items
                _missionItemsToUpload = new List<MAVLink.mavlink_mission_item_int_t>();

                // seq=0: HOME
                _missionItemsToUpload.Add(CreateHomeWaypoint(waypoints[0]));

                // seq=1+: waypoints
                for (int i = 0; i < waypoints.Count; i++)
                {
                    _missionItemsToUpload.Add(ConvertToMissionItem(waypoints[i], i + 1));
                }

                // 2. Очищаем старую миссию
                ClearMission();
                await Task.Delay(300);

                // 3. Начинаем handshake: отправляем MISSION_COUNT
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

                // 4. Ждём завершения handshake (FC будет запрашивать items по одному)
                // Timeout: 15 секунд
                var timeoutTask = Task.Delay(15000);
                var completedTask = await Task.WhenAny(_missionUploadTcs.Task, timeoutTask);

                if (completedTask == timeoutTask)
                {
                    // Timeout — fallback: шлём items принудительно
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
                    ErrorOccurred?.Invoke(this, "FC отклонил миссию");
                }

                return result;
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"❌ Ошибка: {ex.Message}");
                ErrorOccurred?.Invoke(this, $"Ошибка загрузки миссии: {ex.Message}");
                return false;
            }
            finally
            {
                _missionItemsToUpload = null;
            }
        }

        /// <summary>
        /// Fallback: последовательная отправка без handshake (как было раньше)
        /// </summary>
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

        /// <summary>
        /// Обработка MISSION_REQUEST_INT от FC (handshake: FC запрашивает конкретный item)
        /// </summary>
        private void ProcessMissionRequestInt(MAVLink.MAVLinkMessage msg)
        {
            var req = (MAVLink.mavlink_mission_request_int_t)msg.data;
            SendRequestedMissionItem(req.seq);
        }

        /// <summary>
        /// Обработка MISSION_REQUEST (старый протокол, некоторые FC используют)
        /// </summary>
        private void ProcessMissionRequest(MAVLink.MAVLinkMessage msg)
        {
            var req = (MAVLink.mavlink_mission_request_t)msg.data;
            SendRequestedMissionItem(req.seq);
        }

        /// <summary>
        /// Отправка запрошенного mission item
        /// </summary>
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

        /// <summary>
        /// Обработка MISSION_ACK от FC (подтверждение или ошибка)
        /// </summary>
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

        #endregion

        #region MISSION DOWNLOAD PROTOCOL

        /// <summary>
        /// Скачать миссию с дрона.
        /// Протокол: MISSION_REQUEST_LIST → MISSION_COUNT → MISSION_REQUEST_INT(0..N) → MISSION_ITEM_INT(0..N) → MISSION_ACK
        /// </summary>
        public async Task<List<MAVLink.mavlink_mission_item_int_t>> DownloadMission(int timeoutMs = 10000)
        {
            if (!IsConnected) return null;

            try
            {
                _isDownloading = true;
                _downloadedMissionItems = new List<MAVLink.mavlink_mission_item_int_t>();
                _missionDownloadTcs = new TaskCompletionSource<List<MAVLink.mavlink_mission_item_int_t>>();
                _missionDownloadExpectedCount = 0;

                // 1. Отправляем MISSION_REQUEST_LIST
                var request = new MAVLink.mavlink_mission_request_list_t
                {
                    target_system = (byte)DroneStatus.SystemId,
                    target_component = (byte)DroneStatus.ComponentId,
                    mission_type = 0 // MAV_MISSION_TYPE_MISSION
                };
                SendMessage(request, MAVLink.MAVLINK_MSG_ID.MISSION_REQUEST_LIST);
                System.Diagnostics.Debug.WriteLine("[DOWNLOAD] Отправлен MISSION_REQUEST_LIST");

                // 2. Ждём завершения (MISSION_COUNT → запросы → items → ACK)
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

        /// <summary>
        /// FC отвечает на MISSION_REQUEST_LIST количеством элементов
        /// </summary>
        private void ProcessMissionCount(MAVLink.MAVLinkMessage msg)
        {
            if (!_isDownloading) return;

            var count = (MAVLink.mavlink_mission_count_t)msg.data;
            _missionDownloadExpectedCount = count.count;
            _downloadedMissionItems = new List<MAVLink.mavlink_mission_item_int_t>();

            System.Diagnostics.Debug.WriteLine($"[DOWNLOAD] MISSION_COUNT: {count.count} элементов");

            if (count.count == 0)
            {
                // Пустая миссия
                SendMissionAck(MAVLink.MAV_MISSION_RESULT.MAV_MISSION_ACCEPTED);
                _missionDownloadTcs?.TrySetResult(_downloadedMissionItems);
                return;
            }

            // Запрашиваем первый элемент
            RequestMissionItem(0);
        }

        /// <summary>
        /// FC отвечает на MISSION_REQUEST_INT конкретным элементом миссии
        /// </summary>
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
                // Все элементы получены — отправляем ACK
                SendMissionAck(MAVLink.MAV_MISSION_RESULT.MAV_MISSION_ACCEPTED);
                _missionDownloadTcs?.TrySetResult(_downloadedMissionItems);
                System.Diagnostics.Debug.WriteLine($"[DOWNLOAD] ✅ Миссия скачана: {received} элементов");
            }
            else
            {
                // Запрашиваем следующий
                RequestMissionItem((ushort)received);
            }
        }

        /// <summary>
        /// Запросить элемент миссии по seq
        /// </summary>
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

        /// <summary>
        /// Отправить MISSION_ACK
        /// </summary>
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
        }        /// <summary>
        /// Изменить один waypoint во время полёта (MISSION_WRITE_PARTIAL_LIST)
        /// Позволяет менять маршрут в реальном времени без полной перезагрузки
        /// </summary>
        public async Task<bool> ModifyWaypointInFlight(WaypointItem wp, int missionSeq)
        {
            if (!IsConnected) return false;

            try
            {
                // 1. Подготавливаем item
                var item = ConvertToMissionItem(wp, missionSeq);
                _missionItemsToUpload = new List<MAVLink.mavlink_mission_item_int_t> { item };
                _missionUploadTcs = new TaskCompletionSource<bool>();

                // 2. Отправляем MISSION_WRITE_PARTIAL_LIST (только 1 item)
                var partial = new MAVLink.mavlink_mission_write_partial_list_t
                {
                    target_system = (byte)DroneStatus.SystemId,
                    target_component = (byte)DroneStatus.ComponentId,
                    start_index = (short)missionSeq,
                    end_index = (short)missionSeq,
                    mission_type = 0
                };

                // Сохраняем seq для правильного ответа на MISSION_REQUEST
                // FC запросит item с seq = missionSeq, но наш массив имеет только 1 элемент
                // Нужно правильно маппить

                SendMessage(partial, MAVLink.MAVLINK_MSG_ID.MISSION_WRITE_PARTIAL_LIST);
                System.Diagnostics.Debug.WriteLine($"✏️ MISSION_WRITE_PARTIAL seq={missionSeq}");

                // 3. Ждём handshake
                var timeoutTask = Task.Delay(5000);
                var completedTask = await Task.WhenAny(_missionUploadTcs.Task, timeoutTask);

                if (completedTask == timeoutTask)
                {
                    // Fallback: отправляем item напрямую
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

        /// <summary>
        /// Перезагрузить всю миссию в полёте (Clear + Upload + Resume)
        /// Используется когда нужно полностью изменить маршрут
        /// </summary>
        public async Task<bool> ReuploadMissionInFlight(List<WaypointItem> waypoints, int resumeFromSeq = -1)
        {
            if (!IsConnected) return false;

            // 1. Загружаем новую миссию
            bool uploaded = await UploadMission(waypoints);
            if (!uploaded) return false;

            // 2. Если указан seq для продолжения — переключаемся на него
            if (resumeFromSeq >= 0)
            {
                await Task.Delay(300);
                SetCurrentWaypoint((ushort)resumeFromSeq);
            }

            return true;
        }

        /// <summary>
        /// Создание HOME точки
        /// </summary>
        private MAVLink.mavlink_mission_item_int_t CreateHomeWaypoint(WaypointItem firstWaypoint)
        {
            return new MAVLink.mavlink_mission_item_int_t
            {
                target_system = (byte)DroneStatus.SystemId,
                target_component = (byte)DroneStatus.ComponentId,
                seq = 0,
                frame = (byte)MAVLink.MAV_FRAME.GLOBAL_RELATIVE_ALT,
                command = (ushort)MAVLink.MAV_CMD.WAYPOINT,
                current = 1, // 1 = текущая (HOME)
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

        /// <summary>
        /// Конвертация WaypointItem в MAVLink mission item
        /// </summary>
        private MAVLink.mavlink_mission_item_int_t ConvertToMissionItem(WaypointItem wp, int sequence)
        {
            ushort mavCmd;
            
            // === ЛОГИКА AUTONEXT ===
            // Если AutoNext = false → бесконечное кружение (ждём команду оператора)
            if (!wp.AutoNext)
            {
                mavCmd = (ushort)MAVLink.MAV_CMD.LOITER_UNLIM;  // 17
            }
            // Если AutoNext = true и есть круги → LOITER_TURNS
            else if (wp.LoiterTurns > 0)
            {
                mavCmd = 18;  // MAV_CMD_NAV_LOITER_TURNS
            }
            // Иначе используем команду из CommandType
            else
            {
                mavCmd = ConvertCommandTypeToMAVCmd(wp.CommandType);
            }

            // Определяем param1 в зависимости от типа команды
            float param1 = 0;
            float param2 = 0;
            float param3 = 0;
            float param4 = 0;

            // ArduPilot: положительный radius = CW, отрицательный = CCW
            float signedRadius = wp.Clockwise ? (float)Math.Abs(wp.Radius) : -(float)Math.Abs(wp.Radius);

            // Если это LOITER_UNLIM (AutoNext=false), устанавливаем радиус
            if (mavCmd == (ushort)MAVLink.MAV_CMD.LOITER_UNLIM)
            {
                param3 = signedRadius;  // Радиус кружения со знаком
            }
            // Если это LOITER_TURNS, устанавливаем количество кругов и радиус
            else if (mavCmd == 18)
            {
                param1 = (float)wp.LoiterTurns;  // Количество кругов
                param3 = signedRadius;           // Радиус со знаком
            }
            else
            {
                // Стандартная логика для других команд
                switch (wp.CommandType)
                {
                    case "VTOL_TRANSITION_FW":
                        param1 = 4;  // MAV_VTOL_STATE_FW (переход в самолёт)
                        break;
                    case "VTOL_TRANSITION_MC":
                        param1 = 3;  // MAV_VTOL_STATE_MC (переход в коптер)
                        break;
                    case "DELAY":
                        param1 = (float)wp.Delay;  // Задержка в секундах
                        break;
                    case "LOITER_TIME":
                        param1 = (float)wp.Delay;  // Время кружения
                        param3 = signedRadius;     // Радиус со знаком
                        break;
                    case "LOITER_TURNS":
                        param1 = (float)wp.LoiterTurns;  // Количество кругов
                        param3 = signedRadius;           // Радиус со знаком
                        break;
                    case "LOITER_UNLIM":
                        param3 = signedRadius;  // Радиус со знаком
                        break;
                    case "WAYPOINT":
                        param1 = (float)wp.Delay;  // Hold time
                        param2 = 0;  // 0 = использовать WPNAV_RADIUS из параметров FC
                        break;
                    case "VTOL_TAKEOFF":
                    case "VTOL_LAND":
                        // param1 не используется
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
                current = 0, // 0 = не текущая
                autocontinue = (byte)(wp.AutoNext ? 1 : 0), // КРИТИЧНО: учитываем AutoNext!
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

        /// <summary>
        /// Конвертация типа команды в MAV_CMD
        /// </summary>
        private ushort ConvertCommandTypeToMAVCmd(string commandType)
        {
            switch (commandType)
            {
                case "WAYPOINT": return (ushort)MAVLink.MAV_CMD.WAYPOINT;               // 16
                case "LOITER_UNLIM": return (ushort)MAVLink.MAV_CMD.LOITER_UNLIM;       // 17
                case "LOITER_TURNS": return 18;                                          // 18
                case "LOITER_TIME": return (ushort)MAVLink.MAV_CMD.LOITER_TIME;         // 19
                case "RETURN_TO_LAUNCH": return (ushort)MAVLink.MAV_CMD.RETURN_TO_LAUNCH; // 20
                case "LAND": return (ushort)MAVLink.MAV_CMD.LAND;                       // 21
                case "TAKEOFF": return (ushort)MAVLink.MAV_CMD.TAKEOFF;                 // 22
                case "VTOL_TAKEOFF": return 84;                                          // MAV_CMD_NAV_VTOL_TAKEOFF
                case "VTOL_LAND": return 85;                                             // MAV_CMD_NAV_VTOL_LAND
                case "VTOL_TRANSITION_FW": return 3000;                                  // MAV_CMD_DO_VTOL_TRANSITION (самолёт)
                case "VTOL_TRANSITION_MC": return 3000;                                  // MAV_CMD_DO_VTOL_TRANSITION (коптер)
                case "DELAY": return (ushort)MAVLink.MAV_CMD.DELAY;                     // 93
                case "CHANGE_SPEED": return (ushort)MAVLink.MAV_CMD.DO_CHANGE_SPEED;    // 178
                case "SET_HOME": return (ushort)MAVLink.MAV_CMD.DO_SET_HOME;            // 179
                default: return (ushort)MAVLink.MAV_CMD.WAYPOINT;
            }
        }

        /// <summary>
        /// Отправка одного mission item
        /// </summary>
        private void SendMissionItem(MAVLink.mavlink_mission_item_int_t item)
        {
            SendMessage(item, MAVLink.MAVLINK_MSG_ID.MISSION_ITEM_INT);
        }

        /// <summary>
        /// Запуск миссии (AUTO режим)
        /// </summary>
        public void StartMission()
        {
            if (!IsConnected) return;

            // 1. Определяем режим AUTO в зависимости от типа дрона
            uint autoMode = 3; // По умолчанию для Copter
            try
            {
                var vehicleType = VehicleManager.Instance.CurrentVehicleType;
                if (vehicleType == VehicleType.QuadPlane)
                {
                    autoMode = 10; // AUTO mode для QuadPlane
                }
            }
            catch { }

            SetMode(autoMode);

            // 2. Отправляем команду старта миссии
            var cmd = new MAVLink.mavlink_command_long_t
            {
                target_system = (byte)DroneStatus.SystemId,
                target_component = (byte)DroneStatus.ComponentId,
                command = (ushort)MAVLink.MAV_CMD.MISSION_START,
                confirmation = 0,
                param1 = 0, // first item (0 = начало)
                param2 = 0, // last item (0 = до конца)
                param3 = 0,
                param4 = 0,
                param5 = 0,
                param6 = 0,
                param7 = 0
            };

            SendMessage(cmd, MAVLink.MAVLINK_MSG_ID.COMMAND_LONG);

            System.Diagnostics.Debug.WriteLine($"[MAVLink] Миссия запущена (AUTO={autoMode} + MISSION_START)");
        }

        /// <summary>
        /// Пауза миссии
        /// </summary>
        public void PauseMission()
        {
            if (!IsConnected) return;

            SetMode(5); // LOITER mode

            System.Diagnostics.Debug.WriteLine("[MAVLink] Миссия приостановлена");
        }

        /// <summary>
        /// Загрузка полной VTOL миссии: HOME → TAKEOFF → TRANSITION_FW → StartCircle → WPs → LandingCircle → TRANSITION_MC → VTOL_LAND
        /// </summary>
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
                ErrorOccurred?.Invoke(this, "Дрон не подключен");
                return false;
            }

            try
            {
                var items = new List<MAVLink.mavlink_mission_item_int_t>();
                int seq = 0;

                // seq=0: HOME
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

                // seq=1: VTOL_TAKEOFF
                items.Add(new MAVLink.mavlink_mission_item_int_t
                {
                    target_system = (byte)DroneStatus.SystemId,
                    target_component = (byte)DroneStatus.ComponentId,
                    seq = (ushort)seq++,
                    frame = (byte)MAVLink.MAV_FRAME.GLOBAL_RELATIVE_ALT,
                    command = 84, // MAV_CMD_NAV_VTOL_TAKEOFF
                    current = 0,
                    autocontinue = 1,
                    param1 = 0,
                    param2 = 1, // transition heading = NEXT_WAYPOINT
                    x = (int)(home.Latitude * 1e7),
                    y = (int)(home.Longitude * 1e7),
                    z = (float)takeoffAltitude,
                    mission_type = 0
                });

                // seq=2: DO_VTOL_TRANSITION → Fixed Wing
                items.Add(new MAVLink.mavlink_mission_item_int_t
                {
                    target_system = (byte)DroneStatus.SystemId,
                    target_component = (byte)DroneStatus.ComponentId,
                    seq = (ushort)seq++,
                    frame = (byte)MAVLink.MAV_FRAME.GLOBAL_RELATIVE_ALT,
                    command = 3000, // MAV_CMD_DO_VTOL_TRANSITION
                    current = 0,
                    autocontinue = 1,
                    param1 = 4, // MAV_VTOL_STATE_FW
                    x = 0, y = 0, z = 0,
                    mission_type = 0
                });

                // seq=3: START CIRCLE
                if (startCircle != null)
                    items.Add(CreateLoiterItem(startCircle, seq++));

                // seq=4..N: Обычные вейпоинты
                foreach (var wp in waypoints)
                    items.Add(CreateLoiterItem(wp, seq++));

                // seq=N+1: LANDING CIRCLE
                if (landingCircle != null)
                    items.Add(CreateLoiterItem(landingCircle, seq++));

                // seq=N+2: DO_VTOL_TRANSITION → Multicopter
                items.Add(new MAVLink.mavlink_mission_item_int_t
                {
                    target_system = (byte)DroneStatus.SystemId,
                    target_component = (byte)DroneStatus.ComponentId,
                    seq = (ushort)seq++,
                    frame = (byte)MAVLink.MAV_FRAME.GLOBAL_RELATIVE_ALT,
                    command = 3000,
                    current = 0,
                    autocontinue = 1,
                    param1 = 3, // MAV_VTOL_STATE_MC
                    x = 0, y = 0, z = 0,
                    mission_type = 0
                });

                // seq=N+3: VTOL_LAND
                items.Add(new MAVLink.mavlink_mission_item_int_t
                {
                    target_system = (byte)DroneStatus.SystemId,
                    target_component = (byte)DroneStatus.ComponentId,
                    seq = (ushort)seq++,
                    frame = (byte)MAVLink.MAV_FRAME.GLOBAL_RELATIVE_ALT,
                    command = 85, // MAV_CMD_NAV_VTOL_LAND
                    current = 0,
                    autocontinue = 0,
                    param1 = 0,
                    x = (int)(home.Latitude * 1e7),
                    y = (int)(home.Longitude * 1e7),
                    z = 0,
                    mission_type = 0
                });

                // === ОТПРАВКА ===
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
                ErrorOccurred?.Invoke(this, $"Ошибка VTOL миссии: {ex.Message}");
                return false;
            }
        }

        /// <summary>
        /// Создать LOITER/WAYPOINT item с учётом AutoNext
        /// AutoNext=false → LOITER_UNLIM (17), бесконечное кружение
        /// AutoNext=true + LoiterTurns>0 → LOITER_TURNS (18), N кругов
        /// Иначе → NAV_WAYPOINT (16)
        /// </summary>
        private MAVLink.mavlink_mission_item_int_t CreateLoiterItem(WaypointItem wp, int sequence)
        {
            ushort command;
            float param1 = 0;

            if (!wp.AutoNext)
            {
                command = 17; // LOITER_UNLIM
                param1 = 0;
            }
            else if (wp.LoiterTurns > 0)
            {
                command = 18; // LOITER_TURNS
                param1 = wp.LoiterTurns;
            }
            else
            {
                command = 16; // NAV_WAYPOINT
                param1 = (float)wp.Delay;
            }

            // ArduPilot: положительный radius = CW, отрицательный = CCW
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

        /// <summary>
        /// Установить текущую точку миссии
        /// </summary>
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

        /// <summary>
        /// Очистить миссию на дроне
        /// </summary>
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

        #endregion

        #region ALIASES (для совместимости с MainWindow)


        #endregion

        #region SEND MESSAGE

        /// <summary>
        /// Отправка сообщения MAVLink (универсальный метод для Serial и UDP)
        /// </summary>
        private void SendMessage(object message, MAVLink.MAVLINK_MSG_ID messageId)
        {
            try
            {
                byte[] packet = _parser.GenerateMAVLinkPacket20(
                    messageId, message, false,
                    GCS_SYSTEM_ID, GCS_COMPONENT_ID, _packetSequence++);

                if (packet == null || packet.Length == 0) return;

                // UDP режим
                if (_isUdpMode && _udpClient != null)
                {
                    if (_remoteEndPoint != null)
                    {
                        _udpClient.Send(packet, packet.Length, _remoteEndPoint);
                        TotalPacketsSent++;
                        DroneStatus.PacketsSent++;
                    }
                    // Если _remoteEndPoint == null, просто пропускаем (ждём первый пакет от дрона)
                }
                // Serial режим
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

        #endregion

        #region STATISTICS

        /// <summary>
        /// Получить статистику подключения
        /// </summary>
        public string GetStatistics()
        {
            return $"Получено: {TotalBytesReceived} байт, " +
                   $"Пакетов: {TotalPacketsReceived}, " +
                   $"Отправлено: {TotalPacketsSent}, " +
                   $"Ошибок: {PacketErrors}";
        }

        /// <summary>
        /// Сбросить статистику
        /// </summary>
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

        #endregion
    }
}