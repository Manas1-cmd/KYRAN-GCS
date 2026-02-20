using System;
using System.Diagnostics;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Tasks;

namespace SimpleDroneGCS.Services
{
    /// <summary>
    /// Сервис управления камерой Z30T через TCP
    /// Протокол: Проприетарный "33 33" header (НЕ ViewPro Viewlink, НЕ SIYI SDK)
    /// Формат пакета: [33 33 02 03] [CMD] [LEN] [DATA...] [CRC_LO] [CRC_HI]
    /// CRC: CRC_LO = sum(все байты) & 0xFF, CRC_HI = sum(промежуточных сумм) & 0xFF
    /// Верифицировано: 13/13 пакетов совпали с Wireshark захватами TRA
    /// </summary>
    public class Z30TCameraService : IDisposable
    {
        #region Protocol Reference

        // ======================== ПОДТВЕРЖДЁННЫЕ ИЗ WIRESHARK (13/13 ✅) ===================
        //
        // Yaw (Pan):       CMD=0x07 LEN=0x02 [speed_lo][speed_hi]     ✅ w6
        // Pitch (Tilt):    CMD=0x08 LEN=0x02 [speed_lo][speed_hi]     ✅ w6
        // Tracking ON:     CMD=0x0C LEN=0x01 DATA=0x01                ✅ w8
        // Tracking OFF:    CMD=0x0C LEN=0x01 DATA=0x00                ✅ w8
        // Home/Center:     CMD=0x0F LEN=0x01 DATA=0x00                ✅ w8
        // Snapshot:        CMD=0x1C LEN=0x00                           ✅ w9
        // Record:          CMD=0x1D LEN=0x00                           ✅ w9
        // Camera EO:       CMD=0x20 LEN=0x01 DATA=0x00                ✅ w8
        // Camera IR:       CMD=0x20 LEN=0x01 DATA=0x01                ✅ w8
        // IR Palette:      CMD=0x21 LEN=0x01 DATA=[0x00-0x09]         ✅ w8
        // Track Point:     CMD=0x26 LEN=0x0A [X_f32][Y_f32][32 00]   ✅ w8
        // Zoom In:         CMD=0x31 LEN=0x01 DATA=0x01                ✅ w6
        // Zoom Out:        CMD=0x31 LEN=0x01 DATA=0x00                ✅ w6
        // Laser ON/OFF:    CMD=0x33 LEN=0x00                           ✅ w10 (toggle)
        // Fill Light ON:   CMD=0x65 LEN=0x03 DATA=00 01 00            ✅ w10
        // Fill Light OFF:  CMD=0x65 LEN=0x03 DATA=00 00 00            ✅ w10
        // Measure Temp:    CMD=0x70 LEN=0x01 DATA=0x01                ✅ w11
        //
        // ======================== НЕ ПОДТВЕРЖДЁННЫЕ (⚠️) =================================
        //
        // Zoom Stop:       CMD=0x31 LEN=0x01 DATA=0x02               ⚠️ логичное предположение
        // Autofocus:       CMD=0x14 LEN=0x01 DATA=0x01               ⚠️ предположение
        // Camera PIP:      CMD=0x20 LEN=0x01 DATA=0x02-0x04          ⚠️ предположение
        // OSD:             CMD=0x24/0x25 LEN=0x00                     ⚠️ предположение
        //
        // ======================== ЗАМЕТКИ ================================================
        //
        // 1. Лазер (CMD=0x33) — это TOGGLE, одна команда вкл/выкл.
        //    НЕ существует отдельных команд для Single/Continuous/Stop.
        //
        // 2. ZoomStop DATA=0x02 — в захватах TRA просто прекращали слать
        //    ZoomIn/ZoomOut. Пробуем DATA=0x02 как стоп-маркер.
        //
        // 3. Телеметрия: ответы камеры приходят с header [33 33 03 02]
        //    (инвертированный: 03 02 вместо 02 03).
        //    cmd=0x63 — предположительно телеметрия подвеса.
        //
        // ================================================================================

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
        private bool _isOSDOn = true;
        private int _currentPalette;

        public bool IsConnected => _isConnected;
        public bool IsRecording => _isRecording;
        public bool IsTracking => _isTracking;
        public bool IsIRMode => _isIRMode;
        public bool IsLaserOn => _isLaserOn;
        public bool IsLightOn => _isLightOn;
        public bool IsOSDOn => _isOSDOn;
        public int CurrentPalette => _currentPalette;

