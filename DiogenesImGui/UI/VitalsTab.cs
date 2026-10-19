using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Numerics;
using Caesar;
using Diogenes.Translation;
using ImGuiNET;
using static Diogenes.VitalCatalog;

namespace Diogenes.UI
{
    // New: a dashboard of the readings that matter for a quick health check (coolant, transmission
    // fluid, battery...) found automatically in the CBF and polled live, plus quick facts (VIN,
    // mileage, stored faults). Only read commands are ever sent from here.
    public class VitalsTab
    {
        // the graph is sampled from the smoothed value 10 times a second, so it scrolls evenly: 30 s on screen
        const int HistoryLength = 300;
        const double HistorySampleSeconds = 0.1;

        class Tile
        {
            public Vital Vital;             // null for values the user added
            public Source Source;
            public List<Source> Choices = new List<Source>();
            public HashSet<string> Unsupported = new HashSet<string>();
            public Reading Last;
            public DateTime Updated;

            public readonly float[] History = new float[HistoryLength];
            public int HistoryCount;
            public int HistoryHead;
            public double NextSample;
            public double? Min, Max;    // of the real readings, not the smoothed ones
            public double? Shown;       // eases toward the last reading every frame, in the CBF's unit

            public void Push(float value)
            {
                Min = Min is null ? value : Math.Min(Min.Value, value);
                Max = Max is null ? value : Math.Max(Max.Value, value);
            }

            public void Sample(float value)
            {
                History[HistoryHead] = value;
                HistoryHead = (HistoryHead + 1) % HistoryLength;
                HistoryCount = Math.Min(HistoryCount + 1, HistoryLength);
            }

            public void ResetHistory()
            {
                HistoryCount = 0;
                HistoryHead = 0;
                Min = Max = null;
                Shown = null;
            }

            // glides toward the newest reading so it arrives about when the next one is due
            public void Ease(double dt, double interval)
            {
                if (Last?.Number is not double target)
                {
                    Shown = null;
                    return;
                }
                if (Shown is not double current)
                {
                    Shown = target;
                    return;
                }
                Shown = current + (target - current) * (1 - Math.Exp(-dt * 4 / Math.Max(0.25, interval)));
            }
        }

        readonly App App;
        static DiogenesSharedContext Ctx => DiogenesSharedContext.Singleton;
        static AppSettings Settings => AppSettings.Current;

        List<Source> Sources = new List<Source>();
        List<Tile> Tiles = new List<Tile>();
        List<Tile> InfoTiles = new List<Tile>();
        List<Vital> Missing = new List<Vital>();
        List<Vital> Hidden = new List<Vital>();
        readonly Dictionary<DiagService, byte[]> RequestCache = new Dictionary<DiagService, byte[]>();

        bool Live = false;
        bool WasConnected = false;
        bool ReadInfoPending = false;
        bool ReadOncePending = false;
        bool ReadFaultsPending = false;
        DateTime NextPoll = DateTime.MinValue;
        DateTime LastPoll = DateTime.MinValue;
        int LastPollErrors = 0;

        FaultMemoryReader.Result Faults = null;
        bool ShowFaultList = false;

        string AddSearch = "";
        bool LastUsUnits;

        public bool IsLive => Live && App.Connected;

        // set by the --simulate command line option: no car, made-up readings
        public static bool Simulate = false;
        readonly Random SimRandom = new Random();
        DateTime NextSimulation = DateTime.MinValue;

        public VitalsTab(App app)
        {
            App = app;
            LastUsUnits = Settings.UsUnits;
        }

        // every ECU the dashboard reads from: the main CBF plus the extra ones
        List<EcuSlot> Slots = new List<EcuSlot>();

        public void NotifyCbfOrVariantChange()
        {
            Sources.Clear();
            Tiles.Clear();
            InfoTiles.Clear();
            Missing.Clear();
            Hidden.Clear();
            RequestCache.Clear();
            Slots = App.EcuSlots.ToList();
            if (Slots.Count == 0)
            {
                Live = false;
                Faults = null;
                return;
            }

            foreach (var slot in Slots)
            {
                using (Tr.Use(slot.Container))
                {
                    Sources.AddRange(FindReadableValues(slot.Ecu, slot.Services, slot));
                }
            }

            foreach (var vital in All)
            {
                var matches = Match(vital, Sources);
                Source chosen = matches.FirstOrDefault();
                if (Settings.VitalSources.TryGetValue(vital.Id, out string key))
                {
                    if (key == "")
                    {
                        Hidden.Add(vital);
                        continue;
                    }
                    var picked = Sources.FirstOrDefault(s => s.Key == key);
                    if (picked != null)
                    {
                        chosen = picked;
                        if (!matches.Contains(picked))
                        {
                            matches.Insert(0, picked);
                        }
                    }
                }
                if (chosen is null)
                {
                    Missing.Add(vital);
                    continue;
                }
                (vital.Info ? InfoTiles : Tiles).Add(new Tile { Vital = vital, Source = chosen, Choices = matches });
            }

            foreach (string key in Settings.PinnedValues)
            {
                var source = Sources.FirstOrDefault(s => s.Key == key);
                if (source != null)
                {
                    Tiles.Add(new Tile { Source = source, Choices = new List<Source> { source } });
                }
            }
            StartScan();
        }

        // ------------------------------------------------------------ polling

        // called every frame from Draw, so live updates pause while another tab is open
        void Tick()
        {
            if (Simulate)
            {
                SimulateReadings();
                return;
            }
            bool connected = App.Connected;
            if (connected && !WasConnected)
            {
                // just connected: go live and grab the quick facts once
                Live = Tiles.Count > 0;
                ReadInfoPending = true;
                Faults = null;
            }
            else if (!connected && WasConnected)
            {
                Live = false;
            }
            WasConnected = connected;

            if (Settings.UsUnits != LastUsUnits)
            {
                LastUsUnits = Settings.UsUnits;
                foreach (var t in Tiles)
                {
                    t.ResetHistory();
                }
            }

            if (!connected || App.Busy)
            {
                return;
            }
            // button presses wait for the current live read to finish instead of being refused
            if (ReadInfoPending || ReadOncePending || ReadFaultsPending || (Live && DateTime.Now >= NextPoll))
            {
                Poll(ReadInfoPending || ReadOncePending, ReadInfoPending || ReadFaultsPending);
            }
        }

