using System;
using System.Diagnostics;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Tasks;

namespace SimpleDroneGCS.Services
{
    /// <summary>
    /// Сервис управления камерой ViewPro / LOONG
    /// Протокол ViewLink Serial Command Communication Protocol V3.4.4
    /// 
    /// Верифицировано через Wireshark дамп ViewLink v4.0.7
    /// 
    /// TX (ПК→Камера): EB 90 [LEN] [55 AA DC serial_packet] [SUM]
    /// RX (Камера→ПК): 55 AA DC [serial_packet] (сырой, без EB 90)
    /// 
    /// Frame 0x30 = управление: A1(9) + C1(2) + E1(3) = 14 байт
    /// Frame 0x15 = запрос статуса (шлётся периодически)
    /// Frame 0x40 = feedback от камеры (углы, статус)
    /// Frame 0xAC = ACK от камеры
    /// </summary>
    public class ViewProCameraService : IDisposable
    {
        #region Константы

        private const byte HEADER_55 = 0x55;
        private const byte HEADER_AA = 0xAA;
        private const byte HEADER_DC = 0xDC;
        private const byte TCP_EB = 0xEB;
        private const byte TCP_90 = 0x90;

        private const byte FRAME_COMBINED = 0x30;
        private const byte FRAME_QUERY = 0x15;
        private const byte FRAME_FEEDBACK = 0x40;
        private const byte FRAME_ACK = 0xAC;

        // A1 режимы (верифицировано Wireshark)
        private const byte A1_MOTOR_CTRL = 0x00;
        private const byte A1_SPEED = 0x01;
        private const byte A1_HOME = 0x04;
        private const byte A1_TRACKING = 0x06;
        private const byte A1_ANGLE_ABS = 0x0B;
        private const byte A1_RC = 0x0D;          // RC/PWM — ViewLink использует для джойстика!
        private const byte A1_NO_CHANGE = 0x0F;
        private const byte A1_PITCH_DOWN = 0x12;

        // PWM константы
        private const int PWM_CENTER = 1500;
        private const int PWM_MIN = 1000;
        private const int PWM_MAX = 2000;

        // C1 команды
        private const byte C1_STOP = 0x01;
        private const byte C1_ZOOM_OUT = 0x08;
        private const byte C1_ZOOM_IN = 0x09;
        private const byte C1_FOCUS_FAR = 0x0A;
        private const byte C1_FOCUS_NEAR = 0x0B;
        private const byte C1_IR_WHITE = 0x0E;
        private const byte C1_IR_BLACK = 0x0F;
        private const byte C1_IR_RAINBOW = 0x12;
        private const byte C1_PHOTO = 0x13;
        private const byte C1_REC_START = 0x14;
        private const byte C1_REC_STOP = 0x15;
        private const byte C1_AUTO_FOCUS = 0x19;
        private const byte C1_IR_DZOOM_IN = 0x1B;
        private const byte C1_IR_DZOOM_OUT = 0x1C;

        // LRF дальномер (биты 13-15 поля C1, из ArduPilot Lua-драйвера)
        private const byte LRF_NONE = 0x00;
        private const byte LRF_SINGLE = 0x01;      // однократное измерение
        private const byte LRF_CONTINUOUS = 0x02;   // непрерывное измерение
        private const byte LRF_STOP = 0x03;         // остановить

        // Источники видео
        public const byte SRC_EO = 0x01;
        public const byte SRC_IR = 0x02;
        public const byte SRC_EO_IR_PIP = 0x03;
        public const byte SRC_IR_EO_PIP = 0x04;
        public const byte SRC_FUSION = 0x05;

        // E1 команды
        private const byte E1_NONE = 0x00;
        private const byte E1_STOP = 0x01;
        private const byte E1_SEARCH = 0x02;
        private const byte E1_START = 0x03;
        private const byte E1_AI_TOGGLE = 0x05;

        // Обратная совместимость
        public const byte VIDEO_EO1 = SRC_EO;
        public const byte VIDEO_IR = SRC_IR;
        public const byte VIDEO_EO_IR_PIP = SRC_EO_IR_PIP;
        public const byte VIDEO_IR_EO_PIP = SRC_IR_EO_PIP;
        public const byte VIDEO_FUSION = SRC_FUSION;

