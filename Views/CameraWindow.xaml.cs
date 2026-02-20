using System;
using System.Diagnostics;
using System.IO;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Threading;
using LibVLCSharp.Shared;
using SimpleDroneGCS.Services;

using static SimpleDroneGCS.Helpers.Loc;

namespace SimpleDroneGCS.Views
{
    public partial class CameraWindow : Window
    {
        private Z30TCameraService _cameraService;
        private MAVLinkService _mavLink;
        private LibVLC _libVLC;
        private MediaPlayer _mediaPlayer;
        private DispatcherTimer _joystickTimer;

        private CameraConnectionSettings _connectionSettings;
        private string _rtspUrl = "";

        // Джойстик
        private bool _isJoystickActive = false;
        private double _joystickX = 0;
        private double _joystickY = 0;

        // Клавиатура
        private bool _keyW, _keyA, _keyS, _keyD;
        private bool _keyZoomIn, _keyZoomOut;
        private const int GIMBAL_SPEED = 60;

        // Локальная запись
        private bool _isLocalRecording = false;
        private MediaPlayer _recordPlayer;
        private string _mediaFolder = "";

        public CameraWindow(CameraConnectionSettings settings, MAVLinkService mavLink)
        {
            InitializeComponent();

            _connectionSettings = settings;
            _mavLink = mavLink;
            _rtspUrl = settings.RtspUrl;
            IpTextBox.Text = settings.CameraIP;

            Title = string.Format(Get("Cam_TitleFmt"), settings.CameraType);

            _mediaFolder = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments),
                "SQK_GCS", "Camera");
            Directory.CreateDirectory(_mediaFolder);

            this.KeyDown += OnKeyDown;
            this.KeyUp += OnKeyUp;
            this.Closed += OnWindowClosed;
            this.Loaded += OnWindowLoaded;

