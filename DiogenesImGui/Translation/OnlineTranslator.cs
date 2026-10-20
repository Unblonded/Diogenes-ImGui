using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Threading;

namespace Diogenes.Translation
{
    // Sends German strings to Google Translate in the background and keeps the results in a cache file,
    // so each string is only ever translated once. Callers never wait: they get null until a result is ready.
    // Translations shipped with the program (Translation/translations-de-en.json) are loaded first, so a
    // new install shows them straight away, even offline.
    public static class OnlineTranslator
    {
        const int MaxBatchChars = 1200;
        const int MaxBatchCount = 40;
        const int DelayBetweenRequestsMs = 400;
        const int MaxConsecutiveFailures = 3;

        static string CachePath => Path.Combine(AppSettings.SettingsFolder, "translations-de-en.json");

        const string BuiltInResource = "translations-de-en.json";

        static readonly ConcurrentDictionary<string, string> Cache = new ConcurrentDictionary<string, string>();
        static readonly Dictionary<string, string> BuiltIn = new Dictionary<string, string>();
        static readonly ConcurrentDictionary<string, byte> Queued = new ConcurrentDictionary<string, byte>();
        static readonly BlockingCollection<string> Pending = new BlockingCollection<string>();
        static readonly HttpClient Http = new HttpClient { Timeout = TimeSpan.FromSeconds(20) };

        static Thread Worker;
        static int UnsavedCount = 0;
        static int ConsecutiveFailures = 0;

        // bumped whenever new translations arrive, so the UI can refresh cached labels
        public static int Version { get; private set; }
        public static bool Unavailable { get; private set; }
        public static int PendingCount => Pending.Count;
        public static int CachedCount => Cache.Count;

        public static void Start()
        {
            LoadCache();
            Worker = new Thread(WorkerLoop) { IsBackground = true, Name = "Translation" };
            Worker.Start();
        }

        public static string TryGet(string german)
        {
            if (string.IsNullOrWhiteSpace(german))
            {
                return german;
            }
            if (Cache.TryGetValue(german, out string english))
            {
                return english;
            }
            if (!Unavailable && Queued.TryAdd(german, 0))
            {
                Pending.Add(german);
            }
            return null;
        }

        public static void Flush()
        {
            SaveCache();
        }

        static void WorkerLoop()
        {
            while (true)
            {
                string first = Pending.Take();
                var batch = new List<string> { first };
                int chars = first.Length;
                while (batch.Count < MaxBatchCount && Pending.TryTake(out string next, 50))
                {
                    batch.Add(next);
                    chars += next.Length + 1;
                    if (chars > MaxBatchChars)
                    {
                        break;
                    }
                }

                if (Unavailable)
                {
                    continue;
                }

                try
                {
                    TranslateBatch(batch);
                    ConsecutiveFailures = 0;
                }
                catch (Exception ex)
                {
                    ConsecutiveFailures++;
                    // let the strings be queued again later
                    foreach (string s in batch)
                    {
                        Queued.TryRemove(s, out _);
                    }
                    if (ConsecutiveFailures >= MaxConsecutiveFailures)
                    {
                        Unavailable = true;
                        Console.WriteLine($"Online translation is unavailable ({ex.Message}). Using the built-in dictionary instead.");
                    }
                    else
                    {
                        Thread.Sleep(5000 * ConsecutiveFailures);
                    }
                }

                if (UnsavedCount > 200)
                {
                    SaveCache();
                }
                Thread.Sleep(DelayBetweenRequestsMs);
            }
        }

        static void TranslateBatch(List<string> batch)
        {
            // newlines survive translation as segment boundaries, so a batch can be sent as one request
            // and split again afterwards. Strings that contain newlines themselves are sent alone.
            var multiline = batch.Where(s => s.Contains('\n')).ToList();
            var singleLine = batch.Where(s => !s.Contains('\n')).ToList();

            if (singleLine.Count > 0)
            {
                List<string> results = Request(string.Join("\n", singleLine)).Split('\n').Select(x => x.Trim()).ToList();
                if (results.Count == singleLine.Count)
                {
                    for (int i = 0; i < singleLine.Count; i++)
                    {
                        Store(singleLine[i], results[i]);
                    }
                }
                else
                {
                    foreach (string s in singleLine)
                    {
                        Store(s, Request(s).Trim());
                        Thread.Sleep(DelayBetweenRequestsMs);
                    }
                }
            }
            foreach (string s in multiline)
            {
                Store(s, Request(s).Trim());
            }
        }

        static void Store(string german, string english)
        {
            if (string.IsNullOrWhiteSpace(english))
            {
                return;
            }
            Cache[german] = english;
            Queued.TryRemove(german, out _);
            UnsavedCount++;
            Version++;
        }

        static string Request(string text)
        {
            var content = new FormUrlEncodedContent(new[] { new KeyValuePair<string, string>("q", text) });
            var response = Http.PostAsync("https://translate.googleapis.com/translate_a/single?client=gtx&sl=de&tl=en&dt=t", content).GetAwaiter().GetResult();
            response.EnsureSuccessStatusCode();
            string json = response.Content.ReadAsStringAsync().GetAwaiter().GetResult();

            // shape: [[["translated","original",...],["translated","original",...]], ...]
            using var doc = JsonDocument.Parse(json);
            var sb = new StringBuilder();
            foreach (var segment in doc.RootElement[0].EnumerateArray())
            {
                if (segment.ValueKind == JsonValueKind.Array && segment.GetArrayLength() > 0 && segment[0].ValueKind == JsonValueKind.String)
                {
                    sb.Append(segment[0].GetString());
                }
            }
            return sb.ToString();
        }

        static void LoadCache()
        {
            try
            {
                using var stream = typeof(OnlineTranslator).Assembly.GetManifestResourceStream(BuiltInResource);
                if (stream != null)
                {
                    foreach (var kv in JsonSerializer.Deserialize<Dictionary<string, string>>(stream))
                    {
                        BuiltIn[kv.Key] = kv.Value;
                        Cache[kv.Key] = kv.Value;
                    }
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Built-in translations could not be read: {ex.Message}");
            }

            // this computer's own translations come second, so they win over the built-in ones
            try
            {
                if (File.Exists(CachePath))
                {
                    var stored = JsonSerializer.Deserialize<Dictionary<string, string>>(File.ReadAllText(CachePath));
                    foreach (var kv in stored)
                    {
                        Cache[kv.Key] = kv.Value;
                    }
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Translation cache could not be read: {ex.Message}");
            }
        }

        static readonly object SaveLock = new object();
        static void SaveCache()
        {
            lock (SaveLock)
            {
                try
                {
                    Directory.CreateDirectory(AppSettings.SettingsFolder);
                    // only what this computer learned, the built-in translations ship with the program
                    var snapshot = new SortedDictionary<string, string>(Cache.Where(kv => !BuiltIn.TryGetValue(kv.Key, out string builtIn) || builtIn != kv.Value)
                                                                              .ToDictionary(kv => kv.Key, kv => kv.Value));
                    var options = new JsonSerializerOptions { WriteIndented = true, Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping };
                    File.WriteAllText(CachePath, JsonSerializer.Serialize(snapshot, options));
                    UnsavedCount = 0;
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"Translation cache could not be saved: {ex.Message}");
                }
            }
        }
    }
}
