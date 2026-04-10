using System;
using System.Diagnostics;
using System.Windows;
using System.Windows.Media;
using System.Windows.Threading;
using SimpleDroneGCS.Services;
using static SimpleDroneGCS.Helpers.Loc;

namespace SimpleDroneGCS.UI.Dialogs
{
    public partial class GyroCalibrationDialog : Window
    {
        private readonly MAVLinkService _mavlink;
        private readonly DispatcherTimer _timer;
        private int _elapsedSeconds = 0;
        private bool _calibrating = false;
        private bool _completed = false;
        private bool _isClosed = false;

        public GyroCalibrationDialog(MAVLinkService mavlinkService)
        {
            InitializeComponent();
            _mavlink = mavlinkService;

            _timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
            _timer.Tick += Timer_Tick;

            _mavlink.OnStatusTextReceived += OnStatusText;
        }

        private void StartButton_Click(object sender, RoutedEventArgs e)
        {
            if (_calibrating) return;

            _calibrating = true;
            _completed = false;
            _elapsedSeconds = 0;

            StartButton.IsEnabled = false;
            StartButton.Content = Get("GyroCalib_GoingBtn");
            InstructionText.Text = Get("GyroCalib_InProgress");
            InstructionText.Foreground = new SolidColorBrush((Color)ColorConverter.ConvertFromString("#CCDDDD"));
            WarningText.Foreground = new SolidColorBrush((Color)ColorConverter.ConvertFromString("#FF4444"));
            WarningText.Text = Get("GyroCalib_DontTouch");
            ProgressText.Text = Get("GyroCalib_Calibrating");
            ProgressText.Foreground = new SolidColorBrush((Color)ColorConverter.ConvertFromString("#6698F019"));
            CalibProgress.Value = 0;
            CalibProgress.Foreground = new SolidColorBrush((Color)ColorConverter.ConvertFromString("#98F019"));

            _mavlink.SendPreflightCalibration(gyro: true);

            _timer.Start();
            Debug.WriteLine("[GyroCalib] Calibration started");
        }

        private void Timer_Tick(object sender, EventArgs e)
        {
            if (_isClosed) { _timer.Stop(); return; }
            _elapsedSeconds++;
            double progress = Math.Min(90, _elapsedSeconds * 8);
            CalibProgress.Value = progress;
            ProgressText.Text = Fmt("GyroCalib_Seconds", _elapsedSeconds);

            if (_elapsedSeconds >= 15 && !_completed)
            {
                _timer.Stop();
                ShowResult(false, Get("GyroCalib_Timeout"));
            }
        }

        private void OnStatusText(string text)
        {
            Dispatcher.BeginInvoke(() =>
            {
                if (_isClosed) return;

                StatusText.Text = text;
                Debug.WriteLine($"[GyroCalib] StatusText: {text}");

                string lower = text.ToLower();

                if (lower.Contains("gyro") && (lower.Contains("success") || lower.Contains("complete") || lower.Contains("done")))
                {
                    ShowResult(true, text);
                }
                else if (lower.Contains("gyro") && (lower.Contains("fail") || lower.Contains("error")))
                {
                    ShowResult(false, text);
                }
                else if (lower.Contains("calibrat"))
                {
                    ProgressText.Text = Get("GyroCalib_CalibInProgress");
                }
            });
        }

        private void ShowResult(bool success, string message)
        {
            _timer.Stop();
            _calibrating = false;
            _completed = true;
            CalibProgress.Value = 100;

            if (success)
            {
                CalibProgress.Foreground = new SolidColorBrush((Color)ColorConverter.ConvertFromString("#98F019"));
                InstructionText.Text = Get("GyroCalib_SuccessInstruction");
                InstructionText.Foreground = new SolidColorBrush((Color)ColorConverter.ConvertFromString("#98F019"));
                WarningText.Text = Get("GyroCalib_SuccessWarning");
                WarningText.Foreground = new SolidColorBrush((Color)ColorConverter.ConvertFromString("#98F019"));
                ProgressText.Text = Get("GyroCalib_SuccessStatus");
                ProgressText.Foreground = new SolidColorBrush((Color)ColorConverter.ConvertFromString("#98F019"));
            }
            else
            {
                CalibProgress.Foreground = new SolidColorBrush((Color)ColorConverter.ConvertFromString("#FF4444"));
                InstructionText.Text = Get("GyroCalib_ErrorInstruction");
                InstructionText.Foreground = new SolidColorBrush((Color)ColorConverter.ConvertFromString("#FF4444"));
                WarningText.Text = Get("GyroCalib_ErrorWarning");
                WarningText.Foreground = new SolidColorBrush((Color)ColorConverter.ConvertFromString("#FF4444"));
                ProgressText.Text = Get("GyroCalib_ErrorStatus");
                ProgressText.Foreground = new SolidColorBrush((Color)ColorConverter.ConvertFromString("#FF4444"));
            }

            StatusText.Text = message;
            StartButton.IsEnabled = true;
            StartButton.Content = success ? Get("GyroCalib_DoneBtn") : Get("GyroCalib_RetryBtn");
        }

        private void CloseButton_Click(object sender, RoutedEventArgs e)
        {
            if (_calibrating)
                _mavlink.SendPreflightCalibration();
            Close();
        }

        protected override void OnClosed(EventArgs e)
        {
            _isClosed = true;
            _timer.Stop();
            _mavlink.OnStatusTextReceived -= OnStatusText;
            base.OnClosed(e);
        }
    }
}