        #endregion

        #region События

        public event EventHandler<bool>? ConnectionChanged;
        public event EventHandler<GimbalAngles>? AnglesReceived;
        public event EventHandler<float>? DistanceReceived;
        public event EventHandler<string>? ErrorOccurred;
        public event EventHandler<byte[]>? DataReceived;
        public event Action<string>? StatusChanged;

        #endregion

        #region Свойства

        public bool IsConnected => _tcpClient?.Connected ?? false;
        public string IpAddress { get; set; } = "192.168.1.108";
        public int Port { get; set; } = 2000;
        public int RtspPort { get; set; } = 554;
        public GimbalAngles CurrentAngles { get; private set; } = new GimbalAngles();
        public float CurrentDistance { get; private set; } = 0;
        public bool IsRecording { get; private set; } = false;
        public string RtspUrl => $"rtsp://{IpAddress}:{RtspPort}/stream0";
        public long TotalBytesReceived { get; private set; } = 0;

        #endregion

        #region Приватные поля

        private TcpClient? _tcpClient;
        private NetworkStream? _networkStream;
        private CancellationTokenSource? _cts;
        private Task? _receiveTask;
        private Task? _heartbeatTask;
        private readonly object _sendLock = new object();
        private bool _disposed = false;
        private byte _frameCounter = 0;
        private byte _currentSensor = SRC_EO;
        private volatile bool _userActive = false;
        private DateTime _lastUserCmd = DateTime.MinValue;

        #endregion

        #region Подключение

        public async Task<bool> ConnectAsync(string ip, int port)
        {
            IpAddress = ip;
            Port = port;
            return await ConnectAsync();
        }

        public async Task<bool> ConnectAsync()
        {
            try
            {
                if (IsConnected) Disconnect();

                _tcpClient = new TcpClient();
                _tcpClient.ReceiveTimeout = 5000;
                _tcpClient.SendTimeout = 3000;
                _tcpClient.NoDelay = true;

                Debug.WriteLine($"[ViewPro] Подключение к {IpAddress}:{Port}...");
                StatusChanged?.Invoke($"Подключение к {IpAddress}:{Port}...");

                var connectTask = _tcpClient.ConnectAsync(IpAddress, Port);
                if (await Task.WhenAny(connectTask, Task.Delay(5000)) != connectTask)
                    throw new TimeoutException("Таймаут подключения");
                await connectTask;

                _networkStream = _tcpClient.GetStream();
                _cts = new CancellationTokenSource();
                TotalBytesReceived = 0;

                _receiveTask = Task.Run(() => ReceiveLoop(_cts.Token));
                _heartbeatTask = Task.Run(() => HeartbeatLoop(_cts.Token));

                // Инициализация как ViewLink: Frame 0x15 → Motor ON
                await Task.Delay(100);
                SendQuery(0x43);
                await Task.Delay(100);
                SendMotorOn();

                ConnectionChanged?.Invoke(this, true);
                StatusChanged?.Invoke("Подключено");
                Debug.WriteLine($"[ViewPro] Подключено к {IpAddress}:{Port}");
                return true;
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[ViewPro] Ошибка подключения: {ex.Message}");
                ErrorOccurred?.Invoke(this, $"Ошибка: {ex.Message}");
                StatusChanged?.Invoke($"Ошибка: {ex.Message}");
                return false;
            }
        }

        public void Disconnect()
        {
            try
            {
                _cts?.Cancel();
                _networkStream?.Close();
                _tcpClient?.Close();
                _networkStream = null;
                _tcpClient = null;
                ConnectionChanged?.Invoke(this, false);
                StatusChanged?.Invoke("Отключено");
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[ViewPro] Disconnect: {ex.Message}");
            }
        }

        #endregion

        #region ═══ ПОСТРОЕНИЕ И ОТПРАВКА ПАКЕТОВ ═══

