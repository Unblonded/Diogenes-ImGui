using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;

namespace Diogenes
{
    // Replaces Properties.Settings from the WinForms version. Stored as JSON in %APPDATA%\DiogenesImGui.
    public class AppSettings
    {
        const int RecentFileMaxCount = 12;

        public static string SettingsFolder => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "DiogenesImGui");
        static string SettingsPath => Path.Combine(SettingsFolder, "settings.json");

        public List<string> RecentCbfFiles { get; set; } = new List<string>();
        public int LastSelectedDevice { get; set; } = 0;

        public bool TranslateToEnglish { get; set; } = true;
        public bool UseOnlineTranslation { get; set; } = true;
        public bool ShowOriginalNames { get; set; } = false;
        public bool AdvancedMode { get; set; } = false;
        public bool AutoIdentifyVariant { get; set; } = true;
        public float FontSize { get; set; } = 18f;

        public static AppSettings Current { get; private set; } = Load();

        static AppSettings Load()
        {
            try
            {
                if (File.Exists(SettingsPath))
                {
                    return JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(SettingsPath)) ?? new AppSettings();
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Settings could not be read, using defaults: {ex.Message}");
            }
            return new AppSettings();
        }

        public void Save()
        {
            try
            {
                Directory.CreateDirectory(SettingsFolder);
                File.WriteAllText(SettingsPath, JsonSerializer.Serialize(this, new JsonSerializerOptions { WriteIndented = true }));
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Settings could not be saved: {ex.Message}");
            }
        }

        public void AddRecentFile(string path)
        {
            RecentCbfFiles.Remove(path);
            RecentCbfFiles.Insert(0, path);
            if (RecentCbfFiles.Count > RecentFileMaxCount)
            {
                RecentCbfFiles.RemoveRange(RecentFileMaxCount, RecentCbfFiles.Count - RecentFileMaxCount);
            }
            Save();
        }

        public void RemoveRecentFile(string path)
        {
            if (RecentCbfFiles.Remove(path))
            {
                Save();
            }
        }
    }
}
