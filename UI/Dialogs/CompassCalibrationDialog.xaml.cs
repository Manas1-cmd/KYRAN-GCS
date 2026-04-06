using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Shapes;
using System.Windows.Threading;
using SimpleDroneGCS.Services;
using static SimpleDroneGCS.Helpers.Loc;

namespace SimpleDroneGCS.UI.Dialogs
{
    public partial class CompassCalibrationDialog : Window
    {
        private readonly MAVLinkService _mavlink;
        private readonly DispatcherTimer _timer;
        private bool _calibrating = false;
        private bool _completed = false;
        private int _elapsedSeconds = 0;

        private readonly bool[] _sectorsCovered = new bool[80];
        private readonly List<Ellipse> _sectorDots = new List<Ellipse>();
        private byte _lastCompletionPct = 0;

        public CompassCalibrationDialog(MAVLinkService mavlinkService)
        {
            InitializeComponent();
            _mavlink = mavlinkService;

            _timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
            _timer.Tick += Timer_Tick;

            _mavlink.OnStatusTextReceived += OnStatusText;
            _mavlink.OnMagCalProgress += OnMagCalProgress;
            _mavlink.OnMagCalReport += OnMagCalReport;

            DrawSectorGrid();
        }

        private void DrawSectorGrid()
        {
            double cx = 130, cy = 130, radius = 110;

            for (int i = 0; i < 80; i++)
            {
                int row = i / 8;
                int col = i % 8;

                double lat = 80.0 - row * 17.78;
                double lon = col * 45.0;

                double latRad = lat * Math.PI / 180.0;
                double lonRad = lon * Math.PI / 180.0;

                double r = radius * Math.Cos(latRad) * 0.85;
                double x = cx + r * Math.Sin(lonRad);
                double y = cy - radius * Math.Sin(latRad) * 0.85;

                var dot = new Ellipse
                {
                    Width = 10,
                    Height = 10,
                    Fill = new SolidColorBrush(Color.FromArgb(30, 42, 67, 97)),
                    Stroke = new SolidColorBrush(Color.FromArgb(40, 42, 67, 97)),
                    StrokeThickness = 0.5
                };

                Canvas.SetLeft(dot, x - 5);
                Canvas.SetTop(dot, y - 5);
                SphereCanvas.Children.Add(dot);
                _sectorDots.Add(dot);
            }
        }

        private void UpdateSectorVisual(int sectorIndex, bool covered)
        {
            if (sectorIndex < 0 || sectorIndex >= _sectorDots.Count) return;
            if (_sectorsCovered[sectorIndex]) return;

            _sectorsCovered[sectorIndex] = covered;
            var dot = _sectorDots[sectorIndex];

            if (covered)
            {
                dot.Fill = new SolidColorBrush(Color.FromArgb(200, 152, 240, 25));
                dot.Stroke = new SolidColorBrush(Color.FromArgb(100, 152, 240, 25));
                double oldLeft = Canvas.GetLeft(dot);
                double oldTop = Canvas.GetTop(dot);
                dot.Width = 12;
                dot.Height = 12;
                Canvas.SetLeft(dot, oldLeft - 1);
                Canvas.SetTop(dot, oldTop - 1);
            }
        }

        private void UpdateCompletionMask(byte[] mask)
        {
            if (mask == null || mask.Length < 10) return;

            for (int byteIdx = 0; byteIdx < 10 && byteIdx < mask.Length; byteIdx++)
            {
                for (int bit = 0; bit < 8; bit++)
                {
                    int sectorIdx = byteIdx * 8 + bit;
                    if (sectorIdx >= 80) break;

                    bool covered = (mask[byteIdx] & (1 << bit)) != 0;
                    if (covered)
                        UpdateSectorVisual(sectorIdx, true);
                }
            }
        }

        private void StartButton_Click(object sender, RoutedEventArgs e)
        {
            if (_calibrating) return;
            _calibrating = true;
            _completed = false;
            _elapsedSeconds = 0;
            _lastCompletionPct = 0;

            for (int i = 0; i < 80; i++) _sectorsCovered[i] = false;

            StartButton.IsEnabled = false;
            StartButton.Content = Get("CompassCalib_GoingBtn");
            AcceptButton.IsEnabled = false;
            AcceptButton.Opacity = 0.4;

            StatusText.Text = Get("CompassCalib_Sending");
            InstructionText.Text = Get("CompassCalib_Rotate");

            _mavlink.SendCommandLong(42424,
                param1: 0, param2: 0, param3: 0, param4: 0, param5: 0);

            _timer.Start();
            Debug.WriteLine("[CompassCalib] MAG_CAL started");
        }