        /// <summary>
        /// Серийный пакет: 55 AA DC [LEN_CTR] [FRAME_ID] [DATA...] [XOR]
        /// bodyLength = DATA_LEN + 3 (LEN_CTR + FRAME_ID + XOR)
        /// </summary>
        private byte[] BuildSerialPacket(byte frameId, byte[] data)
        {
            int bodyLength = data.Length + 3;
            byte lenCtr = (byte)(((_frameCounter & 0x03) << 6) | (bodyLength & 0x3F));
            _frameCounter = (byte)((_frameCounter + 1) & 0x03);

            byte[] packet = new byte[3 + 1 + 1 + data.Length + 1];
            packet[0] = HEADER_55;
            packet[1] = HEADER_AA;
            packet[2] = HEADER_DC;
            packet[3] = lenCtr;
            packet[4] = frameId;
            Array.Copy(data, 0, packet, 5, data.Length);

            byte xor = 0;
            for (int i = 3; i < packet.Length - 1; i++)
                xor ^= packet[i];
            packet[packet.Length - 1] = xor;

            return packet;
        }

        /// <summary>
        /// TCP-обёртка: EB 90 [LEN] [serial_packet] [SUM]
        ///   LEN = длина серийного пакета в байтах
        ///   SUM = (сумма всех байтов серийного пакета) & 0xFF
        /// 
        /// ⚠ Ранее отсутствовал байт LEN — из-за этого камера игнорировала все пакеты!
        /// Верифицировано через Wireshark дамп ViewLink v4.0.7
        /// </summary>
        private byte[] WrapTcp(byte[] serial)
        {
            byte sum = 0;
            foreach (byte b in serial) sum += b;

            byte[] wrapped = new byte[2 + 1 + serial.Length + 1]; // EB 90 + LEN + serial + SUM
            wrapped[0] = TCP_EB;
            wrapped[1] = TCP_90;
            wrapped[2] = (byte)serial.Length;  // ← КЛЮЧЕВОЙ БАЙТ! Раньше отсутствовал!
            Array.Copy(serial, 0, wrapped, 3, serial.Length);
            wrapped[wrapped.Length - 1] = sum;
            return wrapped;
        }

        private bool SendPacket(byte[] serialPacket)
        {
            if (!IsConnected || _networkStream == null) return false;

            byte[] toSend = WrapTcp(serialPacket);

            _ = Task.Run(() =>
            {
                try
                {
                    lock (_sendLock)
                    {
                        _networkStream?.Write(toSend, 0, toSend.Length);
                    }
                }
                catch (Exception ex)
                {
                    Debug.WriteLine($"[ViewPro] TX ошибка: {ex.Message}");
                }
            });

            return true;
        }

        private bool SendFrame30(byte[] a1, ushort c1, byte[] e1)
        {
            byte[] payload = new byte[14];
            Array.Copy(a1, 0, payload, 0, Math.Min(a1.Length, 9));
            payload[9] = (byte)((c1 >> 8) & 0xFF);
            payload[10] = (byte)(c1 & 0xFF);
            Array.Copy(e1, 0, payload, 11, Math.Min(e1.Length, 3));
            return SendPacket(BuildSerialPacket(FRAME_COMBINED, payload));
        }

        private bool SendQuery(byte queryType)
        {
            return SendPacket(BuildSerialPacket(FRAME_QUERY, new byte[] { queryType }));
        }

        #endregion

        #region Билдеры

        private byte[] BuildA1(byte mode, int yaw = 0, int pitch = 0)
        {
            byte[] a1 = new byte[9];
            a1[0] = mode;
            a1[1] = (byte)((yaw >> 24) & 0xFF);
            a1[2] = (byte)((yaw >> 16) & 0xFF);
            a1[3] = (byte)((yaw >> 8) & 0xFF);
            a1[4] = (byte)(yaw & 0xFF);
            a1[5] = (byte)((pitch >> 24) & 0xFF);
            a1[6] = (byte)((pitch >> 16) & 0xFF);
            a1[7] = (byte)((pitch >> 8) & 0xFF);
            a1[8] = (byte)(pitch & 0xFF);
            return a1;
        }

        private byte[] BuildA1_RC(int yawPwm, int pitchPwm)
        {
            return BuildA1(A1_RC, Math.Clamp(yawPwm, PWM_MIN, PWM_MAX), Math.Clamp(pitchPwm, PWM_MIN, PWM_MAX));
        }