        // smooth made-up values around typical readings, with the transmission running a bit hot so
        // the warning colors show up
        void SimulateReadings()
        {
            if (DateTime.Now < NextSimulation)
            {
                return;
            }
            NextSimulation = DateTime.Now.AddSeconds(Math.Max(0.25f, Settings.VitalsInterval));
            LastPoll = DateTime.Now;
            double t = ImGui.GetTime();
            foreach (var tile in Tiles.Concat(InfoTiles))
            {
                string id = tile.Vital?.Id;
                if (id == "vin")
                {
                    tile.Last = new Reading { Text = "WDD2040081A000000" };
                    continue;
                }
                if (id == "gear" || id == "oillevel")
                {
                    tile.Last = new Reading { Text = id == "gear" ? "D" : "OK" };
                    continue;
                }
                double typical = id switch
                {
                    "coolant" => 92, "trans" => 112, "oiltemp" => 104, "battery" => 14.1, "rpm" => 2300, "speed" => 88,
                    "intake" => 36, "outside" => 21, "oilpressure" => 3200, "boost" => 1450, "load" => 34, "pedal" => 18,
                    "fuel" => 41, "odometer" => 148213, _ => 50,
                };
                double wave = id == "odometer" ? 0 : 0.06 * Math.Sin(t * 0.6 + typical) + 0.015 * (SimRandom.NextDouble() - 0.5);
                Apply(tile, new Reading { Number = typical * (1 + wave) });
            }
            Faults ??= new FaultMemoryReader.Result
            {
                Faults =
                {
                    new FaultMemoryReader.Fault { Code = "P012800", Meaning = "Coolant thermostat: temperature below regulating range (example)", Status = 0x0C, Pending = true },
                    new FaultMemoryReader.Fault { Code = "U010087", Meaning = "Lost communication with the transmission control unit (example)", Status = 0x09, Active = true },
                },
            };
        }

        byte[] RequestFor(DiagService ds)
        {
            if (!RequestCache.TryGetValue(ds, out byte[] request))
            {
                var runner = new DiagServiceRunner(ds);
                runner.GenerateRequestBitArray();
                request = BitArrayExtension.ToBytes(runner.DiagBits);
                RequestCache[ds] = request;
            }
            return request;
        }

        void Poll(bool includeInfo, bool includeFaults)
        {
            ReadInfoPending = false;
            ReadOncePending = false;
            ReadFaultsPending = false;
            var tiles = includeInfo ? Tiles.Concat(InfoTiles).ToList() : Tiles.ToList();
            // one request per command per ECU, even when several cards show values from the same answer
            var requests = tiles.Select(t => (t.Source.Slot, t.Source.Service)).Distinct()
                .Select(x => (x.Slot, x.Service, request: RequestFor(x.Service))).Where(x => x.request.Length > 0).ToList();
            var responses = new Dictionary<(EcuSlot, DiagService), byte[]>();
            var errors = new Dictionary<(EcuSlot, DiagService), string>();
            var faultJobs = includeFaults ? Slots.Select(s => (slot: s, codes: s.Codes)).ToList() : null;
            var faults = new List<(EcuSlot, FaultMemoryReader.Result)>();

            App.RunBackground("Reading vitals", () =>
            {
                foreach (var (slot, ds, request) in requests)
                {
                    var channel = slot.Channel;
                    if (channel is null)
                    {
                        errors[(slot, ds)] = slot.Error ?? $"{slot.Name} is not connected";
                        continue;
                    }
                    try
                    {
                        responses[(slot, ds)] = channel.Send(request, true);
                    }
                    catch (Exception ex)
                    {
                        errors[(slot, ds)] = ex.Message;
                    }
                }
                foreach (var (slot, codes) in faultJobs ?? new List<(EcuSlot, List<DTC>)>())
                {
                    FaultMemoryReader.Result result;
                    try
                    {
                        result = FaultMemoryReader.Read(slot.Channel, slot.Ecu, codes);
                    }
                    catch (Exception ex)
                    {
                        result = new FaultMemoryReader.Result { Error = ex.Message };
                    }
                    faults.Add((slot, result));
                }
            }, () =>
            {
                LastPoll = DateTime.Now;
                NextPoll = LastPoll.AddSeconds(Math.Max(0.25f, Settings.VitalsInterval));
                LastPollErrors = 0;
                foreach (var tile in tiles)
                {
                    var key = (tile.Source.Slot, tile.Source.Service);
                    if (responses.TryGetValue(key, out byte[] response))
                    {
                        Apply(tile, Decode(tile.Source, response));
                    }
                    else if (errors.TryGetValue(key, out string error))
                    {
                        Apply(tile, new Reading { Error = error });
                    }
                }
                if (faultJobs != null)
                {
                    SetFaults(faults);
                }
            }, quiet: true);
        }

        void Apply(Tile tile, Reading reading)
        {
            // the best guess isn't supported by this ECU version, quietly move on to the next candidate
            if (reading.Unsupported && tile.Vital != null)
            {
                tile.Unsupported.Add(tile.Source.Key);
                var next = tile.Choices.FirstOrDefault(c => !tile.Unsupported.Contains(c.Key));
                if (next != null)
                {
                    Console.WriteLine($"{tile.Vital.Label}: {tile.Source.Service.Qualifier} is not supported by this ECU, trying {next.Service.Qualifier}");
                    tile.Source = next;
                    tile.Last = null;
                    tile.ResetHistory();
                    return;
                }
            }
            tile.Last = reading;
            tile.Updated = DateTime.Now;
            if (reading.Error != null)
            {
                LastPollErrors++;
                return;
            }
            if (reading.Number is double n)
            {
                var (shown, _) = ToDisplay(n, tile.Source.UnitText, Settings.UsUnits);
                tile.Push((float)shown);
            }
        }

        // ECUs whose fault memory couldn't be read, shown next to the results
        List<string> FaultErrors = new List<string>();

        void SetFaults(List<(EcuSlot slot, FaultMemoryReader.Result result)> perEcu)
        {
            var faults = new FaultMemoryReader.Result();
            FaultErrors = new List<string>();
            foreach (var (slot, result) in perEcu)
            {
                if (result.Error != null)
                {
                    FaultErrors.Add($"{slot.Name}: {result.Error}");
                    continue;
                }
                // fault texts live in each ECU's own CBF
                using (Tr.Use(slot.Container))
                {
                    foreach (var f in result.Faults)
                    {
                        f.Meaning = f.Dtc is null ? null : Tr.Ctf(f.Dtc.Description_CTF);
                        faults.Faults.Add(f);
                    }
                }
            }
            if (perEcu.Count > 0 && FaultErrors.Count == perEcu.Count)
            {
                faults.Error = string.Join("\n", FaultErrors);
            }
            Faults = faults;
            ShowFaultList = faults.Faults.Count > 0;
            if (faults.Error != null)
            {
                Console.WriteLine($"Fault memory: {faults.Error}");
            }
            else
            {
                Console.WriteLine(faults.Faults.Count == 0 ? "Fault memory: no faults stored" : $"Fault memory: {faults.Faults.Count} fault(s) stored");
                foreach (var f in faults.Faults)
                {
                    Console.WriteLine($"  {f.EcuName} {f.Code}{(f.Active ? " (active)" : "")}: {f.Meaning ?? "not listed in the CBF"}");
                }
            }
        }

        // ------------------------------------------------------------ evaluation

        // number = the value to show (the smoothed one on the gauges), colors always come from the real reading
        static string Display(Tile tile, out Vector4 color, out string note, double? number = null)
        {
            color = Theme.Accent;
            note = null;
            var r = tile.Last;
            if (r is null)
            {
                color = Theme.Dim;
                return "--";
            }
            if (r.Error != null)
            {
                color = Theme.Dim;
                note = r.Error;
                return "--";
            }
            if (r.Number is double n)
            {
                Evaluate(tile, n, out color, out note);
                var (shown, _) = ToDisplay(number ?? n, tile.Source.UnitText, Settings.UsUnits);
                return FormatNumber(shown);
            }
            return string.IsNullOrWhiteSpace(r.Text) ? "--" : r.Text;
        }

