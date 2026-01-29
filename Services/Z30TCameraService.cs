using System;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Tasks;
using System.Diagnostics;

namespace SimpleDroneGCS.Services
{
    /// <summary>
    /// Сервис управления камерой Z30T через TCP
    /// Протокол: Проприетарный "33 33" header
    /// IP: 192.168.144.68, Port: 2000
    /// RTSP: rtsp://192.168.144.68:554/chn0
    /// </summary>
    public class Z30TCameraService : IDisposable
    {
        #region Singleton
        private static Z30TCameraService _instance;
        private static readonly object _lock = new object();

        public static Z30TCameraService Instance
        {
            get
            {
                lock (_lock)
                {
                    return _instance ??= new Z30TCameraService();
                }
            }
        }
        #endregion

        #region Constants

        public const string DEFAULT_IP = "192.168.144.68";
        public const int DEFAULT_PORT = 2000;
        public const string RTSP_URL = "rtsp://192.168.144.68:554/chn0";

        private static readonly byte[] CMD_OSD_OFF = { 0x33, 0x33, 0x02, 0x03, 0x24, 0x00, 0x8F, 0x8A };
        private static readonly byte[] CMD_OSD_ON = { 0x33, 0x33, 0x02, 0x03, 0x25, 0x00, 0x90, 0x8C };

        private bool _isOSDOn = true;
        public bool IsOSDOn => _isOSDOn;

        // Протокол Z30T - Header
        private static readonly byte[] HEADER = { 0x33, 0x33, 0x02, 0x03 };

        // Команды
        private const byte CMD_YAW = 0x07;        // Pan (влево/вправо)
        private const byte CMD_PITCH = 0x08;      // Tilt (вверх/вниз)
        private const byte CMD_ZOOM = 0x31;       // Zoom
        private const byte CMD_CAMERA_MODE = 0x20; // EO/IR переключение
        private const byte CMD_IR_PALETTE = 0x21; // Палитра тепловизора
        private const byte CMD_TRACKING_TOGGLE = 0x0C; // Tracking ON/OFF
        private const byte CMD_HOME = 0x0F;       // Return to center
        private const byte CMD_SNAPSHOT = 0x1C;   // Снимок
        private const byte CMD_RECORD = 0x1D;     // Запись
        private const byte CMD_TRACKING_POINT = 0x26; // Координаты цели
        private const byte CMD_LASER = 0x33;      // Лазер
        private const byte CMD_FILL_LIGHT = 0x65; // Подсветка
        private const byte CMD_MEASURE_TEMP = 0x70; // Измерение температуры

        #endregion

        #region Predefined Commands (with CRC)

        // Zoom
        private static readonly byte[] CMD_ZOOM_IN = { 0x33, 0x33, 0x02, 0x03, 0x11, 0x01, 0x03, 0x80, 0xE5 };
        private static readonly byte[] CMD_ZOOM_OUT = { 0x33, 0x33, 0x02, 0x03, 0x12, 0x01, 0x03, 0x81, 0xE8 };
        private static readonly byte[] CMD_ZOOM_STOP = { 0x33, 0x33, 0x02, 0x03, 0x13, 0x00, 0x7E, 0x68 };

        // Camera Mode
        private static readonly byte[] CMD_SWITCH_EO = { 0x33, 0x33, 0x02, 0x03, 0x20, 0x01, 0x00, 0x8C, 0x0F };
        private static readonly byte[] CMD_SWITCH_IR = { 0x33, 0x33, 0x02, 0x03, 0x20, 0x01, 0x01, 0x8D, 0x10 };

        // Tracking
        private static readonly byte[] CMD_TRACKING_ON = { 0x33, 0x33, 0x02, 0x03, 0x0C, 0x01, 0x01, 0x79, 0xD4 };
        private static readonly byte[] CMD_TRACKING_OFF = { 0x33, 0x33, 0x02, 0x03, 0x0C, 0x01, 0x00, 0x78, 0xD3 };

        // Home/Center
        private static readonly byte[] CMD_RETURN_HOME = { 0x33, 0x33, 0x02, 0x03, 0x0F, 0x01, 0x00, 0x7B, 0xDC };