        private ushort BuildC1(byte cmd = 0, byte sensor = 0, byte zoomSpd = 5, byte lrf = 0)
        {
            if (cmd == 0 && sensor == 0 && lrf == 0) return 0x0000;

            if (sensor == 0) sensor = _currentSensor;
            ushort c1 = 0;
            c1 |= (ushort)(sensor & 0x07);           // биты 0-2
            c1 |= (ushort)((zoomSpd & 0x07) << 3);   // биты 3-5
            c1 |= (ushort)((cmd & 0x7F) << 6);        // биты 6-12
            c1 |= (ushort)((lrf & 0x07) << 13);       // биты 13-15
            return c1;
        }

        private byte[] BuildE1(byte cmd = 0, byte x = 0, byte y = 0)
            => new byte[] { cmd, x, y };

        #endregion

        #region Heartbeat

        private async Task HeartbeatLoop(CancellationToken ct)
        {
            int tick = 0;
            while (!ct.IsCancellationRequested)
            {
                try
                {
                    if (!_userActive || (DateTime.UtcNow - _lastUserCmd).TotalMilliseconds > 200)
                    {
                        _userActive = false;
                        if (tick % 4 == 0)
                            SendQuery(0x43);
                        else
                            SendFrame30(BuildA1(A1_NO_CHANGE), BuildC1(), BuildE1());
                    }
                    tick++;
                    await Task.Delay(500, ct);
                }
                catch (OperationCanceledException) { break; }
                catch (Exception ex) { Debug.WriteLine($"[ViewPro] HB: {ex.Message}"); }
            }
        }

        public void SendHeartbeat() =>
            SendFrame30(BuildA1(A1_NO_CHANGE), BuildC1(), BuildE1());

        #endregion

        #region Мотор

        public void SendMotorOn()
        {
            byte[] a1 = BuildA1(A1_MOTOR_CTRL);
            a1[1] = 0x01;
            SendFrame30(a1, BuildC1(), BuildE1());
            Debug.WriteLine("[ViewPro] Мотор ВКЛ");
        }

        public void SendMotorOff()
        {
            byte[] a1 = BuildA1(A1_MOTOR_CTRL);
            a1[2] = 0x01;
            SendFrame30(a1, BuildC1(), BuildE1());
        }

        #endregion

        #region ═══ УПРАВЛЕНИЕ ПОДВЕСОМ ═══

        /// <summary>
        /// Управление через RC/PWM (0x0D) — как ViewLink
        /// yawPct/pitchPct: -100..+100 → PWM 1000-2000
        /// </summary>
        public void SetGimbalSpeed(int yawPct, int pitchPct)
        {
            _userActive = true;
            _lastUserCmd = DateTime.UtcNow;
            int yawPwm = PWM_CENTER + (yawPct * 500 / 100);
            int pitchPwm = PWM_CENTER - (pitchPct * 500 / 100); // инвертировано: PWM>1500 = вниз у ViewPro
            SendFrame30(BuildA1_RC(yawPwm, pitchPwm), BuildC1(), BuildE1());
        }

        public bool SetGimbalAngles(int yawPwm, int pitchPwm)
        {
            _userActive = true;
            _lastUserCmd = DateTime.UtcNow;
            return SendFrame30(BuildA1_RC(yawPwm, pitchPwm), BuildC1(), BuildE1());
        }

        public bool SetAbsoluteAngle(float yawDeg, float pitchDeg)
        {
            _userActive = true;
            _lastUserCmd = DateTime.UtcNow;
            return SendFrame30(BuildA1(A1_ANGLE_ABS,
                (int)(yawDeg * 65536 / 360),
                (int)(pitchDeg * 65536 / 360)), BuildC1(), BuildE1());
        }

        public void StopGimbal()
        {
            _userActive = false;
            SendFrame30(BuildA1_RC(PWM_CENTER, PWM_CENTER), BuildC1(), BuildE1());
        }

        public bool ReturnToCenter()
        {
            _userActive = false;
            return SendFrame30(BuildA1(A1_HOME), BuildC1(), BuildE1());
        }

        public bool LookDown()
        {
            _userActive = false;
            return SendFrame30(BuildA1(A1_PITCH_DOWN), BuildC1(), BuildE1());
        }