        static string UnitLabel(Tile tile)
        {
            string unit = ToDisplay(0, tile.Source.UnitText, Settings.UsUnits).unit;
            return unit == "l" ? "L" : unit;
        }

        // limits are written in °C and V, the CBF's own units
        static void Evaluate(Tile tile, double raw, out Vector4 color, out string note)
        {
            color = Theme.Accent;
            note = null;
            var v = tile.Vital;
            if (v is null)
            {
                return;
            }
            var (n, unit) = ToDisplay(raw, tile.Source.UnitText, false);
            if (v.Unit == UnitKind.Temperature && !IsCelsius(unit))
            {
                return;
            }
            if (v.BadHigh is double bh && n >= bh) { color = Theme.Bad; note = "Too high"; return; }
            if (v.BadLow is double bl && n <= bl) { color = Theme.Bad; note = "Too low"; return; }
            if (v.WarnHigh is double wh && n >= wh) { color = Theme.Warn; note = "High"; return; }
            if (v.WarnLow is double wl && n <= wl) { color = Theme.Warn; note = "Low"; return; }
            if (v.ColdBelow is double cold && n < cold) { color = Theme.Accent; note = "Warming up"; return; }
            if (v.BadHigh != null || v.BadLow != null || v.WarnHigh != null || v.WarnLow != null)
            {
                color = Theme.Good;
                note = "OK";
            }
        }

        static bool IsCelsius(string unit)
        {
            string u = unit.Trim().ToLowerInvariant().Replace(" ", "");
            return u is "°c" or "gradc" or "degc" or "grad" or "c" or "";
        }

        static double? GaugeFraction(Tile tile)
        {
            var v = tile.Vital;
            if (v?.GaugeMin is not double lo || v.GaugeMax is not double hi || (tile.Shown ?? tile.Last?.Number) is not double raw || hi <= lo)
            {
                return null;
            }
            var (n, unit) = ToDisplay(raw, tile.Source.UnitText, false);
            if (v.Unit == UnitKind.Temperature && !IsCelsius(unit))
            {
                return null;
            }
            return Math.Clamp((n - lo) / (hi - lo), 0, 1);
        }

        string TileName(Tile tile)
        {
            if (tile.Vital != null)
            {
                return tile.Vital.Label;
            }
            var s = tile.Source;
            return s.OfficialName ?? (string.IsNullOrWhiteSpace(s.PresOriginal) ? s.Name : Tr.Text(s.PresOriginal, Tr.Kind.Title));
        }

        // ------------------------------------------------------------ settings changes

        void ChooseSource(Tile tile, Source source)
        {
            tile.Source = source;
            tile.Last = null;
            tile.ResetHistory();
            Settings.VitalSources[tile.Vital.Id] = source.Key;
            Settings.Save();
        }

        void Remove(Tile tile)
        {
            if (tile.Vital != null)
            {
                Settings.VitalSources[tile.Vital.Id] = "";
                Hidden.Add(tile.Vital);
            }
            else
            {
                Settings.PinnedValues.Remove(tile.Source.Key);
            }
            Settings.Save();
            Tiles.Remove(tile);
        }

        void Pin(Source source)
        {
            if (!Settings.PinnedValues.Contains(source.Key))
            {
                Settings.PinnedValues.Add(source.Key);
                Settings.Save();
                Tiles.Add(new Tile { Source = source, Choices = new List<Source> { source } });
            }
        }

        void Unhide(Vital vital)
        {
            Settings.VitalSources.Remove(vital.Id);
            Settings.Save();
            NotifyCbfOrVariantChange();
        }

        // ------------------------------------------------------------ drawing

        static string IconFor(Tile tile) => tile.Vital?.Id switch
        {
            "coolant" or "trans" or "oiltemp" or "intake" => Icons.Thermometer,
            "battery" => Icons.Battery,
            "rpm" => Icons.Speedometer,
            "speed" => Icons.Road,
            "gear" => Icons.Cog,
            "outside" => Icons.Sun,
            "oilpressure" or "oillevel" => Icons.Drop,
            "boost" => Icons.Pulse,
            "load" or "pedal" => Icons.Gauge,
            "fuel" => Icons.Flame,
            "vin" => Icons.Key,
            "odometer" => Icons.Road,
            _ => Icons.Chart,
        };

        // the green / yellow / red bands around a gauge, from the vital's limits
        static (float, float, Vector4)[] Zones(Vital v)
        {
            if (v?.GaugeMin is not double lo || v.GaugeMax is not double hi || hi <= lo)
            {
                return null;
            }
            if (v.WarnHigh is null && v.BadHigh is null && v.WarnLow is null && v.BadLow is null)
            {
                return null;
            }
            float F(double x) => (float)Math.Clamp((x - lo) / (hi - lo), 0, 1);
            var zones = new List<(float, float, Vector4)>();
            float okFrom = 0, okTo = 1;
            if (v.BadLow is double bl)
            {
                zones.Add((0, F(bl), Theme.Bad));
                okFrom = F(bl);
            }
            if (v.WarnLow is double wl)
            {
                zones.Add((okFrom, F(wl), Theme.Warn));
                okFrom = F(wl);
            }
            if (v.ColdBelow is double cold)
            {
                zones.Add((okFrom, F(cold), Theme.Accent));
                okFrom = F(cold);
            }
            if (v.BadHigh is double bh)
            {
                zones.Add((F(bh), 1, Theme.Bad));
                okTo = F(bh);
            }
            if (v.WarnHigh is double wh)
            {
                zones.Add((F(wh), okTo, Theme.Warn));
                okTo = F(wh);
            }
            zones.Add((okFrom, okTo, Theme.Good));
            return zones.ToArray();
        }

        // text centered on centerX with its top at y, in the given font
        static void Centered(ImFontPtr font, string text, Vector4 color, float centerX, float y)
        {
            Fonts.Push(font);
            var size = ImGui.CalcTextSize(text);
            ImGui.SetCursorScreenPos(new Vector2(centerX - size.X * 0.5f, y));
            ImGui.PushStyleColor(ImGuiCol.Text, color);
            ImGui.TextUnformatted(text);
            ImGui.PopStyleColor();
            Fonts.Pop();
        }

        static float FontHeight(ImFontPtr font)
        {
            Fonts.Push(font);
            float h = ImGui.GetTextLineHeight();
            Fonts.Pop();
            return h;
        }

        public void Draw()
        {
            Tick();
            DrawHeader();
            ImGui.Spacing();
            DrawQuickFacts();
            ImGui.Spacing();
            DrawCoverage();
            ImGui.Spacing();
            DrawTiles();
            ImGui.Spacing();
            DrawHidden();
        }

