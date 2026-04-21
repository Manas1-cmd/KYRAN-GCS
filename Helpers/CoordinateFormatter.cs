using System;
using System.Globalization;
using System.Text.RegularExpressions;

namespace SimpleDroneGCS.Helpers
{
    public enum CoordinateFormat
    {
        DD,
        DMS
    }

    public static class CoordinateFormatter
    {
        private static readonly CultureInfo Inv = CultureInfo.InvariantCulture;

        public static string Format(double degrees, bool isLatitude, CoordinateFormat format)
        {
            if (double.IsNaN(degrees) || double.IsInfinity(degrees))
                return "---";

            return format switch
            {
                CoordinateFormat.DMS => FormatDMS(degrees, isLatitude),
                _ => FormatDD(degrees)
            };
        }

        public static string FormatDD(double degrees)
        {
            return degrees.ToString("F6", Inv);
        }

        public static string FormatDMS(double degrees, bool isLatitude)
        {
            char hemi = isLatitude
                ? (degrees >= 0 ? 'N' : 'S')
                : (degrees >= 0 ? 'E' : 'W');

            double abs = Math.Abs(degrees);
            int deg = (int)abs;
            double minFull = (abs - deg) * 60.0;
            int min = (int)minFull;
            double sec = (minFull - min) * 60.0;

            if (sec >= 59.995)
            {
                sec = 0;
                min++;
                if (min >= 60) { min = 0; deg++; }
            }

            return string.Format(Inv, "{0}°{1:D2}'{2:00.00}\"{3}", deg, min, sec, hemi);
        }

        public static bool TryParse(string input, out double degrees)
        {
            degrees = 0;
            if (string.IsNullOrWhiteSpace(input)) return false;

            string s = input.Trim().Replace(',', '.');

            if (TryParseDMS(s, out degrees)) return true;

            return double.TryParse(s, NumberStyles.Float, Inv, out degrees);
        }

        private static readonly Regex DmsRegex = new(
            @"^\s*(?<sign>[-+])?\s*(?<deg>\d+(\.\d+)?)\s*[°d\s]\s*" +
            @"(?:(?<min>\d+(\.\d+)?)\s*['m\s]\s*)?" +
            @"(?:(?<sec>\d+(\.\d+)?)\s*[""s\s]?\s*)?" +
            @"(?<hemi>[NSEW])?\s*$",
            RegexOptions.IgnoreCase | RegexOptions.Compiled);

        public static bool TryParseDMS(string input, out double degrees)
        {
            degrees = 0;
            if (string.IsNullOrWhiteSpace(input)) return false;

            var m = DmsRegex.Match(input.Trim());
            if (!m.Success) return false;

            if (!double.TryParse(m.Groups["deg"].Value, NumberStyles.Float, Inv, out double d))
                return false;

            double mm = 0, ss = 0;
            if (m.Groups["min"].Success && !string.IsNullOrEmpty(m.Groups["min"].Value))
                double.TryParse(m.Groups["min"].Value, NumberStyles.Float, Inv, out mm);
            if (m.Groups["sec"].Success && !string.IsNullOrEmpty(m.Groups["sec"].Value))
                double.TryParse(m.Groups["sec"].Value, NumberStyles.Float, Inv, out ss);

            degrees = d + mm / 60.0 + ss / 3600.0;

            string hemi = m.Groups["hemi"].Value?.ToUpperInvariant() ?? "";
            if (hemi == "S" || hemi == "W")
                degrees = -degrees;

            if (m.Groups["sign"].Value == "-")
                degrees = -degrees;

            return true;
        }
    }
}