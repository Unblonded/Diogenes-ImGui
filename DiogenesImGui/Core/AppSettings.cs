using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;

namespace Diogenes
{
    // Replaces Properties.Settings from the WinForms version. Stored as JSON in %APPDATA%\DiogenesImGui,
    // or ~/Library/Application Support/DiogenesImGui on a Mac.
    public class AppSettings
    {
        const int RecentFileMaxCount = 12;

        public static string SettingsFolder => OperatingSystem.IsMacOS()
            ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Library", "Application Support", "DiogenesImGui")
            : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "DiogenesImGui");
        static string SettingsPath => Path.Combine(SettingsFolder, "settings.json");

        public List<string> RecentCbfFiles { get; set; } = new List<string>();
        public int LastSelectedDevice { get; set; } = 0;
        // J2534 driver libraries added by hand (macOS and Linux have no registry that lists them)
        public List<string> J2534DriverFiles { get; set; } = new List<string>();

        public bool TranslateToEnglish { get; set; } = true;
        public bool UseOnlineTranslation { get; set; } = true;
        public bool ShowOriginalNames { get; set; } = false;
        public bool AdvancedMode { get; set; } = false;
        public bool AutoIdentifyVariant { get; set; } = true;
        public float FontSize { get; set; } = 18f;

        // Vitals tab
        public bool UsUnits { get; set; } = false;
        public float VitalsInterval { get; set; } = 1f;
        // vital id -> chosen value key ("ECU|command|value|bit"), "" hides the vital
        public Dictionary<string, string> VitalSources { get; set; } = new Dictionary<string, string>();
        // values the user added to the dashboard ("ECU|command|value|bit")
        public List<string> PinnedValues { get; set; } = new List<string>();
        // CBFs loaded next to the main one for the Vitals tab
        public List<string> ExtraCbfFiles { get; set; } = new List<string>();

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