            UpdateStatus(Get("Cam_Init"));
        }

        #region Init & Connect

        private async void OnWindowLoaded(object sender, RoutedEventArgs e)
        {
            if (JoystickThumb != null && JoystickArea != null)
            {
                Canvas.SetLeft(JoystickThumb, (JoystickArea.Width - JoystickThumb.Width) / 2);
                Canvas.SetTop(JoystickThumb, (JoystickArea.Height - JoystickThumb.Height) / 2);
            }

            // 1. Камера Z30T
            _cameraService = new Z30TCameraService();
            _cameraService.IpAddress = _connectionSettings.CameraIP;
            _cameraService.Port = _connectionSettings.TcpPort;
            _cameraService.StatusChanged += s => Dispatcher.Invoke(() => UpdateStatus(s));
            _cameraService.ErrorOccurred += (_, msg) => Dispatcher.Invoke(() => UpdateStatus($"Ошибка: {msg}"));
            _cameraService.AnglesReceived += (_, angles) => Dispatcher.Invoke(() =>
            {
                YawText.Text = string.Format(Get("Cam_HeadingFmt"), angles.Yaw);
                PitchText.Text = string.Format(Get("Cam_PitchFmt"), angles.Pitch);
                RollText.Text = string.Format(Get("Cam_RollFmt"), angles.Roll);
            });
            _cameraService.DistanceReceived += (_, dist) => Dispatcher.Invoke(() =>
            {
                DistanceText.Text = string.Format(Get("Cam_DistFmt"), dist);
            });

            _joystickTimer = new DispatcherTimer();
            _joystickTimer.Interval = TimeSpan.FromMilliseconds(50);
            _joystickTimer.Tick += JoystickTimer_Tick;
            _joystickTimer.Start();

            // 2. LibVLC
            try
            {
                UpdateStatus(Get("Cam_LoadingVideo"));
                await Task.Run(() => Core.Initialize());
                _libVLC = new LibVLC("--no-xlib", "--network-caching=150", "--rtsp-tcp");
                _mediaPlayer = new MediaPlayer(_libVLC);
                await Task.Delay(150);
                VideoView.MediaPlayer = _mediaPlayer;
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[Камера] LibVLC init error: {ex.Message}");
                UpdateStatus($"Видео недоступно: {ex.Message}");
            }

            // 3. Авто-подключение
            AutoConnect();
        }

        private async void AutoConnect()
        {
            if (_cameraService == null) return;

            bool ok = await _cameraService.ConnectAsync();
            if (ok)
            {
                UpdateStatus(Get("Connected"));
                ConnectButton.Content = "Отключить";
                StartVideo();
            }
            else
            {
                UpdateStatus("Камера не найдена");
                ConnectButton.Content = "Повторить";
            }
        }

        private async void ConnectButton_Click(object sender, RoutedEventArgs e)
        {
            if (_cameraService == null) return;

            if (_cameraService.IsConnected)
            {
                StopVideo();
                _cameraService.Disconnect();
                ConnectButton.Content = "Подключить";
                UpdateStatus("Отключено");
            }
            else
            {
                _cameraService.IpAddress = IpTextBox.Text.Trim();
                ConnectButton.Content = "...";
                ConnectButton.IsEnabled = false;

                bool ok = await _cameraService.ConnectAsync();

                ConnectButton.IsEnabled = true;
                if (ok)
                {
                    ConnectButton.Content = "Отключить";
                    StartVideo();
                }
                else
                {
                    ConnectButton.Content = "Подключить";
                    UpdateStatus("Ошибка подключения");
                }
            }
        }

        #endregion

        #region Видео

        private void StartVideo()
        {
            if (_mediaPlayer == null || _libVLC == null) return;

            try
            {
                string url = _rtspUrl;
                if (string.IsNullOrEmpty(url))
                    url = _cameraService?.RtspUrl;

                UpdateStatus($"Запуск видео: {url}");

                var media = new Media(_libVLC, url, FromType.FromLocation);
                media.AddOption(":network-caching=150");
                media.AddOption(":rtsp-tcp");
                media.AddOption(":live-caching=50");
                _mediaPlayer.Play(media);
            }
            catch (Exception ex)
            {
                UpdateStatus($"Ошибка видео: {ex.Message}");
            }
        }

        private void StopVideo() => _mediaPlayer?.Stop();

        #endregion

        #region Управление подвесом

        private void JoystickTimer_Tick(object sender, EventArgs e)
        {
            if (_cameraService == null || !_cameraService.IsConnected) return;

            int yaw = 0, pitch = 0;

            if (_keyW) pitch += GIMBAL_SPEED;
            if (_keyS) pitch -= GIMBAL_SPEED;
            if (_keyD) yaw += GIMBAL_SPEED;
            if (_keyA) yaw -= GIMBAL_SPEED;

            if (_isJoystickActive)
            {
                yaw = (int)(_joystickX * 100);
                pitch = (int)(-_joystickY * 100);
            }

            if (yaw != 0 || pitch != 0)
                _cameraService.SetGimbalSpeed(yaw, pitch);

            if (_keyZoomIn) _cameraService.ZoomIn();
            else if (_keyZoomOut) _cameraService.ZoomOut();
        }

        private void JoystickArea_MouseDown(object sender, MouseButtonEventArgs e)
        {
            if (sender is Canvas canvas)
            {
                _isJoystickActive = true;
                canvas.CaptureMouse();
                UpdateJoystickPosition(e.GetPosition(canvas), canvas);
            }
        }

        private void JoystickArea_MouseMove(object sender, MouseEventArgs e)
        {
            if (_isJoystickActive && sender is Canvas canvas)
                UpdateJoystickPosition(e.GetPosition(canvas), canvas);
        }

        private void JoystickArea_MouseUp(object sender, MouseButtonEventArgs e)
        {
            if (sender is Canvas canvas)
            {
                _isJoystickActive = false;
                canvas.ReleaseMouseCapture();
                _joystickX = 0;
                _joystickY = 0;
                UpdateJoystickThumb(canvas);
                _cameraService?.StopGimbal();
            }
        }

        private void UpdateJoystickPosition(Point pos, Canvas canvas)
        {
            double cx = canvas.Width / 2, cy = canvas.Height / 2, r = cx - 15;
            double dx = pos.X - cx, dy = pos.Y - cy;
            double dist = Math.Sqrt(dx * dx + dy * dy);
            if (dist > r) { dx = dx * r / dist; dy = dy * r / dist; }
            _joystickX = dx / r;
            _joystickY = dy / r;
            UpdateJoystickThumb(canvas);
        }

        private void UpdateJoystickThumb(Canvas canvas)
        {
            if (JoystickThumb == null) return;
            double cx = canvas.Width / 2, cy = canvas.Height / 2, r = cx - 15;
            Canvas.SetLeft(JoystickThumb, cx + _joystickX * r - JoystickThumb.Width / 2);
            Canvas.SetTop(JoystickThumb, cy + _joystickY * r - JoystickThumb.Height / 2);
        }

        private void HomeButton_Click(object sender, RoutedEventArgs e) => _cameraService?.ReturnToCenter();
        private void LookDownButton_Click(object sender, RoutedEventArgs e) => _cameraService?.LookDown();

        #endregion

        #region Зум и фокус

        private void ZoomIn_Down(object sender, MouseButtonEventArgs e) => _cameraService?.ZoomIn();
        private void ZoomOut_Down(object sender, MouseButtonEventArgs e) => _cameraService?.ZoomOut();
        private void Zoom_Up(object sender, MouseButtonEventArgs e) => _cameraService?.ZoomStop();
        private void ZoomStop_Click(object sender, RoutedEventArgs e) => _cameraService?.ZoomStop();
        private void AutoFocus_Click(object sender, RoutedEventArgs e) => _cameraService?.AutoFocus();

        #endregion

        #region Фото и видео

        private void Photo_Click(object sender, RoutedEventArgs e)
        {
            _cameraService?.TakePhoto();
            TakeLocalSnapshot();
        }

        private void Record_Click(object sender, RoutedEventArgs e)
        {
            _cameraService?.ToggleRecording();
            ToggleLocalRecording();
        }

        #endregion

        #region Локальное сохранение

        private void TakeLocalSnapshot()
        {
            if (_mediaPlayer == null || !_mediaPlayer.IsPlaying) return;

            try
            {
                string file = Path.Combine(_mediaFolder,
                    $"photo_{DateTime.Now:yyyyMMdd_HHmmss}.png");
                _mediaPlayer.TakeSnapshot(0, file, 0, 0);
                UpdateStatus($"Фото: {Path.GetFileName(file)}");
            }
            catch (Exception ex)
            {
                UpdateStatus($"Ошибка фото: {ex.Message}");
            }
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
                string url = _rtspUrl;
                if (string.IsNullOrEmpty(url))
                    url = _cameraService?.RtspUrl;

                string file = Path.Combine(_mediaFolder,
                    $"rec_{DateTime.Now:yyyyMMdd_HHmmss}.mp4");

                _recordPlayer = new MediaPlayer(_libVLC);
                var media = new Media(_libVLC, url, FromType.FromLocation);
                media.AddOption(":network-caching=150");
                media.AddOption(":rtsp-tcp");
                media.AddOption($":sout=#file{{dst={file}}}");
                media.AddOption(":sout-keep");

                _recordPlayer.Play(media);
                _isLocalRecording = true;

                RecordButton.Content = "⏹ Стоп";
                UpdateStatus($"Запись: {Path.GetFileName(file)}");
            }
            catch (Exception ex)
            {
                UpdateStatus($"Ошибка записи: {ex.Message}");
            }
        }

        private void StopLocalRecording()
        {
            try
            {
                _recordPlayer?.Stop();
                _recordPlayer?.Dispose();
                _recordPlayer = null;
                _isLocalRecording = false;

                RecordButton.Content = "⏺ Запись";
                UpdateStatus("Запись сохранена");
            }
            catch (Exception ex)
            {
                UpdateStatus($"Ошибка остановки: {ex.Message}");
            }
        }

        #endregion

        #region Источник видео

        private void SensorEO_Click(object sender, RoutedEventArgs e) => _cameraService?.SetVideoEO();
        private void SensorIR_Click(object sender, RoutedEventArgs e) => _cameraService?.SetVideoIR();
        private void SensorPIP1_Click(object sender, RoutedEventArgs e) => _cameraService?.SetVideoEO_IR_PIP();
        private void SensorPIP2_Click(object sender, RoutedEventArgs e) => _cameraService?.SetVideoIR_EO_PIP();
        private void SensorFusion_Click(object sender, RoutedEventArgs e) => _cameraService?.SetVideoFusion();

        #endregion

        #region ИК палитра (10 режимов)

        private void IRPalette_Click(object sender, RoutedEventArgs e)
        {
            if (sender is Button btn && btn.Tag != null && int.TryParse(btn.Tag.ToString(), out int idx))
                _cameraService?.SetIRPalette(idx);
        }

        #endregion

        #region Температура (Gear 1-3)

        private void TempGear_Click(object sender, RoutedEventArgs e)
        {
            if (sender is Button btn && btn.Tag != null && int.TryParse(btn.Tag.ToString(), out int gear))
                _cameraService?.SetTempGear(gear);
        }

        #endregion

        #region Дальномер (Laser toggle)

        private void LaserToggle_Click(object sender, RoutedEventArgs e) => _cameraService?.ToggleLaser();

        #endregion

        #region Подсветка и OSD

        private void FillLight_Click(object sender, RoutedEventArgs e) => _cameraService?.ToggleFillLight();
        private void OSD_Click(object sender, RoutedEventArgs e) => _cameraService?.ToggleOSD();

        #endregion

        #region Трекинг

        private void VideoView_MouseDown(object sender, MouseButtonEventArgs e)
        {
            if (e.LeftButton == MouseButtonState.Pressed && Keyboard.Modifiers == ModifierKeys.Control)
            {
                var pos = e.GetPosition(VideoView);
                _cameraService?.SetTrackingPoint(
                    (float)(pos.X / VideoView.ActualWidth),
                    (float)(pos.Y / VideoView.ActualHeight));
            }
        }

        private void SearchMode_Click(object sender, RoutedEventArgs e) => _cameraService?.EnableSearchMode();
        private void StopTracking_Click(object sender, RoutedEventArgs e) => _cameraService?.StopTracking();
        private void ToggleAI_Click(object sender, RoutedEventArgs e) => _cameraService?.ToggleAIDetection();

        #endregion

        #region Клавиатура

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
                case Key.Space: _cameraService?.TakePhoto(); break;
                case Key.R: Record_Click(sender, e); break;
                case Key.H: _cameraService?.ReturnToCenter(); break;
                case Key.F: _cameraService?.AutoFocus(); break;
                case Key.L: _cameraService?.ToggleLaser(); break;
                case Key.T: _cameraService?.MeasureTemperature(); break;
                case Key.I: _cameraService?.ToggleFillLight(); break;
                case Key.O: _cameraService?.ToggleOSD(); break;
                case Key.P: _cameraService?.NextPalette(); break;
            }
            e.Handled = true;
        }

        private void OnKeyUp(object sender, KeyEventArgs e)
        {
            if (e.OriginalSource is TextBox) return;

            switch (e.Key)
            {
                case Key.W: _keyW = false; break;
                case Key.A: _keyA = false; break;
                case Key.S: _keyS = false; break;
                case Key.D: _keyD = false; break;
                case Key.Add: case Key.OemPlus: _keyZoomIn = false; _cameraService?.ZoomStop(); break;
                case Key.Subtract: case Key.OemMinus: _keyZoomOut = false; _cameraService?.ZoomStop(); break;
            }
            if (!_keyW && !_keyA && !_keyS && !_keyD && !_isJoystickActive)
                _cameraService?.StopGimbal();
            e.Handled = true;
        }

        #endregion

        #region Утилиты

        private void UpdateStatus(string msg)
        {
            StatusText.Text = msg;
            Debug.WriteLine($"[Камера] {msg}");
        }

        private void OnWindowClosed(object sender, EventArgs e)
        {
            _joystickTimer?.Stop();
            if (_isLocalRecording) StopLocalRecording();
            _cameraService?.Disconnect();
            _cameraService?.Dispose();
            _mediaPlayer?.Stop();
            _mediaPlayer?.Dispose();
            _libVLC?.Dispose();
        }

        #endregion
    }
}
