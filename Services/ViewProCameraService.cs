using System;
using System.Diagnostics;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Tasks;

namespace SimpleDroneGCS.Services
{
    // ─────────────────────────────────────────────────────────────
    //  ViewPro A40TR Pro — полный сервис управления
    //  Протокол: Viewlink V3.3.3
    //  TCP: EB 90 [serial body] [CS]   (CS = sum mod 256)
    //  Serial: 55 AA DC [lenCtr] [frameID] [data] [XOR]
    // ─────────────────────────────────────────────────────────────
    public class ViewProCameraService : IDisposable
    {
        // ── Заголовки ──────────────────────────────────────────
        private const byte H0 = 0x55, H1 = 0xAA, H2 = 0xDC;
        private const byte TCP_EB = 0xEB, TCP_90 = 0x90;

        // ── Frame ID (Console → Camera) ────────────────────────
        private const byte FRAME_30 = 0x30;   // A1+C1+E1 (основная)
        private const byte FRAME_32 = 0x32;   // A1+C1+E1+S1
        private const byte FRAME_QUERY = 0x15; // Запрос конфигурации

        // ── Frame ID (Camera → Console) ────────────────────────
        private const byte FRAME_40 = 0x40;   // T1+F1+B1+D1 (телеметрия)
        private const byte FRAME_ACK = 0xAC;  // ACK

        // ── A1: режимы гимбала ─────────────────────────────────
        private const byte A1_MOTOR = 0x00;  // Motor ON/OFF
        private const byte A1_SPEED = 0x01;  // Скоростной режим (°/сек)
        private const byte A1_HOME = 0x04;  // Домой (центр)
        private const byte A1_TRACKING = 0x06;  // Режим трекинга
        private const byte A1_REL_ANG = 0x09;  // Относительный угол
        private const byte A1_ABS_ANG = 0x0B;  // Абсолютный угол
        private const byte A1_RC = 0x0D;  // RC-режим (PWM)
        private const byte A1_NOOP = 0x0F;  // Без изменений
        private const byte A1_FOLLOW_YAW_ON = 0x03;
        private const byte A1_FOLLOW_YAW_OFF = 0x0A;

        // ── PWM диапазоны ──────────────────────────────────────
        private const int PWM_MIN = 1100;
        private const int PWM_CENTER = 1500;
        private const int PWM_MAX = 1900;

        // ── C1: команды камеры (биты 6–12) ────────────────────
        private const byte C1_STOP = 0x01;
        private const byte C1_BRIGHTNESS_UP = 0x02;
        private const byte C1_BRIGHTNESS_DN = 0x03;
        private const byte C1_ZOOM_OUT = 0x08;  // FOV+, уменьшить зум
        private const byte C1_ZOOM_IN = 0x09;  // FOV-, увеличить зум
        private const byte C1_FOCUS_FAR = 0x0A;
        private const byte C1_FOCUS_NEAR = 0x0B;
        private const byte C1_IR_WHITE_HOT = 0x0E;
        private const byte C1_IR_BLACK_HOT = 0x0F;
        private const byte C1_IR_RAINBOW = 0x12;
        private const byte C1_PHOTO = 0x13;
        private const byte C1_REC_START = 0x14;
        private const byte C1_REC_STOP = 0x15;
        private const byte C1_MODE_PHOTO = 0x16;
        private const byte C1_MODE_VIDEO = 0x17;
        private const byte C1_AUTOFOCUS = 0x19;
        private const byte C1_FOCUS_MANUAL = 0x1A;
        private const byte C1_IR_DZOOM_IN = 0x1B;
        private const byte C1_IR_DZOOM_OUT = 0x1C;
        private const byte C1_SD_STATUS = 0x1E;
        private const byte C1_SD_TOTAL = 0x1F;
        private const byte C1_SD_FREE = 0x20;

        // ── C2: расширенные команды (infrequently used) ────────
        private const byte C2_EO_DZOOM_ON = 0x06;
        private const byte C2_EO_DZOOM_OFF = 0x07;
        private const byte C2_IR_COLORBAR_ON = 0x12;
        private const byte C2_IR_COLORBAR_OFF = 0x13;
        private const byte C2_EO_FLIP_OFF = 0x14;
        private const byte C2_EO_FLIP_ON = 0x15;
        private const byte C2_DEFOG_OFF = 0x16;
        private const byte C2_DEFOG_ON = 0x17;
        private const byte C2_OSD_ON = 0x18;
        private const byte C2_OSD_OFF = 0x19;
        private const byte C2_NIR_ON = 0x4A;  // Near-infrared
        private const byte C2_NIR_OFF = 0x4B;
        private const byte C2_REBOOT = 0x4E;
        private const byte C2_SET_ZOOM = 0x53;  // + 2 байта zoom*10

        // ── LRF (биты 13–15 в C1) ─────────────────────────────
        private const byte LRF_NONE = 0x00;
        private const byte LRF_SINGLE = 0x01;
        private const byte LRF_CONTINUOUS = 0x02;
        private const byte LRF_LPCL = 0x03;
        private const byte LRF_STOP = 0x05;

