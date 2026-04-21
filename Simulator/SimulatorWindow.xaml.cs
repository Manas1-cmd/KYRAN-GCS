using System;
using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using SimpleDroneGCS.Simulator.Core;

namespace SimpleDroneGCS.Simulator
{
    public partial class SimulatorWindow : Window
    {
        private readonly SimulatedDrone _drone;

        // Bright orange for active failure state.
        private static readonly Brush ActiveFailBrush =
            new SolidColorBrush(Color.FromRgb(0xF9, 0x73, 0x16));
        private static readonly Brush InactiveFailBrush =
            new SolidColorBrush(Color.FromRgb(0x3D, 0x3D, 0x3D));
        private static readonly Brush ActiveFailBorder =
            new SolidColorBrush(Color.FromRgb(0xEA, 0x58, 0x0C));
        private static readonly Brush InactiveFailBorder =
            new SolidColorBrush(Color.FromRgb(0x4D, 0x4D, 0x4D));

        public SimulatorWindow()
        {
            InitializeComponent();

            _drone = new SimulatedDrone(VehicleType.Vtol);

            // Заполнить поля HOME значениями по умолчанию.
            HomeLatBox.Text = SimulatedDrone.DefaultHomeLat.ToString("F6", CultureInfo.InvariantCulture);
            HomeLonBox.Text = SimulatedDrone.DefaultHomeLon.ToString("F6", CultureInfo.InvariantCulture);
            HomeAltBox.Text = SimulatedDrone.DefaultHomeAltAmsl.ToString("F0", CultureInfo.InvariantCulture);

            // Подписка на события SimulatedDrone.
            _drone.StateChanged += OnStateChanged;
            _drone.LogMessage += OnLogMessage;
            _drone.GcsConnected += OnGcsConnected;
            _drone.GcsDisconnected += OnGcsDisconnected;

            // Выгрузить накопленные логи (после конструктора).
            foreach (var msg in _drone.DrainLog()) AppendLog(msg);

            // Применить начальные значения HUD.
            UpdateHud(_drone.State.Snapshot());
        }

        // =====================================================================
        // Управление
        // =====================================================================

