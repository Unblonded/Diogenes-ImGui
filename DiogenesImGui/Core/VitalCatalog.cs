using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using Caesar;
using Diogenes.Translation;

namespace Diogenes
{
    // The handful of readings that matter for a quick health check (coolant, trans temp, battery...)
    // and how to find them in a CBF. A CBF covers one ECU, so each file only has some of them:
    // the engine ECU knows coolant and oil, the transmission ECU knows the fluid temperature, and so on.
    public static class VitalCatalog
    {
        public enum UnitKind
        {
            Any,
            Temperature,
            Voltage,
            Rpm,
            Speed,
            Pressure,
            Percent,
            Distance,
            Volume,
        }

        // the kind of ECU a reading normally lives in, used to tell you which CBF to load
        public enum EcuHome
        {
            Engine,
            Transmission,
            Cluster,
            Any,
        }

        public static string HomeLabel(EcuHome home) => home switch
        {
            EcuHome.Engine => "Engine ECU CBF (ME, MED, CDI, CRD...)",
            EcuHome.Transmission => "Transmission ECU CBF (VGS, EGS, NAG2, TCM...)",
            EcuHome.Cluster => "Instrument cluster CBF (IC, KI...)",
            _ => "Almost any ECU's CBF",
        };

        public class Vital
        {
            public string Id;
            public EcuHome Home;
            public string Label;
            public string Usually;              // which ECU normally has it, shown when this CBF doesn't
            public UnitKind Unit;
            public bool Info;                   // read once (VIN, mileage) instead of polled
            public string[][] Patterns;         // any pattern matches when all of its words are present
            public string[] Exclude = new string[] { };

            // in the CBF's own unit (°C, V...), null = no limit
            public double? WarnLow, BadLow, WarnHigh, BadHigh;
            public double? GaugeMin, GaugeMax;
            public double? ColdBelow;           // fluids: below this the engine is still warming up
            public string Advice;
        }

        // words in a command name that mean "not the plain live value": targets, limits, models, counters, fault snapshots.
        // Only checked against command names, descriptions often mention filtering or substitute values for the real thing too.
        static readonly string[] CommonExclude =
        {
            "soll", "target", "grenz", "limit", "schwelle", "ersatz", "modell", "model", "offset", "korrektur", "korrigiert",
            "adapt", "zaehler", "nenner", "umwelt", "freeze", "speicher", "gespeichert", "letzt", "max", " min ", "klasse",
            "fehler", "defekt", "diagnose", "plausib", "gradient", "abschalt", "verursacher", "abbruch", "anforder",
            "rohwert", " roh ", "kennlinie", "anzahl", "kaltstart", "regelabweichung", "abweichung", "reset", "historical",
            "end of line", "rbm", "wia", "assyst", "stoppverbot", "start stopp", "lernvorgang", "nachlauf", "can id",
        };

