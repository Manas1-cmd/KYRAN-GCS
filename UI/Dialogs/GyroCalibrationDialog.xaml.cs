using System;
using System.Diagnostics;
using System.Windows;
using System.Windows.Media;
using System.Windows.Threading;
using SimpleDroneGCS.Services;

namespace SimpleDroneGCS.UI.Dialogs
{
    public partial class GyroCalibrationDialog : Window
    {
        private readonly MAVLinkService _mavlink;
        private readonly DispatcherTimer _timer;
        private int _elapsedSeconds = 0;
        private bool _calibrating = false;
        private bool _completed = false;

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
            StartButton.Content = "ИДЁТ...";
            InstructionText.Text = "Калибровка гироскопа...\nНе двигайте дрон!";
            WarningText.Foreground = new SolidColorBrush((Color)ColorConverter.ConvertFromString("#FF4444"));
            WarningText.Text = "⚠ НЕ ТРОГАЙТЕ ДРОН!";
            ProgressText.Text = "Калибровка...";

            // Используем существующий метод MAVLinkService
            _mavlink.SendPreflightCalibration(gyro: true);

            _timer.Start();
            Debug.WriteLine("[GyroCalib] Calibration started");
        }

        private void Timer_Tick(object sender, EventArgs e)
        {
            _elapsedSeconds++;
            double progress = Math.Min(100, _elapsedSeconds * 20);
            CalibProgress.Value = progress;
            ProgressText.Text = $"{_elapsedSeconds} сек...";

            if (_elapsedSeconds >= 15 && !_completed)
            {
                _timer.Stop();
                ShowResult(false, "Таймаут — нет ответа от дрона");
            }
        }

        private void OnStatusText(string text)
        {
            Dispatcher.BeginInvoke(() =>
            {
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
                    ProgressText.Text = "Идёт калибровка...";
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
                InstructionText.Text = "Калибровка завершена!";
                InstructionText.Foreground = new SolidColorBrush((Color)ColorConverter.ConvertFromString("#98F019"));
                WarningText.Text = "✓ Гироскоп откалиброван";
                WarningText.Foreground = new SolidColorBrush((Color)ColorConverter.ConvertFromString("#98F019"));
                ProgressText.Text = "УСПЕХ";
                ProgressText.Foreground = new SolidColorBrush((Color)ColorConverter.ConvertFromString("#98F019"));
            }
            else
            {
                CalibProgress.Foreground = new SolidColorBrush((Color)ColorConverter.ConvertFromString("#FF4444"));
                InstructionText.Text = "Калибровка не удалась";
                InstructionText.Foreground = new SolidColorBrush((Color)ColorConverter.ConvertFromString("#FF4444"));
                WarningText.Text = "✗ Попробуйте снова";
                WarningText.Foreground = new SolidColorBrush((Color)ColorConverter.ConvertFromString("#FF4444"));
                ProgressText.Text = "ОШИБКА";
                ProgressText.Foreground = new SolidColorBrush((Color)ColorConverter.ConvertFromString("#FF4444"));
            }

            StatusText.Text = message;
            StartButton.IsEnabled = true;
            StartButton.Content = success ? "ГОТОВО" : "ПОВТОРИТЬ";
        }

        private void CloseButton_Click(object sender, RoutedEventArgs e) => Close();

        protected override void OnClosed(EventArgs e)
        {
            _timer.Stop();
            _mavlink.OnStatusTextReceived -= OnStatusText;
            base.OnClosed(e);
        }
    }
}