        // ── Видеоисточники (биты 0–2 в C1) ────────────────────
        public const byte SRC_EO = 0x01;
        public const byte SRC_IR = 0x02;
        public const byte SRC_EO_IR = 0x03;  // EO + IR PIP
        public const byte SRC_IR_EO = 0x04;  // IR + EO PIP
        public const byte SRC_FUSION = 0x06;

        // ── E1: трекинг ────────────────────────────────────────
        private const byte E1_NONE = 0x00;
        private const byte E1_STOP = 0x01;
        private const byte E1_SEARCH = 0x02;
        private const byte E1_START = 0x03;
        private const byte E1_AI_TOGGLE = 0x05;

        // ── Tracking sizes (E2 infrequently used) ─────────────
        public const byte TRACK_SIZE_SMALL = 0x24;
        public const byte TRACK_SIZE_MID = 0x28;
        public const byte TRACK_SIZE_LARGE = 0x30;
        public const byte TRACK_SIZE_AUTO = 0x2C;

        // ══════════════════════════════════════════════════════
        //  Состояние
        // ══════════════════════════════════════════════════════
        private TcpClient? _tcp;
        private NetworkStream? _stream;
        private CancellationTokenSource? _cts;
        private Task? _receiveTask;
        private Task? _heartbeatTask;
        private readonly object _sendLock = new();
        private byte _frameCounter = 0;
        private bool _disposed = false;
        private bool _isConnected = false;
        private volatile bool _userActive = false;
        private DateTime _lastUserCmd = DateTime.MinValue;

        // Текущие значения (для toggle-команд)
        private byte _currentSensor = SRC_EO;
        private bool _isRecording = false;
        private bool _isOsdOn = true;
        private bool _isDefogOn = false;
        private bool _isEoDzoomOn = false;
        private bool _isNirOn = false;
        private bool _isEoFlipOn = false;
        private bool _isIrColorBarOn = false;
        private bool _isFollowYaw = false;
        private byte _currentIrPalette = 0; // 0=White,1=Black,2=Rainbow
        private byte _trackSize = TRACK_SIZE_AUTO;

        // ══════════════════════════════════════════════════════
        //  Публичные свойства
        // ══════════════════════════════════════════════════════
        public string IpAddress { get; set; } = "192.168.2.119";
        public int Port { get; set; } = 2000;
        public int RtspPort { get; set; } = 554;
        public string RtspUrl => $"rtsp://{IpAddress}:{RtspPort}";

        public bool IsConnected => _isConnected && (_tcp?.Connected ?? false);
        public bool IsRecording => _isRecording;
        public bool IsOsdOn => _isOsdOn;
        public bool IsDefogOn => _isDefogOn;
        public bool IsFollowYaw => _isFollowYaw;
        public bool IsIrColorBarOn => _isIrColorBarOn;
        public bool IsEoFlipOn => _isEoFlipOn;
        public bool IsNirOn => _isNirOn;
        public bool IsEoDzoomOn => _isEoDzoomOn;
        public byte CurrentSensor => _currentSensor;

        public GimbalAngles CurrentAngles { get; private set; } = new();
        public float CurrentDistance { get; private set; } = 0;
        public long TotalBytesRx { get; private set; } = 0;

        // Телеметрия оптики (из D1)
        public byte CurrentIrPalette { get; private set; } = 0;
        public byte IrDigitalZoom { get; private set; } = 1;
        public byte EoDigitalZoom { get; private set; } = 1;
        public bool IsIrBlackHot { get; private set; } = false;
        public bool RecordingActive { get; private set; } = false;
        public byte TrackStatus { get; private set; } = 0;

        // ══════════════════════════════════════════════════════
        //  События
        // ══════════════════════════════════════════════════════
        public event EventHandler<bool>? ConnectionChanged;
        public event EventHandler<GimbalAngles>? AnglesReceived;
        public event EventHandler<float>? DistanceReceived;
        public event EventHandler<CameraStatus>? StatusUpdated;  // D1 телеметрия
        public event EventHandler<string>? ErrorOccurred;
        public event Action<string>? StatusChanged;

        // ══════════════════════════════════════════════════════
        //  Подключение
        // ══════════════════════════════════════════════════════
        public async Task<bool> ConnectAsync(string ip, int port)
        {
            IpAddress = ip; Port = port;
            return await ConnectAsync();
        }

        public async Task<bool> ConnectAsync()
        {
            try
            {
                if (IsConnected) Disconnect();

                _tcp = new TcpClient { NoDelay = true };
                _tcp.ReceiveTimeout = 5000;
                _tcp.SendTimeout = 3000;

                StatusChanged?.Invoke($"Подключение к {IpAddress}:{Port}...");
                Debug.WriteLine($"[ViewPro] Подключение {IpAddress}:{Port}");

                var conn = _tcp.ConnectAsync(IpAddress, Port);
                if (await Task.WhenAny(conn, Task.Delay(5000)) != conn)
                    throw new TimeoutException("Таймаут подключения");
                await conn;

                _stream = _tcp.GetStream();
                _isConnected = true;
                _cts?.Dispose();
                _cts = new CancellationTokenSource();
                TotalBytesRx = 0;

                _receiveTask = Task.Run(() => ReceiveLoop(_cts.Token));
                _heartbeatTask = Task.Run(() => HeartbeatLoop(_cts.Token));

                await Task.Delay(100);
                SendQuery(0x43);          // Запрос конфигурации
                await Task.Delay(100);
                SendMotorOn();            // Включить моторы

                ConnectionChanged?.Invoke(this, true);
                StatusChanged?.Invoke("Подключено");
                Debug.WriteLine("[ViewPro] Подключено");
                return true;
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[ViewPro] Ошибка подключения: {ex.Message}");
                ErrorOccurred?.Invoke(this, ex.Message);
                StatusChanged?.Invoke($"Ошибка: {ex.Message}");
                return false;
            }
        }

