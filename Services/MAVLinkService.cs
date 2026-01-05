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
using static MAVLink;

namespace SimpleDroneGCS.Services
{
    /// <summary>
    /// Полноценный сервис MAVLink для управления дроном
    /// Поддержка: телеметрия, команды, миссии, параметры
    /// </summary>
    public class MAVLinkService
    {
        private SerialPort _serialPort;
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

        // События
        public event EventHandler<Telemetry> TelemetryUpdated;
        public event EventHandler<string> ConnectionStatusChanged;
        public event EventHandler<string> ErrorOccurred;
        public event EventHandler<bool> ConnectionStatusChanged_Bool;
        public event EventHandler TelemetryReceived;
        public event EventHandler<string> MessageReceived; // Текстовые сообщения от дрона
        public event Action<string> OnStatusTextReceived;

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
            _connectionStartTime = DateTime.MinValue; // СБРАСЫВАЕМ СЕКУНДОМЕР
            DroneStatus.IsConnected = false;
            DroneStatus.LastHeartbeat = DateTime.MinValue;

            _heartbeatTimer?.Stop();
            _telemetryRequestTimer?.Stop();
            _cts?.Cancel();

            try
            {
                _serialPort?.Close();
                _serialPort?.Dispose();
            }
            catch { }

            ConnectionStatusChanged?.Invoke(this, "Отключено");
            ConnectionStatusChanged_Bool?.Invoke(this, false);

            System.Diagnostics.Debug.WriteLine("[MAVLink] Отключено");
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
            if (!IsConnected || _serialPort == null || !_serialPort.IsOpen)
                return;
            try
            {
                var heartbeat = new MAVLink.mavlink_heartbeat_t
                {
                    type = _vehicleMavType,  // ← ВОТ ЗДЕСЬ ЗАМЕНЯЕМ
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
            var vehicleType = VehicleManager.Instance.CurrentVehicleType;

            if (vehicleType == VehicleType.Copter)
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
        public void SetArm(bool arm)
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
                param1 = arm ? 1 : 0, // 1 = ARM, 0 = DISARM
                param2 = 0, // force (0 = normal, 21196 = force)
                param3 = 0,
                param4 = 0,
                param5 = 0,
                param6 = 0,
                param7 = 0
            };

            SendMessage(cmd, MAVLink.MAVLINK_MSG_ID.COMMAND_LONG);

            System.Diagnostics.Debug.WriteLine($"[MAVLink] Команда {(arm ? "ARM" : "DISARM")} отправлена");
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
        /// Смена режима полета
        /// </summary>
        public void SetFlightMode(string modeName)
        {
            if (!IsConnected || _serialPort == null) return;

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
        { "SMART_RTL", 21 }
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
        { "RTL", 11 },        // ⚠️ PLANE RTL = 11
        { "LOITER", 12 },
        { "GUIDED", 15 },
        { "QSTABILIZE", 17 }, // VTOL режимы
        { "QHOVER", 18 },
        { "QLOITER", 19 },
        { "QLAND", 20 },
        { "QRTL", 21 },
        { "QACRO", 23 },
        { "TAKEOFF", 13 },
        { "THERMAL", 25 }
    };

            // Выбираем правильную карту
            var modeMap = (vehicleType == VehicleType.Copter) ? copterModeMap : planeModeMap;

            if (!modeMap.ContainsKey(modeName))
            {
                System.Diagnostics.Debug.WriteLine($"⚠️ Неизвестный режим: {modeName}");
                return;
            }

            uint customMode = modeMap[modeName];

            try
            {
                var packet = new MAVLink.mavlink_set_mode_t
                {
                    target_system = (byte)DroneStatus.SystemId,
                    base_mode = (byte)MAVLink.MAV_MODE_FLAG.CUSTOM_MODE_ENABLED,
                    custom_mode = customMode
                };

                byte[] buffer = _parser.GenerateMAVLinkPacket20(
                    MAVLink.MAVLINK_MSG_ID.SET_MODE,
                    packet,
                    false,
                    GCS_SYSTEM_ID,
                    GCS_COMPONENT_ID,
                    _packetSequence++
                );

                _serialPort.Write(buffer, 0, buffer.Length);
                TotalPacketsSent++;

                System.Diagnostics.Debug.WriteLine($"[MAVLink] {vehicleType} режим {modeName} (custom_mode={customMode}) отправлен");
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[MAVLink] Ошибка смены режима: {ex.Message}");
            }
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
                // 1. Очищаем старую миссию
                ClearMission();
                await Task.Delay(500);

                // 2. Отправляем количество точек
                int count = waypoints.Count + 1; // +1 для HOME точки

                var missionCount = new MAVLink.mavlink_mission_count_t
                {
                    target_system = (byte)DroneStatus.SystemId,
                    target_component = (byte)DroneStatus.ComponentId,
                    count = (ushort)count,
                    mission_type = 0 // 0 = Mission items
                };

                SendMessage(missionCount, MAVLink.MAVLINK_MSG_ID.MISSION_COUNT);
                System.Diagnostics.Debug.WriteLine($"📊 Отправлено MISSION_COUNT: {count} точек");

                await Task.Delay(500);

                // 3. Отправляем HOME точку (индекс 0)
                var home = CreateHomeWaypoint(waypoints[0]);
                SendMissionItem(home);
                System.Diagnostics.Debug.WriteLine("🏠 HOME точка отправлена");

                await Task.Delay(200);

                // 4. Отправляем все waypoints
                for (int i = 0; i < waypoints.Count; i++)
                {
                    var missionItem = ConvertToMissionItem(waypoints[i], i + 1);
                    SendMissionItem(missionItem);
                    System.Diagnostics.Debug.WriteLine($"✈️ WP{i + 1} отправлен: {waypoints[i].CommandType}");
                    await Task.Delay(200);
                }

                System.Diagnostics.Debug.WriteLine("✅ Миссия успешно загружена");
                return true;
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"❌ Ошибка загрузки миссии: {ex.Message}");
                ErrorOccurred?.Invoke(this, $"Ошибка загрузки миссии: {ex.Message}");
                return false;
            }
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
            ushort mavCmd = ConvertCommandTypeToMAVCmd(wp.CommandType);

