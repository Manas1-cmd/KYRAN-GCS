using System;
using System.Diagnostics;
using System.IO;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using LibVLCSharp.Shared;
using VlcMediaPlayer = LibVLCSharp.Shared.MediaPlayer;
using SimpleDroneGCS.Services;
using static SimpleDroneGCS.Helpers.Loc;

namespace SimpleDroneGCS.Views
{
    public partial class CameraWindow : Window
    {
        // ── Сервисы ────────────────────────────────────────────
        private ViewProCameraService _cam;
        private LibVLC _libVLC;
        private VlcMediaPlayer _mediaPlayer;
        private VlcMediaPlayer _recordPlayer;
        private DispatcherTimer _joystickTimer;

        // ── Настройки подключения ──────────────────────────────
        private CameraConnectionSettings _settings;
        private string _rtspUrl = "";
        private string _mediaFolder = "";

        // ── Джойстик ──────────────────────────────────────────
        private bool _joystickActive = false;
        private double _joyX = 0, _joyY = 0;

        // ── Клавиатура ────────────────────────────────────────
        private bool _keyW, _keyA, _keyS, _keyD;
        private bool _keyZoomIn, _keyZoomOut;
        private bool _keyFocusFar, _keyFocusNear;
        private const int GIMBAL_SPEED = 70; // % из 100

        // ── Запись ────────────────────────────────────────────
        private bool _isLocalRecording = false;

        // ══════════════════════════════════════════════════════
        public CameraWindow(CameraConnectionSettings settings, MAVLinkService mavLink)
        {
            InitializeComponent();
            _settings = settings;
            _rtspUrl = settings.RtspUrl;
            IpTextBox.Text = settings.CameraIP;

            _mediaFolder = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments),
                "SQK_GCS", "Camera");
            Directory.CreateDirectory(_mediaFolder);

            KeyDown += OnKeyDown;
            KeyUp += OnKeyUp;
            Closed += OnWindowClosed;
            Loaded += OnWindowLoaded;

            Deactivated += (s, e) =>
            {
                _keyW = _keyA = _keyS = _keyD = false;
                _keyZoomIn = _keyZoomOut = false;
                _keyFocusFar = _keyFocusNear = false;
                _joystickActive = false;
                _joyX = 0; _joyY = 0;
                _cam?.StopGimbal();
                _cam?.ZoomStop();
            };