        public string IpAddress { get; set; } = "192.168.144.68";
        public int Port { get; set; } = 2000;
        public string RtspUrl => $"rtsp://{IpAddress}:554/chn0";

        public event Action<string> StatusChanged;
        public event EventHandler<string> ErrorOccurred;
        public event EventHandler<GimbalAngles> AnglesReceived;
        public event EventHandler<double> DistanceReceived;

        #endregion

        #region CRC Algorithm

        /// <summary>
        /// CRC Z30T (проверено на 17 пакетах из Wireshark — 17/17 совпадений):
        ///   CRC_LO = sum(все байты) & 0xFF
        ///   CRC_HI = sum(все промежуточные суммы) & 0xFF
        /// </summary>
        private static (byte lo, byte hi) CalcCRC(byte[] data, int length)
        {
            int sum = 0;
            int runningSum = 0;
            for (int i = 0; i < length; i++)
            {
                sum += data[i];
                runningSum += sum;
            }
            return ((byte)(sum & 0xFF), (byte)(runningSum & 0xFF));
        }

        /// <summary>
        /// Построить пакет: [33 33 02 03] [cmd] [dataLen] [data...] [crc_lo] [crc_hi]
        /// </summary>
        private static byte[] BuildPacket(byte cmd, byte dataLen, params byte[] data)
        {
            int total = 4 + 1 + 1 + dataLen + 2; // header(4) + cmd(1) + len(1) + data + crc(2)
            byte[] pkt = new byte[total];

            pkt[0] = 0x33; pkt[1] = 0x33; pkt[2] = 0x02; pkt[3] = 0x03;
            pkt[4] = cmd;
            pkt[5] = dataLen;

            if (data != null && data.Length > 0)
                Array.Copy(data, 0, pkt, 6, Math.Min(data.Length, dataLen));

            var (lo, hi) = CalcCRC(pkt, total - 2);
            pkt[total - 2] = lo;
            pkt[total - 1] = hi;

            return pkt;
        }

        #endregion

        #region Connection

        public async Task<bool> ConnectAsync()
        {
            if (_isConnected) return true;

            try
            {
                _tcpClient = new TcpClient();
                _tcpClient.ReceiveTimeout = 5000;
                _tcpClient.SendTimeout = 2000;

                StatusChanged?.Invoke($"Подключение к {IpAddress}:{Port}...");

                var connectTask = _tcpClient.ConnectAsync(IpAddress, Port);
                if (await Task.WhenAny(connectTask, Task.Delay(5000)) != connectTask)
                    throw new TimeoutException("Таймаут подключения");
                await connectTask;

                _stream = _tcpClient.GetStream();
                _isConnected = true;

                _cts = new CancellationTokenSource();
                _ = Task.Run(() => ReceiveLoop(_cts.Token));

                StatusChanged?.Invoke("Подключено к Z30T");
                Debug.WriteLine($"[Z30T] Connected to {IpAddress}:{Port}");
                return true;
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[Z30T] Connection failed: {ex.Message}");
                StatusChanged?.Invoke($"Ошибка: {ex.Message}");
                ErrorOccurred?.Invoke(this, ex.Message);
                Disconnect();
                return false;
            }
        }

        public void Disconnect()
        {
            _cts?.Cancel();
            try { _stream?.Close(); } catch { }
            try { _tcpClient?.Close(); } catch { }
            _stream = null;
            _tcpClient = null;
            _isConnected = false;
            StatusChanged?.Invoke("Отключено");
            Debug.WriteLine("[Z30T] Disconnected");
        }

        #endregion

        #region Receive & Parse

        private async Task ReceiveLoop(CancellationToken token)
        {
            byte[] buffer = new byte[1024];
            bool firstData = true;

            while (!token.IsCancellationRequested && _isConnected)
            {
                try
                {
                    if (_stream?.DataAvailable == true)
                    {
                        int n = await _stream.ReadAsync(buffer, 0, buffer.Length, token);
                        if (n > 0)
                        {
                            if (firstData)
                            {
                                Debug.WriteLine($"[Z30T] ★ Первые данные от камеры! ({n} байт)");
                                firstData = false;
                            }
                            ParseResponse(buffer, n);
                        }
                    }
                    else
                    {
                        await Task.Delay(50, token);
                    }
                }
                catch (OperationCanceledException) { break; }
                catch (Exception ex)
                {
                    Debug.WriteLine($"[Z30T] RX error: {ex.Message}");
                }
            }
        }