        public bool GimbalCenter() => ReturnToCenter();
        public bool GimbalHome() => ReturnToCenter();
        public bool GimbalLookDown() => LookDown();
        public void GimbalControlSpeed(int y, int p) => SetGimbalSpeed(y, p);
        public void GimbalStop() => StopGimbal();
        public void MoveUp() => SetGimbalSpeed(0, 50);
        public void MoveDown() => SetGimbalSpeed(0, -50);
        public void MoveLeft() => SetGimbalSpeed(-50, 0);
        public void MoveRight() => SetGimbalSpeed(50, 0);
        public void StopMovement() => StopGimbal();

        #endregion

        #region ═══ ЗУМ И ФОКУС ═══

        public bool ZoomIn(int speed = 5)
        {
            _userActive = true; _lastUserCmd = DateTime.UtcNow;
            return SendFrame30(BuildA1(A1_NO_CHANGE), BuildC1(C1_ZOOM_IN, 0, (byte)Math.Clamp(speed, 1, 7)), BuildE1());
        }

        public bool ZoomOut(int speed = 5)
        {
            _userActive = true; _lastUserCmd = DateTime.UtcNow;
            return SendFrame30(BuildA1(A1_NO_CHANGE), BuildC1(C1_ZOOM_OUT, 0, (byte)Math.Clamp(speed, 1, 7)), BuildE1());
        }

        public bool ZoomStop() { _userActive = false; return SendFrame30(BuildA1(A1_NO_CHANGE), BuildC1(C1_STOP), BuildE1()); }
        public bool AutoFocus() => SendFrame30(BuildA1(A1_NO_CHANGE), BuildC1(C1_AUTO_FOCUS), BuildE1());
        public bool FocusFar() => SendFrame30(BuildA1(A1_NO_CHANGE), BuildC1(C1_FOCUS_FAR), BuildE1());
        public bool FocusNear() => SendFrame30(BuildA1(A1_NO_CHANGE), BuildC1(C1_FOCUS_NEAR), BuildE1());
        public bool FocusStop() => ZoomStop();
        public bool FocusAuto() => AutoFocus();

        #endregion

        #region ═══ ФОТО И ВИДЕО ═══

        public bool TakePhoto() { StatusChanged?.Invoke("Фото"); return SendFrame30(BuildA1(A1_NO_CHANGE), BuildC1(C1_PHOTO), BuildE1()); }
        public bool StartRecording() { IsRecording = true; StatusChanged?.Invoke("Запись..."); return SendFrame30(BuildA1(A1_NO_CHANGE), BuildC1(C1_REC_START), BuildE1()); }
        public bool StopRecording() { IsRecording = false; StatusChanged?.Invoke("Стоп запись"); return SendFrame30(BuildA1(A1_NO_CHANGE), BuildC1(C1_REC_STOP), BuildE1()); }
        public bool ToggleRecording() => IsRecording ? StopRecording() : StartRecording();
        public bool RecordToggle() => ToggleRecording();

        #endregion

        #region ═══ ИСТОЧНИК ВИДЕО ═══

        public bool SetVideoSource(byte src) { _currentSensor = src; return SendFrame30(BuildA1(A1_NO_CHANGE), BuildC1(0, src), BuildE1()); }
        public bool SetVideoEO() => SetVideoSource(SRC_EO);
        public bool SetVideoIR() => SetVideoSource(SRC_IR);
        public bool SetVideoEO_IR_PIP() => SetVideoSource(SRC_EO_IR_PIP);
        public bool SetVideoIR_EO_PIP() => SetVideoSource(SRC_IR_EO_PIP);
        public bool SetVideoFusion() => SetVideoSource(SRC_FUSION);
        public bool SetVideoEO_IR_PiP() => SetVideoEO_IR_PIP();
        public bool SetVideoIR_EO_PiP() => SetVideoIR_EO_PIP();
        public bool SetSensorEO() => SetVideoEO();
        public bool SetSensorIR() => SetVideoIR();
        public bool SetSensorEO_IR_PIP() => SetVideoEO_IR_PIP();
        public bool SetSensorIR_EO_PIP() => SetVideoIR_EO_PIP();
        public bool SetSensorFusion() => SetVideoFusion();

        #endregion