        public void Disconnect()
        {
            try
            {
                _cts?.Cancel();
                _cts?.Dispose();
                _cts = null;
                _stream?.Close();
                _tcp?.Close();
                _stream = null;
                _tcp = null;
                _isConnected = false;
                ConnectionChanged?.Invoke(this, false);
                StatusChanged?.Invoke("Отключено");
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[ViewPro] Disconnect: {ex.Message}");
            }
        }

        // ══════════════════════════════════════════════════════
        //  Построение пакетов
        // ══════════════════════════════════════════════════════

        // Serial packet: 55 AA DC [lenCtr] [frameID] [data] [XOR]
        private byte[] BuildSerial(byte frameId, byte[] data)
        {
            int bodyLen = data.Length + 3; // lenCtr + frameId + data + XOR
            byte lenCtr = (byte)(((_frameCounter & 0x03) << 6) | (bodyLen & 0x3F));
            _frameCounter = (byte)((_frameCounter + 1) & 0x03);

            byte[] pkt = new byte[3 + 1 + 1 + data.Length + 1];
            pkt[0] = H0; pkt[1] = H1; pkt[2] = H2;
            pkt[3] = lenCtr;
            pkt[4] = frameId;
            Array.Copy(data, 0, pkt, 5, data.Length);

            byte xor = 0;
            for (int i = 3; i < pkt.Length - 1; i++) xor ^= pkt[i];
            pkt[^1] = xor;
            return pkt;
        }

        // TCP wrapper: EB 90 [serial body] [CS]
        // CS = сумма всех байт serial body mod 256
        // ВАЖНО: без байта длины — прямо данные после 90
        private byte[] WrapTcp(byte[] serial)
        {
            byte cs = 0;
            foreach (byte b in serial) cs += b;

            byte[] out_ = new byte[2 + serial.Length + 1];
            out_[0] = TCP_EB;
            out_[1] = TCP_90;
            Array.Copy(serial, 0, out_, 2, serial.Length);
            out_[^1] = cs;
            return out_;
        }

        private bool Send(byte[] serial)
        {
            if (!IsConnected || _stream == null) return false;
            byte[] toSend = WrapTcp(serial);
            try
            {
                lock (_sendLock)
                {
                    _stream?.Write(toSend, 0, toSend.Length);
                }
                return true;
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[ViewPro] TX ошибка: {ex.Message}");
                ErrorOccurred?.Invoke(this, ex.Message);
                return false;
            }
        }

        // ── A1: 9 байт, параметры по 2 байта big-endian ───────
        private byte[] A1(byte mode, int p1 = 0, int p2 = 0, int p3 = 0, int p4 = 0)
        {
            byte[] a = new byte[9];
            a[0] = mode;
            a[1] = (byte)((p1 >> 8) & 0xFF); a[2] = (byte)(p1 & 0xFF);
            a[3] = (byte)((p2 >> 8) & 0xFF); a[4] = (byte)(p2 & 0xFF);
            a[5] = (byte)((p3 >> 8) & 0xFF); a[6] = (byte)(p3 & 0xFF);
            a[7] = (byte)((p4 >> 8) & 0xFF); a[8] = (byte)(p4 & 0xFF);
            return a;
        }

        // ── C1: 2 байта big-endian ─────────────────────────────
        // bits 0-2: sensor, 3-5: zoomSpd, 6-12: cmd, 13-15: lrf
        private ushort C1(byte cmd = 0, byte sensor = 0, byte zoomSpd = 5, byte lrf = 0)
        {
            if (cmd == 0 && sensor == 0 && lrf == 0) return 0;
            if (sensor == 0) sensor = _currentSensor;
            ushort c = 0;
            c |= (ushort)(sensor & 0x07);
            c |= (ushort)((zoomSpd & 0x07) << 3);
            c |= (ushort)((cmd & 0x7F) << 6);
            c |= (ushort)((lrf & 0x07) << 13);
            return c;
        }

        // ── E1: 3 байта [cmd, x, y] ────────────────────────────
        private byte[] E1(byte cmd = 0, byte x = 0, byte y = 0) => new[] { cmd, x, y };

        // ── Frame 0x30 = A1(9) + C1(2) + E1(3) ────────────────
        private bool Frame30(byte[] a1, ushort c1, byte[] e1)
        {
            byte[] payload = new byte[14];
            Array.Copy(a1, 0, payload, 0, 9);
            payload[9] = (byte)((c1 >> 8) & 0xFF);
            payload[10] = (byte)(c1 & 0xFF);
            Array.Copy(e1, 0, payload, 11, 3);
            return Send(BuildSerial(FRAME_30, payload));
        }