        private void StartBtn_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                _drone.Start();
                StartBtn.IsEnabled = false;
                StopBtn.IsEnabled = true;
                PauseBtn.IsEnabled = true;
                PauseBtn.Content = "⏸  Пауза";
            }
            catch (Exception ex)
            {
                AppendLog("[ERROR] " + ex.Message);
            }
        }

        private void StopBtn_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                _drone.Stop();
                StartBtn.IsEnabled = true;
                StopBtn.IsEnabled = false;
                PauseBtn.IsEnabled = false;
                // Reset радио на x1.
                Speed1.IsChecked = true;
            }
            catch (Exception ex)
            {
                AppendLog("[ERROR] " + ex.Message);
            }
        }

        private void PauseBtn_Click(object sender, RoutedEventArgs e)
        {
            bool paused = _drone.TimeScale > 0.01;
            _drone.Pause(paused);
            PauseBtn.Content = paused ? "▶  Продолжить" : "⏸  Пауза";
        }

        private void Speed_Checked(object sender, RoutedEventArgs e)
        {
            if (_drone == null) return;
            if (sender is RadioButton rb && rb.Tag is string tag &&
                double.TryParse(tag, NumberStyles.Any, CultureInfo.InvariantCulture, out double scale))
            {
                _drone.TimeScale = scale;
            }
        }

        private void Vehicle_Checked(object sender, RoutedEventArgs e)
        {
            if (_drone == null) return;
            if (sender is RadioButton rb && rb.Tag is string tag)
            {
                var type = tag == "Vtol" ? VehicleType.Vtol : VehicleType.Copter;
                _drone.SetVehicleType(type);
                UpdateMotor5Visibility(type);
            }
        }

        private void UpdateMotor5Visibility(VehicleType type)
        {
            // M5 доступен только для VTOL.
            FailMotor5.IsEnabled = type == VehicleType.Vtol;
        }

        private void ApplyHomeBtn_Click(object sender, RoutedEventArgs e)
        {
            if (!double.TryParse(HomeLatBox.Text, NumberStyles.Any, CultureInfo.InvariantCulture, out double lat) ||
                !double.TryParse(HomeLonBox.Text, NumberStyles.Any, CultureInfo.InvariantCulture, out double lon) ||
                !double.TryParse(HomeAltBox.Text, NumberStyles.Any, CultureInfo.InvariantCulture, out double alt))
            {
                AppendLog("[ERROR] Некорректные координаты HOME");
                return;
            }
            if (lat < -90 || lat > 90 || lon < -180 || lon > 180)
            {
                AppendLog("[ERROR] HOME вне диапазона");
                return;
            }
            _drone.SetHome(lat, lon, alt);
        }

        // =====================================================================
        // Ветер
        // =====================================================================

        private void Wind_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
        {
            if (_drone == null) return;
            if (WindDirSlider == null || WindSpeedSlider == null) return;

            double dir = WindDirSlider.Value;
            double speed = WindSpeedSlider.Value;

            if (WindDirLabel != null) WindDirLabel.Text = $"{dir:F0}°";
            if (WindSpeedLabel != null) WindSpeedLabel.Text = $"{speed:F1} м/с";

            _drone.SetWind(dir, speed);
        }

        // =====================================================================
        // Failures
        // =====================================================================

        private void FailureToggle_Click(object sender, RoutedEventArgs e)
        {
            if (sender is not Button btn) return;
            bool isActive = btn.Tag is string s && s == "True";
            bool newState = !isActive;

            if (btn == FailGps)
            {
                if (newState) _drone.InjectGpsLoss(); else _drone.ClearGpsLoss();
            }
            else if (btn == FailRc)
            {
                if (newState) _drone.InjectRcFailsafe(); else _drone.ClearRcFailsafe();
            }
            else if (btn == FailBattLow)
            {
                if (newState)
                {
                    _drone.InjectBatteryLow();
                    // BattLow и BattCrit — взаимоисключающие.
                    if (FailBattCrit.Tag is string bc && bc == "True")
                        SetFailButton(FailBattCrit, false);
                }
                else _drone.ClearBatteryFailure();
            }
            else if (btn == FailBattCrit)
            {
                if (newState)
                {
                    _drone.InjectBatteryCritical();
                    if (FailBattLow.Tag is string bl && bl == "True")
                        SetFailButton(FailBattLow, false);
                }
                else _drone.ClearBatteryFailure();
            }
            else if (btn == FailCompass)
            {
                if (newState) _drone.InjectCompassError(); else _drone.ClearCompassError();
            }
            else if (btn == FailEkf)
            {
                if (newState) _drone.InjectEkfDivergence(); else _drone.ClearEkfDivergence();
            }

            SetFailButton(btn, newState);
        }

        private void MotorFailure_Click(object sender, RoutedEventArgs e)
        {
            if (sender is not Button btn) return;

            int motorIndex = btn == FailMotor1 ? 0
                           : btn == FailMotor2 ? 1
                           : btn == FailMotor3 ? 2
                           : btn == FailMotor4 ? 3
                           : btn == FailMotor5 ? 4 : -1;
            if (motorIndex < 0) return;

            bool isActive = btn.Tag is string s && s == "True";
            bool newState = !isActive;

            if (newState)
            {
                // Только один мотор одновременно — сбросим остальные.
                foreach (var other in new[] { FailMotor1, FailMotor2, FailMotor3, FailMotor4, FailMotor5 })
                    if (other != btn) SetFailButton(other, false);

                _drone.InjectMotorFailure(motorIndex);
            }
            else
            {
                _drone.ClearMotorFailure();
            }
            SetFailButton(btn, newState);
        }

        private void ClearAllFailures_Click(object sender, RoutedEventArgs e)
        {
            _drone.ClearAllFailures();
            foreach (var b in new[] { FailGps, FailRc, FailBattLow, FailBattCrit,
                                      FailCompass, FailEkf,
                                      FailMotor1, FailMotor2, FailMotor3, FailMotor4, FailMotor5 })
                SetFailButton(b, false);
        }

        private static void SetFailButton(Button btn, bool active)
        {
            btn.Tag = active ? "True" : "False";
            btn.Background = active ? ActiveFailBrush : InactiveFailBrush;
            btn.BorderBrush = active ? ActiveFailBorder : InactiveFailBorder;
            btn.Foreground = active ? Brushes.White : new SolidColorBrush(Color.FromRgb(0xE0, 0xE0, 0xE0));
        }

        // =====================================================================
        // Log
        // =====================================================================

        private void OnLogMessage(object sender, string msg)
        {
            Dispatcher.BeginInvoke(new Action(() => AppendLog(msg)));
        }

        private void AppendLog(string msg)
        {
            LogList.Items.Add(msg);
            // Ограничить ~200 строк.
            while (LogList.Items.Count > 200)
                LogList.Items.RemoveAt(0);
            // Автоскролл вниз.
            if (LogList.Items.Count > 0)
                LogList.ScrollIntoView(LogList.Items[LogList.Items.Count - 1]);
        }

        private void ClearLog_Click(object sender, RoutedEventArgs e)
        {
            LogList.Items.Clear();
        }

        // =====================================================================
        // State updates
        // =====================================================================

        private void OnStateChanged(object sender, SimState snap)
        {
            Dispatcher.BeginInvoke(new Action(() => UpdateHud(snap)));
        }

        private void UpdateHud(SimState s)
        {
            if (s == null) return;

            HudLat.Text = s.Position.Lat.ToString("F6", CultureInfo.InvariantCulture);
            HudLon.Text = s.Position.Lon.ToString("F6", CultureInfo.InvariantCulture);
            HudAlt.Text = $"{s.Position.AltRelative:F1} м";
            HudGs.Text = $"{s.Velocity.GroundSpeed:F1} м/с";
            HudAs.Text = $"{s.Velocity.AirSpeed:F1} м/с";

            double hdg = s.Attitude.Yaw * 180.0 / Math.PI;
            while (hdg < 0) hdg += 360;
            while (hdg >= 360) hdg -= 360;
            HudHdg.Text = $"{hdg:F0}°";

            HudRoll.Text = $"{s.Attitude.Roll * 180.0 / Math.PI:+0;-0;0}°";
            HudPitch.Text = $"{s.Attitude.Pitch * 180.0 / Math.PI:+0;-0;0}°";
            HudBatt.Text = $"{s.Battery.Percent:F0}% ({s.Battery.VoltageV:F1} В)";
            HudMode.Text = FormatMode(s);
            HudArm.Text = s.Armed ? "✓ Armed" : "— Disarmed";
            HudArm.Foreground = s.Armed
                ? new SolidColorBrush(Color.FromRgb(0x22, 0xC5, 0x5E))
                : new SolidColorBrush(Color.FromRgb(0x9C, 0xA3, 0xAF));
        }

        private static string FormatMode(SimState s)
        {
            uint m = s.CustomMode;
            if (s.Vehicle == VehicleType.Vtol)
            {
                return m switch
                {
                    0 => "MANUAL",
                    1 => "CIRCLE",
                    2 => "STABILIZE",
                    5 => "FBWA",
                    6 => "FBWB",
                    7 => "CRUISE",
                    10 => "AUTO",
                    11 => "RTL",
                    12 => "LOITER",
                    13 => "TAKEOFF",
                    15 => "GUIDED",
                    17 => "QSTABILIZE",
                    18 => "QHOVER",
                    19 => "QLOITER",
                    20 => "QLAND",
                    21 => "QRTL",
                    _ => $"MODE({m})",
                };
            }
            return m switch
            {
                0 => "STABILIZE",
                2 => "ALT_HOLD",
                3 => "AUTO",
                4 => "GUIDED",
                5 => "LOITER",
                6 => "RTL",
                9 => "LAND",
                16 => "POSHOLD",
                17 => "BRAKE",
                21 => "SMART_RTL",
                _ => $"MODE({m})",
            };
        }

        // =====================================================================
        // GCS indicators
        // =====================================================================

        private void OnGcsConnected(object sender, EventArgs e)
        {
            Dispatcher.BeginInvoke(new Action(() =>
            {
                HudGcs.Text = "Подкл.";
                HudGcs.Foreground = new SolidColorBrush(Color.FromRgb(0x22, 0xC5, 0x5E));
            }));
        }

        private void OnGcsDisconnected(object sender, EventArgs e)
        {
            Dispatcher.BeginInvoke(new Action(() =>
            {
                HudGcs.Text = "Не подкл.";
                HudGcs.Foreground = new SolidColorBrush(Color.FromRgb(0x9C, 0xA3, 0xAF));
            }));
        }

        // =====================================================================
        // Close
        // =====================================================================

        protected override void OnClosed(EventArgs e)
        {
            try { _drone?.Dispose(); } catch { }
            base.OnClosed(e);
        }
    }
}