        #region ═══ ИК ПАЛИТРА ═══

        public bool SetIRPaletteWhiteHot() => SendFrame30(BuildA1(A1_NO_CHANGE), BuildC1(C1_IR_WHITE), BuildE1());
        public bool SetIRPaletteBlackHot() => SendFrame30(BuildA1(A1_NO_CHANGE), BuildC1(C1_IR_BLACK), BuildE1());
        public bool SetIRPaletteRainbow() => SendFrame30(BuildA1(A1_NO_CHANGE), BuildC1(C1_IR_RAINBOW), BuildE1());
        public bool IRDigitalZoomIn() => SendFrame30(BuildA1(A1_NO_CHANGE), BuildC1(C1_IR_DZOOM_IN), BuildE1());
        public bool IRDigitalZoomOut() => SendFrame30(BuildA1(A1_NO_CHANGE), BuildC1(C1_IR_DZOOM_OUT), BuildE1());
        public bool SetIrDigitalZoom(int level) { if (level > 1) return IRDigitalZoomIn(); return true; }

        #endregion

        #region ═══ ДАЛЬНОМЕР (LRF) ═══

        /// <summary>Однократное измерение дальности</summary>
        public bool LRFMeasureSingle() => SendFrame30(BuildA1(A1_NO_CHANGE), BuildC1(0, _currentSensor, 0, LRF_SINGLE), BuildE1());

        /// <summary>Непрерывное измерение дальности</summary>
        public bool LRFMeasureContinuous() => SendFrame30(BuildA1(A1_NO_CHANGE), BuildC1(0, _currentSensor, 0, LRF_CONTINUOUS), BuildE1());

        /// <summary>Остановить измерение дальности</summary>
        public bool LRFStop() => SendFrame30(BuildA1(A1_NO_CHANGE), BuildC1(0, _currentSensor, 0, LRF_STOP), BuildE1());

        #endregion

        #region ═══ ТРЕКИНГ ═══

        public bool StartTracking(byte src = SRC_EO) => SendFrame30(BuildA1(A1_TRACKING), BuildC1(0, src), BuildE1(E1_START));
        public bool StopTracking(byte src = SRC_EO) => SendFrame30(BuildA1(A1_NO_CHANGE), BuildC1(0, src), BuildE1(E1_STOP));
        public bool EnableSearchMode() => SendFrame30(BuildA1(A1_NO_CHANGE), BuildC1(), BuildE1(E1_SEARCH));
        public bool ToggleAIDetection() => SendFrame30(BuildA1(A1_NO_CHANGE), BuildC1(), BuildE1(E1_AI_TOGGLE));

        public bool SetTrackingPoint(float nx, float ny)
        {
            byte x = (byte)Math.Clamp(nx * 255, 0, 255);
            byte y = (byte)Math.Clamp(ny * 255, 0, 255);
            return SendFrame30(BuildA1(A1_TRACKING), BuildC1(), BuildE1(E1_START, x, y));
        }

        public bool StartTrackingMode() => StartTracking();
        public bool StartTracking(double nx, double ny) => SetTrackingPoint((float)nx, (float)ny);
        public bool TrackAtScreenPosition(int sx, int sy, int sw, int sh) => SetTrackingPoint((float)sx / sw, (float)sy / sh);
        public bool TrackAtPosition(int sx, int sy, byte sz = 0) => TrackAtScreenPosition(sx, sy, 1920, 1080);
        public bool SetTrackingSize(byte s) => true;

        #endregion

        #region Заглушки

        public bool SaveGimbalSettings() => true;
        public bool RestoreGimbalSettings() => true;
        public bool QuerySdCardStatus() => true;
        public bool QuerySdCardFreeSpace() => true;
        public bool QuerySdCardTotalCapacity() => true;
        public bool FormatSdCard() => true;
        public bool SetZoomLevel(int l) => true;

        #endregion

        #region ═══ ПРИЁМ ДАННЫХ ═══