            UpdateStatus("Инициализация...");
        }

        // ══════════════════════════════════════════════════════
        //  Загрузка окна
        // ══════════════════════════════════════════════════════
        private async void OnWindowLoaded(object sender, RoutedEventArgs e)
        {
            // Центровка джойстика
            if (JoystickThumb != null && JoystickArea != null)
            {
                Canvas.SetLeft(JoystickThumb, (JoystickArea.Width - JoystickThumb.Width) / 2);
                Canvas.SetTop(JoystickThumb, (JoystickArea.Height - JoystickThumb.Height) / 2);
            }

            // Сервис камеры
            _cam = new ViewProCameraService();
            _cam.IpAddress = _settings.CameraIP;
            _cam.Port = _settings.TcpPort;

            _cam.StatusChanged += s => Dispatcher.Invoke(() => UpdateStatus(s));
            _cam.ErrorOccurred += (_, m) => Dispatcher.Invoke(() => UpdateStatus($"Ошибка: {m}"));
            _cam.ConnectionChanged += (_, ok) => Dispatcher.Invoke(() => OnConnectionChanged(ok));
            _cam.AnglesReceived += (_, a) => Dispatcher.Invoke(() => UpdateAngles(a));
            _cam.DistanceReceived += (_, d) => Dispatcher.Invoke(() => UpdateDistance(d));
            _cam.StatusUpdated += (_, s) => Dispatcher.Invoke(() => UpdateCameraStatus(s));

            // Таймер джойстика
            _joystickTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(50) };
            _joystickTimer.Tick += JoystickTick;
            _joystickTimer.Start();

            // LibVLC
            try
            {
                UpdateStatus("Загрузка видео...");
                await Task.Run(() => Core.Initialize());
                _libVLC = new LibVLC("--no-xlib", "--network-caching=150", "--rtsp-tcp");
                _mediaPlayer = new VlcMediaPlayer(_libVLC);
                await Task.Delay(150);
                VideoView.MediaPlayer = _mediaPlayer;
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[Cam] LibVLC: {ex.Message}");
                UpdateStatus($"Видео недоступно: {ex.Message}");
            }

            AutoConnect();
        }

        private async void AutoConnect()
        {
            if (_cam == null) return;
            bool ok = await _cam.ConnectAsync();
            if (!IsLoaded) return;
            if (ok) StartVideo();
            else ConnectButton.Content = "ПОВТОР";
        }

        // ══════════════════════════════════════════════════════
        //  Обновление UI
        // ══════════════════════════════════════════════════════
        private void OnConnectionChanged(bool connected)
        {
            ConnectButton.Content = connected ? Get("Disconnect") : Get("Connect");
            ConnStatusText.Text = connected ? "Подключено" : "Отключено";
            ConnStatusText.Foreground = new SolidColorBrush(
                connected ? Color.FromRgb(0x98, 0xF0, 0x19) : Color.FromRgb(0x55, 0x66, 0xAA));
            if (!connected) StopVideo();
        }

        private void UpdateAngles(GimbalAngles a)
        {
            YawText.Text = $"YAW:   {a.Yaw:+000.0;-000.0;  000.0}°";
            PitchText.Text = $"PITCH: {a.Pitch:+00.0;-00.0;  00.0}°";
            RollText.Text = $"ROLL:  {a.Roll:+00.0;-00.0;  00.0}°";
        }

        private void UpdateDistance(float dist) =>
            DistanceText.Text = $"LRF: {dist:F1} м";

        private void UpdateCameraStatus(CameraStatus s)
        {
            // Видеоисточник
            SensorText.Text = s.Sensor switch
            {
                ViewProCameraService.SRC_EO => "EO",
                ViewProCameraService.SRC_IR => "IR",
                ViewProCameraService.SRC_EO_IR => "EO+IR",
                ViewProCameraService.SRC_IR_EO => "IR+EO",
                ViewProCameraService.SRC_FUSION => "FUSION",
                _ => $"SRC{s.Sensor}"
            };

            // Запись
            bool rec = s.IsRecording;
            RecText.Text = rec ? "● REC" : "";
            RecordingBanner.Visibility = rec ? Visibility.Visible : Visibility.Collapsed;
            RecordButton.Content = rec ? "⏹ СТОП" : "⏺ ЗАПИСЬ";

            // Зум
            string zoom = "";
            if (s.EoDigitalZoom > 1) zoom += $"EO×{s.EoDigitalZoom} ";
            if (s.IrDigitalZoom > 1) zoom += $"IR×{s.IrDigitalZoom}";
            ZoomText.Text = string.IsNullOrEmpty(zoom) ? "" : zoom.Trim();

            // Трекинг
            TrackText.Text = s.TrackStatus > 0 ? "🎯 TRACK" : "";
        }

        private void UpdateStatus(string msg)
        {
            StatusText.Text = msg;
            Debug.WriteLine($"[Cam] {msg}");
        }

        // ══════════════════════════════════════════════════════
        //  Видео
        // ══════════════════════════════════════════════════════
        private void StartVideo()
        {
            if (_mediaPlayer == null || _libVLC == null) return;
            try
            {
                string url = string.IsNullOrEmpty(_rtspUrl) ? _cam?.RtspUrl : _rtspUrl;
                if (string.IsNullOrEmpty(url)) { UpdateStatus("RTSP URL не задан"); return; }
                UpdateStatus($"Видео: {url}");
                var media = new Media(_libVLC, url, FromType.FromLocation);
                media.AddOption(":network-caching=150");
                media.AddOption(":rtsp-tcp");
                media.AddOption(":live-caching=50");
                _mediaPlayer.Play(media);
            }
            catch (Exception ex) { UpdateStatus($"Ошибка видео: {ex.Message}"); }
        }

        private void StopVideo() => _mediaPlayer?.Stop();

        // ══════════════════════════════════════════════════════
        //  Подключение
        // ══════════════════════════════════════════════════════
        private async void ConnectButton_Click(object sender, RoutedEventArgs e)
        {
            if (_cam == null) return;
            if (_cam.IsConnected)
            {
                StopVideo();
                _cam.Disconnect();
            }
            else
            {
                _cam.IpAddress = IpTextBox.Text.Trim();
                ConnectButton.Content = "...";
                ConnectButton.IsEnabled = false;
                bool ok = await _cam.ConnectAsync();
                if (!IsLoaded) return;
                ConnectButton.IsEnabled = true;
                if (ok) StartVideo();
            }
        }

        // ══════════════════════════════════════════════════════
        //  Джойстик
        // ══════════════════════════════════════════════════════
        private void JoystickTick(object sender, EventArgs e)
        {
            if (_cam == null || !_cam.IsConnected) return;

            int yaw = 0, pitch = 0;
            if (_keyW) pitch += GIMBAL_SPEED;
            if (_keyS) pitch -= GIMBAL_SPEED;
            if (_keyD) yaw += GIMBAL_SPEED;
            if (_keyA) yaw -= GIMBAL_SPEED;

            if (_joystickActive)
            {
                yaw = (int)(_joyX * 100);
                pitch = (int)(-_joyY * 100);
            }

            if (yaw != 0 || pitch != 0)
                _cam.SetGimbalSpeed(yaw, pitch);
            else if (_joystickActive)
                _cam.StopGimbal();
            else if (!_keyW && !_keyA && !_keyS && !_keyD)
            {
                // idle — стоп уже был отправлен
            }

            if (_keyZoomIn) _cam.ZoomIn();
            else if (_keyZoomOut) _cam.ZoomOut();

            if (_keyFocusFar) _cam.FocusFar();
            else if (_keyFocusNear) _cam.FocusNear();
        }

        private void JoystickArea_MouseDown(object sender, MouseButtonEventArgs e)
        {
            if (sender is Canvas c) { _joystickActive = true; c.CaptureMouse(); UpdateJoy(e.GetPosition(c), c); }
        }
        private void JoystickArea_MouseMove(object sender, MouseEventArgs e)
        {
            if (_joystickActive && sender is Canvas c) UpdateJoy(e.GetPosition(c), c);
        }
        private void JoystickArea_MouseUp(object sender, MouseButtonEventArgs e)
        {
            if (sender is Canvas c)
            {
                _joystickActive = false;
                c.ReleaseMouseCapture();
                _joyX = 0; _joyY = 0;
                UpdateJoyThumb(c);
                _cam?.StopGimbal();
            }
        }

        private void UpdateJoy(Point pos, Canvas c)
        {
            double cx = c.Width / 2, cy = c.Height / 2, r = cx - 15;
            double dx = pos.X - cx, dy = pos.Y - cy;
            double dist = Math.Sqrt(dx * dx + dy * dy);
            if (dist > r) { dx = dx * r / dist; dy = dy * r / dist; }
            _joyX = dx / r; _joyY = dy / r;
            UpdateJoyThumb(c);
        }

        private void UpdateJoyThumb(Canvas c)
        {
            if (JoystickThumb == null) return;
            double cx = c.Width / 2, cy = c.Height / 2, r = cx - 15;
            Canvas.SetLeft(JoystickThumb, cx + _joyX * r - JoystickThumb.Width / 2);
            Canvas.SetTop(JoystickThumb, cy + _joyY * r - JoystickThumb.Height / 2);
        }

        // ══════════════════════════════════════════════════════
        //  Гимбал — кнопки
        // ══════════════════════════════════════════════════════
        private void HomeButton_Click(object sender, RoutedEventArgs e) => _cam?.ReturnToCenter();
        private void LookDownButton_Click(object sender, RoutedEventArgs e) => _cam?.LookDown();
        private void FollowYaw_Click(object sender, RoutedEventArgs e)
        {
            if (_cam == null) return;
            _cam.ToggleFollowYaw();
            FollowYawBtn.Content = _cam.IsFollowYaw ? "Follow Yaw: ВКЛ" : "Follow Yaw: ВЫКЛ";
            FollowYawBtn.Foreground = new SolidColorBrush(
                _cam.IsFollowYaw ? Color.FromRgb(0x98, 0xF0, 0x19) : Color.FromRgb(0x66, 0x77, 0xAA));
        }

        // ══════════════════════════════════════════════════════
        //  Зум и фокус
        // ══════════════════════════════════════════════════════
        private void ZoomIn_Down(object sender, MouseButtonEventArgs e) => _cam?.ZoomIn();
        private void ZoomOut_Down(object sender, MouseButtonEventArgs e) => _cam?.ZoomOut();
        private void Zoom_Up(object sender, MouseButtonEventArgs e) => _cam?.ZoomStop();
        private void ZoomStop_Click(object sender, RoutedEventArgs e) => _cam?.ZoomStop();
        private void AutoFocus_Click(object sender, RoutedEventArgs e) => _cam?.AutoFocus();

        private void EoDzoomIn_Click(object sender, RoutedEventArgs e) => _cam?.SetEoDzoom(true);
        private void EoDzoomOut_Click(object sender, RoutedEventArgs e) => _cam?.SetEoDzoom(false);
        private void IrDzoomIn_Click(object sender, RoutedEventArgs e) => _cam?.IrDzoomIn();
        private void IrDzoomOut_Click(object sender, RoutedEventArgs e) => _cam?.IrDzoomOut();

        private void FocusFar_Down(object sender, MouseButtonEventArgs e) => _cam?.FocusFar();
        private void FocusNear_Down(object sender, MouseButtonEventArgs e) => _cam?.FocusNear();
        private void Focus_Up(object sender, MouseButtonEventArgs e) => _cam?.FocusStop();

        // ══════════════════════════════════════════════════════
        //  Фото и Запись
        // ══════════════════════════════════════════════════════
        private void Photo_Click(object sender, RoutedEventArgs e)
        {
            _cam?.TakePhoto();
            TakeLocalSnapshot();
        }

        private void Record_Click(object sender, RoutedEventArgs e)
        {
            _cam?.ToggleRecording();
            ToggleLocalRecording();
        }

        private void ModePhoto_Click(object sender, RoutedEventArgs e) => _cam?.SwitchToPhotoMode();
        private void ModeVideo_Click(object sender, RoutedEventArgs e) => _cam?.SwitchToVideoMode();

        private void TakeLocalSnapshot()
        {
            if (_mediaPlayer == null || !_mediaPlayer.IsPlaying) return;
            try
            {
                string file = Path.Combine(_mediaFolder, $"photo_{DateTime.Now:yyyyMMdd_HHmmss}.png");
                _mediaPlayer.TakeSnapshot(0, file, 0, 0);
                UpdateStatus($"Фото сохранено: {Path.GetFileName(file)}");
            }
            catch (Exception ex) { UpdateStatus($"Ошибка снимка: {ex.Message}"); }
        }

        private void ToggleLocalRecording()
        {
            if (_isLocalRecording) StopLocalRecording();
            else StartLocalRecording();
        }

        private void StartLocalRecording()
        {
            if (_libVLC == null) return;
            try
            {
                string url = string.IsNullOrEmpty(_rtspUrl) ? _cam?.RtspUrl : _rtspUrl;
                if (string.IsNullOrEmpty(url)) { UpdateStatus("RTSP URL не задан"); return; }
                string file = Path.Combine(_mediaFolder, $"rec_{DateTime.Now:yyyyMMdd_HHmmss}.mp4");
                _recordPlayer = new VlcMediaPlayer(_libVLC);
                var media = new Media(_libVLC, url, FromType.FromLocation);
                media.AddOption(":network-caching=150");
                media.AddOption(":rtsp-tcp");
                media.AddOption($":sout=#file{{dst='{file}'}}");
                media.AddOption(":sout-keep");
                _recordPlayer.Play(media);
                _isLocalRecording = true;
                UpdateStatus($"Локальная запись: {Path.GetFileName(file)}");
            }
            catch (Exception ex) { UpdateStatus($"Ошибка записи: {ex.Message}"); }
        }

        private void StopLocalRecording()
        {
            try
            {
                _recordPlayer?.Stop();
                _recordPlayer?.Dispose();
                _recordPlayer = null;
                _isLocalRecording = false;
                UpdateStatus("Запись сохранена");
            }
            catch (Exception ex) { UpdateStatus($"Стоп запись: {ex.Message}"); }
        }

        // ══════════════════════════════════════════════════════
        //  Видеоисточник
        // ══════════════════════════════════════════════════════
        private void SensorEO_Click(object sender, RoutedEventArgs e) => _cam?.SetVideoEO();
        private void SensorIR_Click(object sender, RoutedEventArgs e) => _cam?.SetVideoIR();
        private void SensorPIP1_Click(object sender, RoutedEventArgs e) => _cam?.SetVideoEO_IR_PIP();
        private void SensorPIP2_Click(object sender, RoutedEventArgs e) => _cam?.SetVideoIR_EO_PIP();
        private void SensorFusion_Click(object sender, RoutedEventArgs e) => _cam?.SetVideoFusion();

        // ══════════════════════════════════════════════════════
        //  IR Палитры
        // ══════════════════════════════════════════════════════
        private void IRWhiteHot_Click(object sender, RoutedEventArgs e) => _cam?.SetIRPaletteWhiteHot();
        private void IRBlackHot_Click(object sender, RoutedEventArgs e) => _cam?.SetIRPaletteBlackHot();
        private void IRRainbow_Click(object sender, RoutedEventArgs e) => _cam?.SetIRPaletteRainbow();
        private void IRColorBar_Click(object sender, RoutedEventArgs e)
        {
            if (_cam == null) return;
            _cam.ToggleIRColorBar();
            bool on = _cam.IsIrColorBarOn;
            IRColorBarBtn.Content = on ? "IR Шкала: ВКЛ" : "IR Шкала: ВЫКЛ";
            IRColorBarBtn.Foreground = new SolidColorBrush(
                on ? Color.FromRgb(0x98, 0xF0, 0x19) : Color.FromRgb(0x66, 0x77, 0xAA));
        }

        // ══════════════════════════════════════════════════════
        //  Изображение
        // ══════════════════════════════════════════════════════
        private void BrightnessUp_Click(object sender, RoutedEventArgs e) => _cam?.BrightnessUp();
        private void BrightnessDown_Click(object sender, RoutedEventArgs e) => _cam?.BrightnessDown();

        private void OSD_Click(object sender, RoutedEventArgs e)
        {
            if (_cam == null) return;
            _cam.ToggleOSD();
            OsdBtn.Content = _cam.IsOsdOn ? "OSD: ВКЛ" : "OSD: ВЫКЛ";
            OsdBtn.Foreground = new SolidColorBrush(
                _cam.IsOsdOn ? Color.FromRgb(0x98, 0xF0, 0x19) : Color.FromRgb(0x66, 0x77, 0xAA));
        }

        private void Defog_Click(object sender, RoutedEventArgs e)
        {
            if (_cam == null) return;
            _cam.ToggleDefog();
            DefogBtn.Content = _cam.IsDefogOn ? "Дефог: ВКЛ" : "Дефог: ВЫКЛ";
            DefogBtn.Foreground = new SolidColorBrush(
                _cam.IsDefogOn ? Color.FromRgb(0x98, 0xF0, 0x19) : Color.FromRgb(0x66, 0x77, 0xAA));
        }

        private void Flip_Click(object sender, RoutedEventArgs e)
        {
            if (_cam == null) return;
            _cam.ToggleEOFlip();
            bool on = _cam.IsEoFlipOn;
            FlipBtn.Content = on ? "Flip: ВКЛ" : "Flip: ВЫКЛ";
            FlipBtn.Foreground = new SolidColorBrush(
                on ? Color.FromRgb(0x98, 0xF0, 0x19) : Color.FromRgb(0x66, 0x77, 0xAA));
        }

        private void NIR_Click(object sender, RoutedEventArgs e)
        {
            if (_cam == null) return;
            _cam.ToggleNIR();
            bool on = _cam.IsNirOn;
            NirBtn.Content = on ? "NIR: ВКЛ" : "NIR: ВЫКЛ";
            NirBtn.Foreground = new SolidColorBrush(
                on ? Color.FromRgb(0x98, 0xF0, 0x19) : Color.FromRgb(0x66, 0x77, 0xAA));
        }

        // ══════════════════════════════════════════════════════
        //  LRF
        // ══════════════════════════════════════════════════════
        private void LRFSingle_Click(object sender, RoutedEventArgs e) => _cam?.LRFSingle();
        private void LRFContinuous_Click(object sender, RoutedEventArgs e) => _cam?.LRFContinuous();
        private void LRFLpcl_Click(object sender, RoutedEventArgs e) => _cam?.LRFLpcl();
        private void LRFStop_Click(object sender, RoutedEventArgs e) => _cam?.LRFStop();

        // ══════════════════════════════════════════════════════
        //  AI Трекинг
        // ══════════════════════════════════════════════════════
        private void StartTracking_Click(object sender, RoutedEventArgs e) => _cam?.StartTracking();
        private void StopTracking_Click(object sender, RoutedEventArgs e) => _cam?.StopTracking();
        private void SearchMode_Click(object sender, RoutedEventArgs e) => _cam?.EnableSearchMode();
        private void ToggleAI_Click(object sender, RoutedEventArgs e) => _cam?.ToggleAIDetection();

        private void TrackSize_Click(object sender, RoutedEventArgs e)
        {
            if (_cam == null || sender is not Button btn) return;
            byte size = btn.Tag?.ToString() switch
            {
                "S" => ViewProCameraService.TRACK_SIZE_SMALL,
                "M" => ViewProCameraService.TRACK_SIZE_MID,
                "L" => ViewProCameraService.TRACK_SIZE_LARGE,
                "Auto" => ViewProCameraService.TRACK_SIZE_AUTO,
                _ => ViewProCameraService.TRACK_SIZE_AUTO
            };
            _cam.SetTrackingSize(size);
        }

        private void VideoView_MouseDown(object sender, MouseButtonEventArgs e)
        {
            if (e.LeftButton == MouseButtonState.Pressed && Keyboard.Modifiers == ModifierKeys.Control)
            {
                var pos = e.GetPosition(VideoView);
                _cam?.SetTrackingPoint(
                    (float)(pos.X / VideoView.ActualWidth),
                    (float)(pos.Y / VideoView.ActualHeight));
            }
        }

        // ══════════════════════════════════════════════════════
        //  SD Карта
        // ══════════════════════════════════════════════════════
        private void SDStatus_Click(object sender, RoutedEventArgs e) => _cam?.QuerySDStatus();
        private void SDFree_Click(object sender, RoutedEventArgs e) => _cam?.QuerySDFree();
        private void SDTotal_Click(object sender, RoutedEventArgs e) => _cam?.QuerySDTotal();

        // ══════════════════════════════════════════════════════
        //  Система
        // ══════════════════════════════════════════════════════
        private void Reboot_Click(object sender, RoutedEventArgs e) => _cam?.Reboot();

        // ══════════════════════════════════════════════════════
        //  Клавиатура
        // ══════════════════════════════════════════════════════
        private void OnKeyDown(object sender, KeyEventArgs e)
        {
            if (e.OriginalSource is TextBox) return;
            switch (e.Key)
            {
                case Key.W: _keyW = true; break;
                case Key.A: _keyA = true; break;
                case Key.S: _keyS = true; break;
                case Key.D: _keyD = true; break;
                case Key.Add: case Key.OemPlus: _keyZoomIn = true; break;
                case Key.Subtract: case Key.OemMinus: _keyZoomOut = true; break;
                case Key.PageUp: _keyFocusFar = true; break;
                case Key.PageDown: _keyFocusNear = true; break;
                case Key.Space:
                    _cam?.TakePhoto();
                    TakeLocalSnapshot();
                    break;
                case Key.R:
                    _cam?.ToggleRecording();
                    ToggleLocalRecording();
                    break;
                case Key.H: _cam?.ReturnToCenter(); break;
                case Key.F: _cam?.AutoFocus(); break;
                case Key.L: _cam?.LRFSingle(); break;
                case Key.P: _cam?.NextIRPalette(); break;
                case Key.O: OSD_Click(null, null); break;
            }
            e.Handled = true;
        }

        private void OnKeyUp(object sender, KeyEventArgs e)
        {
            if (e.OriginalSource is TextBox) return;
            bool wasMoving = _keyW || _keyA || _keyS || _keyD;
            switch (e.Key)
            {
                case Key.W: _keyW = false; break;
                case Key.A: _keyA = false; break;
                case Key.S: _keyS = false; break;
                case Key.D: _keyD = false; break;
                case Key.Add: case Key.OemPlus: _keyZoomIn = false; _cam?.ZoomStop(); break;
                case Key.Subtract: case Key.OemMinus: _keyZoomOut = false; _cam?.ZoomStop(); break;
                case Key.PageUp: _keyFocusFar = false; _cam?.FocusStop(); break;
                case Key.PageDown: _keyFocusNear = false; _cam?.FocusStop(); break;
            }
            // Остановить гимбал если все клавиши отпущены
            if (wasMoving && !_keyW && !_keyA && !_keyS && !_keyD && !_joystickActive)
                _cam?.StopGimbal();
            e.Handled = true;
        }

        // ══════════════════════════════════════════════════════
        //  Закрытие окна
        // ══════════════════════════════════════════════════════
        private void OnWindowClosed(object sender, EventArgs e)
        {
            _joystickTimer?.Stop();
            if (_isLocalRecording) StopLocalRecording();
            _cam?.Disconnect();
            _cam?.Dispose();
            _mediaPlayer?.Stop();
            _mediaPlayer?.Dispose();
            _libVLC?.Dispose();
        }
    }
}