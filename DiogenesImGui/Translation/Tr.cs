using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Caesar;

namespace Diogenes.Translation
{
    // Everything the UI shows from a CBF goes through here.
    //  1. If the CBF ships its own English string table, that is used (best quality).
    //  2. Otherwise the online translation is used once it has arrived.
    //  3. Until then (or when offline) the built-in dictionary translation is shown.
    public static class Tr
    {
        public enum Kind
        {
            Title,      // "Read Vehicle Identification Number"
            Sentence,   // "Timer not yet elapsed"
        }

        static List<string> GermanTable = null;
        static List<string> EnglishTable = null;

        public static string EnglishTableName { get; private set; } = null;
        public static bool HasOfficialEnglish => EnglishTable != null;

        static bool Enabled => AppSettings.Current.TranslateToEnglish;

        public static void SetContainer(CaesarContainer container)
        {
            GermanTable = null;
            EnglishTable = null;
            EnglishTableName = null;

            var languages = container?.CaesarCTFHeader?.CtfLanguages;
            if (languages is null || languages.Count == 0)
            {
                return;
            }
            GermanTable = languages[0].StringEntries;

            foreach (var language in languages.Skip(1))
            {
                string q = (language.Qualifier ?? "").Trim().ToLowerInvariant();
                bool looksEnglish = q.StartsWith("en") || q == "e" || q == "gb" || q == "us" || q.Contains("engl");
                if (looksEnglish && TableDiffers(GermanTable, language.StringEntries))
                {
                    EnglishTable = language.StringEntries;
                    EnglishTableName = language.Qualifier;
                    Console.WriteLine($"This CBF contains an English text table ({language.Qualifier}), using it for translations");
                    return;
                }
            }
            string available = string.Join(", ", languages.Select(x => x.Qualifier));
            Console.WriteLine($"CBF text languages: {available}. Translating German text automatically.");
        }

        // guards against a table that was read from the same offset as the German one
        static bool TableDiffers(List<string> a, List<string> b)
        {
            if (b is null || b.Count == 0)
            {
                return false;
            }
            int compared = 0;
            int different = 0;
            for (int i = 0; i < Math.Min(a.Count, b.Count); i++)
            {
                if (string.IsNullOrEmpty(a[i]))
                {
                    continue;
                }
                compared++;
                if (a[i] != b[i])
                {
                    different++;
                }
            }
            return compared > 0 && different > compared / 10;
        }

        // original (German) text for a CTF string id
        public static string Original(int ctfId)
        {
            if (GermanTable is null || ctfId < 0 || ctfId >= GermanTable.Count)
            {
                return "";
            }
            return GermanTable[ctfId] ?? "";
        }

        // display text for a CTF string id
        public static string Ctf(int ctfId, Kind kind = Kind.Sentence)
        {
            if (Enabled && EnglishTable != null && ctfId >= 0 && ctfId < EnglishTable.Count && !string.IsNullOrWhiteSpace(EnglishTable[ctfId]))
            {
                return EnglishTable[ctfId];
            }
            return Text(Original(ctfId), kind);
        }

        // display text for any German string (enum names, interpreted values, script messages...)
        public static string Text(string german, Kind kind = Kind.Sentence)
        {
            if (!Enabled || string.IsNullOrWhiteSpace(german))
            {
                return german ?? "";
            }

            if (AppSettings.Current.UseOnlineTranslation && GermanText.LooksGerman(german))
            {
                string online = OnlineTranslator.TryGet(german);
                if (online != null)
                {
                    return ApplyCase(online, kind);
                }
            }
            return ApplyCase(OfflineTranslator.Translate(german), kind);
        }

        // for values read from the ECU ("Spannung: 12,5 V"). Text with numbers in it changes on every read,
        // so it only goes through the offline dictionary instead of filling the online queue.
        public static string Value(string german)
        {
            if (!Enabled || string.IsNullOrWhiteSpace(german))
            {
                return german ?? "";
            }
            if (german.Any(char.IsDigit))
            {
                return ApplyCase(OfflineTranslator.Translate(german), Kind.Sentence);
            }
            return Text(german, Kind.Sentence);
        }

        // turns a qualifier like "DT_STO_Fahrzeug_Geschwindigkeit" into "Vehicle Speed"
        public static string Qualifier(string qualifier)
        {
            if (string.IsNullOrEmpty(qualifier))
            {
                return "";
            }
            string body = ServiceCategories.StripPrefix(qualifier).Replace('_', ' ').Trim();
            if (body.Length == 0)
            {
                body = qualifier.Replace('_', ' ');
            }
            if (!Enabled)
            {
                return body;
            }
            return Text(body, Kind.Title);
        }

        public static bool IsTranslated(string original, string shown)
        {
            return Enabled && !string.IsNullOrWhiteSpace(original) && !string.Equals(original.Trim(), shown?.Trim(), StringComparison.OrdinalIgnoreCase);
        }

        static string ApplyCase(string s, Kind kind)
        {
            if (string.IsNullOrEmpty(s))
            {
                return s;
            }
            if (kind == Kind.Sentence)
            {
                return char.ToUpper(s[0]) + s.Substring(1);
            }

            // title case, but leave acronyms and codes (ECU, VIN, KI211) alone
            string[] small = { "a", "an", "the", "and", "or", "of", "to", "in", "on", "at", "for", "with", "via", "from", "by", "is", "not" };
            var words = s.Split(' ');
            for (int i = 0; i < words.Length; i++)
            {
                string w = words[i];
                if (w.Length == 0 || w.Any(char.IsDigit) || (w.Length > 1 && w.All(c => !char.IsLetter(c) || char.IsUpper(c))))
                {
                    continue;
                }
                if (i > 0 && small.Contains(w.ToLowerInvariant()))
                {
                    words[i] = w.ToLowerInvariant();
                    continue;
                }
                words[i] = char.ToUpper(w[0]) + w.Substring(1);
            }
            return string.Join(" ", words);
        }
    }
}