        // ── Frame 0x31 = A2(2) + C2(3) + E2(5) ────────────────
        private bool Frame31_C2(byte c2cmd, byte c2p1 = 0, byte c2p2 = 0)
        {
            // A2=2, C2=3, E2=5 → total 10
            byte[] payload = new byte[10];
            // A2: byte1=0 (no action), byte2=0
            payload[0] = 0x00; payload[1] = 0x00;
            // C2: cmd + 2 params
            payload[2] = c2cmd; payload[3] = c2p1; payload[4] = c2p2;
            // E2: all zeros
            return Send(BuildSerial(0x31, payload));
        }

        private bool SendQuery(byte queryType) =>
            Send(BuildSerial(FRAME_QUERY, new[] { queryType }));

        // ══════════════════════════════════════════════════════
        //  Heartbeat
        // ══════════════════════════════════════════════════════
        private async Task HeartbeatLoop(CancellationToken ct)
        {
            int tick = 0;
            while (!ct.IsCancellationRequested)
            {
                try
                {
                    bool userIdle = !_userActive ||
                        (DateTime.UtcNow - _lastUserCmd).TotalMilliseconds > 200;

                    if (userIdle)
                    {
                        _userActive = false;
                        if (tick % 4 == 0)
                            SendQuery(0x43);
                        else
                            Frame30(A1(A1_NOOP), C1(), E1());
                    }
                    tick++;
                    await Task.Delay(500, ct);
                }
                catch (OperationCanceledException) { break; }
                catch (Exception ex) { Debug.WriteLine($"[ViewPro] HB: {ex.Message}"); }
            }
        }

        private void UserCmd() { _userActive = true; _lastUserCmd = DateTime.UtcNow; }

        // ══════════════════════════════════════════════════════
        //  Приём и парсинг
        // ══════════════════════════════════════════════════════
        private async Task ReceiveLoop(CancellationToken ct)
        {
            byte[] buf = new byte[2048];
            bool first = true;
            try
            {
                while (!ct.IsCancellationRequested && IsConnected && _stream != null)
                {
                    int n = await _stream.ReadAsync(buf, 0, buf.Length, ct);
                    if (n <= 0)
                    {
                        _isConnected = false;
                        ConnectionChanged?.Invoke(this, false);
                        StatusChanged?.Invoke("Соединение закрыто");
                        break;
                    }
                    TotalBytesRx += n;
                    if (first)
                    {
                        StatusChanged?.Invoke("Камера отвечает");
                        first = false;
                    }
                    Parse(buf, n);
                }
            }
            catch (OperationCanceledException) { }
            catch (Exception ex)
            {
                _isConnected = false;
                ConnectionChanged?.Invoke(this, false);
                Debug.WriteLine($"[ViewPro] RX ошибка: {ex.Message}");
            }
            finally
            {
                // Гарантируем уведомление при любом выходе из loop
                if (_isConnected)
                {
                    _isConnected = false;
                    ConnectionChanged?.Invoke(this, false);
                    StatusChanged?.Invoke("Соединение потеряно");
                }
            }
        }

        private void Parse(byte[] data, int len)
        {
            for (int i = 0; i < len - 5; i++)
            {
                if (data[i] != H0 || data[i + 1] != H1 || data[i + 2] != H2) continue;

                byte lenCtr = data[i + 3];
                int bodyLen = lenCtr & 0x3F;
                if (bodyLen < 4 || i + 3 + bodyLen > len) continue;

                byte frameId = data[i + 4];

                // Проверка XOR
                byte xor = 0;
                for (int j = i + 3; j < i + 3 + bodyLen - 1; j++) xor ^= data[j];
                if (xor != data[i + 3 + bodyLen - 1])
                {
                    Debug.WriteLine($"[ViewPro] XOR ошибка frame 0x{frameId:X2}");
                    continue;
                }

                int dLen = bodyLen - 3;
                int dOff = i + 5;

                switch (frameId)
                {
                    case FRAME_40: // T1+F1+B1+D1
                        if (dLen >= 29) ParseFeedback(data, dOff, dLen);
                        break;
                    case FRAME_ACK:
                        Debug.WriteLine($"[ViewPro] ACK frame 0x{(dLen > 0 ? data[dOff] : 0):X2}");
                        break;
                }

                i += 2 + bodyLen;
            }
        }

