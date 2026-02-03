using SimpleDroneGCS.Views;
using SimpleDroneGCS.Services;
using System;
using System.Windows;
using System.Diagnostics; // ДОБАВИТЬ

namespace SimpleDroneGCS
{
    public partial class App : Application
    {
        protected override void OnStartup(StartupEventArgs e)
        {
            base.OnStartup(e);

            // ⭐ КРИТИЧНО! Говорим WPF не закрываться пока мы не скажем!
            ShutdownMode = ShutdownMode.OnExplicitShutdown;

            Debug.WriteLine("[APP] Starting application...");

            // Показываем Splash Screen
            var splash = new Views.SplashScreen();
            splash.Show();

            // Запускаем через 4.5 секунды
            var timer = new System.Windows.Threading.DispatcherTimer
            {
                Interval = TimeSpan.FromSeconds(3)
            };

            timer.Tick += (s, args) =>
            {
                timer.Stop();
                Debug.WriteLine("[APP] Splash timer finished, showing vehicle selection...");

                // ЗАКРЫВАЕМ SPLASH!
                splash.Close();

                // После splash screen показываем выбор типа дрона
                ShowVehicleSelectionOrMainWindow();
            };

            timer.Start();
        }

        private void ShowVehicleSelectionOrMainWindow()
        {
            try
            {
                var settings = AppSettings.Instance;
                Debug.WriteLine($"[APP] IsFirstRun: {settings.IsFirstRun}");

                if (settings.ShowVehicleSelectionOnStartup || settings.IsFirstRun)
                {
                    Debug.WriteLine("[APP] Showing vehicle selection window...");
                    var vehicleSelection = new VehicleSelectionWindow();
                    bool? result = vehicleSelection.ShowDialog();

                    Debug.WriteLine($"[APP] Vehicle selection result: {result}, SelectionMade: {vehicleSelection.SelectionMade}");

                    if (result == true && vehicleSelection.SelectionMade)
                    {
                        Debug.WriteLine($"[APP] Vehicle selected: {vehicleSelection.SelectedVehicle}");
                        settings.LastSelectedVehicleType = vehicleSelection.SelectedVehicle;
                        settings.IsFirstRun = false;
                        VehicleManager.Instance.SetVehicleType(vehicleSelection.SelectedVehicle);

                        Debug.WriteLine("[APP] Creating MainWindow...");
                        var mainWindow = new MainWindow();

                        // ⭐ Устанавливаем главное окно
                        Application.Current.MainWindow = mainWindow;

                        // ⭐ ВОЗВРАЩАЕМ нормальный режим - закрытие при закрытии главного окна
                        ShutdownMode = ShutdownMode.OnMainWindowClose;

                        mainWindow.Show();
                        Debug.WriteLine("[APP] MainWindow shown!");
                    }
                    else
                    {
                        Debug.WriteLine("[APP] Vehicle selection cancelled, shutting down...");
                        Shutdown();
                    }
                }
                else
                {
                    Debug.WriteLine("[APP] Using saved vehicle type...");
                    VehicleManager.Instance.SetVehicleType(settings.LastSelectedVehicleType);

                    var mainWindow = new MainWindow();

                    // ⭐ Устанавливаем главное окно
                    Application.Current.MainWindow = mainWindow;

                    // ⭐ ВОЗВРАЩАЕМ нормальный режим - закрытие при закрытии главного окна
                    ShutdownMode = ShutdownMode.OnMainWindowClose;

                    mainWindow.Show();
                    Debug.WriteLine("[APP] MainWindow shown with saved settings!");
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[APP] ERROR: {ex.Message}");
                Debug.WriteLine($"[APP] StackTrace: {ex.StackTrace}");
                MessageBox.Show($"Ошибка запуска: {ex.Message}", "Ошибка", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }
    }
}