        void DrawHeader()
        {
            bool connected = App.Connected;

            Fonts.Push(Fonts.Title);
            ImGui.TextUnformatted("Vehicle health");
            Fonts.Pop();
            ImGui.SameLine();
            ImGui.SetCursorPosY(ImGui.GetCursorPosY() + (FontHeight(Fonts.Title) - ImGui.GetTextLineHeight()) * 0.5f);
            DrawOverallStatus();

            // controls
            if (Live)
            {
                if (Widgets.ColoredButton($"{Icons.Stop}  Stop live", Theme.Bad, new Vector2(Widgets.S(150), 0)))
                {
                    Live = false;
                }
            }
            else
            {
                if (Widgets.PrimaryButton($"{Icons.Play}  Go live", Theme.Accent, new Vector2(Widgets.S(150), 0), connected && Tiles.Count > 0))
                {
                    Live = true;
                    NextPoll = DateTime.MinValue;
                }
                if (!connected)
                {
                    Widgets.Tooltip("Connect to the car first (step 3 at the top)");
                }
            }
            ImGui.SameLine();
            if (Widgets.Button($"{Icons.Refresh}  Read once", connected && !Live && !ReadOncePending, Live ? "Already reading live" : "Connect to the car first (step 3 at the top)"))
            {
                ReadOncePending = true;
            }
            ImGui.SameLine();
            ImGui.SetNextItemWidth(Widgets.S(170));
            float interval = Settings.VitalsInterval;
            if (ImGui.SliderFloat("##interval", ref interval, 0.25f, 5f, "every %.2f s"))
            {
                Settings.VitalsInterval = interval;
            }
            if (ImGui.IsItemDeactivatedAfterEdit())
            {
                Settings.Save();
            }
            Widgets.Tooltip("How often live values are read. Faster is smoother but keeps the bus busier.");
            ImGui.SameLine();
            bool us = Settings.UsUnits;
            if (ImGui.Checkbox("°F / mph / psi", ref us))
            {
                Settings.UsUnits = us;
                Settings.Save();
            }

            ImGui.SameLine();
            if (IsLive || Simulate)
            {
                Widgets.StatusDot(LastPollErrors > 0 ? Theme.Warn : Theme.Good, pulse: true);
                ImGui.TextDisabled(LastPoll == DateTime.MinValue ? "Live" : $"Live  -  updated {(DateTime.Now - LastPoll).TotalSeconds:0.0} s ago");
                Widgets.Tooltip("Live reading pauses while you are on another tab.");
            }
            else if (!connected)
            {
                ImGui.TextDisabled("Offline - showing what this ECU can report");
            }
        }

        void DrawOverallStatus()
        {
            if (Simulate)
            {
                Widgets.Pill("Simulated - not real data", Theme.Accent2);
                ImGui.SameLine();
            }
            else if (!App.Connected)
            {
                Widgets.Pill("Offline", Theme.Dim);
                return;
            }
            int bad = 0, warn = 0, read = 0;
            foreach (var tile in Tiles)
            {
                if (tile.Last?.Number is double n)
                {
                    read++;
                    Evaluate(tile, n, out Vector4 color, out _);
                    if (color == Theme.Bad)
                    {
                        bad++;
                    }
                    else if (color == Theme.Warn)
                    {
                        warn++;
                    }
                }
            }
            int activeFaults = Faults?.Faults.Count(f => f.Active) ?? 0;
            if (bad > 0)
            {
                Widgets.Pill($"{Icons.Error}  {bad} reading{(bad == 1 ? "" : "s")} need attention", Theme.Bad);
            }
            else if (warn > 0)
            {
                Widgets.Pill($"{Icons.Warning}  {warn} reading{(warn == 1 ? "" : "s")} to watch", Theme.Warn);
            }
            else if (read == 0)
            {
                Widgets.Pill("Waiting for data", Theme.Accent);
            }
            else
            {
                Widgets.Pill($"{Icons.Check}  Readings look good", Theme.Good);
            }
            if (activeFaults > 0)
            {
                ImGui.SameLine();
                Widgets.Pill($"{activeFaults} active fault{(activeFaults == 1 ? "" : "s")}", Theme.Bad);
            }
        }

        void DrawQuickFacts()
        {
            float height = ImGui.GetTextLineHeightWithSpacing() * 1.2f + FontHeight(Fonts.DisplaySmall) + ImGui.GetStyle().WindowPadding.Y * 2 + Widgets.S(6);
            float badge = Widgets.S(46);
            if (!ImGui.BeginTable("facts", 4, ImGuiTableFlags.SizingStretchSame))
            {
                return;
            }

            void Fact(string id, string icon, Vector4 color, string label, Action value)
            {
                ImGui.TableNextColumn();
                Widgets.BeginCard($"fact_{id}", new Vector2(0, height));
                ImGui.SetCursorPosY(ImGui.GetCursorPosY() + (height - ImGui.GetStyle().WindowPadding.Y * 2 - badge) * 0.5f - Widgets.S(2));
                Widgets.IconBadge(icon, color, badge);
                ImGui.SameLine();
                ImGui.BeginGroup();
                ImGui.TextDisabled(label);
                value();
                ImGui.EndGroup();
                Widgets.EndCard(color);
            }

            string ecuLine = Ctx.PrimaryVariant != null ? App.VariantName(Ctx.PrimaryVariant) : Tr.Ctf(Ctx.PrimaryEcu.EcuDescription_CTF, Tr.Kind.Sentence).Split('\n')[0].Trim();
            Fact("ecu", Icons.Chip, Theme.Accent2, Slots.Count > 1 ? $"{Slots.Count} control units" : "Control unit", () =>
            {
                Fonts.Push(Fonts.DisplaySmall);
                ImGui.TextUnformatted(Slots.Count > 1 ? $"{Ctx.PrimaryEcu.Qualifier} +{Slots.Count - 1}" : Ctx.PrimaryEcu.Qualifier);
                Fonts.Pop();
                Widgets.Tooltip(Slots.Count > 1 ? string.Join("\n", Slots.Select(s => s.Name + (s.IsPrimary ? " (main)" : ""))) : ecuLine);
            });

            foreach (string id in new[] { "vin", "odometer" })
            {
                var vital = All.First(v => v.Id == id);
                var tile = InfoTiles.FirstOrDefault(t => t.Vital == vital);
                Fact(id, id == "vin" ? Icons.Key : Icons.Road, Theme.Accent, vital.Label, () =>
                {
                    if (tile is null)
                    {
                        ImGui.TextDisabled("not available from this ECU");
                        return;
                    }
                    string value = Display(tile, out _, out string note);
                    string unit = tile.Last?.Number != null ? UnitLabel(tile) : "";
                    Fonts.Push(Fonts.DisplaySmall);
                    ImGui.TextUnformatted($"{value} {unit}".Trim());
                    Fonts.Pop();
                    if (ImGui.BeginPopupContextItem($"copy_{id}"))
                    {
                        if (ImGui.MenuItem($"{Icons.Copy}  Copy"))
                        {
                            ImGui.SetClipboardText(value);
                        }
                        ImGui.EndPopup();
                    }
                    Widgets.Tooltip(note ?? (tile.Last is null ? (App.Connected ? "Press Read once" : "Connect to read") : "Right-click to copy"));
                });
            }

            Vector4 faultColor = Faults is null || Faults.Error != null ? Theme.Dim
                : Faults.Faults.Count == 0 ? Theme.Good
                : Faults.Faults.Any(f => f.Active) ? Theme.Bad : Theme.Warn;
            Fact("faults", Faults != null && Faults.Error is null && Faults.Faults.Count == 0 ? Icons.CheckCircle : Icons.Warning, faultColor, "Fault memory", () =>
            {
                Fonts.Push(Fonts.DisplaySmall);
                ImGui.PushStyleColor(ImGuiCol.Text, faultColor == Theme.Dim ? Theme.Text : faultColor);
                if (Faults is null)
                {
                    ImGui.TextUnformatted("--");
                }
                else if (Faults.Error != null)
                {
                    ImGui.TextUnformatted("Couldn't read");
                }
                else if (Faults.Faults.Count == 0)
                {
                    ImGui.TextUnformatted("All clear");
                }
                else
                {
                    int active = Faults.Faults.Count(f => f.Active);
                    ImGui.TextUnformatted(active > 0 ? $"{Faults.Faults.Count} stored, {active} active" : $"{Faults.Faults.Count} stored");
                }
                ImGui.PopStyleColor();
                Fonts.Pop();
                Widgets.Tooltip(Faults?.Error ?? "Faults stored in this ECU (read only, nothing is cleared)");

                ImGui.SameLine();
                if (Widgets.Button(Icons.Refresh, App.Connected && !ReadFaultsPending, "Connect to the car first (step 3 at the top)"))
                {
                    ReadFaultsPending = true;
                }
                Widgets.Tooltip("Read fault memory again");
                if (Faults != null && Faults.Faults.Count > 0)
                {
                    ImGui.SameLine();
                    if (ImGui.Button(ShowFaultList ? "Hide" : "Show"))
                    {
                        ShowFaultList = !ShowFaultList;
                    }
                }
            });

            ImGui.EndTable();

            if (ShowFaultList && Faults != null && Faults.Faults.Count > 0)
            {
                ImGui.Spacing();
                DrawFaultList();
            }
        }