        public static readonly Vital[] All =
        {
            new Vital
            {
                Id = "coolant", Home = EcuHome.Engine, Label = "Coolant temp", Unit = UnitKind.Temperature, Usually = "the engine ECU (ME / CDI / CRD / MED), also the instrument cluster",
                Patterns = new[] { W("kuehlmittel", "temp"), W("kuehlwasser", "temp"), W("coolant", "temp"), W("motortemp"), W("wassertemp") },
                Exclude = new[] { "agr", "egr", "abgasrueck", "niedertemp", "ladeluft", "start" },
                WarnHigh = 108, BadHigh = 118, ColdBelow = 60, GaugeMin = -20, GaugeMax = 130,
                Advice = "Normal when warm is about 80-105 °C. Above ~110 °C check coolant level, fan and thermostat.",
            },
            new Vital
            {
                Id = "trans", Home = EcuHome.Transmission, Label = "Transmission fluid temp", Unit = UnitKind.Temperature, Usually = "the transmission ECU (VGS / EGS / TCM)",
                Patterns = new[] { W("getriebeoel", "temp"), W("getriebe", "temp"), W("atf", "temp"), W("trans", "temp"), W("transmission", "oil", "temp"), W("gearbox", "temp") },
                WarnHigh = 110, BadHigh = 125, ColdBelow = 40, GaugeMin = -20, GaugeMax = 150,
                Advice = "Normal is about 70-100 °C. Sustained 110 °C+ ages the fluid quickly; 125 °C+ is overheating.",
            },
            new Vital
            {
                Id = "oiltemp", Home = EcuHome.Engine, Label = "Engine oil temp", Unit = UnitKind.Temperature, Usually = "the engine ECU",
                Patterns = new[] { W("motoroel", "temp"), W("oeltemp"), W("engine oil", "temp"), W("oil temp") },
                Exclude = new[] { "getriebe", "trans", "atf", "gear" },
                WarnHigh = 130, BadHigh = 145, ColdBelow = 60, GaugeMin = -20, GaugeMax = 160,
                Advice = "Normal is about 90-120 °C once warm.",
            },
            new Vital
            {
                Id = "battery", Home = EcuHome.Any, Label = "Battery voltage", Unit = UnitKind.Voltage, Usually = "almost every ECU (look for supply / terminal 30 voltage)",
                Patterns = new[] { W("batteriespannung"), W("bordnetzspannung"), W("versorgungsspannung"), W("klemme 30"), W("klemme30"), W("kl30"), W("kl. 30"), W("kl 30"), W("battery", "volt"), W("supply volt"), W("terminal 30"), W("ubatt"), W("u_batt") },
                BadLow = 11.0, WarnLow = 12.0, WarnHigh = 15.0, BadHigh = 16.0, GaugeMin = 9, GaugeMax = 16,
                Advice = "Engine off: 12.4-12.8 V is a healthy battery. Engine running: 13.5-14.8 V means the alternator is charging.",
            },
            new Vital
            {
                Id = "rpm", Home = EcuHome.Engine, Label = "Engine speed", Unit = UnitKind.Rpm, Usually = "the engine ECU, also the transmission ECU",
                Patterns = new[] { W("motordrehzahl"), W("drehzahl motor"), W("drehzahl", "motor"), W("engine speed"), W("engine rpm"), W("n_mot"), W("nmot") },
                Exclude = new[] { "leerlauf", "idle" },
                GaugeMin = 0, GaugeMax = 7000,
            },
            new Vital
            {
                Id = "speed", Home = EcuHome.Cluster, Label = "Vehicle speed", Unit = UnitKind.Speed, Usually = "the instrument cluster, ESP or engine ECU",
                Patterns = new[] { W("fahrzeuggeschwindigkeit"), W("geschwindigkeit", "fahrzeug"), W("vehicle speed"), W("v_fzg"), W("vfzg") },
                GaugeMin = 0, GaugeMax = 260,
            },
            new Vital
            {
                Id = "gear", Home = EcuHome.Transmission, Label = "Gear / selector", Unit = UnitKind.Any, Usually = "the transmission ECU, also the engine ECU",
                Patterns = new[] { W("istgang"), W("gang ist"), W("ist gang"), W("aktueller gang"), W("eingelegter gang"), W("fahrstufe"), W("current gear"), W("actual gear"), W("engaged gear") },
                Exclude = new[] { "soll", "target", "ziel", "max", "min", "moment" },
            },
            new Vital
            {
                Id = "intake", Home = EcuHome.Engine, Label = "Intake air temp", Unit = UnitKind.Temperature, Usually = "the engine ECU",
                Patterns = new[] { W("ansaugluft", "temp"), W("ansauglufttemp"), W("intake air", "temp"), W("intake", "temp"), W("ladeluft", "temp"), W("charge air", "temp") },
                WarnHigh = 70, BadHigh = 90, GaugeMin = -20, GaugeMax = 100,
            },
            new Vital
            {
                Id = "outside", Home = EcuHome.Cluster, Label = "Outside temp", Unit = UnitKind.Temperature, Usually = "the instrument cluster or climate control",
                Patterns = new[] { W("aussentemp"), W("aussenluft", "temp"), W("outside", "temp"), W("outdoor", "temp"), W("ambient", "temp"), W("umgebungstemp") },
                GaugeMin = -30, GaugeMax = 50,
            },
            new Vital
            {
                Id = "oilpressure", Home = EcuHome.Engine, Label = "Oil pressure", Unit = UnitKind.Pressure, Usually = "the engine ECU (not every engine has a sensor)",
                Patterns = new[] { W("oeldruck"), W("oil pressure") },
                Exclude = new[] { "getriebe", "trans" },
            },
            new Vital
            {
                Id = "oillevel", Home = EcuHome.Engine, Label = "Oil level", Unit = UnitKind.Any, Usually = "the engine ECU or instrument cluster",
                Patterns = new[] { W("oelstand"), W("oelniveau"), W("oelfuellstand"), W("oil level") },
                Exclude = new[] { "getriebe", "trans" },
            },
            new Vital
            {
                Id = "boost", Home = EcuHome.Engine, Label = "Boost pressure", Unit = UnitKind.Pressure, Usually = "the engine ECU (turbo engines)",
                Patterns = new[] { W("ladedruck"), W("boost"), W("charge pressure") },
                Exclude = new[] { "regelung", "steller", "sollwert" },
            },
            new Vital
            {
                Id = "load", Home = EcuHome.Engine, Label = "Engine load", Unit = UnitKind.Percent, Usually = "the engine ECU",
                Patterns = new[] { W("motorlast"), W("engine load"), W("relative last"), W("relative luftfuellung") },
                GaugeMin = 0, GaugeMax = 100,
            },
            new Vital
            {
                Id = "pedal", Home = EcuHome.Engine, Label = "Accelerator pedal", Unit = UnitKind.Percent, Usually = "the engine ECU",
                Patterns = new[] { W("fahrpedal"), W("pedalwert"), W("accelerator pedal"), W("gaspedal") },
                GaugeMin = 0, GaugeMax = 100,
            },
            new Vital
            {
                Id = "fuel", Home = EcuHome.Cluster, Label = "Fuel level", Unit = UnitKind.Any, Usually = "the instrument cluster",
                Patterns = new[] { W("tankinhalt"), W("tankfuellstand"), W("fuellstand", "tank"), W("kraftstoff", "fuellstand"), W("fuel level"), W("tank level"), W("tank content") },
            },
            new Vital
            {
                Id = "vin", Home = EcuHome.Any, Label = "VIN", Unit = UnitKind.Any, Info = true, Usually = "most ECUs",
                Patterns = new[] { W("fahrzeugidentifizierungsnummer"), W("fahrzeugidentnummer"), W("fahrgestellnummer"), W("vehicle identification"), W(" vin ") },
                Exclude = new[] { "original", "schreib", "write", "fingerprint" },
            },
            new Vital
            {
                Id = "odometer", Home = EcuHome.Cluster, Label = "Mileage", Unit = UnitKind.Distance, Info = true, Usually = "the instrument cluster, also many other ECUs",
                Patterns = new[] { W("kilometerstand"), W("gesamtwegstrecke"), W("gesamtkilometer"), W("odometer"), W("total distance") },
                Exclude = new[] { "schaltqualitaet", "handover", "historical", "reset" },
            },
        };

