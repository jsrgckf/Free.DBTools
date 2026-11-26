using System;
using System.IO;
using System.Windows;
using Newtonsoft.Json;

namespace Free.DBTools
{
    public static class ThemeManager
    {
        public const string Light = "Light";
        public const string Dark = "Dark";

        private static readonly string SettingsFilePath = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "Free.DBTools",
            "settings.json");

        public static string Current { get; private set; } = Light;

        public static string LoadSaved()
        {
            try
            {
                if (!File.Exists(SettingsFilePath))
                {
                    return Light;
                }

                var settings = JsonConvert.DeserializeObject<UserSettings>(
                    File.ReadAllText(SettingsFilePath));
                return Normalize(settings == null ? null : settings.Theme);
            }
            catch
            {
                return Light;
            }
        }

        public static void Apply(string theme)
        {
            theme = Normalize(theme);
            var app = Application.Current;
            if (app == null)
            {
                Current = theme;
                return;
            }

            var merged = app.Resources.MergedDictionaries;
            for (var i = merged.Count - 1; i >= 0; i--)
            {
                var source = merged[i].Source;
                if (source != null &&
                    source.OriginalString.IndexOf("Themes/", StringComparison.OrdinalIgnoreCase) >= 0)
                {
                    merged.RemoveAt(i);
                }
            }

            merged.Add(new ResourceDictionary
            {
                Source = new Uri(
                    "/Free.DBTools;component/Themes/" + (theme == Dark ? "Dark" : "Light") + ".xaml",
                    UriKind.Relative)
            });
            Current = theme;
        }

        public static void Save(string theme)
        {
            try
            {
                var directory = Path.GetDirectoryName(SettingsFilePath);
                if (!string.IsNullOrEmpty(directory))
                {
                    Directory.CreateDirectory(directory);
                }

                File.WriteAllText(
                    SettingsFilePath,
                    JsonConvert.SerializeObject(
                        new UserSettings { Theme = Normalize(theme) },
                        Formatting.Indented));
            }
            catch
            {
            }
        }

        public static string Normalize(string theme)
        {
            return string.Equals(theme, Dark, StringComparison.OrdinalIgnoreCase) ? Dark : Light;
        }

        private sealed class UserSettings
        {
            public string Theme { get; set; }
        }
    }
}
