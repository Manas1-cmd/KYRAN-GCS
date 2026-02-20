using System;
using System.Linq;
using System.Windows;
using SimpleDroneGCS.Properties;

namespace SimpleDroneGCS.Services
{
    public class LocalizationService
    {
        private static LocalizationService _instance;
        public static LocalizationService Instance => _instance ??= new LocalizationService();

        public event EventHandler LanguageChanged;

        private string _currentLanguage = "ru";

        public string CurrentLanguage
        {
            get => _currentLanguage;
            set
            {
                if (_currentLanguage != value)
                {
                    _currentLanguage = value;
                    ApplyLanguage(value); // ⭐ ЗАГРУЖАЕМ только при СМЕНЕ
                    LanguageChanged?.Invoke(this, EventArgs.Empty);
                }
            }
        }

        private LocalizationService()
        {
            // ⭐ ДОБАВЛЯЕМ TRY-CATCH
            try
            {
                _currentLanguage = Properties.Settings.Default.Language ?? "ru";
            }
            catch
            {
                _currentLanguage = "ru"; // По умолчанию русский
            }

            // ⭐ НЕ ЗАГРУЖАЕМ программно - уже загружен через App.xaml!
            // ApplyLanguage(_currentLanguage); // ← УБИРАЕМ ЭТО!

            System.Diagnostics.Debug.WriteLine($"[Localization] Init: {_currentLanguage}");
        }

        private void ApplyLanguage(string language)
        {
            try
            {
                var dict = new ResourceDictionary();

                switch (language)
                {
                    case "kk":
                        dict.Source = new Uri("/Resources/Lang.kk-KZ.xaml", UriKind.Relative);
                        break;
                    case "ru":
                    default:
                        dict.Source = new Uri("/Resources/Lang.ru-RU.xaml", UriKind.Relative);
                        break;
                }

                // Удаляем ВСЕ старые языковые словари (ищем "Lang" без точки — ловит и запятую и точку)
                var oldDicts = Application.Current.Resources.MergedDictionaries
                    .Where(d => d.Source?.OriginalString.Contains("Lang") == true
                             && d.Source.OriginalString.Contains("Resources"))
                    .ToList();

                foreach (var old in oldDicts)
                    Application.Current.Resources.MergedDictionaries.Remove(old);

                Application.Current.Resources.MergedDictionaries.Add(dict);

                Properties.Settings.Default.Language = language;
                Properties.Settings.Default.Save();

                System.Diagnostics.Debug.WriteLine($"[Localization] Язык изменён: {language}");
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[Localization] ОШИБКА: {ex.Message}");
            }
        }

        public void ToggleLanguage()
        {
            CurrentLanguage = (CurrentLanguage == "ru") ? "kk" : "ru";
        }
    }
}