        static string[] W(params string[] words) => words;

        // ---------------------------------------------------------------- readable values in the CBF

        // one decodable value in the answer of a read command that needs no inputs
        public class Source
        {
            public DiagService Service;
            public DiagPreparation Prep;
            public DiagPresentation Pres;
            public string Name;         // English
            public string Original;     // German
            public string UnitText;
            public UnitKind Unit;
            public string SearchText;   // normalized German + English + qualifiers
            public string QualifierText; // normalized command name without its DT_ prefix, the most reliable hint
            public string PresOriginal; // the value's own German description, can be empty
            public string OfficialName; // from the CBF's English table, when it has one
            public string EcuName;
            public EcuSlot Slot;        // which loaded ECU to ask

            public string Key => $"{EcuName}|{Service.Qualifier}|{Prep.Qualifier}|{Prep.BitPosition}";
        }

        // Text ids are resolved with whatever CBF Tr is set to, so call this inside Tr.Use(slot.Container).
        // translate: false skips translation, for quickly checking what a CBF could provide.
        public static List<Source> FindReadableValues(ECU ecu, IEnumerable<DiagService> services, EcuSlot slot = null, bool translate = true)
        {
            var result = new List<Source>();
            var seen = new HashSet<string>();
            foreach (var ds in services)
            {
                if (ds is null || ds.RequestBytes is null || ds.RequestBytes.Length == 0 || ds.DiagServiceCode.Count > 0)
                {
                    continue;
                }
                if (ServiceCategories.Of(ds) != ServiceCategories.Category.ReadValue)
                {
                    continue;
                }
                // commands that need the user to pick something can't be polled blindly
                if (ds.InputPreparationPresentations.Any(p => p.FieldType == DiagPreparation.InferredDataType.PrepPresentationType))
                {
                    continue;
                }
                string serviceOriginal = Tr.Original(ds.Name_CTF) ?? "";
                foreach (var prepSet in ds.OutputPreparations)
                {
                    foreach (var prep in prepSet)
                    {
                        if (prep is null || prep.PresPoolIndex < 0 || prep.PresPoolIndex >= ecu.GlobalPresentations.Count)
                        {
                            continue;
                        }
                        var pres = ecu.GlobalPresentations[prep.PresPoolIndex];
                        var source = new Source { Service = ds, Prep = prep, Pres = pres, Slot = slot, EcuName = ecu.Qualifier };
                        if (!seen.Add(source.Key))
                        {
                            continue;
                        }
                        string presOriginal = Tr.Original(pres.Description_CTF) ?? "";
                        source.PresOriginal = presOriginal;
                        source.Original = presOriginal.Length > 0 ? presOriginal : (serviceOriginal.Length > 0 ? serviceOriginal : prep.Qualifier);
                        source.UnitText = (Tr.Original(pres.DisplayedUnit_CTF) ?? "").Trim();
                        source.Unit = ClassifyUnit(source.UnitText);
                        if (translate)
                        {
                            source.OfficialName = Tr.OfficialEnglish(presOriginal.Length > 0 ? pres.Description_CTF : ds.Name_CTF);
                            source.Name = presOriginal.Length > 0 ? Tr.Ctf(pres.Description_CTF, Tr.Kind.Title)
                                : serviceOriginal.Length > 0 ? Tr.Ctf(ds.Name_CTF, Tr.Kind.Title)
                                : Tr.Qualifier(prep.Qualifier);
                            source.SearchText = Normalize($" {presOriginal} {serviceOriginal} {prep.Qualifier} {ds.Qualifier} {source.Name} {Tr.Qualifier(ds.Qualifier)} ");
                        }
                        else
                        {
                            source.Name = source.Original;
                            source.SearchText = Normalize($" {presOriginal} {serviceOriginal} {prep.Qualifier} {ds.Qualifier} ");
                        }
                        source.QualifierText = " " + Normalize(ServiceCategories.StripPrefix(ds.Qualifier ?? "").Replace('_', ' ')) + " ";
                        result.Add(source);
                    }
                }
            }
            return result;
        }