        // Snapshot & Record
        private static readonly byte[] CMD_TAKE_SNAPSHOT = { 0x33, 0x33, 0x02, 0x03, 0x1C, 0x00, 0x87, 0x7A };
        private static readonly byte[] CMD_TOGGLE_RECORD = { 0x33, 0x33, 0x02, 0x03, 0x1D, 0x00, 0x88, 0x7C };

        // Laser
        private static readonly byte[] CMD_LASER_ON = { 0x33, 0x33, 0x02, 0x03, 0x31, 0x01, 0x01, 0x9E, 0x43 };
        private static readonly byte[] CMD_LASER_OFF = { 0x33, 0x33, 0x02, 0x03, 0x31, 0x01, 0x00, 0x9D, 0x42 };

        // Fill Light
        private static readonly byte[] CMD_LIGHT_ON = { 0x33, 0x33, 0x02, 0x03, 0x65, 0x03, 0x00, 0x01, 0x00, 0xD4, 0x8A };
        private static readonly byte[] CMD_LIGHT_OFF = { 0x33, 0x33, 0x02, 0x03, 0x65, 0x03, 0x00, 0x00, 0x00, 0xD3, 0x88 };

        // Measure Temperature
        private static readonly byte[] CMD_TEMP_MEASURE = { 0x33, 0x33, 0x02, 0x03, 0x70, 0x01, 0x01, 0xDD, 0x00 };

        // IR Palettes (index 0-9)
        private static readonly byte[][] IR_PALETTES =
        {
            new byte[] { 0x33, 0x33, 0x02, 0x03, 0x21, 0x01, 0x00, 0x8D, 0x12 }, // White Hot
            new byte[] { 0x33, 0x33, 0x02, 0x03, 0x21, 0x01, 0x01, 0x8E, 0x13 }, // Lava
            new byte[] { 0x33, 0x33, 0x02, 0x03, 0x21, 0x01, 0x02, 0x8F, 0x14 }, // Iron Red
            new byte[] { 0x33, 0x33, 0x02, 0x03, 0x21, 0x01, 0x03, 0x90, 0x15 }, // Hot Iron
            new byte[] { 0x33, 0x33, 0x02, 0x03, 0x21, 0x01, 0x04, 0x91, 0x16 }, // Medical
            new byte[] { 0x33, 0x33, 0x02, 0x03, 0x21, 0x01, 0x05, 0x92, 0x17 }, // Arctic
            new byte[] { 0x33, 0x33, 0x02, 0x03, 0x21, 0x01, 0x06, 0x93, 0x18 }, // Rainbow 1
            new byte[] { 0x33, 0x33, 0x02, 0x03, 0x21, 0x01, 0x07, 0x94, 0x19 }, // Rainbow 2
            new byte[] { 0x33, 0x33, 0x02, 0x03, 0x21, 0x01, 0x08, 0x95, 0x1A }, // Red Trace
            new byte[] { 0x33, 0x33, 0x02, 0x03, 0x21, 0x01, 0x09, 0x96, 0x1B }, // Black Hot
        };

        // Captured Yaw/Pitch commands for different speeds
        private static readonly byte[] YAW_LEFT_SLOW = { 0x33, 0x33, 0x02, 0x03, 0x07, 0x02, 0x31, 0xFF, 0xA4, 0x9B };
        private static readonly byte[] YAW_RIGHT_SLOW = { 0x33, 0x33, 0x02, 0x03, 0x07, 0x02, 0x05, 0x00, 0x79, 0x44 };
        private static readonly byte[] YAW_RIGHT_FAST = { 0x33, 0x33, 0x02, 0x03, 0x07, 0x02, 0x74, 0x02, 0xEA, 0x24 };
        private static readonly byte[] PITCH_UP = { 0x33, 0x33, 0x02, 0x03, 0x08, 0x02, 0x23, 0x02, 0x9A, 0x86 };
        private static readonly byte[] PITCH_DOWN = { 0x33, 0x33, 0x02, 0x03, 0x08, 0x02, 0x9A, 0xFF, 0x0E, 0x71 };

        #endregion

        #region Properties & Events

        private TcpClient _tcpClient;
        private NetworkStream _stream;
        private CancellationTokenSource _cts;
        private bool _isConnected;
        private bool _isRecording;
        private bool _isTracking;
        private bool _isIRMode;
        private bool _isLaserOn;
        private bool _isLightOn;
        private int _currentPalette;