        // T1(22) + F1(1) + B1(6) + D1(12) = 41 байт
        private void ParseFeedback(byte[] data, int off, int len)
        {
            try
            {
                // ── B1: Servo status (начинается с off+23) ──
                // Byte 0 (off+23): bits 0-3=Roll bits 8-11, bits 4-7=servo status
                // Byte 1 (off+24): Roll bits 0-7
                // Bytes 2-3 (off+25..26): Yaw angle  (signed, 1bit=360/65536°)
                // Bytes 4-5 (off+27..28): Tilt angle (signed, 1bit=360/65536°)
                if (off + 28 >= data.Length) return;

                int rollRaw = ((data[off + 23] & 0x0F) << 8) | data[off + 24];
                float roll = rollRaw * (180f / 4095f) - 90f;
                float yaw = (short)((data[off + 25] << 8) | data[off + 26]) * (360f / 65536f);
                float pitch = -(short)((data[off + 27] << 8) | data[off + 28]) * (360f / 65536f);

                CurrentAngles = new GimbalAngles { Roll = roll, Yaw = yaw, Pitch = pitch };
                AnglesReceived?.Invoke(this, CurrentAngles);

                // ── D1: Optical status (начинается с off+29) ──
                // Byte 0 (off+29): bits 0-2=sensor, bits 3-6=IR Dzoom, bit7=black hot
                // Byte 1 (off+30): bit0=LRF counter, bits 2-7=LRF latency
                // Bytes 2-3 (off+31..32): rec status, EO Dzoom, track status
                // Bytes 4-5 (off+33..34): LRF distance (unsigned, 0.1м/бит)
                if (len >= 35 && off + 34 < data.Length)
                {
                    byte d0 = data[off + 29];
                    byte reportedSensor = (byte)(d0 & 0x07);
                    if (reportedSensor != 0) _currentSensor = reportedSensor; // 0 = "no change"
                    IrDigitalZoom = (byte)(((d0 >> 3) & 0x0F) + 1);
                    IsIrBlackHot = (d0 & 0x80) != 0;
                    CurrentIrPalette = IsIrBlackHot ? (byte)1 : (byte)0;

                    byte d2 = data[off + 31];
                    RecordingActive = (d2 & 0x03) == 1;
                    _isRecording = RecordingActive; // sync internal state from camera telemetry
                    EoDigitalZoom = (byte)(((d2 >> 6) & 0x0F) + 1);
                    TrackStatus = (byte)((d2 >> 2) & 0x0F);

                    ushort rawDist = (ushort)((data[off + 33] << 8) | data[off + 34]);
                    if (rawDist > 0 && rawDist < 30000)
                    {
                        CurrentDistance = rawDist * 0.1f;
                        DistanceReceived?.Invoke(this, CurrentDistance);
                    }

                    StatusUpdated?.Invoke(this, new CameraStatus
                    {
                        Sensor = _currentSensor,
                        IsRecording = RecordingActive,
                        IrDigitalZoom = IrDigitalZoom,
                        EoDigitalZoom = EoDigitalZoom,
                        IsIrBlackHot = IsIrBlackHot,
                        TrackStatus = TrackStatus,
                        Distance = CurrentDistance
                    });
                }
            }
            catch (Exception ex) { Debug.WriteLine($"[ViewPro] ParseFeedback: {ex.Message}"); }
        }

        // ══════════════════════════════════════════════════════
        //  1. УПРАВЛЕНИЕ ГИМБАЛОМ
        // ══════════════════════════════════════════════════════

        /// <summary>Motor ON</summary>
        public bool SendMotorOn()
        {
            StatusChanged?.Invoke("Мотор ВКЛ");
            return Frame30(A1(A1_MOTOR, 0x0100), C1(), E1());
        }

        /// <summary>Motor OFF</summary>
        public bool SendMotorOff()
        {
            StatusChanged?.Invoke("Мотор ВЫКЛ");
            return Frame30(A1(A1_MOTOR, 0x0001), C1(), E1());
        }

        /// <summary>Управление скоростью гимбала через RC (PWM).
        /// yawPct, pitchPct: -100..+100 процентов</summary>
        public bool SetGimbalSpeed(int yawPct, int pitchPct)
        {
            UserCmd();
            int yawPwm = PWM_CENTER + (yawPct * (PWM_MAX - PWM_CENTER) / 100);
            int pitchPwm = PWM_CENTER - (pitchPct * (PWM_MAX - PWM_CENTER) / 100);
            yawPwm = Math.Clamp(yawPwm, PWM_MIN, PWM_MAX);
            pitchPwm = Math.Clamp(pitchPwm, PWM_MIN, PWM_MAX);
            // A1_RC: P1=скор.Yaw(0=дефолт), P2=PWM Yaw, P3=скор.Pitch(0=дефолт), P4=PWM Pitch
            return Frame30(A1(A1_RC, 0, yawPwm, 0, pitchPwm), C1(), E1());
        }

        /// <summary>Прямое управление скоростью в °/сек.
        /// yaw/pitch: -3600..+3600 (единица = 0.01°/сек)</summary>
        public bool SetGimbalSpeedDeg(int yawCentideg, int pitchCentideg)
        {
            UserCmd();
            return Frame30(A1(A1_SPEED, yawCentideg, pitchCentideg), C1(), E1());
        }

        /// <summary>Остановить гимбал</summary>
        public bool StopGimbal()
        {
            _userActive = false;
            return Frame30(A1(A1_RC, 0, PWM_CENTER, 0, PWM_CENTER), C1(), E1());
        }

        /// <summary>Домой (центр)</summary>
        public bool ReturnToCenter()
        {
            _userActive = false;
            StatusChanged?.Invoke("Гимбал → центр");
            return Frame30(A1(A1_HOME), C1(), E1());
        }

        /// <summary>Смотреть вертикально вниз</summary>
        public bool LookDown()
        {
            // Абсолютный угол: Pitch = -90°
            int pitchAngle = (int)(-90f * 65536 / 360);
            UserCmd();
            StatusChanged?.Invoke("Гимбал → вниз");
            return Frame30(A1(A1_ABS_ANG, 0, pitchAngle), C1(), E1());
        }