        // lower case, umlauts spelled out, separators as spaces, so "Kühlmittel-Temp." and "Kuehlmittel_Temp" look the same
        public static string Normalize(string text)
        {
            var sb = new StringBuilder(text.Length + 8);
            foreach (char raw in text.ToLowerInvariant())
            {
                switch (raw)
                {
                    case 'ä': sb.Append("ae"); break;
                    case 'ö': sb.Append("oe"); break;
                    case 'ü': sb.Append("ue"); break;
                    case 'ß': sb.Append("ss"); break;
                    case '-': case '.': case ',': case ':': case '(': case ')': case '/': sb.Append(' '); break;
                    default: sb.Append(raw); break;
                }
            }
            // keep '_' as written so qualifier patterns like "n_mot" still match, but also offer the spaced form
            string s = sb.ToString();
            return s + " " + s.Replace('_', ' ');
        }

        public static UnitKind ClassifyUnit(string unit)
        {
            string u = unit.Trim().ToLowerInvariant().Replace(" ", "");
            if (u.Length == 0)
            {
                return UnitKind.Any;
            }
            if (u is "°c" or "grad" or "gradc" or "degc" or "°f" or "degf" or "k" or "c" || u.StartsWith("°"))
            {
                return UnitKind.Temperature;
            }
            if (u is "v" or "mv" or "volt")
            {
                return UnitKind.Voltage;
            }
            if (u is "1/min" or "u/min" or "rpm" or "min-1" or "1/min." or "min^-1" or "upm")
            {
                return UnitKind.Rpm;
            }
            if (u is "km/h" or "kmh" or "mph" or "km/std")
            {
                return UnitKind.Speed;
            }
            if (u is "hpa" or "mbar" or "bar" or "kpa" or "pa" or "psi")
            {
                return UnitKind.Pressure;
            }
            if (u is "%")
            {
                return UnitKind.Percent;
            }
            if (u is "km" or "mi" or "m")
            {
                return UnitKind.Distance;
            }
            if (u is "l" or "liter" or "ltr")
            {
                return UnitKind.Volume;
            }
            return UnitKind.Any;
        }