        private void OnMagCalProgress(byte compassId, byte completionPct, byte[] completionMask,
            float directionX, float directionY, float directionZ)
        {
            Dispatcher.BeginInvoke(() =>
            {
                _lastCompletionPct = completionPct;
                CalibProgress.Value = completionPct;
                CenterPercent.Text = $"{completionPct}%";

                UpdateCompletionMask(completionMask);

                int covered = 0;
                foreach (bool s in _sectorsCovered) if (s) covered++;
                CoverageText.Text = Fmt("CompassCalib_SectionsCount", covered);
                CompassIdText.Text = $"MAG #{compassId + 1}";

                StatusText.Text = Fmt("CompassCalib_InProgress", completionPct);
                StatusText.Foreground = new SolidColorBrush((Color)ColorConverter.ConvertFromString("#FFAA00"));

                if (completionPct > 80)
                    CalibProgress.Foreground = new SolidColorBrush((Color)ColorConverter.ConvertFromString("#98F019"));
                else if (completionPct > 40)
                    CalibProgress.Foreground = new SolidColorBrush((Color)ColorConverter.ConvertFromString("#FFAA00"));
            });
        }

        private void OnMagCalReport(byte compassId, byte calStatus, float fitness,
            float ofsX, float ofsY, float ofsZ)
        {
            Dispatcher.BeginInvoke(() =>
            {
                _timer.Stop();
                _calibrating = false;
                _completed = true;

                bool success = calStatus == 4;

                CalibProgress.Value = 100;
                CenterPercent.Text = success ? "✓" : "✗";

                if (success)
                {
                    CalibProgress.Foreground = new SolidColorBrush((Color)ColorConverter.ConvertFromString("#98F019"));
                    StatusText.Text = Get("CompassCalib_SuccessStatus");
                    StatusText.Foreground = new SolidColorBrush((Color)ColorConverter.ConvertFromString("#98F019"));

                    FitnessText.Text = $"{fitness:F1}";
                    FitnessText.Foreground = new SolidColorBrush(
                        (Color)ColorConverter.ConvertFromString(fitness < 25 ? "#98F019" : fitness < 50 ? "#FFAA00" : "#FF4444"));

                    DroneStatusText.Text = $"Offsets: X={ofsX:F1}  Y={ofsY:F1}  Z={ofsZ:F1}  |  Fitness={fitness:F1}";
                    InstructionText.Text = Get("CompassCalib_SuccessInstruction");

                    AcceptButton.IsEnabled = true;
                    AcceptButton.Opacity = 1.0;
                }
                else
                {
                    CalibProgress.Foreground = new SolidColorBrush((Color)ColorConverter.ConvertFromString("#FF4444"));
                    StatusText.Text = Fmt("CompassCalib_ErrorStatus", calStatus);
                    StatusText.Foreground = new SolidColorBrush((Color)ColorConverter.ConvertFromString("#FF4444"));
                    InstructionText.Text = Get("CompassCalib_ErrorInstruction");

                    StartButton.IsEnabled = true;
                    StartButton.Content = Get("CompassCalib_RetryBtn");
                }

                Debug.WriteLine($"[CompassCalib] Report: status={calStatus}, fitness={fitness}");
            });
        }

        private void AcceptButton_Click(object sender, RoutedEventArgs e)
        {

            _mavlink.SendCommandLong(42425, param1: 0);

            StatusText.Text = Get("CompassCalib_Saved");
            AcceptButton.IsEnabled = false;
            AcceptButton.Content = Get("CompassCalib_SavedBtn");
            Debug.WriteLine("[CompassCalib] Calibration accepted");
        }

        private void OnStatusText(string text)
        {
            Dispatcher.BeginInvoke(() => { DroneStatusText.Text = text; });
        }

        private void Timer_Tick(object sender, EventArgs e)
        {
            _elapsedSeconds++;
            if (_elapsedSeconds >= 120 && !_completed)
            {
                _timer.Stop();
                StatusText.Text = Get("CompassCalib_Timeout");
                StatusText.Foreground = new SolidColorBrush((Color)ColorConverter.ConvertFromString("#FF4444"));

                _mavlink.SendCommandLong(42426);
                _calibrating = false;
                StartButton.IsEnabled = true;
                StartButton.Content = Get("CompassCalib_RetryBtn");
            }
        }

        private void CancelButton_Click(object sender, RoutedEventArgs e)
        {
            if (_calibrating)
            {
                _mavlink.SendCommandLong(42426);
                Debug.WriteLine("[CompassCalib] Cancelled");
            }
            Close();
        }

        protected override void OnClosed(EventArgs e)
        {
            _timer.Stop();
            _mavlink.OnStatusTextReceived -= OnStatusText;
            _mavlink.OnMagCalProgress -= OnMagCalProgress;
            _mavlink.OnMagCalReport -= OnMagCalReport;
            base.OnClosed(e);
        }
    }
}