        void DrawFaultList()
        {
            float rowHeight = ImGui.GetTextLineHeightWithSpacing() + ImGui.GetStyle().CellPadding.Y * 2;
            float height = Math.Min(Widgets.S(320), rowHeight * (Faults.Faults.Count + 1) + ImGui.GetStyle().WindowPadding.Y * 2 + ImGui.GetTextLineHeightWithSpacing() * 2);
            Widgets.BeginCard("faultlist_card", new Vector2(0, height));
            Widgets.SectionTitle(Icons.Warning, "Stored faults");
            if (ImGui.BeginTable("faultlist", 4, ImGuiTableFlags.RowBg | ImGuiTableFlags.BordersInnerH | ImGuiTableFlags.SizingStretchProp | ImGuiTableFlags.ScrollY))
            {
                ImGui.TableSetupScrollFreeze(0, 1);
                ImGui.TableSetupColumn("ECU", ImGuiTableColumnFlags.WidthFixed, Widgets.S(110));
                ImGui.TableSetupColumn("Code", ImGuiTableColumnFlags.WidthFixed, Widgets.S(110));
                ImGui.TableSetupColumn("State", ImGuiTableColumnFlags.WidthFixed, Widgets.S(130));
                ImGui.TableSetupColumn("Meaning", ImGuiTableColumnFlags.WidthStretch);
                ImGui.TableHeadersRow();
                foreach (var f in Faults.Faults)
                {
                    ImGui.TableNextRow();
                    ImGui.TableNextColumn();
                    ImGui.TextDisabled(f.EcuName ?? "");
                    ImGui.TableNextColumn();
                    Fonts.Push(Fonts.Mono);
                    ImGui.TextUnformatted(f.Code);
                    Fonts.Pop();
                    ImGui.TableNextColumn();
                    if (f.Active)
                    {
                        Widgets.Pill("Active now", Theme.Bad);
                    }
                    else if (f.Pending)
                    {
                        Widgets.Pill("Pending", Theme.Warn);
                    }
                    else
                    {
                        Widgets.Pill("Stored", Theme.Dim);
                    }
                    Widgets.Tooltip($"Status byte 0x{f.Status:X2}");
                    ImGui.TableNextColumn();
                    if (f.Meaning is null)
                    {
                        Widgets.Dim("Not described in this CBF - look the code up online.");
                    }
                    else
                    {
                        ImGui.TextWrapped(f.Meaning);
                    }
                }
                ImGui.EndTable();
            }
            Widgets.EndCard();
            foreach (string error in FaultErrors)
            {
                Widgets.Colored(Theme.Warn, $"{Icons.Warning}  Couldn't read {error}");
            }
            ImGui.TextDisabled("To clear them, open that ECU's CBF as the main file and use the Commands tab (search \"clear\" or \"löschen\"), after noting them down.");
        }

        void DrawTiles()
        {
            Widgets.SectionTitle(Icons.Pulse, "Live readings");
            Widgets.Help("Found automatically in this CBF. Use the ... button on a card to pick a different source value or hide it. Colors: green OK, yellow worth watching, red act now. Limits are general guidance, check your car's specifications.");

            float cardWidth = Widgets.S(300);
            int columns = Math.Max(1, (int)(ImGui.GetContentRegionAvail().X / cardWidth));
            float line = ImGui.GetTextLineHeightWithSpacing();
            float radius = Widgets.S(72);
            float cardHeight = line * 2.4f + radius * 2.15f + Widgets.S(44) + ImGui.GetStyle().WindowPadding.Y * 2;

            if (ImGui.BeginTable("tiles", columns, ImGuiTableFlags.SizingStretchSame))
            {
                for (int i = 0; i < Tiles.Count; i++)
                {
                    ImGui.TableNextColumn();
                    ImGui.PushID(i);
                    bool changed = DrawTile(Tiles[i], cardHeight, radius);
                    ImGui.PopID();
                    if (changed)
                    {
                        break;  // list changed
                    }
                }
                ImGui.TableNextColumn();
                DrawAddCard(cardHeight);
                ImGui.EndTable();
            }
            if (Tiles.Count == 0)
            {
                Widgets.Dim("None of the usual live readings were found in this CBF. Use \"Add a value\" to put any readable value on the dashboard.");
            }
        }