        // all values in this CBF that look like the vital, best first
        public static List<Source> Match(Vital vital, List<Source> sources)
        {
            var scored = new List<(Source source, int score)>();
            foreach (var s in sources)
            {
                if (!vital.Patterns.Any(p => p.All(word => s.SearchText.Contains(word))))
                {
                    continue;
                }
                if (vital.Exclude.Any(x => s.SearchText.Contains(x)))
                {
                    continue;
                }
                if (vital.Unit != UnitKind.Any)
                {
                    // a temperature with a unit of volts is the sensor's voltage, not the temperature,
                    // and a yes/no flag about the temperature isn't the temperature either
                    bool unitOk = s.Unit == vital.Unit || s.UnitText.Length == 0;
                    if (!unitOk || !IsNumeric(s.Pres))
                    {
                        continue;
                    }
                }
                int score = 100;
                if (vital.Unit != UnitKind.Any && s.Unit == vital.Unit)
                {
                    score += 40;
                }
                // Mercedes names its read commands plainly (DT_Kuehlmitteltemperatur, DT_Motordrehzahl), so a match
                // in the command name counts most, and the shorter the name the more likely it is the plain value
                if (vital.Patterns.Any(p => p.All(word => s.QualifierText.Contains(word))))
                {
                    score += 60;
                }
                int extraWords = s.QualifierText.Split(' ', StringSplitOptions.RemoveEmptyEntries).Length / 2 - 1;
                score -= 8 * Math.Max(0, extraWords);
                score -= 25 * CommonExclude.Count(x => s.QualifierText.Contains(x));
                if (vital.Id == "vin" && SafeType(s.Pres) == DiagPresentation.PresentationTypes.PresString)
                {
                    score += 40;
                }
                scored.Add((s, score));
            }
            return scored.OrderByDescending(x => x.score).Select(x => x.source).ToList();
        }

        static DiagPresentation.PresentationTypes? SafeType(DiagPresentation pres)
        {
            try
            {
                return pres.GetDataType();
            }
            catch (Exception)
            {
                return null;
            }
        }

        static bool IsNumeric(DiagPresentation pres)
        {
            var type = SafeType(pres);
            // a scaled enum with a real scale is a number with a few special states ("signal not available")
            if (type == DiagPresentation.PresentationTypes.PresScaledEnum)
            {
                return pres.Scales.Any(x => x.MultiplyFactor != 0);
            }
            return type is DiagPresentation.PresentationTypes.PresScaledDecimal or DiagPresentation.PresentationTypes.PresInt
                or DiagPresentation.PresentationTypes.PresUInt or DiagPresentation.PresentationTypes.PresIEEEFloat;
        }

        // ---------------------------------------------------------------- decoding

        public class Reading
        {
            public double? Number;      // in the CBF's unit
            public string Text;         // when the value isn't a number (gear "D", VIN...)
            public string Error;
            public bool Unsupported;    // the ECU doesn't know this value, another candidate may work
        }

