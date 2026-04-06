using System;
using System.Collections.Generic;

namespace SimpleDroneGCS.Services
{
    public sealed class StatusTextTranslator : IStatusTextTranslator
    {
        private static readonly Lazy<StatusTextTranslator> _lazy =
            new(() => new StatusTextTranslator(), isThreadSafe: true);

        public static StatusTextTranslator Instance => _lazy.Value;

        private readonly LocalizationService _localization;

        private StatusTextTranslator()
        {
            _localization = LocalizationService.Instance;
        }

        public string Translate(string text)
        {
            if (string.IsNullOrWhiteSpace(text)) return text;

            string lang = _localization.CurrentLanguage;

            return TryPattern(text, lang, out string patternResult)
                ? patternResult
                : TryExact(text, lang, out string exactResult)
                    ? exactResult
                    : text;
        }

        private static bool TryPattern(string text, string lang, out string result)
        {
            var patterns = lang == "kk" ? KazakhPatterns : RussianPatterns;

            foreach (var (prefix, translation) in patterns)
            {
                if (text.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                {
                    string rest = text.Substring(prefix.Length).Trim(' ', ':');
                    result = string.IsNullOrEmpty(rest)
                        ? translation
                        : $"{translation}: {rest}";
                    return true;
                }
            }

            result = null;
            return false;
        }

        private static bool TryExact(string text, string lang, out string result)
        {
            var dict = lang == "kk" ? KazakhExact : RussianExact;
            return dict.TryGetValue(text.Trim(), out result);
        }


        private static readonly (string Prefix, string Translation)[] RussianPatterns =
        {
            ("PreArm: Waiting for RC",          "Предполётная проверка: ожидание RC сигнала"),
            ("PreArm: Baro not healthy",         "Предполётная проверка: барометр неисправен"),
            ("PreArm: Compass not calibrated",   "Предполётная проверка: компас не откалиброван"),
            ("PreArm: Compass offsets too high", "Предполётная проверка: отклонение компаса велико"),
            ("PreArm: GPS not healthy",          "Предполётная проверка: GPS неисправен"),
            ("PreArm: Need 3D Fix",              "Предполётная проверка: требуется 3D фикс GPS"),
            ("PreArm: Battery failsafe",         "Предполётная проверка: сработал аварийный режим батареи"),
            ("PreArm: RC not calibrated",        "Предполётная проверка: RC не откалиброван"),
            ("PreArm: Throttle too high",        "Предполётная проверка: газ слишком высокий"),
            ("PreArm: Gyros not healthy",        "Предполётная проверка: гироскоп неисправен"),
            ("PreArm: Gyros inconsistent",       "Предполётная проверка: гироскопы рассогласованы"),
            ("PreArm: Accelerometers not healthy","Предполётная проверка: акселерометр неисправен"),
            ("PreArm: Accelerometers inconsistent","Предполётная проверка: акселерометры рассогласованы"),
            ("PreArm: INS not calibrated",       "Предполётная проверка: INS не откалиброван"),
            ("PreArm: Mag field error",          "Предполётная проверка: ошибка магнитного поля"),
            ("PreArm:",                          "Предполётная проверка"),

            ("Arm: ",                            "Арминг"),
            ("Arming: ",                         "Арминг"),

            ("EKF3 IMU",                         "EKF3 ИМБ"),
            ("EKF3 waiting for GPS config",      "EKF3: ожидание конфигурации GPS"),
            ("EKF3 ",                            "EKF3"),

            ("GCS Failsafe On",                  "Аварийный режим GCS: активирован"),
            ("GCS Failsafe Off",                 "Аварийный режим GCS: отключён"),
            ("Battery Failsafe",                 "Аварийный режим: батарея"),
            ("Radio Failsafe",                   "Аварийный режим: радио"),
            ("GPS Failsafe",                     "Аварийный режим: GPS"),
            ("Failsafe ",                        "Аварийный режим"),

            ("Initialising ArduPilot",           "Инициализация системы"),
            ("Place vehicle level",              "Установите дрон горизонтально"),
            ("Compass calibration",              "Калибровка компаса"),
            ("Gyro calibration",                 "Калибровка гироскопа"),
            ("Calibration successful",           "Калибровка успешна"),
            ("Calibration failed",               "Калибровка не удалась"),

            ("Mission Complete",                 "Миссия завершена"),
            ("Reached waypoint",                 "Достигнута точка маршрута"),
            ("Passed waypoint",                  "Пройдена точка маршрута"),
            ("Next WP ",                         "Следующая точка"),
            ("WP ",                              "Точка"),
            ("Takeoff complete",                 "Взлёт завершён"),
            ("Mission: Takeoff",                 "Миссия: взлёт"),
            ("Mission paused",                   "Миссия приостановлена"),
            ("Mission resumed",                  "Миссия продолжена"),
        };

        private static readonly (string Prefix, string Translation)[] KazakhPatterns =
        {
            ("PreArm: Waiting for RC",           "Ұшу алды тексеру: RC сигналын күту"),
            ("PreArm: Baro not healthy",         "Ұшу алды тексеру: барометр ақаулы"),
            ("PreArm: Compass not calibrated",   "Ұшу алды тексеру: компас калибрленбеген"),
            ("PreArm: Compass offsets too high", "Ұшу алды тексеру: компас ауытқуы жоғары"),
            ("PreArm: GPS not healthy",          "Ұшу алды тексеру: GPS ақаулы"),
            ("PreArm: Need 3D Fix",              "Ұшу алды тексеру: GPS 3D фиксі қажет"),
            ("PreArm: Battery failsafe",         "Ұшу алды тексеру: батарея авариялық режимі"),
            ("PreArm: RC not calibrated",        "Ұшу алды тексеру: RC калибрленбеген"),
            ("PreArm: Throttle too high",        "Ұшу алды тексеру: газ тым жоғары"),
            ("PreArm: Gyros not healthy",        "Ұшу алды тексеру: гироскоп ақаулы"),
            ("PreArm: Gyros inconsistent",       "Ұшу алды тексеру: гироскоптар сәйкессіз"),
            ("PreArm: Accelerometers not healthy","Ұшу алды тексеру: акселерометр ақаулы"),
            ("PreArm: Accelerometers inconsistent","Ұшу алды тексеру: акселерометрлер сәйкессіз"),
            ("PreArm: INS not calibrated",       "Ұшу алды тексеру: INS калибрленбеген"),
            ("PreArm: Mag field error",          "Ұшу алды тексеру: магнит өрісі қатесі"),
            ("PreArm:",                          "Ұшу алды тексеру"),

            ("Arm: ",                            "Арминг"),
            ("Arming: ",                         "Арминг"),

            ("EKF3 IMU",                         "EKF3 ИӨБ"),
            ("EKF3 waiting for GPS config",      "EKF3: GPS конфигурациясын күту"),
            ("EKF3 ",                            "EKF3"),

            ("GCS Failsafe On",                  "GCS авариялық режимі: қосылды"),
            ("GCS Failsafe Off",                 "GCS авариялық режимі: өшірілді"),
            ("Battery Failsafe",                 "Авариялық режим: батарея"),
            ("Radio Failsafe",                   "Авариялық режим: радио"),
            ("GPS Failsafe",                     "Авариялық режим: GPS"),
            ("Failsafe ",                        "Авариялық режим"),

            ("Initialising ArduPilot",           "Жүйені іске қосу"),
            ("Place vehicle level",              "Дронды горизонталь орнатыңыз"),
            ("Compass calibration",              "Компасты калибрлеу"),
            ("Gyro calibration",                 "Гироскопты калибрлеу"),
            ("Calibration successful",           "Калибрлеу сәтті"),
            ("Calibration failed",               "Калибрлеу сәтсіз"),

            ("Mission Complete",                 "Миссия аяқталды"),
            ("Reached waypoint",                 "Маршрут нүктесіне жетілді"),
            ("Passed waypoint",                  "Маршрут нүктесі өтілді"),
            ("Next WP ",                         "Келесі нүкте"),
            ("WP ",                              "Нүкте"),
            ("Takeoff complete",                 "Ұшып шығу аяқталды"),
            ("Mission: Takeoff",                 "Миссия: ұшып шығу"),
            ("Mission paused",                   "Миссия тоқтатылды"),
            ("Mission resumed",                  "Миссия жалғасты"),
        };


        private static readonly Dictionary<string, string> RussianExact =
            new(StringComparer.OrdinalIgnoreCase)
            {
                ["Armed"] = "Заармлен",
                ["Disarmed"] = "Разармлен",
                ["Land complete"] = "Посадка завершена",
                ["Takeoff"] = "Взлёт",
                ["Mission Complete"] = "Миссия завершена",
                ["Low Battery"] = "Низкий заряд батареи",
                ["Critical Battery"] = "Критический заряд батареи",
                ["No dataflash inserted"] = "Карта памяти не вставлена",
                ["EKF3 IMU0 is using GPS"] = "EKF3: навигация по GPS активна",
                ["EKF3 IMU1 is using GPS"] = "EKF3: навигация по GPS активна",
                ["EKF3 IMU0 origin set to GPS"] = "EKF3: исходная точка установлена по GPS",
                ["EKF3 IMU1 origin set to GPS"] = "EKF3: исходная точка установлена по GPS",
                ["EKF3 waiting for GPS config data"] = "EKF3: ожидание данных GPS",
                ["GPS: u-blox 1 saving config"] = "GPS: сохранение конфигурации",
                ["VTOL: transition to fixed-wing"] = "Переход в режим самолёта",
                ["VTOL: transition to multicopter"] = "Переход в режим коптера",
                ["Throttle armed"] = "Газ активирован",
            };

        private static readonly Dictionary<string, string> KazakhExact =
            new(StringComparer.OrdinalIgnoreCase)
            {
                ["Armed"] = "Белсендірілді",
                ["Disarmed"] = "Сөндірілді",
                ["Land complete"] = "Қону аяқталды",
                ["Takeoff"] = "Ұшу",
                ["Mission Complete"] = "Миссия аяқталды",
                ["Low Battery"] = "Батарея заряды төмен",
                ["Critical Battery"] = "Батарея заряды сыни деңгейде",
                ["No dataflash inserted"] = "Жад картасы салынбаған",
                ["EKF3 IMU0 is using GPS"] = "EKF3: GPS навигациясы белсенді",
                ["EKF3 IMU1 is using GPS"] = "EKF3: GPS навигациясы белсенді",
                ["EKF3 IMU0 origin set to GPS"] = "EKF3: бастапқы нүкте GPS бойынша орнатылды",
                ["EKF3 IMU1 origin set to GPS"] = "EKF3: бастапқы нүкте GPS бойынша орнатылды",
                ["EKF3 waiting for GPS config data"] = "EKF3: GPS деректерін күту",
                ["GPS: u-blox 1 saving config"] = "GPS: конфигурацияны сақтау",
                ["VTOL: transition to fixed-wing"] = "Ұшақ режиміне өту",
                ["VTOL: transition to multicopter"] = "Коптер режиміне өту",
                ["Throttle armed"] = "Газ белсендірілді",
            };
    }
}