        public bool IsConnected => _isConnected;
        public bool IsRecording => _isRecording;
        public bool IsTracking => _isTracking;
        public bool IsIRMode => _isIRMode;
        public bool IsLaserOn => _isLaserOn;
        public bool IsLightOn => _isLightOn;
        public int CurrentPalette => _currentPalette;

        public string CameraIP { get; private set; } = DEFAULT_IP;
        public int CameraPort { get; private set; } = DEFAULT_PORT;

        public event EventHandler<bool> ConnectionChanged;
        public event EventHandler<string> StatusMessage;
        public event EventHandler<CameraTelemetry> TelemetryReceived;

        #endregion

        #region Connection

        public async Task<bool> ConnectAsync(string ip = null, int port = 0)
        {
            if (_isConnected) return true;

            CameraIP = ip ?? DEFAULT_IP;
            CameraPort = port > 0 ? port : DEFAULT_PORT;

            try
            {
                _tcpClient = new TcpClient();
                _tcpClient.ReceiveTimeout = 5000;
                _tcpClient.SendTimeout = 2000;

                StatusMessage?.Invoke(this, $"Подключение к {CameraIP}:{CameraPort}...");

                var connectTask = _tcpClient.ConnectAsync(CameraIP, CameraPort);
                if (await Task.WhenAny(connectTask, Task.Delay(5000)) != connectTask)
                {
                    throw new TimeoutException("Таймаут подключения");
                }

                await connectTask; // Propagate exception if any

                _stream = _tcpClient.GetStream();
                _isConnected = true;

                _cts = new CancellationTokenSource();
                _ = Task.Run(() => ReceiveLoop(_cts.Token));

                StatusMessage?.Invoke(this, "Подключено к камере Z30T");
                ConnectionChanged?.Invoke(this, true);

                Debug.WriteLine($"[Z30T] Connected to {CameraIP}:{CameraPort}");
                return true;
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[Z30T] Connection failed: {ex.Message}");
                StatusMessage?.Invoke(this, $"Ошибка: {ex.Message}");
                Disconnect();
                return false;
            }
        }

        public void Disconnect()
        {
            _cts?.Cancel();
            _stream?.Close();
            _tcpClient?.Close();

            _stream = null;
            _tcpClient = null;
            _isConnected = false;

            ConnectionChanged?.Invoke(this, false);
            StatusMessage?.Invoke(this, "Отключено");

            Debug.WriteLine("[Z30T] Disconnected");
        }

        private async Task ReceiveLoop(CancellationToken token)
        {
            byte[] buffer = new byte[1024];

            while (!token.IsCancellationRequested && _isConnected)
            {
                try
                {
                    if (_stream?.DataAvailable == true)
                    {
                        int bytesRead = await _stream.ReadAsync(buffer, 0, buffer.Length, token);
                        if (bytesRead > 0)
                        {
                            ParseTelemetry(buffer, bytesRead);
                        }
                    }
                    else
                    {
                        await Task.Delay(50, token);
                    }
                }
                catch (OperationCanceledException)
                {
                    break;
                }
                catch (Exception ex)
                {
                    Debug.WriteLine($"[Z30T] Receive error: {ex.Message}");
                }
            }
        }

        private void ParseTelemetry(byte[] data, int length)
        {
            // Парсинг телеметрии от камеры
            // Формат: 33 33 03 02 63 1c [roll] [pitch] [yaw] [zoom] ...
            // TODO: Реализовать полный парсинг
        }

        #endregion

        #region Send Commands

        private bool SendCommand(byte[] command)
        {
            if (!_isConnected || _stream == null) return false;

            try
            {
                _stream.Write(command, 0, command.Length);
                Debug.WriteLine($"[Z30T] Sent: {BitConverter.ToString(command)}");
                return true;
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[Z30T] Send error: {ex.Message}");
                StatusMessage?.Invoke(this, $"Ошибка отправки: {ex.Message}");
                return false;
            }
        }

        #endregion

        #region Gimbal Control