        /// <summary>
        /// Парсинг ответов камеры.
        /// Ответы: [33 33 03 02] [cmd] [len] [data...] [crc]
        /// Заголовок инвертирован: 03 02 вместо 02 03 (камера → GCS)
        /// </summary>
        private void ParseResponse(byte[] data, int length)
        {
            for (int i = 0; i < length - 6; i++)
            {
                if (data[i] != 0x33 || data[i + 1] != 0x33) continue;
                if (data[i + 2] != 0x03 || data[i + 3] != 0x02) continue;

                byte cmd = data[i + 4];
                byte dataLen = data[i + 5];

                if (i + 6 + dataLen > length) continue;

                // Телеметрия гимбала (cmd=0x63 — предположение из w6)
                if (cmd == 0x63 && dataLen >= 0x1C)
                {
                    try
                    {
                        // Смещения предположительные — требуют проверки на реальной камере
                        int rawYaw = BitConverter.ToInt16(data, i + 6);
                        int rawPitch = BitConverter.ToInt16(data, i + 8);
                        int rawRoll = BitConverter.ToInt16(data, i + 10);

                        AnglesReceived?.Invoke(this, new GimbalAngles
                        {
                            Yaw = (float)(rawYaw * 0.01),
                            Pitch = (float)(rawPitch * 0.01),
                            Roll = (float)(rawRoll * 0.01)
                        });
                    }
                    catch { }
                }

                // Дальномер (предположительно cmd=0x33 ответ с дистанцией)
                if (cmd == 0x33 && dataLen >= 4)
                {
                    try
                    {
                        float distance = BitConverter.ToSingle(data, i + 6);
                        if (distance > 0 && distance < 50000)
                        {
                            DistanceReceived?.Invoke(this, distance);
                        }
                    }
                    catch { }
                }

                Debug.WriteLine($"[Z30T] RX cmd=0x{cmd:X2} len={dataLen}");
            }
        }

        #endregion

        #region Send

        private bool Send(byte[] packet)
        {
            if (!_isConnected || _stream == null) return false;
            try
            {
                _stream.Write(packet, 0, packet.Length);
                Debug.WriteLine($"[Z30T] TX: {BitConverter.ToString(packet)}");
                return true;
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[Z30T] TX error: {ex.Message}");
                ErrorOccurred?.Invoke(this, ex.Message);
                return false;
            }
        }

        #endregion

        #region ✅ Gimbal Control (Wireshark: w6)

        /// <summary>
        /// ✅ Управление подвесом. Yaw/Pitch: -1000..+1000 (int16 LE)
        /// CameraWindow вызывает с диапазоном ~-100..+100
        /// </summary>
        public void SetGimbalSpeed(int yaw, int pitch)
        {
            int yawSpeed = Clamp(yaw * 10, -1000, 1000);
            int pitchSpeed = Clamp(pitch * 10, -1000, 1000);

            // ✅ CMD=0x07 LEN=0x02 — Yaw
            if (yaw != 0)
            {
                byte lo = (byte)(yawSpeed & 0xFF);
                byte hi = (byte)((yawSpeed >> 8) & 0xFF);
                Send(BuildPacket(0x07, 0x02, lo, hi));
            }

            // ✅ CMD=0x08 LEN=0x02 — Pitch
            if (pitch != 0)
            {
                byte lo = (byte)(pitchSpeed & 0xFF);
                byte hi = (byte)((pitchSpeed >> 8) & 0xFF);
                Send(BuildPacket(0x08, 0x02, lo, hi));
            }
        }

        /// <summary>
        /// ✅ Стоп gimbal — speed=0 для обоих осей
        /// </summary>
        public void StopGimbal()
        {
            Send(BuildPacket(0x07, 0x02, 0x00, 0x00)); // Yaw stop
            Send(BuildPacket(0x08, 0x02, 0x00, 0x00)); // Pitch stop
        }

        /// <summary>
        /// ✅ Возврат в центр — CMD=0x0F DATA=0x00 (Wireshark: w8)
        /// </summary>
        public void ReturnToCenter()
        {
            Send(BuildPacket(0x0F, 0x01, 0x00));
            StatusChanged?.Invoke("Подвес → центр");
        }