        /// <summary>
        /// Камера отвечает СЫРЫМИ 55 AA DC пакетами (без EB 90!)
        /// Верифицировано Wireshark
        /// </summary>
        private async Task ReceiveLoop(CancellationToken ct)
        {
            byte[] buffer = new byte[1024];
            bool firstData = true;

            try
            {
                while (!ct.IsCancellationRequested && IsConnected && _networkStream != null)
                {
                    int n = await _networkStream.ReadAsync(buffer, 0, buffer.Length, ct);
                    if (n <= 0) break;

                    TotalBytesReceived += n;

                    if (firstData)
                    {
                        Debug.WriteLine($"[ViewPro] ★ ПЕРВЫЕ ДАННЫЕ ОТ КАМЕРЫ! {n} байт");
                        StatusChanged?.Invoke($"Камера отвечает!");
                        firstData = false;
                    }
                    Debug.WriteLine($"[ViewPro] RX [{n}б]");

                    ParseReceivedData(buffer, n);
                    DataReceived?.Invoke(this, buffer[..n]);
                }
            }
            catch (OperationCanceledException) { }
            catch (Exception ex) { Debug.WriteLine($"[ViewPro] RX ошибка: {ex.Message}"); }

            if (firstData)
                Debug.WriteLine("[ViewPro] ⚠ КАМЕРА НЕ ОТПРАВИЛА НИ ОДНОГО БАЙТА!");
        }

        private void ParseReceivedData(byte[] data, int length)
        {
            for (int i = 0; i < length - 5; i++)
            {
                if (data[i] != HEADER_55 || data[i + 1] != HEADER_AA || data[i + 2] != HEADER_DC)
                    continue;

                byte lenCtr = data[i + 3];
                int bodyLen = lenCtr & 0x3F;

                if (bodyLen < 4 || i + 3 + bodyLen > length) continue;

                byte frameId = data[i + 4];

                byte xor = 0;
                for (int j = i + 3; j < i + 3 + bodyLen - 1; j++)
                    xor ^= data[j];
                if (xor != data[i + 3 + bodyLen - 1])
                {
                    Debug.WriteLine($"[ViewPro] RX XOR ошибка frame 0x{frameId:X2}");
                    continue;
                }

                int dataLen = bodyLen - 3;
                int dataOff = i + 5;

                Debug.WriteLine($"[ViewPro] RX Frame 0x{frameId:X2}, {dataLen}б");

                switch (frameId)
                {
                    case FRAME_FEEDBACK:
                        if (dataLen >= 29) ParseFeedback(data, dataOff, dataLen);
                        break;
                    case FRAME_ACK:
                        Debug.WriteLine($"[ViewPro] ACK на Frame 0x{(dataLen > 0 ? data[dataOff] : 0):X2}");
                        break;
                    case FRAME_QUERY:
                        Debug.WriteLine("[ViewPro] Ответ на запрос 0x15");
                        break;
                }

                i += 2 + bodyLen;
            }
        }

        private void ParseFeedback(byte[] data, int off, int len)
        {
            try
            {
                if (off + 29 > data.Length) return;

                float roll = ((data[off + 23] & 0x0F) << 8 | data[off + 24]) * (180f / 4095f) - 90f;
                float yaw = (short)((data[off + 25] << 8) | data[off + 26]) * (360f / 65536f);
                float pitch = -(short)((data[off + 27] << 8) | data[off + 28]) * (360f / 65536f);

                CurrentAngles = new GimbalAngles { Roll = roll, Pitch = pitch, Yaw = yaw };
                AnglesReceived?.Invoke(this, CurrentAngles);

                // D1 секция: LRF дистанция (байты 30-31 = uint16 в дециметрах)
                if (len >= 32 && off + 31 < data.Length)
                {
                    ushort rawDist = (ushort)((data[off + 30] << 8) | data[off + 31]);
                    if (rawDist > 0 && rawDist < 30000) // 0-3000м
                    {
                        float distM = rawDist * 0.1f;
                        CurrentDistance = distM;
                        DistanceReceived?.Invoke(this, distM);
                    }
                }
            }
            catch (Exception ex) { Debug.WriteLine($"[ViewPro] Parse feedback: {ex.Message}"); }
        }

        #endregion

        #region IDisposable

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            Disconnect();
        }

        #endregion
    }

    public class GimbalAngles
    {
        public float Roll { get; set; }
        public float Pitch { get; set; }
        public float Yaw { get; set; }
    }
}