        /// <summary>Абсолютный угол (от позиции HOME)</summary>
        public bool SetAbsoluteAngle(float yawDeg, float pitchDeg)
        {
            UserCmd();
            int yaw = (int)(yawDeg * 65536 / 360);
            int pitch = (int)(pitchDeg * 65536 / 360);
            return Frame30(A1(A1_ABS_ANG, yaw, pitch), C1(), E1());
        }

        /// <summary>Относительный угол (от текущей позиции)</summary>
        public bool SetRelativeAngle(float yawDeg, float pitchDeg, float speedDegSec = 30)
        {
            UserCmd();
            int yawAng = (int)(yawDeg * 65536 / 360);
            int pitchAng = (int)(pitchDeg * 65536 / 360);
            int speedY = (int)(speedDegSec * 10);  // 0.1°/сек
            int speedP = (int)(speedDegSec * 10);
            // A1_REL_ANG: P1=скор.Yaw, P2=угол Yaw, P3=скор.Pitch, P4=угол Pitch
            return Frame30(A1(A1_REL_ANG, speedY, yawAng, speedP, pitchAng), C1(), E1());
        }

        /// <summary>Follow Yaw вкл/выкл</summary>
        public bool SetFollowYaw(bool enable)
        {
            _isFollowYaw = enable;
            byte mode = enable ? A1_FOLLOW_YAW_ON : A1_FOLLOW_YAW_OFF;
            StatusChanged?.Invoke(enable ? "Follow Yaw: ВКЛ" : "Follow Yaw: ВЫКЛ");
            return Frame30(A1(mode), C1(), E1());
        }

        public bool ToggleFollowYaw() => SetFollowYaw(!_isFollowYaw);

        // Алиасы
        public bool GimbalCenter() => ReturnToCenter();
        public bool GimbalLookDown() => LookDown();
        public void MoveUp() => SetGimbalSpeed(0, 50);
        public void MoveDown() => SetGimbalSpeed(0, -50);
        public void MoveLeft() => SetGimbalSpeed(-50, 0);
        public void MoveRight() => SetGimbalSpeed(50, 0);
        public void StopMovement() => StopGimbal();

        // ══════════════════════════════════════════════════════
        //  2. ЗУМИРОВАНИЕ И ФОКУС
        // ══════════════════════════════════════════════════════

        public bool ZoomIn(int speed = 5)
        {
            UserCmd();
            return Frame30(A1(A1_NOOP), C1(C1_ZOOM_IN, 0, (byte)Math.Clamp(speed, 1, 7)), E1());
        }

        public bool ZoomOut(int speed = 5)
        {
            UserCmd();
            return Frame30(A1(A1_NOOP), C1(C1_ZOOM_OUT, 0, (byte)Math.Clamp(speed, 1, 7)), E1());
        }

        public bool ZoomStop()
        {
            _userActive = false;
            return Frame30(A1(A1_NOOP), C1(C1_STOP), E1());
        }

        /// <summary>Установить зум напрямую (1.0–40.0x)</summary>
        public bool SetZoomLevel(float zoomTimes)
        {
            UserCmd();
            // C2 команда 0x53, параметр = zoom * 10 (2 байта)
            ushort val = (ushort)(zoomTimes * 10);
            return Frame31_C2(C2_SET_ZOOM, (byte)(val >> 8), (byte)(val & 0xFF));
        }

        public bool AutoFocus() => Frame30(A1(A1_NOOP), C1(C1_AUTOFOCUS), E1());
        public bool ManualFocus() => Frame30(A1(A1_NOOP), C1(C1_FOCUS_MANUAL), E1());
        public bool FocusFar() => Frame30(A1(A1_NOOP), C1(C1_FOCUS_FAR), E1());
        public bool FocusNear() => Frame30(A1(A1_NOOP), C1(C1_FOCUS_NEAR), E1());
        public bool FocusStop() => ZoomStop();

        // ── EO цифровой зум ───────────────────────────────────
        public bool SetEoDzoom(bool enable)
        {
            _isEoDzoomOn = enable;
            StatusChanged?.Invoke(enable ? "EO Dzoom: ВКЛ" : "EO Dzoom: ВЫКЛ");
            return Frame31_C2(enable ? C2_EO_DZOOM_ON : C2_EO_DZOOM_OFF);
        }
        public bool ToggleEoDzoom() => SetEoDzoom(!_isEoDzoomOn);

        // ── IR цифровой зум ───────────────────────────────────
        public bool IrDzoomIn() => Frame30(A1(A1_NOOP), C1(C1_IR_DZOOM_IN), E1());
        public bool IrDzoomOut() => Frame30(A1(A1_NOOP), C1(C1_IR_DZOOM_OUT), E1());

        // ══════════════════════════════════════════════════════
        //  3. ВИДЕОИСТОЧНИК
        // ══════════════════════════════════════════════════════

        public bool SetVideoSource(byte src)
        {
            _currentSensor = src;
            string name = src switch
            {
                SRC_EO => "EO",
                SRC_IR => "IR",
                SRC_EO_IR => "EO+IR PIP",
                SRC_IR_EO => "IR+EO PIP",
                SRC_FUSION => "Fusion",
                _ => $"Sensor {src}"
            };
            StatusChanged?.Invoke($"Видео: {name}");
            return Frame30(A1(A1_NOOP), C1(0, src), E1());
        }