        /// <summary>
        /// Взгляд вниз — pitch speed = -900 (0xFC7C в LE)
        /// </summary>
        public void LookDown()
        {
            Send(BuildPacket(0x08, 0x02, 0x7C, 0xFC));
            StatusChanged?.Invoke("Подвес → вниз");
        }

        #endregion

        #region ✅ Zoom (Wireshark: w6 — CMD=0x31)

        /// <summary>
        /// ✅ Zoom In — CMD=0x31 DATA=0x01 (Wireshark подтверждён)
        /// </summary>
        public void ZoomIn()
        {
            Send(BuildPacket(0x31, 0x01, 0x01));
        }

        /// <summary>
        /// ✅ Zoom Out — CMD=0x31 DATA=0x00 (Wireshark подтверждён)
        /// </summary>
        public void ZoomOut()
        {
            Send(BuildPacket(0x31, 0x01, 0x00));
        }

        /// <summary>
        /// ⚠️ Zoom Stop — CMD=0x31 DATA=0x02
        /// НЕ подтверждён из Wireshark (TRA просто прекращала отправку).
        /// Если не работает — ZoomStop просто не шлёт ничего (зум остановится сам).
        /// </summary>
        public void ZoomStop()
        {
            Send(BuildPacket(0x31, 0x01, 0x02));
        }

        /// <summary>
        /// ⚠️ Автофокус — CMD=0x14 DATA=0x01 (НЕ подтверждён из Wireshark).
        /// Если не работает на камере — можно попробовать CMD=0x15 или CMD=0x10.
        /// </summary>
        public void AutoFocus()
        {
            Send(BuildPacket(0x14, 0x01, 0x01));
            StatusChanged?.Invoke("Автофокус");
        }

        #endregion

        #region ✅ Camera Mode EO/IR (Wireshark: w8 — CMD=0x20)

        /// <summary>
        /// ✅ EO (видимый свет) — CMD=0x20 DATA=0x00
        /// </summary>
        public void SetVideoEO()
        {
            if (Send(BuildPacket(0x20, 0x01, 0x00)))
            {
                _isIRMode = false;
                StatusChanged?.Invoke("Режим: EO");
            }
        }

        /// <summary>
        /// ✅ IR (тепловизор) — CMD=0x20 DATA=0x01
        /// </summary>
        public void SetVideoIR()
        {
            if (Send(BuildPacket(0x20, 0x01, 0x01)))
            {
                _isIRMode = true;
                StatusChanged?.Invoke("Режим: IR");
            }
        }

        /// <summary>
        /// ⚠️ EO + IR PIP — CMD=0x20 DATA=0x02 (предположение по аналогии)
        /// </summary>
        public void SetVideoEO_IR_PIP()
        {
            Send(BuildPacket(0x20, 0x01, 0x02));
            StatusChanged?.Invoke("Режим: EO+IR PIP");
        }

        /// <summary>
        /// ⚠️ IR + EO PIP — CMD=0x20 DATA=0x03 (предположение)
        /// </summary>
        public void SetVideoIR_EO_PIP()
        {
            Send(BuildPacket(0x20, 0x01, 0x03));
            StatusChanged?.Invoke("Режим: IR+EO PIP");
        }

        /// <summary>
        /// ⚠️ Fusion — CMD=0x20 DATA=0x04 (предположение)
        /// </summary>
        public void SetVideoFusion()
        {
            Send(BuildPacket(0x20, 0x01, 0x04));
            StatusChanged?.Invoke("Режим: Фьюжн");
        }

        #endregion

        #region ✅ IR Palette (CMD=0x21) — 10 палитр из Wireshark w12

        /// <summary>
        /// ✅ Все 10 палитр подтверждены из Wireshark (w12.pcapng, сопоставлены с TRA):
        ///   00=White Hot, 01=Lava, 02=Iron Red, 03=Hot Iron, 04=Medical,
        ///   05=Arctic, 06=Rainbow 1, 07=Rainbow 2, 08=Red Trace, 09=Black Hot
        /// </summary>
        public static readonly string[] PaletteNames =
        {
            "White Hot", "Lava", "Iron Red", "Hot Iron", "Medical",
            "Arctic", "Rainbow 1", "Rainbow 2", "Red Trace", "Black Hot"
        };

