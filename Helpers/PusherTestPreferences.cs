using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;

namespace SimpleDroneGCS.Helpers
{
    /// <summary>
    /// Лёгкая обёртка над JSON-файлом %AppData%\SQK_GCS\settings.json
    /// для пользовательских предпочтений UI диалогов.
    /// 
    /// Использует точечный доступ (key-value), не привязан к типизированному
    /// AppSettings — чтобы не конфликтовать с другими настройками приложения.
    /// При ошибках чтения/записи фоллбэк на дефолты, не бросает исключения.
    /// </summary>
    public static class PusherTestPreferences
    {
        private static readonly string SettingsPath = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "SQK_GCS",
            "settings.json");

        private const string KeyHideWarning = "HidePusherTestWarning";

        public static bool HideWarning
        {
            get => ReadBool(KeyHideWarning, false);
            set => WriteBool(KeyHideWarning, value);
        }

        private static bool ReadBool(string key, bool defaultValue)
        {
            try
            {
                if (!File.Exists(SettingsPath)) return defaultValue;

                var json = File.ReadAllText(SettingsPath);
                using var doc = JsonDocument.Parse(json);
                if (doc.RootElement.TryGetProperty(key, out var el))
                {
                    if (el.ValueKind == JsonValueKind.True) return true;
                    if (el.ValueKind == JsonValueKind.False) return false;
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[PusherTestPreferences] Ошибка чтения: {ex.Message}");
            }
            return defaultValue;
        }

        private static void WriteBool(string key, bool value)
        {
            try
            {
                var dir = Path.GetDirectoryName(SettingsPath);
                if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
                    Directory.CreateDirectory(dir);

                Dictionary<string, JsonElement> existing;
                if (File.Exists(SettingsPath))
                {
                    try
                    {
                        var json = File.ReadAllText(SettingsPath);
                        existing = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(json)
                                   ?? new Dictionary<string, JsonElement>();
                    }
                    catch
                    {
                        existing = new Dictionary<string, JsonElement>();
                    }
                }
                else
                {
                    existing = new Dictionary<string, JsonElement>();
                }

                var output = new Dictionary<string, object>();
                foreach (var kvp in existing)
                {
                    if (kvp.Key == key) continue;
                    output[kvp.Key] = JsonElementToObject(kvp.Value);
                }
                output[key] = value;

                var newJson = JsonSerializer.Serialize(output,
                    new JsonSerializerOptions { WriteIndented = true });
                File.WriteAllText(SettingsPath, newJson);
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[PusherTestPreferences] Ошибка записи: {ex.Message}");
            }
        }

        private static object JsonElementToObject(JsonElement el)
        {
            return el.ValueKind switch
            {
                JsonValueKind.True => true,
                JsonValueKind.False => false,
                JsonValueKind.Number => el.TryGetInt64(out var l) ? (object)l : el.GetDouble(),
                JsonValueKind.String => el.GetString(),
                JsonValueKind.Null => null,
                _ => el.GetRawText()
            };
        }
    }
}