        public bool SetVideoEO() => SetVideoSource(SRC_EO);
        public bool SetVideoIR() => SetVideoSource(SRC_IR);
        public bool SetVideoEO_IR_PIP() => SetVideoSource(SRC_EO_IR);
        public bool SetVideoIR_EO_PIP() => SetVideoSource(SRC_IR_EO);
        public bool SetVideoFusion() => SetVideoSource(SRC_FUSION);

        // ══════════════════════════════════════════════════════
        //  4. IR ПАЛИТРЫ
        // ══════════════════════════════════════════════════════

        public bool SetIRPaletteWhiteHot()
        {
            _currentIrPalette = 0;
            StatusChanged?.Invoke("IR: White Hot");
            return Frame30(A1(A1_NOOP), C1(C1_IR_WHITE_HOT), E1());
        }

        public bool SetIRPaletteBlackHot()
        {
            _currentIrPalette = 1;
            StatusChanged?.Invoke("IR: Black Hot");
            return Frame30(A1(A1_NOOP), C1(C1_IR_BLACK_HOT), E1());
        }

        public bool SetIRPaletteRainbow()
        {
            _currentIrPalette = 2;
            StatusChanged?.Invoke("IR: Rainbow");
            return Frame30(A1(A1_NOOP), C1(C1_IR_RAINBOW), E1());
        }

        public bool NextIRPalette()
        {
            _currentIrPalette = (byte)((_currentIrPalette + 1) % 3);
            return _currentIrPalette switch
            {
                0 => SetIRPaletteWhiteHot(),
                1 => SetIRPaletteBlackHot(),
                _ => SetIRPaletteRainbow()
            };
        }

        // ── IR цветовая шкала (термометрия) ───────────────────
        public bool SetIRColorBar(bool enable)
        {
            _isIrColorBarOn = enable;
            StatusChanged?.Invoke(enable ? "IR шкала: ВКЛ" : "IR шкала: ВЫКЛ");
            return Frame31_C2(enable ? C2_IR_COLORBAR_ON : C2_IR_COLORBAR_OFF);
        }
        public bool ToggleIRColorBar() => SetIRColorBar(!_isIrColorBarOn);

        // ══════════════════════════════════════════════════════
        //  5. ФОТО И ЗАПИСЬ
        // ══════════════════════════════════════════════════════

        public bool TakePhoto()
        {
            StatusChanged?.Invoke("📸 Снимок");
            return Frame30(A1(A1_NOOP), C1(C1_PHOTO), E1());
        }

        public bool StartRecording()
        {
            _isRecording = true;
            StatusChanged?.Invoke("⏺ Запись...");
            return Frame30(A1(A1_NOOP), C1(C1_REC_START), E1());
        }

        public bool StopRecording()
        {
            _isRecording = false;
            StatusChanged?.Invoke("⏹ Запись остановлена");
            return Frame30(A1(A1_NOOP), C1(C1_REC_STOP), E1());
        }

        public bool ToggleRecording() => _isRecording ? StopRecording() : StartRecording();

        public bool SwitchToPhotoMode()
        {
            StatusChanged?.Invoke("Режим: Фото");
            return Frame30(A1(A1_NOOP), C1(C1_MODE_PHOTO), E1());
        }

        public bool SwitchToVideoMode()
        {
            StatusChanged?.Invoke("Режим: Видео");
            return Frame30(A1(A1_NOOP), C1(C1_MODE_VIDEO), E1());
        }

        // ══════════════════════════════════════════════════════
        //  6. ИЗОБРАЖЕНИЕ
        // ══════════════════════════════════════════════════════

        public bool BrightnessUp() => Frame30(A1(A1_NOOP), C1(C1_BRIGHTNESS_UP), E1());
        public bool BrightnessDown() => Frame30(A1(A1_NOOP), C1(C1_BRIGHTNESS_DN), E1());

        public bool SetOSD(bool enable)
        {
            _isOsdOn = enable;
            StatusChanged?.Invoke(enable ? "OSD: ВКЛ" : "OSD: ВЫКЛ");
            return Frame31_C2(enable ? C2_OSD_ON : C2_OSD_OFF);
        }
        public bool ToggleOSD() => SetOSD(!_isOsdOn);

        public bool SetDefog(bool enable)
        {
            _isDefogOn = enable;
            StatusChanged?.Invoke(enable ? "Дефог: ВКЛ" : "Дефог: ВЫКЛ");
            return Frame31_C2(enable ? C2_DEFOG_ON : C2_DEFOG_OFF);
        }
        public bool ToggleDefog() => SetDefog(!_isDefogOn);

        public bool SetEOFlip(bool enable)
        {
            _isEoFlipOn = enable;
            StatusChanged?.Invoke(enable ? "Flip: ВКЛ" : "Flip: ВЫКЛ");
            return Frame31_C2(enable ? C2_EO_FLIP_ON : C2_EO_FLIP_OFF);
        }
        public bool ToggleEOFlip() => SetEOFlip(!_isEoFlipOn);

        public bool SetNIR(bool enable)
        {
            _isNirOn = enable;
            StatusChanged?.Invoke(enable ? "NIR: ВКЛ" : "NIR: ВЫКЛ");
            return Frame31_C2(enable ? C2_NIR_ON : C2_NIR_OFF);
        }
        public bool ToggleNIR() => SetNIR(!_isNirOn);