        public void SetIRPalette(int index)
        {
            if (index < 0 || index >= PaletteNames.Length) return;
            if (Send(BuildPacket(0x21, 0x01, (byte)index)))
            {
                _currentPalette = index;
                StatusChanged?.Invoke($"ИК палитра: {PaletteNames[index]}");
            }
        }

        public void SetIRPaletteWhiteHot() => SetIRPalette(0);
        public void SetIRPaletteLava() => SetIRPalette(1);
        public void SetIRPaletteIronRed() => SetIRPalette(2);
        public void SetIRPaletteHotIron() => SetIRPalette(3);
        public void SetIRPaletteMedical() => SetIRPalette(4);
        public void SetIRPaletteArctic() => SetIRPalette(5);
        public void SetIRPaletteRainbow1() => SetIRPalette(6);
        public void SetIRPaletteRainbow2() => SetIRPalette(7);
        public void SetIRPaletteRedTrace() => SetIRPalette(8);
        public void SetIRPaletteBlackHot() => SetIRPalette(9);
        public void NextPalette() => SetIRPalette((_currentPalette + 1) % PaletteNames.Length);

        #endregion



        #region ✅ Photo & Record (Wireshark: w9)

        /// <summary>
        /// ✅ Снимок — CMD=0x1C LEN=0x00 (на SD-карту камеры)
        /// </summary>
        public void TakePhoto()
        {
            Send(BuildPacket(0x1C, 0x00));
            StatusChanged?.Invoke("📸 Снимок");
        }

        /// <summary>
        /// ✅ Запись вкл/выкл — CMD=0x1D LEN=0x00 (toggle на SD-карту камеры)
        /// </summary>
        public void ToggleRecording()
        {
            if (Send(BuildPacket(0x1D, 0x00)))
            {
                _isRecording = !_isRecording;
                StatusChanged?.Invoke(_isRecording ? "⏺ Запись..." : "⏹ Стоп");
            }
        }

        #endregion

        #region ✅ Tracking (Wireshark: w8)

        /// <summary>
        /// ✅ Трекинг ВКЛ — CMD=0x0C DATA=0x01
        /// </summary>
        public void EnableSearchMode()
        {
            if (Send(BuildPacket(0x0C, 0x01, 0x01)))
            {
                _isTracking = true;
                StatusChanged?.Invoke("Трекинг: ВКЛ");
            }
        }

        /// <summary>
        /// ✅ Трекинг ВЫКЛ — CMD=0x0C DATA=0x00
        /// </summary>
        public void StopTracking()
        {
            if (Send(BuildPacket(0x0C, 0x01, 0x00)))
            {
                _isTracking = false;
                StatusChanged?.Invoke("Трекинг: ВЫКЛ");
            }
        }

        /// <summary>
        /// Переключение трекинга
        /// </summary>
        public void ToggleAIDetection()
        {
            if (_isTracking) StopTracking();
            else EnableSearchMode();
        }

        /// <summary>
        /// ✅ Точка трекинга — CMD=0x26 LEN=0x0A [X_f32 LE][Y_f32 LE][0x32 0x00]
        /// Координаты: 0.0–1.0 нормализованные (от верхнего-левого угла)
        /// </summary>
        public void SetTrackingPoint(float normX, float normY)
        {
            byte[] xBytes = BitConverter.GetBytes(normX);
            byte[] yBytes = BitConverter.GetBytes(normY);

            byte[] data = new byte[10];
            Array.Copy(xBytes, 0, data, 0, 4);
            Array.Copy(yBytes, 0, data, 4, 4);
            data[8] = 0x32;
            data[9] = 0x00;

            Send(BuildPacket(0x26, 0x0A, data));

            if (!_isTracking) EnableSearchMode();
            StatusChanged?.Invoke($"Цель: ({normX:F2}, {normY:F2})");
        }

        #endregion

        #region ✅ Laser / LRF (Wireshark: w10 — CMD=0x33)

        /// <summary>
        /// ✅ Лазерный дальномер — CMD=0x33 LEN=0x00 (TOGGLE: вкл/выкл)
        /// ВАЖНО: Это ОДНА команда — toggle! Нет отдельных On/Off.
        /// Каждый вызов переключает состояние.
        /// </summary>
        public void ToggleLaser()
        {
            if (Send(BuildPacket(0x33, 0x00)))
            {
                _isLaserOn = !_isLaserOn;
                StatusChanged?.Invoke(_isLaserOn ? "🔴 Лазер: ВКЛ" : "Лазер: ВЫКЛ");
            }
        }

