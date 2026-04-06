using System;
using System.Windows;
using System.Windows.Media;
using System.Windows.Threading;
using SimpleDroneGCS.Simulator;
using static SimpleDroneGCS.Helpers.Loc;

namespace SimpleDroneGCS
{
    public partial class SimulatorWindow : Window
    {
        private SimulatedDrone? _drone;
        private DispatcherTimer _uiTimer;
        private DateTime _flightStart;
        private bool _flightTimerRunning;

        private static readonly SolidColorBrush BrushGreen = new(Color.FromRgb(0x98, 0xF0, 0x19));
        private static readonly SolidColorBrush BrushRed = new(Color.FromRgb(0xE2, 0x4B, 0x4A));
        private static readonly SolidColorBrush BrushAmber = new(Color.FromRgb(0xEF, 0x9F, 0x27));
        private static readonly SolidColorBrush BrushBlue = new(Color.FromRgb(0x85, 0xB7, 0xEB));
        private static readonly SolidColorBrush BrushMuted = new(Color.FromRgb(0x4A, 0x60, 0x80));
        private static readonly SolidColorBrush BrushDim = new(Color.FromRgb(0x2A, 0x43, 0x61));
        private static readonly SolidColorBrush BrushText = new(Color.FromRgb(0xC8, 0xD8, 0xE8));

        public SimulatorWindow()
        {
            InitializeComponent();

            _uiTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(200) };
            _uiTimer.Tick += UiTimer_Tick;
        }

        // ─── Start / Stop ─────────────────────────────────────────────────────

        private void StartStopBtn_Click(object sender, RoutedEventArgs e)
        {
            if (_drone == null || !_drone.IsRunning)
                StartSimulator();
            else
                StopSimulator();
        }

        private void StartSimulator()
        {
            _drone = new SimulatedDrone();
            _drone.LogMessage += OnLog;
            _drone.StateChanged += OnStateChanged;
            _drone.SetScenario(GetSelectedScenario());
            _drone.SetCruiseSpeed(SpeedSlider.Value);

            _drone.Start();
            _uiTimer.Start();

            StartStopBtn.Content = Get("Sim_BtnStop");
            StartStopBtn.Foreground = BrushRed;
            StartStopBtn.BorderBrush = BrushRed;

            UdpDot.Fill = BrushGreen;
            UdpLabel.Foreground = BrushGreen;
            UdpLabel.Text = Get("Sim_UdpActive");

            SimStatusBig.Text = Get("Sim_StatusRunning");
            SimStatusBig.Foreground = BrushGreen;
            FlightTimeTxt.Foreground = BrushMuted;
            HomeCoordsTxt.Foreground = BrushMuted;

            VehicleTypeCombo.IsEnabled = false;
            ScenarioCombo.IsEnabled = false;
        }

        private void StopSimulator()
        {
            _uiTimer.Stop();
            _drone?.Stop();
            _drone = null;
            _flightTimerRunning = false;

            StartStopBtn.Content = Get("Sim_BtnStart");
            StartStopBtn.Foreground = BrushGreen;
            StartStopBtn.BorderBrush = BrushGreen;

            UdpDot.Fill = BrushMuted;
            UdpLabel.Foreground = BrushMuted;
            UdpLabel.Text = Get("Sim_UdpReady");
            GcsDot.Fill = BrushMuted;
            GcsLabel.Foreground = BrushMuted;
            GcsLabel.Text = Get("Sim_GcsDisconnected");

            SimStatusBig.Text = Get("Sim_StatusStopped");
            SimStatusBig.Foreground = BrushDim;
            FlightTimeTxt.Text = "00:00:00";
            FlightTimeTxt.Foreground = BrushDim;
            HomeCoordsTxt.Foreground = BrushDim;

            ArmBadge.Text = Get("Sim_BadgeDisarmed");
            ArmBadge.Foreground = BrushRed;
            ModeLabel.Text = "STABILIZE";
            StateLabel.Text = Get("Sim_StateDisarmed");
            StateLabel.Foreground = BrushMuted;
            WpLabel.Text = Get("Sim_WpNone");
            AltLabel.Text = "0.0 м";
            SpeedLabel.Text = "0.0 м/с";
            BattLabel.Text = "100% · 12.6В";
            GpsLabel.Text = Fmt("Sim_GpsOk", "12");
            GpsLabel.Foreground = BrushGreen;

            VehicleTypeCombo.IsEnabled = true;
            ScenarioCombo.IsEnabled = true;
        }

        // ─── UI update ────────────────────────────────────────────────────────

