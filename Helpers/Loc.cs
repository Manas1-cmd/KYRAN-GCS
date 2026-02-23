using System.Windows;

namespace SimpleDroneGCS.Helpers
{
    public static class Loc
    {
        
        public static string Get(string key)
        {
            return Application.Current.TryFindResource(key) as string ?? key;
        }

        public static string Fmt(string key, params object[] args)
        {
            var template = Application.Current.TryFindResource(key) as string ?? key;
            try { return string.Format(template, args); }
            catch { return template; }
        }
    }
}
