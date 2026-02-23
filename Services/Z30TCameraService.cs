using System;
using System.Diagnostics;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Tasks;

namespace SimpleDroneGCS.Services
{
    
    public class Z30TCameraService : IDisposable
    {

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

        private static byte[] BuildPacket(byte cmd, byte dataLen, params byte[] data)
        {
            int total = 4 + 1 + 1 + dataLen + 2; 
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

        private void ParseResponse(byte[] data, int length)
        {
            for (int i = 0; i < length - 6; i++)
            {
                if (data[i] != 0x33 || data[i + 1] != 0x33) continue;
                if (data[i + 2] != 0x03 || data[i + 3] != 0x02) continue;

                byte cmd = data[i + 4];
                byte dataLen = data[i + 5];

                if (i + 6 + dataLen > length) continue;

                if (cmd == 0x63 && dataLen >= 0x1C)
                {
                    try
                    {
                        
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

        public void SetGimbalSpeed(int yaw, int pitch)
        {
            int yawSpeed = Clamp(yaw * 10, -1000, 1000);
            int pitchSpeed = Clamp(pitch * 10, -1000, 1000);

            if (yaw != 0)
            {
                byte lo = (byte)(yawSpeed & 0xFF);
                byte hi = (byte)((yawSpeed >> 8) & 0xFF);
                Send(BuildPacket(0x07, 0x02, lo, hi));
            }

            if (pitch != 0)
            {
                byte lo = (byte)(pitchSpeed & 0xFF);
                byte hi = (byte)((pitchSpeed >> 8) & 0xFF);
                Send(BuildPacket(0x08, 0x02, lo, hi));
            }
        }

        public void StopGimbal()
        {
            Send(BuildPacket(0x07, 0x02, 0x00, 0x00)); 
            Send(BuildPacket(0x08, 0x02, 0x00, 0x00)); 
        }

        public void ReturnToCenter()
        {
            Send(BuildPacket(0x0F, 0x01, 0x00));
            StatusChanged?.Invoke("Подвес → центр");
        }

        public void LookDown()
        {
            Send(BuildPacket(0x08, 0x02, 0x7C, 0xFC));
            StatusChanged?.Invoke("Подвес → вниз");
        }

        public void ZoomIn()
        {
            Send(BuildPacket(0x31, 0x01, 0x01));
        }

        public void ZoomOut()
        {
            Send(BuildPacket(0x31, 0x01, 0x00));
        }

        public void ZoomStop()
        {
            Send(BuildPacket(0x31, 0x01, 0x02));
        }

        public void AutoFocus()
        {
            Send(BuildPacket(0x14, 0x01, 0x01));
            StatusChanged?.Invoke("Автофокус");
        }

        public void SetVideoEO()
        {
            if (Send(BuildPacket(0x20, 0x01, 0x00)))
            {
                _isIRMode = false;
                StatusChanged?.Invoke("Режим: EO");
            }
        }

        public void SetVideoIR()
        {
            if (Send(BuildPacket(0x20, 0x01, 0x01)))
            {
                _isIRMode = true;
                StatusChanged?.Invoke("Режим: IR");
            }
        }

        public void SetVideoEO_IR_PIP()
        {
            Send(BuildPacket(0x20, 0x01, 0x02));
            StatusChanged?.Invoke("Режим: EO+IR PIP");
        }

        public void SetVideoIR_EO_PIP()
        {
            Send(BuildPacket(0x20, 0x01, 0x03));
            StatusChanged?.Invoke("Режим: IR+EO PIP");
        }

        public void SetVideoFusion()
        {
            Send(BuildPacket(0x20, 0x01, 0x04));
            StatusChanged?.Invoke("Режим: Фьюжн");
        }

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

        public void TakePhoto()
        {
            Send(BuildPacket(0x1C, 0x00));
            StatusChanged?.Invoke("📸 Снимок");
        }

        public void ToggleRecording()
        {
            if (Send(BuildPacket(0x1D, 0x00)))
            {
                _isRecording = !_isRecording;
                StatusChanged?.Invoke(_isRecording ? "⏺ Запись..." : "⏹ Стоп");
            }
        }

        public void EnableSearchMode()
        {
            if (Send(BuildPacket(0x0C, 0x01, 0x01)))
            {
                _isTracking = true;
                StatusChanged?.Invoke("Трекинг: ВКЛ");
            }
        }

        public void StopTracking()
        {
            if (Send(BuildPacket(0x0C, 0x01, 0x00)))
            {
                _isTracking = false;
                StatusChanged?.Invoke("Трекинг: ВЫКЛ");
            }
        }

        public void ToggleAIDetection()
        {
            if (_isTracking) StopTracking();
            else EnableSearchMode();
        }

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

        public void ToggleLaser()
        {
            if (Send(BuildPacket(0x33, 0x00)))
            {
                _isLaserOn = !_isLaserOn;
                StatusChanged?.Invoke(_isLaserOn ? "🔴 Лазер: ВКЛ" : "Лазер: ВЫКЛ");
            }
        }

        public void LaserOn()
        {
            if (!_isLaserOn) ToggleLaser();
        }

        public void LaserOff()
        {
            if (_isLaserOn) ToggleLaser();
        }

        public void LRFMeasureSingle() => ToggleLaser();
        public void LRFMeasureContinuous() => LaserOn();
        public void LRFStop() => LaserOff();

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

        private int _tempGear = 0;
        public int TempGear => _tempGear;
        public bool IsTempMeasuring => _tempGear > 0;

        public void SetTempGear(int gear)
        {
            gear = Math.Clamp(gear, 1, 3);
            if (Send(BuildPacket(0x70, 0x01, (byte)gear)))
            {
                _tempGear = gear;
                StatusChanged?.Invoke($"🌡 Temp Gear: {_tempGear}");
            }
        }

        public void NextTempGear()
        {
            if (_tempGear >= 3)
                _tempGear = 0; 
            else
                SetTempGear(_tempGear + 1);

            if (_tempGear == 0)
                StatusChanged?.Invoke("🌡 Температура: ВЫКЛ");
        }

        public void MeasureTemperature() => NextTempGear();

        public void ToggleOSD()
        {
            byte cmd = _isOSDOn ? (byte)0x24 : (byte)0x25;
            if (Send(BuildPacket(cmd, 0x00)))
            {
                _isOSDOn = !_isOSDOn;
                StatusChanged?.Invoke(_isOSDOn ? "OSD: ВКЛ" : "OSD: ВЫКЛ");
            }
        }

        private static int Clamp(int val, int min, int max)
        {
            if (val < min) return min;
            if (val > max) return max;
            return val;
        }

        public void Dispose()
        {
            Disconnect();
        }

    }
}