        // returns true when the tile list changed
        bool DrawTile(Tile tile, float height, float radius)
        {
            bool changed = false;
            tile.Ease(ImGui.GetIO().DeltaTime, Settings.VitalsInterval);
            if (tile.Shown is double shownNow && (IsLive || Simulate) && ImGui.GetTime() >= tile.NextSample)
            {
                tile.NextSample = ImGui.GetTime() + HistorySampleSeconds;
                tile.Sample((float)ToDisplay(shownNow, tile.Source.UnitText, Settings.UsUnits).value);
            }
            string value = Display(tile, out Vector4 color, out string note, tile.Shown);
            bool hasNumber = tile.Last?.Number != null;
            bool error = tile.Last?.Error != null;

            Widgets.BeginCard("card", new Vector2(0, height));
            var style = ImGui.GetStyle();
            var winPos = ImGui.GetWindowPos();
            float width = ImGui.GetWindowWidth();
            float centerX = winPos.X + width * 0.5f;

            // title row
            string name = TileName(tile);
            ImGui.PushStyleColor(ImGuiCol.Text, tile.Last == null || error ? Theme.Dim : color);
            ImGui.TextUnformatted(IconFor(tile));
            ImGui.PopStyleColor();
            ImGui.SameLine();
            Fonts.Push(Fonts.Semibold);
            ImGui.TextUnformatted(name);
            Fonts.Pop();
            Widgets.Tooltip($"{tile.Source.Name}\n{tile.Source.Original}\nFrom {tile.Source.EcuName}: {tile.Source.Service.Qualifier}" + (tile.Vital?.Advice is null ? "" : $"\n\n{tile.Vital.Advice}"));

            float menuWidth = ImGui.CalcTextSize(Icons.More).X + style.FramePadding.X * 2;
            ImGui.SameLine(width - menuWidth - style.WindowPadding.X);
            ImGui.PushStyleColor(ImGuiCol.Button, Vector4.Zero);
            if (ImGui.SmallButton(Icons.More))
            {
                ImGui.OpenPopup("cardmenu");
            }
            ImGui.PopStyleColor();

            // gauge
            float top = ImGui.GetCursorScreenPos().Y + Widgets.S(4);
            var center = new Vector2(centerX, top + radius + Widgets.S(10));
            double? fraction = GaugeFraction(tile);
            bool drawArc = tile.Vital?.GaugeMin != null;
            if (drawArc)
            {
                Widgets.RadialGauge(center, radius, fraction, color, Zones(tile.Vital));
            }

            if (hasNumber || tile.Last == null || error)
            {
                float displayHeight = FontHeight(Fonts.Display);
                Centered(Fonts.Display, value, tile.Last == null || error ? Theme.Dim : (drawArc ? Theme.Text : color), centerX, center.Y - displayHeight * 0.62f);
                string unit = hasNumber ? UnitLabel(tile) : "";
                if (unit.Length > 0)
                {
                    Centered(ImGui.GetFont(), unit, Theme.Dim, centerX, center.Y + displayHeight * 0.38f);
                }
            }
            else
            {
                // text reading (gear, oil level switch...): short ones as big as numbers
                var font = value.Length <= 5 ? Fonts.Display : Fonts.DisplaySmall;
                float h = FontHeight(font);
                Fonts.Push(font);
                string shown = value;
                while (shown.Length > 4 && ImGui.CalcTextSize(shown).X > width - style.WindowPadding.X * 2)
                {
                    shown = shown.Substring(0, shown.Length - 2);
                }
                Fonts.Pop();
                Centered(font, shown == value ? value : shown + "...", Theme.Text, centerX, center.Y - h * 0.5f);
                if (shown != value)
                {
                    Widgets.Tooltip(value);
                }
            }

            // status pill in the open bottom of the gauge
            string pill = note;
            Vector4 pillColor = error ? Theme.Dim : color;
            if (pill is null && tile.Last == null)
            {
                pill = App.Connected ? (Live ? "reading..." : "press Go live") : "offline";
                pillColor = Theme.Dim;
            }
            if (pill != null)
            {
                if (pill.Length > 28)
                {
                    pill = pill.Substring(0, 26) + "...";
                }
                var pillSize = ImGui.CalcTextSize(pill) + new Vector2(Widgets.S(16), Widgets.S(4));
                ImGui.SetCursorScreenPos(new Vector2(centerX - pillSize.X * 0.5f, center.Y + radius * 0.62f));
                Widgets.Pill(pill, pillColor);
                if (error)
                {
                    Widgets.Tooltip(tile.Last.Error);
                }
            }

            // history
            float sparkTop = center.Y + radius + Widgets.S(22);
            ImGui.SetCursorScreenPos(new Vector2(winPos.X + style.WindowPadding.X, sparkTop));
            var sparkColor = tile.Last == null || error ? Theme.Accent : color;
            if (tile.HistoryCount > 1)
            {
                float lo = (float)tile.Min.Value, hi = (float)tile.Max.Value;
                float pad = Math.Max((hi - lo) * 0.15f, 0.5f);
                int offset = tile.HistoryCount < HistoryLength ? 0 : tile.HistoryHead;
                Widgets.Sparkline("##history", tile.History, tile.HistoryCount, offset, lo - pad, hi + pad, sparkColor, new Vector2(width - style.WindowPadding.X * 2, Widgets.S(34)));
                ImGui.TextDisabled($"min {FormatNumber(tile.Min.Value)}   max {FormatNumber(tile.Max.Value)}");
                DrawEcuTag(tile, width);
            }
            else
            {
                // flat placeholder line so empty cards keep their shape
                var p = ImGui.GetCursorScreenPos();
                float w = width - style.WindowPadding.X * 2;
                ImGui.GetWindowDrawList().AddLine(p + new Vector2(0, Widgets.S(24)), p + new Vector2(w, Widgets.S(24)), Theme.U32(Theme.Track), Widgets.S(2));
                ImGui.Dummy(new Vector2(w, Widgets.S(34)));
                ImGui.TextDisabled(" ");
                DrawEcuTag(tile, width);
            }

            if (ImGui.BeginPopup("cardmenu"))
            {
                if (tile.Choices.Count > 1)
                {
                    ImGui.TextDisabled("Read this from:");
                    foreach (var choice in tile.Choices.Take(40))
                    {
                        string label = $"{choice.Name}" + (choice.UnitText.Length > 0 ? $"  [{choice.UnitText}]" : "") + $"   ({choice.EcuName}: {choice.Service.Qualifier})";
                        if (ImGui.Selectable(label, choice == tile.Source))
                        {
                            ChooseSource(tile, choice);
                        }
                        Widgets.Tooltip(choice.Original);
                    }
                    ImGui.Separator();
                }
                if (ImGui.MenuItem($"{Icons.Copy}  Copy value"))
                {
                    ImGui.SetClipboardText($"{name}: {value} {(hasNumber ? UnitLabel(tile) : "")}".Trim());
                }
                if (ImGui.MenuItem($"{Icons.Refresh}  Reset min / max"))
                {
                    tile.ResetHistory();
                }
                if (ImGui.MenuItem($"{Icons.Close}  " + (tile.Vital != null ? "Hide from dashboard" : "Remove from dashboard")))
                {
                    Remove(tile);
                    changed = true;
                }
                if (Settings.AdvancedMode)
                {
                    ImGui.Separator();
                    ImGui.TextDisabled($"{tile.Source.Service.Qualifier} / {tile.Source.Prep.Qualifier}, bit {tile.Source.Prep.BitPosition}, {tile.Source.Prep.SizeInBits} bits");
                    ImGui.TextDisabled($"Request: {BitUtility.BytesToHex(RequestFor(tile.Source.Service), true)}");
                }
                ImGui.EndPopup();
            }

            Widgets.EndCard(tile.Last == null || error ? Theme.Accent : color);
            return changed;
        }