        public static Reading Decode(Source source, byte[] response)
        {
            if (response is null || response.Length == 0)
            {
                return new Reading { Error = "no answer" };
            }
            if (response[0] == 0x7F)
            {
                byte code = response.Length > 2 ? response[2] : (byte)0;
                return new Reading { Error = ServiceCategories.NegativeResponseMeaning(code), Unsupported = code is 0x11 or 0x12 or 0x31 or 0x7E or 0x7F };
            }
            var prep = source.Prep;
            var bits = new BitArray(response);
            if (prep.BitPosition + prep.SizeInBits > bits.Length)
            {
                return new Reading { Error = "not in the answer" };
            }
            var sliced = BitArrayExtension.Slice(bits, prep.BitPosition, prep.SizeInBits);
            var pres = source.Pres;
            try
            {
                switch (pres.GetDataType())
                {
                    case DiagPresentation.PresentationTypes.PresScaledDecimal when pres.Scales.Count > 0:
                        {
                            // same formula as DiagPresentation.Interpret
                            var scale = pres.Scales[0];
                            double raw = BitArrayExtension.PromoteToInt32(sliced, bigEndian: true);
                            return new Reading { Number = raw * scale.MultiplyFactor + scale.AddConstOffset };
                        }
                    case DiagPresentation.PresentationTypes.PresScaledEnum:
                        {
                            // same scale search as DiagPresentation.Interpret, but keeps the number
                            decimal raw = BitArrayExtension.PromoteToInt32(sliced, bigEndian: true);
                            foreach (var scale in pres.Scales)
                            {
                                decimal scaled = scale.MultiplyFactor != 0 ? raw * (decimal)scale.MultiplyFactor + (decimal)scale.AddConstOffset : raw;
                                if ((int)scaled >= scale.EnumLowBound && (int)scaled <= scale.EnumUpBound)
                                {
                                    return scale.MultiplyFactor != 0
                                        ? new Reading { Number = (double)scaled }
                                        : new Reading { Text = Tr.Value(pres.Interpret(sliced)) };
                                }
                            }
                            return new Reading { Text = $"raw value {raw}" };
                        }
                    case DiagPresentation.PresentationTypes.PresInt:
                        return new Reading { Number = BitArrayExtension.PromoteToInt32(sliced, bigEndian: true) };
                    case DiagPresentation.PresentationTypes.PresUInt:
                        return new Reading { Number = BitArrayExtension.PromoteToUInt32(sliced, bigEndian: true) };
                    case DiagPresentation.PresentationTypes.PresIEEEFloat:
                        return new Reading { Number = BitArrayExtension.ToFloat(sliced, true) };
                    case DiagPresentation.PresentationTypes.PresString:
                        return new Reading { Text = pres.Interpret(sliced).Trim('\0', ' ') };
                    default:
                        return new Reading { Text = Tr.Value(pres.Interpret(sliced)) };
                }
            }
            catch (Exception ex)
            {
                return new Reading { Error = ex.Message };
            }
        }

        // ---------------------------------------------------------------- display units

        public static (double value, string unit) ToDisplay(double value, string unit, bool usUnits)
        {
            string u = unit.Trim().ToLowerInvariant().Replace(" ", "");
            if (u == "mv")
            {
                return (value / 1000.0, "V");
            }
            if (u == "k")
            {
                value -= 273.15;
                unit = "°C";
                u = "°c";
            }
            if (!usUnits)
            {
                return (value, unit);
            }
            switch (u)
            {
                case "°c":
                case "gradc":
                case "degc":
                case "grad":
                    return (value * 9.0 / 5.0 + 32.0, "°F");
                case "km/h":
                case "kmh":
                case "km/std":
                    return (value * 0.621371, "mph");
                case "km":
                    return (value * 0.621371, "mi");
                case "hpa":
                case "mbar":
                    return (value * 0.0145038, "psi");
                case "kpa":
                    return (value * 0.145038, "psi");
                case "bar":
                    return (value * 14.5038, "psi");
                case "l":
                    return (value * 0.264172, "gal");
            }
            return (value, unit);
        }

        public static string FormatNumber(double value)
        {
            double abs = Math.Abs(value);
            string format = abs >= 1000 ? "N0" : abs >= 100 ? "0" : abs >= 10 ? "0.0" : "0.00";
            return value.ToString(format, CultureInfo.InvariantCulture);
        }
    }
}