        /// <summary>
        /// Управление Yaw (Pan) - влево/вправо
        /// </summary>
        /// <param name="speed">Скорость: -1000 (влево) до +1000 (вправо)</param>
        public void SetYaw(int speed)
        {
            // Используем захваченные команды для надёжности
            if (speed < -50)
                SendCommand(YAW_LEFT_SLOW);
            else if (speed > 200)
                SendCommand(YAW_RIGHT_FAST);
            else if (speed > 0)
                SendCommand(YAW_RIGHT_SLOW);
            // При speed = 0 не отправляем (стоп будет при отпускании)
        }


        /// <summary>
        /// Управление Pitch (Tilt) - вверх/вниз
        /// </summary>
        /// <param name="speed">Скорость: -1000 (вниз) до +1000 (вверх)</param>
        public void SetPitch(int speed)
        {
            if (speed > 50)
                SendCommand(PITCH_UP);
            else if (speed < -50)
                SendCommand(PITCH_DOWN);
        }

        /// <summary>
        /// Быстрые команды управления gimbal
        /// </summary>
        public void PanLeft() => SendCommand(YAW_LEFT_SLOW);
        public void PanRight() => SendCommand(YAW_RIGHT_SLOW);
        public void TiltUp() => SendCommand(PITCH_UP);
        public void TiltDown() => SendCommand(PITCH_DOWN);

        /// <summary>
        /// Возврат gimbal в центр
        /// </summary>
        public void ReturnToHome() => SendCommand(CMD_RETURN_HOME);

        #endregion

        #region Zoom

        public void ZoomIn() => SendCommand(CMD_ZOOM_IN);
        public void ZoomOut() => SendCommand(CMD_ZOOM_OUT);
        public void ZoomStop() => SendCommand(CMD_ZOOM_STOP);

        #endregion



        #region Camera Mode (EO/IR)

        public void SwitchToEO()
        {
            if (SendCommand(CMD_SWITCH_EO))
            {
                _isIRMode = false;
                StatusMessage?.Invoke(this, "Режим: EO (видимый)");
            }
        }

        public void SwitchToIR()
        {
            if (SendCommand(CMD_SWITCH_IR))
            {
                _isIRMode = true;
                StatusMessage?.Invoke(this, "Режим: IR (тепловизор)");
            }
        }

        public void ToggleCameraMode()
        {
            if (_isIRMode) SwitchToEO();
            else SwitchToIR();
        }

        #endregion

        #region IR Palette

        public static readonly string[] PaletteNames =
        {
            "White Hot", "Lava", "Iron Red", "Hot Iron", "Medical",
            "Arctic", "Rainbow 1", "Rainbow 2", "Red Trace", "Black Hot"
        };

        public void SetIRPalette(int index)
        {
            if (index < 0 || index >= IR_PALETTES.Length) return;

            if (SendCommand(IR_PALETTES[index]))
            {
                _currentPalette = index;
                StatusMessage?.Invoke(this, $"Палитра: {PaletteNames[index]}");
            }
        }

        public void NextPalette()
        {
            int next = (_currentPalette + 1) % IR_PALETTES.Length;
            SetIRPalette(next);
        }

        #endregion

        #region Tracking

        public void StartTracking()
        {
            if (SendCommand(CMD_TRACKING_ON))
            {
                _isTracking = true;
                StatusMessage?.Invoke(this, "Слежение: ВКЛ");
            }
        }

        public void StopTracking()
        {
            if (SendCommand(CMD_TRACKING_OFF))
            {
                _isTracking = false;
                StatusMessage?.Invoke(this, "Слежение: ВЫКЛ");
            }
        }

        public void ToggleTracking()
        {
            if (_isTracking) StopTracking();
            else StartTracking();
        }