            return new MAVLink.mavlink_mission_item_int_t
            {
                target_system = (byte)DroneStatus.SystemId,
                target_component = (byte)DroneStatus.ComponentId,
                seq = (ushort)sequence,
                frame = (byte)MAVLink.MAV_FRAME.GLOBAL_RELATIVE_ALT,
                command = mavCmd,
                current = 0, // 0 = не текущая
                autocontinue = 1,
                param1 = (float)wp.Delay,  // Задержка в секундах
                param2 = 0,
                param3 = 0,
                param4 = 0, // Yaw angle
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
                case "LOITER_TIME": return (ushort)MAVLink.MAV_CMD.LOITER_TIME;         // 19
                case "RETURN_TO_LAUNCH": return (ushort)MAVLink.MAV_CMD.RETURN_TO_LAUNCH; // 20
                case "LAND": return (ushort)MAVLink.MAV_CMD.LAND;                       // 21
                case "TAKEOFF": return (ushort)MAVLink.MAV_CMD.TAKEOFF;                 // 22
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

            SetMode(3); // AUTO mode

            System.Diagnostics.Debug.WriteLine("[MAVLink] Миссия запущена");
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
        /// Отправка сообщения MAVLink
        /// </summary>
        private void SendMessage(object message, MAVLink.MAVLINK_MSG_ID messageId)
        {
            if (_serialPort == null || !_serialPort.IsOpen)
                return;

            try
            {
                // Используем MAVLink библиотеку для генерации пакета
                byte[] packet = _parser.GenerateMAVLinkPacket20(
                    messageId,
                    message,
                    false, // не подписывать
                    GCS_SYSTEM_ID,
                    GCS_COMPONENT_ID,
                    _packetSequence++
                );

                if (packet != null && packet.Length > 0)
                {
                    _serialPort.Write(packet, 0, packet.Length);
                    TotalPacketsSent++;
                    DroneStatus.PacketsSent++;
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[MAVLink] Send error: {ex.Message}");
                ErrorOccurred?.Invoke(this, $"Ошибка отправки: {ex.Message}");
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