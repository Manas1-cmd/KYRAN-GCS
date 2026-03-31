using System;
using System.IO;
using System.Text.Json;
using System.Timers;
using SimpleDroneGCS.Models;

namespace SimpleDroneGCS.Services
{
    public class AppSettings
    {
        private static AppSettings _instance;
        public static AppSettings Instance => _instance ??= new AppSettings();

        private readonly string _settingsPath;
        private Settings _settings;

        public class Settings
        {
            public bool IsFirstRun { get; set; } = true;
            public VehicleType LastSelectedVehicleType { get; set; } = VehicleType.Copter;
            public bool ShowVehicleSelectionOnStartup { get; set; } = true;
            public string LastConnectionString { get; set; } = "COM3:57600";
            public bool DarkModeEnabled { get; set; } = true;
            public string Language { get; set; } = "ru-RU";
            public bool EnableVoiceCommands { get; set; } = false;
            public bool EnableLogging { get; set; } = true;
            public string LogDirectory { get; set; } = "Logs";
            public double MapDefaultZoom { get; set; } = 15;
            public double MapDefaultLat { get; set; } = 43.238949;
            public double MapDefaultLon { get; set; } = 76.889709;
            public bool ShowTelemetryHUD { get; set; } = true;
            public int TelemetryUpdateRate { get; set; } = 10;
            public bool AutoConnect { get; set; } = false;
            public bool ShowPreflightChecklist { get; set; } = true;
            public string PilotName { get; set; } = "";
            public string PilotLicense { get; set; } = "";
        }

        private AppSettings()
        {
            var appDataPath = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
            var sqkFolder = Path.Combine(appDataPath, "SQK_GCS");

            if (!Directory.Exists(sqkFolder))
            {
                Directory.CreateDirectory(sqkFolder);
            }

            _settingsPath = Path.Combine(sqkFolder, "settings.json");
            LoadSettings();
        }

        public bool IsFirstRun
        {
            get => _settings.IsFirstRun;
            set
            {
                _settings.IsFirstRun = value;
                ScheduleSave();
            }
        }

        private Timer _saveTimer;
        private readonly object _saveLock = new object();

        private void ScheduleSave()
        {
            lock (_saveLock) 
            {
                _saveTimer?.Stop();
                _saveTimer?.Dispose();
                _saveTimer = new Timer(500) { AutoReset = false };
                _saveTimer.Elapsed += (s, e) => SaveSettings();
                _saveTimer.Start();
            }
        }

        private void LoadSettings()
        {
            if (File.Exists(_settingsPath))
            {
                try
                {
                    var json = File.ReadAllText(_settingsPath);
                    _settings = JsonSerializer.Deserialize<Settings>(json) ?? new Settings();
                }
                catch
                {
                    _settings = new Settings();
                }
            }
            else
            {
                _settings = new Settings();
                SaveSettings();
            }
        }

        public void SaveSettings()
        {
            try
            {
                var options = new JsonSerializerOptions
                {
                    WriteIndented = true
                };
                var json = JsonSerializer.Serialize(_settings, options);
                File.WriteAllText(_settingsPath, json);
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"Failed to save settings: {ex.Message}");
            }
        }

        public VehicleType LastSelectedVehicleType
        {
            get => _settings.LastSelectedVehicleType;
            set
            {
                _settings.LastSelectedVehicleType = value;
                ScheduleSave();
            }
        }

        public bool ShowVehicleSelectionOnStartup
        {
            get => _settings.ShowVehicleSelectionOnStartup;
            set
            {
                _settings.ShowVehicleSelectionOnStartup = value;
                ScheduleSave();
            }
        }

        public string LastConnectionString
        {
            get => _settings.LastConnectionString;
            set
            {
                _settings.LastConnectionString = value;
                ScheduleSave();
            }
        }

        public bool DarkModeEnabled
        {
            get => _settings.DarkModeEnabled;
            set
            {
                _settings.DarkModeEnabled = value;
                ScheduleSave();
            }
        }

        public Settings GetSettings() => _settings;

        public void ResetToDefaults()
        {
            _settings = new Settings();
            SaveSettings();
        }
    }
}