        void DrawAddCard(float height)
        {
            ImGui.PushStyleColor(ImGuiCol.Border, Theme.WithAlpha(Theme.Accent, 0.25f));
            Widgets.BeginCard("addcard", new Vector2(0, height));
            ImGui.PopStyleColor();
            ImGui.PushStyleColor(ImGuiCol.ChildBg, Theme.Panel);
            var winPos = ImGui.GetWindowPos();
            var size = ImGui.GetWindowSize();
            float centerX = winPos.X + size.X * 0.5f;
            float centerY = winPos.Y + size.Y * 0.5f;

            ImGui.SetCursorScreenPos(winPos);
            bool clicked = ImGui.InvisibleButton("add", size);
            bool hovered = ImGui.IsItemHovered();
            var color = hovered ? Theme.Accent : Theme.Dim;
            float plusHeight = FontHeight(Fonts.Display);
            Centered(Fonts.Display, "+", color, centerX, centerY - plusHeight * 0.9f);
            Centered(Fonts.Semibold, "Add a value", hovered ? Theme.Text : Theme.Dim, centerX, centerY + Widgets.S(4));
            Centered(ImGui.GetFont(), $"{Sources.Count} readable values in this CBF", Theme.Dim, centerX, centerY + Widgets.S(4) + ImGui.GetTextLineHeightWithSpacing() * 1.1f);
            if (clicked)
            {
                AddSearch = "";
                ImGui.OpenPopup("addvalue");
            }

            ImGui.SetNextWindowSize(new Vector2(Widgets.S(700), Widgets.S(520)));
            if (ImGui.BeginPopup("addvalue"))
            {
                Widgets.SectionTitle(Icons.Add, "Add a value to the dashboard");
                ImGui.SetNextItemWidth(-1);
                if (ImGui.IsWindowAppearing())
                {
                    ImGui.SetKeyboardFocusHere();
                }
                ImGui.InputTextWithHint("##addsearch", $"{Icons.Search}  Search, e.g. pressure, Drehzahl, lambda...", ref AddSearch, 128);
                string[] terms = Normalize(AddSearch).Split(' ', StringSplitOptions.RemoveEmptyEntries).Distinct().ToArray();
                var matches = Sources.Where(s => terms.All(t => s.SearchText.Contains(t))).Take(400).ToList();
                ImGui.BeginChild("addlist");
                for (int i = 0; i < matches.Count; i++)
                {
                    var s = matches[i];
                    string label = s.Name + (s.UnitText.Length > 0 ? $"  [{s.UnitText}]" : "");
                    if (Settings.ShowOriginalNames && Tr.IsTranslated(s.Original, s.Name))
                    {
                        label += $"   [{s.Original}]";
                    }
                    if (ImGui.Selectable($"{label}##add{i}"))
                    {
                        Pin(s);
                        ImGui.CloseCurrentPopup();
                    }
                    Widgets.Tooltip($"{s.Original}\n{s.Service.Qualifier}");
                }
                if (matches.Count == 0)
                {
                    ImGui.TextDisabled("Nothing matches.");
                }
                ImGui.EndChild();
                ImGui.EndPopup();
            }
            ImGui.PopStyleColor();
            Widgets.EndCard(Theme.Accent);
        }

        // small "from VGSNAG2" on the right of a card's footer, only when several ECUs are loaded
        void DrawEcuTag(Tile tile, float width)
        {
            if (Slots.Count < 2)
            {
                return;
            }
            string tag = tile.Source.EcuName;
            ImGui.SameLine(width - ImGui.CalcTextSize(tag).X - ImGui.GetStyle().WindowPadding.X);
            ImGui.TextDisabled(tag);
        }

        // ------------------------------------------------------------ coverage: which ECUs are loaded and what's still missing

        class Suggestion
        {
            public string Path;
            public string EcuName;
            public string Description;
            public HashSet<string> Provides = new HashSet<string>();
        }

        // what each CBF on disk can provide, kept for the session (null = not useful or unreadable)
        static readonly Dictionary<string, Suggestion> ScanCache = new Dictionary<string, Suggestion>(StringComparer.OrdinalIgnoreCase);
        List<string> ScanFiles = new List<string>();
        int ScanGeneration = 0;
        int ScanPending = 0;

        static string ScanKey(string path)
        {
            var info = new FileInfo(path);
            return $"{path}|{info.Length}|{info.LastWriteTimeUtc.Ticks}";
        }

        // looks at the other CBF files in the folders you open CBFs from, to suggest ones that fill the gaps
        void StartScan()
        {
            var loaded = new HashSet<string>(Slots.Select(s => s.Path).Where(p => p != null), StringComparer.OrdinalIgnoreCase);
            var folders = Slots.Select(s => s.Path).Concat(Settings.RecentCbfFiles)
                .Where(p => !string.IsNullOrEmpty(p)).Select(p => System.IO.Path.GetDirectoryName(p))
                .Where(d => d != null && Directory.Exists(d)).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
            var files = new List<string>();
            foreach (string folder in folders)
            {
                try
                {
                    files.AddRange(Directory.EnumerateFiles(folder, "*.cbf"));
                }
                catch (Exception)
                {
                    // unreadable folder, skip it
                }
            }
            ScanFiles = files.Distinct(StringComparer.OrdinalIgnoreCase).Where(f => !loaded.Contains(f)).Take(80).ToList();
            var toScan = ScanFiles.Where(f => !ScanCache.ContainsKey(ScanKey(f))).ToList();
            int generation = ++ScanGeneration;
            ScanPending = toScan.Count;
            if (toScan.Count == 0)
            {
                return;
            }
            Task.Run(() =>
            {
                foreach (string file in toScan)
                {
                    if (generation != ScanGeneration)
                    {
                        return;
                    }
                    CaesarContainer container = null;
                    try
                    {
                        byte[] bytes = File.ReadAllBytes(file);
                        if (CaesarContainer.VerifyChecksum(bytes, out _))
                        {
                            // the parser prints debug notes for some files, keep them out of the Messages panel
                            var output = Console.Out;
                            Console.SetOut(TextWriter.Null);
                            try
                            {
                                container = new CaesarContainer(bytes);
                            }
                            finally
                            {
                                Console.SetOut(output);
                            }
                        }
                    }
                    catch (Exception)
                    {
                        container = null;
                    }
                    string key = ScanKey(file);
                    App.OnMainThread(() =>
                    {
                        ScanCache[key] = container is null ? null : Analyze(file, container);
                        if (generation == ScanGeneration)
                        {
                            ScanPending = Math.Max(0, ScanPending - 1);
                        }
                    });
                }
            });
        }

        static Suggestion Analyze(string path, CaesarContainer container)
        {
            var ecu = container.CaesarECUs.FirstOrDefault();
            if (ecu is null)
            {
                return null;
            }
            var suggestion = new Suggestion { Path = path, EcuName = ecu.Qualifier };
            using (Tr.Use(container))
            {
                suggestion.Description = Tr.Ctf(ecu.EcuDescription_CTF, Tr.Kind.Sentence).Split('\n')[0].Trim();
                var sources = FindReadableValues(ecu, ecu.GlobalDiagServices, null, translate: false);
                foreach (var vital in All.Where(v => !v.Info))
                {
                    if (Match(vital, sources).Count > 0)
                    {
                        suggestion.Provides.Add(vital.Id);
                    }
                }
            }
            return suggestion.Provides.Count > 0 ? suggestion : null;
        }