        /// <summary>
        /// Включить лазер (если выключен)
        /// </summary>
        public void LaserOn()
        {
            if (!_isLaserOn) ToggleLaser();
        }

        /// <summary>
        /// Выключить лазер (если включен)
        /// </summary>
        public void LaserOff()
        {
            if (_isLaserOn) ToggleLaser();
        }

        // Обёртки для совместимости с CameraWindow:
        public void LRFMeasureSingle() => ToggleLaser();
        public void LRFMeasureContinuous() => LaserOn();
        public void LRFStop() => LaserOff();

        #endregion

        #region ✅ Fill Light (Wireshark: w10 — CMD=0x65)

        /// <summary>
        /// ✅ Подсветка ВКЛ  — CMD=0x65 LEN=0x03 DATA=00 01 00
        /// ✅ Подсветка ВЫКЛ — CMD=0x65 LEN=0x03 DATA=00 00 00
        /// </summary>
        public void SetFillLight(bool on)
        {
            byte[] data = on
                ? new byte[] { 0x00, 0x01, 0x00 }
                : new byte[] { 0x00, 0x00, 0x00 };

            if (Send(BuildPacket(0x65, 0x03, data)))
            {
                _isLightOn = on;
                StatusChanged?.Invoke(on ? "💡 Подсветка ВКЛ" : "Подсветка ВЫКЛ");
            }
        }

        public void ToggleFillLight() => SetFillLight(!_isLightOn);

        #endregion

        #region ✅ Temperature (Wireshark: w2, w11 — CMD=0x70)

        // ✅ Подтверждено из Wireshark:
        //   Gear 1: 33 33 02 03 70 01 01 DD 00
        //   Gear 2: 33 33 02 03 70 01 02 DE 01
        //   Gear 3: 33 33 02 03 70 01 03 DF 02

        private int _tempGear = 0;
        public int TempGear => _tempGear;
        public bool IsTempMeasuring => _tempGear > 0;

        /// <summary>
        /// ✅ Установить уровень измерения температуры (Gear 1-3)
        /// Gear 1 = базовый, Gear 2 = средний, Gear 3 = расширенный
        /// </summary>
        public void SetTempGear(int gear)
        {
            gear = Math.Clamp(gear, 1, 3);
            if (Send(BuildPacket(0x70, 0x01, (byte)gear)))
            {
                _tempGear = gear;
                StatusChanged?.Invoke($"🌡 Temp Gear: {_tempGear}");
            }
        }

        /// <summary>
        /// Циклическое переключение Gear: 1→2→3→выкл→1...
        /// </summary>
        public void NextTempGear()
        {
            if (_tempGear >= 3)
                _tempGear = 0; // выкл (просто перестаём слать)
            else
                SetTempGear(_tempGear + 1);

            if (_tempGear == 0)
                StatusChanged?.Invoke("🌡 Температура: ВЫКЛ");
        }

        /// <summary>
        /// Совместимость — Gear 1 или toggle
        /// </summary>
        public void MeasureTemperature() => NextTempGear();

        #endregion

        #region ⚠️ OSD (НЕ подтверждено)

        /// <summary>
        /// ⚠️ OSD toggle — CMD=0x24 (OFF) / CMD=0x25 (ON)
        /// Команды предположительные. Если не работают — попробовать:
        ///   CMD=0x24 DATA=0x00/0x01, или CMD=0x1E/0x1F, или CMD=0x60
        /// </summary>
        public void ToggleOSD()
        {
            byte cmd = _isOSDOn ? (byte)0x24 : (byte)0x25;
            if (Send(BuildPacket(cmd, 0x00)))
            {
                _isOSDOn = !_isOSDOn;
                StatusChanged?.Invoke(_isOSDOn ? "OSD: ВКЛ" : "OSD: ВЫКЛ");
            }
        }

        #endregion

        #region Helpers

        private static int Clamp(int val, int min, int max)
        {
            if (val < min) return min;
            if (val > max) return max;
            return val;
        }

        #endregion

        #region Dispose

        public void Dispose()
        {
            Disconnect();
        }

        #endregion
    }
}
