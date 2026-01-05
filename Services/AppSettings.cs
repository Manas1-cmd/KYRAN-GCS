using System;
using System.IO;
using System.Text.Json;
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
            public bool IsFirstRun { get; set; } = true; // НОВОЕ
            public VehicleType LastSelectedVehicleType { get; set; } = VehicleType.Copter;
            public bool ShowVehicleSelectionOnStartup { get; set; } = true;
            public string LastConnectionString { get; set; } = "COM3:57600";
            public bool DarkModeEnabled { get; set; } = true;
            public string Language { get; set; } = "en-US";
            public bool EnableVoiceCommands { get; set; } = false;
            public bool EnableLogging { get; set; } = true;
            public string LogDirectory { get; set; } = "Logs";
            public double MapDefaultZoom { get; set; } = 15;
            public double MapDefaultLat { get; set; } = 43.238949;  // Almaty
            public double MapDefaultLon { get; set; } = 76.889709;
            public bool ShowTelemetryHUD { get; set; } = true;
            public int TelemetryUpdateRate { get; set; } = 10; // Hz
            public bool AutoConnect { get; set; } = false;
            public bool ShowPreflightChecklist { get; set; } = true;
            public string PilotName { get; set; } = "";
            public string PilotLicense { get; set; } = "";
        }
        
        private AppSettings()
        {
            var appDataPath = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
            var kyranFolder = Path.Combine(appDataPath, "KYRAN_GCS");
            
            if (!Directory.Exists(kyranFolder))
            {
                Directory.CreateDirectory(kyranFolder);
            }
            
            _settingsPath = Path.Combine(kyranFolder, "settings.json");
            LoadSettings();
        }


        // ДОБАВЬ ЭТО СВОЙСТВО-ОБЕРТКУ:
        public bool IsFirstRun
        {
            get => _settings.IsFirstRun;
            set
            {
                _settings.IsFirstRun = value;
                SaveSettings();
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
        
        // Properties for easy access
        public VehicleType LastSelectedVehicleType
        {
            get => _settings.LastSelectedVehicleType;
            set
            {
                _settings.LastSelectedVehicleType = value;
                SaveSettings();
            }
        }
        
        public bool ShowVehicleSelectionOnStartup
        {
            get => _settings.ShowVehicleSelectionOnStartup;
            set
            {
                _settings.ShowVehicleSelectionOnStartup = value;
                SaveSettings();
            }
        }
        
        public string LastConnectionString
        {
            get => _settings.LastConnectionString;
            set
            {
                _settings.LastConnectionString = value;
                SaveSettings();
            }
        }
        
        public bool DarkModeEnabled
        {
            get => _settings.DarkModeEnabled;
            set
            {
                _settings.DarkModeEnabled = value;
                SaveSettings();
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