        // ══════════════════════════════════════════════════════
        //  7. LRF ДАЛЬНОМЕР
        // ══════════════════════════════════════════════════════

        public bool LRFSingle()
        {
            StatusChanged?.Invoke("LRF: одиночный замер");
            return Frame30(A1(A1_NOOP), C1(0, _currentSensor, 0, LRF_SINGLE), E1());
        }

        public bool LRFContinuous()
        {
            StatusChanged?.Invoke("LRF: непрерывный замер");
            return Frame30(A1(A1_NOOP), C1(0, _currentSensor, 0, LRF_CONTINUOUS), E1());
        }

        public bool LRFLpcl()
        {
            StatusChanged?.Invoke("LRF: LPCL режим");
            return Frame30(A1(A1_NOOP), C1(0, _currentSensor, 0, LRF_LPCL), E1());
        }

        public bool LRFStop()
        {
            StatusChanged?.Invoke("LRF: стоп");
            return Frame30(A1(A1_NOOP), C1(0, _currentSensor, 0, LRF_STOP), E1());
        }

        // ══════════════════════════════════════════════════════
        //  8. AI ТРЕКИНГ
        // ══════════════════════════════════════════════════════

        public bool StartTracking()
        {
            StatusChanged?.Invoke("Трекинг: ВКЛ");
            return Frame30(A1(A1_TRACKING), C1(0, _currentSensor), E1(E1_START));
        }

        public bool StopTracking()
        {
            StatusChanged?.Invoke("Трекинг: ВЫКЛ");
            return Frame30(A1(A1_NOOP), C1(0, _currentSensor), E1(E1_STOP));
        }

        public bool EnableSearchMode()
        {
            StatusChanged?.Invoke("Поиск: ВКЛ");
            return Frame30(A1(A1_NOOP), C1(), E1(E1_SEARCH));
        }

        public bool ToggleAIDetection()
        {
            StatusChanged?.Invoke("AI: toggle");
            return Frame30(A1(A1_NOOP), C1(), E1(E1_AI_TOGGLE));
        }

        /// <summary>Захватить цель по нормализованным координатам (0.0–1.0)</summary>
        public bool SetTrackingPoint(float normX, float normY)
        {
            byte x = (byte)Math.Clamp(normX * 255, 0, 255);
            byte y = (byte)Math.Clamp(normY * 255, 0, 255);
            StatusChanged?.Invoke($"Цель: ({normX:F2}, {normY:F2})");
            return Frame30(A1(A1_TRACKING), C1(0, _currentSensor), E1(E1_START, x, y));
        }

        /// <summary>Выбор размера рамки трекинга</summary>
        public bool SetTrackingSize(byte size)
        {
            _trackSize = size;
            string name = size switch
            {
                TRACK_SIZE_SMALL => "Малая",
                TRACK_SIZE_MID => "Средняя",
                TRACK_SIZE_LARGE => "Большая",
                TRACK_SIZE_AUTO => "Авто",
                _ => $"0x{size:X2}"
            };
            StatusChanged?.Invoke($"Размер цели: {name}");
            // Отправляется через infrequently used E2 команду
            byte[] payload = new byte[10];
            payload[2] = size; // C2 byte
            return Send(BuildSerial(0x31, payload));
        }

        // ══════════════════════════════════════════════════════
        //  9. SD КАРТА
        // ══════════════════════════════════════════════════════

        public bool QuerySDStatus() => Frame30(A1(A1_NOOP), C1(C1_SD_STATUS), E1());
        public bool QuerySDTotal() => Frame30(A1(A1_NOOP), C1(C1_SD_TOTAL), E1());
        public bool QuerySDFree() => Frame30(A1(A1_NOOP), C1(C1_SD_FREE), E1());

        /// <summary>Форматировать SD карту (через C2 команду 0x1D)</summary>
        public bool FormatSD() => Frame30(A1(A1_NOOP), C1(0x1D), E1());

        // ══════════════════════════════════════════════════════
        //  10. СИСТЕМА
        // ══════════════════════════════════════════════════════

        public bool Reboot()
        {
            StatusChanged?.Invoke("Перезагрузка камеры...");
            return Frame31_C2(C2_REBOOT, 0x01); // 0x01 = Reboot
        }

        public bool SendHeartbeat() => Frame30(A1(A1_NOOP), C1(), E1());

        // ══════════════════════════════════════════════════════
        //  Dispose
        // ══════════════════════════════════════════════════════
        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            Disconnect();
        }
    }

    // ══════════════════════════════════════════════════════════
    //  Вспомогательные классы
    // ══════════════════════════════════════════════════════════
    public class GimbalAngles
    {
        public float Roll { get; set; }
        public float Pitch { get; set; }
        public float Yaw { get; set; }
    }

    public class CameraStatus
    {
        public byte Sensor { get; set; }
        public bool IsRecording { get; set; }
        public byte IrDigitalZoom { get; set; }
        public byte EoDigitalZoom { get; set; }
        public bool IsIrBlackHot { get; set; }
        public byte TrackStatus { get; set; }
        public float Distance { get; set; }
    }
}