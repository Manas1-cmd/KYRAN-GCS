using System.Windows;

namespace SimpleDroneGCS.Helpers
{
    public static class Loc
    {
        /// <summary>
        /// Get localized string by key from DynamicResource
        /// </summary>
        public static string Get(string key)
        {
            return Application.Current.TryFindResource(key) as string ?? key;
        }

        /// <summary>
        /// Format localized string with arguments
        /// Example: Loc.Fmt("MissionSaved", count, filename)
        /// </summary>
        public static string Fmt(string key, params object[] args)
        {
            var template = Application.Current.TryFindResource(key) as string ?? key;
            try { return string.Format(template, args); }
            catch { return template; }
        }
    }
}