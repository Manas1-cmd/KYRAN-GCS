using SimpleDroneGCS.Views;
using SimpleDroneGCS.Services;
using System;
using System.Windows;
using System.Diagnostics; 

namespace SimpleDroneGCS
{
    public partial class App : Application
    {
        protected override void OnStartup(StartupEventArgs e)
        {
            base.OnStartup(e);

            ShutdownMode = ShutdownMode.OnExplicitShutdown;

            Debug.WriteLine("[APP] Starting application...");

            var splash = new Views.SplashScreen();
            splash.Show();

            var timer = new System.Windows.Threading.DispatcherTimer
            {
                Interval = TimeSpan.FromSeconds(3)
            };

            timer.Tick += (s, args) =>
            {
                timer.Stop();
                Debug.WriteLine("[APP] Splash timer finished, showing vehicle selection...");

                splash.Close();

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

                        Application.Current.MainWindow = mainWindow;

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

                    Application.Current.MainWindow = mainWindow;

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
                Shutdown();
            }
        }
    }
}