        void DrawCoverage()
        {
            var keyVitals = All.Where(v => !v.Info).ToList();
            var missing = Missing.Where(v => !v.Info).ToList();
            int have = keyVitals.Count - missing.Count;
            var loadedNames = new HashSet<string>(Slots.Select(s => s.Name));

            // CBFs on disk that add something still missing, most useful first
            var missingIds = new HashSet<string>(missing.Select(v => v.Id));
            var suggestions = ScanFiles.Select(f => ScanCache.TryGetValue(ScanKey(f), out var s) ? s : null)
                .Where(s => s != null && !loadedNames.Contains(s.EcuName) && s.Provides.Overlaps(missingIds))
                .GroupBy(s => s.EcuName).Select(g => g.First())
                .OrderByDescending(s => s.Provides.Count(missingIds.Contains)).ToList();

            ImGui.PushStyleColor(ImGuiCol.ChildBg, Theme.Card);
            ImGui.BeginChild("coverage", Vector2.Zero, ImGuiChildFlags.Border | ImGuiChildFlags.AutoResizeY, ImGuiWindowFlags.NoScrollbar | ImGuiWindowFlags.NoScrollWithMouse);

            // title, count and progress bar
            Widgets.SectionTitle(Icons.Car, "Control units");
            ImGui.SameLine();
            var countColor = missing.Count == 0 ? Theme.Good : Theme.Accent;
            ImGui.PushStyleColor(ImGuiCol.Text, countColor);
            ImGui.TextUnformatted($"{have} of {keyVitals.Count} key readings available");
            ImGui.PopStyleColor();
            ImGui.SameLine();
            {
                float barWidth = Widgets.S(180), barHeight = Widgets.S(6);
                var p = ImGui.GetCursorScreenPos() + new Vector2(0, (ImGui.GetTextLineHeight() - barHeight) * 0.5f);
                var draw = ImGui.GetWindowDrawList();
                draw.AddRectFilled(p, p + new Vector2(barWidth, barHeight), Theme.U32(Theme.Track), barHeight);
                draw.AddRectFilled(p, p + new Vector2(Math.Max(barHeight, barWidth * have / Math.Max(1, keyVitals.Count)), barHeight), Theme.U32(countColor), barHeight);
                ImGui.Dummy(new Vector2(barWidth, ImGui.GetTextLineHeight()));
            }

            // loaded ECUs as chips
            for (int i = 0; i < Slots.Count; i++)
            {
                var slot = Slots[i];
                ImGui.PushID(i);
                var dot = slot.Connected ? Theme.Good : slot.Error != null ? Theme.Bad : Theme.Dim;
                ImGui.PushStyleColor(ImGuiCol.Button, Theme.WithAlpha(dot, 0.12f));
                ImGui.PushStyleColor(ImGuiCol.ButtonHovered, Theme.WithAlpha(dot, 0.20f));
                ImGui.PushStyleColor(ImGuiCol.ButtonActive, Theme.WithAlpha(dot, 0.28f));
                string chip = $"{(slot.Connected ? Icons.CheckCircle : slot.Error != null ? Icons.Error : Icons.Chip)}  {slot.Name}" + (slot.IsPrimary ? "  (main)" : "");
                ImGui.Button(chip);
                ImGui.PopStyleColor(3);
                string description;
                using (Tr.Use(slot.Container))
                {
                    description = Tr.Ctf(slot.Ecu.EcuDescription_CTF, Tr.Kind.Sentence).Split('\n')[0].Trim();
                }
                Widgets.Tooltip($"{description}\n{slot.Path}\n\n" + (slot.Connected ? "Connected" : slot.Error != null ? $"Couldn't connect: {slot.Error}" : "Not connected")
                    + (slot.IsPrimary ? "\n\nThe main CBF is also used by the Commands and Memory tabs." : ""));
                if (!slot.IsPrimary)
                {
                    ImGui.SameLine(0, Widgets.S(2));
                    if (ImGui.SmallButton(Icons.Close))
                    {
                        App.RemoveExtraCbf(slot);
                        ImGui.PopID();
                        break;
                    }
                    Widgets.Tooltip($"Stop reading {slot.Name}");
                }
                ImGui.SameLine();
                ImGui.PopID();
            }
            if (Widgets.ColoredButton($"{Icons.Add}  Add another CBF...", Theme.Accent))
            {
                App.OpenExtraCbfDialog();
            }
            Widgets.Tooltip("Load another control unit's CBF (e.g. the transmission) so its readings show up here too. They are read over the same adapter connection.");

            foreach (var slot in Slots.Where(s => s.Error != null))
            {
                Widgets.Colored(Theme.Warn, $"{Icons.Warning}  {slot.Name}: couldn't connect ({slot.Error}). ECUs on a different bus speed than the main one can't be read at the same time.");
            }

            if (missing.Count == 0)
            {
                Widgets.Colored(Theme.Good, $"{Icons.Check}  Everything on the checklist is covered.");
            }
            else
            {
                ImGui.Spacing();
                if (suggestions.Count > 0)
                {
                    ImGui.TextDisabled("Found in your CBF folders - add these to fill the gaps:");
                    foreach (var s in suggestions.Take(6))
                    {
                        ImGui.PushID(s.Path);
                        if (Widgets.PrimaryButton($"{Icons.Add}  Add", Theme.Accent))
                        {
                            App.AddExtraCbf(s.Path);
                        }
                        ImGui.SameLine();
                        Fonts.Push(Fonts.Semibold);
                        ImGui.TextUnformatted(System.IO.Path.GetFileName(s.Path));
                        Fonts.Pop();
                        Widgets.Tooltip($"{s.EcuName} - {s.Description}\n{s.Path}");
                        ImGui.SameLine();
                        var adds = All.Where(v => s.Provides.Contains(v.Id) && missingIds.Contains(v.Id)).Select(v => v.Label);
                        ImGui.TextDisabled($"{s.EcuName}  -  adds {string.Join(", ", adds)}");
                        ImGui.PopID();
                    }
                }
                var coveredBySuggestion = new HashSet<string>(suggestions.SelectMany(s => s.Provides));
                var stillMissing = missing.Where(v => !coveredBySuggestion.Contains(v.Id)).ToList();
                if (stillMissing.Count > 0)
                {
                    ImGui.Spacing();
                    ImGui.TextDisabled(suggestions.Count > 0 ? "Still missing after that - you need these CBF files:" : "To get the rest, add the CBF for:");
                    // group by the ECU they usually come from, so it reads as a shopping list
                    foreach (var group in stillMissing.GroupBy(v => v.Home).OrderBy(g => g.Key))
                    {
                        ImGui.PushStyleColor(ImGuiCol.Text, Theme.Accent);
                        ImGui.TextUnformatted(Icons.Chip);
                        ImGui.PopStyleColor();
                        ImGui.SameLine();
                        ImGui.TextUnformatted(HomeLabel(group.Key));
                        Widgets.Tooltip(string.Join("\n", group.Select(v => $"{v.Label}: usually in {v.Usually}")));
                        ImGui.SameLine();
                        ImGui.TextDisabled($"-  {string.Join(", ", group.Select(v => v.Label))}");
                    }
                }
                if (ScanPending > 0)
                {
                    Widgets.StatusDot(Theme.Accent, pulse: true);
                    ImGui.TextDisabled($"Checking {ScanPending} other CBF file{(ScanPending == 1 ? "" : "s")} in your folders...");
                }
            }

            ImGui.EndChild();
            ImGui.PopStyleColor();
        }

        void DrawHidden()
        {
            if (Hidden.Count > 0&& ImGui.CollapsingHeader($"{Icons.Close}  Hidden ({Hidden.Count})"))
            {
                foreach (var v in Hidden.ToList())
                {
                    ImGui.PushID(v.Id);
                    if (ImGui.SmallButton("Show"))
                    {
                        Unhide(v);
                    }
                    ImGui.SameLine();
                    ImGui.TextUnformatted(v.Label);
                    ImGui.PopID();
                }
            }
        }
    }
}