        /// <summary>
        /// Установить точку слежения (нормализованные координаты 0.0-1.0)
        /// </summary>
        public void SetTrackingPoint(float x, float y)
        {
            // Формат: 33 33 02 03 26 0A [X_float][Y_float][32 00] [crc]
            // X, Y - float32 little-endian (0.0 - 1.0)

            byte[] xBytes = BitConverter.GetBytes(x);
            byte[] yBytes = BitConverter.GetBytes(y);

            byte[] cmd = new byte[16];
            cmd[0] = 0x33; cmd[1] = 0x33; cmd[2] = 0x02; cmd[3] = 0x03;
            cmd[4] = 0x26; cmd[5] = 0x0A;
            Array.Copy(xBytes, 0, cmd, 6, 4);
            Array.Copy(yBytes, 0, cmd, 10, 4);
            cmd[14] = 0x32; cmd[15] = 0x00;

            // Добавляем CRC (упрощённый - сумма байт)
            int sum = 0;
            for (int i = 0; i < cmd.Length; i++) sum += cmd[i];

            byte[] fullCmd = new byte[18];
            Array.Copy(cmd, fullCmd, 16);
            fullCmd[16] = (byte)(sum & 0xFF);
            fullCmd[17] = (byte)((sum >> 8) & 0xFF);

            SendCommand(fullCmd);

            if (!_isTracking) StartTracking();
        }

        #endregion

        #region Snapshot & Record

        public void TakeSnapshot()
        {
            SendCommand(CMD_TAKE_SNAPSHOT);
            StatusMessage?.Invoke(this, "📸 Снимок сохранён");
        }

        public void ToggleRecord()
        {
            if (SendCommand(CMD_TOGGLE_RECORD))
            {
                _isRecording = !_isRecording;
                StatusMessage?.Invoke(this, _isRecording ? "⏺️ Запись..." : "⏹️ Запись остановлена");
            }
        }

        #endregion

        #region Laser & Light

        public void ToggleLaser()
        {
            if (_isLaserOn)
                SendCommand(CMD_LASER_OFF);
            else
                SendCommand(CMD_LASER_ON);

            _isLaserOn = !_isLaserOn;
            StatusMessage?.Invoke(this, _isLaserOn ? "🔴 Лазер ВКЛ" : "Лазер ВЫКЛ");
            Debug.WriteLine($"[Z30T] Laser: {(_isLaserOn ? "ON" : "OFF")}");
        }

        public void SetFillLight(bool on)
        {
            if (SendCommand(on ? CMD_LIGHT_ON : CMD_LIGHT_OFF))
            {
                _isLightOn = on;
                StatusMessage?.Invoke(this, on ? "💡 Подсветка ВКЛ" : "Подсветка ВЫКЛ");
            }
        }

        public void ToggleFillLight() => SetFillLight(!_isLightOn);

        #endregion

        #region Temperature

        public void MeasureTemperature()
        {
            SendCommand(CMD_TEMP_MEASURE);
            StatusMessage?.Invoke(this, "🌡️ Измерение температуры...");
        }

        #endregion

        #region Gimbal Angle Control

        private double _currentYaw = 0;
        private double _currentPitch = 0;

        public double CurrentYaw => _currentYaw;
        public double CurrentPitch => _currentPitch;

        /// <summary>
        /// Управление Yaw через слайдер (как джойстик)
        /// </summary>
        public void SetYawAngle(double value)
        {
            if (value > 10)
                SendCommand(YAW_RIGHT_SLOW);
            else if (value < -10)
                SendCommand(YAW_LEFT_SLOW);

            _currentYaw = value;
        }

        public void SetPitchAngle(double value)
        {
            if (value > 10)
                SendCommand(PITCH_UP);
            else if (value < -10)
                SendCommand(PITCH_DOWN);

            _currentPitch = value;
        }

        public void UpdateStoredAngles(double yaw, double pitch)
        {
            _currentYaw = yaw;
            _currentPitch = pitch;
        }

        public void ToggleOSD()
        {
            if (_isOSDOn)
                SendCommand(CMD_OSD_OFF);
            else
                SendCommand(CMD_OSD_ON);

            _isOSDOn = !_isOSDOn;
            Debug.WriteLine($"[Z30T] OSD: {(_isOSDOn ? "ON" : "OFF")}");
        }

        #endregion

        #region Dispose

        public void Dispose()
        {
            Disconnect();
            _instance = null;
        }

        #endregion
    }

    /// <summary>
    /// Телеметрия камеры
    /// </summary>
    public class CameraTelemetry
    {
        public float Roll { get; set; }
        public float Pitch { get; set; }
        public float Yaw { get; set; }
        public float Zoom { get; set; }
        public float Temperature { get; set; }
        public string Mode { get; set; }
    }


}