        private void UiTimer_Tick(object? sender, EventArgs e)
        {
            if (_drone == null) return;
            var p = _drone.Physics;

            // GCS connection indicator
            if (_drone.IsGcsConnected)
            {
                GcsDot.Fill = BrushGreen;
                GcsLabel.Foreground = BrushGreen;
                GcsLabel.Text = Get("Sim_GcsConnected");
            }

            // ARM badge
            bool armed = p.Armed;
            ArmBadge.Text = armed ? Get("Sim_BadgeArmed") : Get("Sim_BadgeDisarmed");
            ArmBadge.Foreground = armed ? BrushGreen : BrushRed;

            // Mode
            ModeLabel.Text = p.FlightMode;

            // State
            StateLabel.Text = p.State switch
            {
                SimState.Disarmed => Get("Sim_StateDisarmed"),
                SimState.Armed => Get("Sim_StateArmed"),
                SimState.Takeoff => Get("Sim_StateTakeoff"),
                SimState.Mission => Get("Sim_StateMission"),
                SimState.Rtl => Get("Sim_StateRtl"),
                SimState.Landing => Get("Sim_StateLanding"),
                _ => p.State.ToString()
            };
            StateLabel.Foreground = p.State switch
            {
                SimState.Mission => BrushGreen,
                SimState.Takeoff => BrushBlue,
                SimState.Rtl => BrushAmber,
                SimState.Landing => BrushAmber,
                SimState.Armed => BrushText,
                _ => BrushMuted
            };

            // WP progress
            WpLabel.Text = p.TotalWpCount > 0
                ? Fmt("Sim_WpProgress", Math.Max(0, p.CurrentWpIndex), p.TotalWpCount)
                : Get("Sim_WpNone");

            // Telemetry
            AltLabel.Text = $"{p.AltRel:F1} м";
            SpeedLabel.Text = $"{p.Speed:F1}{Get("Sim_SpeedUnit")}";

            var battColor = p.BattPct > 30 ? BrushAmber : BrushRed;
            BattLabel.Text = $"{p.BattPct:F0}% · {p.Voltage:F1}В";
            BattLabel.Foreground = battColor;

            bool gpsOk = p.GpsFixType >= 3;
            GpsLabel.Text = gpsOk
                ? Fmt("Sim_GpsOk", p.SatCount)
                : Fmt("Sim_GpsLost", p.SatCount);
            GpsLabel.Foreground = gpsOk ? BrushGreen : BrushRed;

            // Flight timer
            if (p.Armed && !_flightTimerRunning)
            {
                _flightStart = DateTime.Now;
                _flightTimerRunning = true;
            }
            else if (!p.Armed && _flightTimerRunning && p.State == SimState.Disarmed)
            {
                _flightTimerRunning = false;
            }

            if (_flightTimerRunning)
            {
                var elapsed = DateTime.Now - _flightStart;
                FlightTimeTxt.Text = elapsed.ToString(@"hh\:mm\:ss");
            }
        }

        // ─── Events from drone (background threads → Dispatcher) ──────────────

        private void OnLog(string msg) =>
            Dispatcher.InvokeAsync(() =>
            {
                LogList.Items.Add(msg);
                if (LogList.Items.Count > 200)
                    LogList.Items.RemoveAt(0);
                LogList.ScrollIntoView(LogList.Items[^1]);
            });

        private void OnStateChanged() =>
            Dispatcher.InvokeAsync(() => UiTimer_Tick(null, EventArgs.Empty));

        // ─── Controls ─────────────────────────────────────────────────────────

        private void SpeedSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
        {
            if (SpeedSliderLabel == null) return;
            SpeedSliderLabel.Text = $"{(int)e.NewValue}{Get("Sim_SpeedUnit")}";
            _drone?.SetCruiseSpeed(e.NewValue);
        }

        private void VehicleTypeCombo_SelectionChanged(object sender, System.Windows.Controls.SelectionChangedEventArgs e)
        {
            // Reserved for VTOL support in future version
        }

        private void ScenarioCombo_SelectionChanged(object sender, System.Windows.Controls.SelectionChangedEventArgs e)
        {
            _drone?.SetScenario(GetSelectedScenario());
        }

        private string GetSelectedScenario() =>
            (ScenarioCombo?.SelectedItem as System.Windows.Controls.ComboBoxItem)?.Content?.ToString()
            ?? Get("Sim_ScenarioNormal");

        // ─── Window closing ───────────────────────────────────────────────────

        private void Window_Closing(object sender, System.ComponentModel.CancelEventArgs e)
        {
            StopSimulator();
        }
    }
}
