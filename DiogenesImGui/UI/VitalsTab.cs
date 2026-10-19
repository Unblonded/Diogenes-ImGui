using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
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
        const int HistoryLength = 120;

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
            public double? Min, Max;

            public void Push(float value)
            {
                History[HistoryHead] = value;
                HistoryHead = (HistoryHead + 1) % HistoryLength;
                HistoryCount = Math.Min(HistoryCount + 1, HistoryLength);
                Min = Min is null ? value : Math.Min(Min.Value, value);
                Max = Max is null ? value : Math.Max(Max.Value, value);
            }

            public void ResetHistory()
            {
                HistoryCount = 0;
                HistoryHead = 0;
                Min = Max = null;
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

        public VitalsTab(App app)
        {
            App = app;
            LastUsUnits = Settings.UsUnits;
        }

        string EcuKey => Ctx.PrimaryEcu?.Qualifier ?? "";

        public void NotifyCbfOrVariantChange()
        {
            Sources.Clear();
            Tiles.Clear();
            InfoTiles.Clear();
            Missing.Clear();
            Hidden.Clear();
            RequestCache.Clear();
            if (!App.CbfLoaded)
            {
                Live = false;
                Faults = null;
                return;
            }

            var ecu = Ctx.PrimaryEcu;
            IEnumerable<DiagService> services = Ctx.PrimaryVariant != null ? Ctx.PrimaryVariant.DiagServices : ecu.GlobalDiagServices;
            Sources = FindReadableValues(ecu, services);

            foreach (var vital in All)
            {
                var matches = Match(vital, Sources);
                Source chosen = matches.FirstOrDefault();
                if (Settings.VitalSources.TryGetValue($"{EcuKey}|{vital.Id}", out string key))
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

            if (Settings.VitalPins.TryGetValue(EcuKey, out var pins))
            {
                foreach (string key in pins)
                {
                    var source = Sources.FirstOrDefault(s => s.Key == key);
                    if (source != null)
                    {
                        Tiles.Add(new Tile { Source = source, Choices = new List<Source> { source } });
                    }
                }
            }
        }

        // ------------------------------------------------------------ polling

        // called every frame from Draw, so live updates pause while another tab is open
        void Tick()
        {
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
            var requests = tiles.Select(t => t.Source.Service).Distinct().Select(ds => (ds, request: RequestFor(ds))).Where(x => x.request.Length > 0).ToList();
            var responses = new Dictionary<DiagService, byte[]>();
            var errors = new Dictionary<DiagService, string>();
            var ecu = Ctx.PrimaryEcu;
            var codes = (Ctx.PrimaryVariant != null && Ctx.PrimaryVariant.DTCs.Length > 0 ? Ctx.PrimaryVariant.DTCs : ecu.GlobalDTCs.ToArray()).ToList();
            FaultMemoryReader.Result faults = null;

            App.RunBackground("Reading vitals", () =>
            {
                foreach (var (ds, request) in requests)
                {
                    var channel = Ctx.Channel;
                    if (channel is null)
                    {
                        break;
                    }
                    try
                    {
                        responses[ds] = channel.Send(request, true);
                    }
                    catch (Exception ex)
                    {
                        errors[ds] = ex.Message;
                    }
                }
                if (includeFaults)
                {
                    try
                    {
                        faults = FaultMemoryReader.Read(ecu, codes);
                    }
                    catch (Exception ex)
                    {
                        faults = new FaultMemoryReader.Result { Error = ex.Message };
                    }
                }
            }, () =>
            {
                LastPoll = DateTime.Now;
                NextPoll = LastPoll.AddSeconds(Math.Max(0.25f, Settings.VitalsInterval));
                LastPollErrors = 0;
                foreach (var tile in tiles)
                {
                    if (responses.TryGetValue(tile.Source.Service, out byte[] response))
                    {
                        Apply(tile, Decode(tile.Source, response));
                    }
                    else if (errors.TryGetValue(tile.Source.Service, out string error))
                    {
                        Apply(tile, new Reading { Error = error });
                    }
                }
                if (faults != null)
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

        void SetFaults(FaultMemoryReader.Result faults)
        {
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
                    Console.WriteLine($"  {f.Code}{(f.Active ? " (active)" : "")}: {f.Meaning ?? "not listed in this CBF"}");
                }
            }
        }

        // ------------------------------------------------------------ evaluation

        static string Display(Tile tile, out Vector4 color, out string note)
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
                var (shown, _) = ToDisplay(n, tile.Source.UnitText, Settings.UsUnits);
                return FormatNumber(shown);
            }
            return string.IsNullOrWhiteSpace(r.Text) ? "--" : r.Text;
        }

        static string UnitLabel(Tile tile)
        {
            return ToDisplay(0, tile.Source.UnitText, Settings.UsUnits).unit;
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
            if (v?.GaugeMin is not double lo || v.GaugeMax is not double hi || tile.Last?.Number is not double raw || hi <= lo)
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
            string original = Tr.Original(tile.Source.Pres.Description_CTF);
            return string.IsNullOrWhiteSpace(original) ? tile.Source.Name : Tr.Ctf(tile.Source.Pres.Description_CTF, Tr.Kind.Title);
        }

        // ------------------------------------------------------------ settings changes

        void ChooseSource(Tile tile, Source source)
        {
            tile.Source = source;
            tile.Last = null;
            tile.ResetHistory();
            Settings.VitalSources[$"{EcuKey}|{tile.Vital.Id}"] = source.Key;
            Settings.Save();
        }

        void Remove(Tile tile)
        {
            if (tile.Vital != null)
            {
                Settings.VitalSources[$"{EcuKey}|{tile.Vital.Id}"] = "";
                Hidden.Add(tile.Vital);
            }
            else if (Settings.VitalPins.TryGetValue(EcuKey, out var pins))
            {
                pins.Remove(tile.Source.Key);
            }
            Settings.Save();
            Tiles.Remove(tile);
        }

        void Pin(Source source)
        {
            if (!Settings.VitalPins.TryGetValue(EcuKey, out var pins))
            {
                pins = new List<string>();
                Settings.VitalPins[EcuKey] = pins;
            }
            if (!pins.Contains(source.Key))
            {
                pins.Add(source.Key);
                Settings.Save();
                Tiles.Add(new Tile { Source = source, Choices = new List<Source> { source } });
            }
        }

        void Unhide(Vital vital)
        {
            Settings.VitalSources.Remove($"{EcuKey}|{vital.Id}");
            Settings.Save();
            NotifyCbfOrVariantChange();
        }

        // ------------------------------------------------------------ drawing

        public void Draw()
        {
            Tick();
            DrawToolbar();
            ImGui.Spacing();
            DrawQuickFacts();
            ImGui.Spacing();
            DrawTiles();
            ImGui.Spacing();
            DrawMissing();
        }

        void DrawToolbar()
        {
            bool connected = App.Connected;
            if (Live)
            {
                if (Widgets.ColoredButton("Stop live", Theme.Bad, new Vector2(Widgets.S(130), 0)))
                {
                    Live = false;
                }
            }
            else
            {
                if (Widgets.ColoredButton("Start live", Theme.Good, new Vector2(Widgets.S(130), 0), connected && Tiles.Count > 0))
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
            if (Widgets.Button("Read once", connected && !Live && !ReadOncePending, Live ? "Already reading live" : "Connect to the car first (step 3 at the top)"))
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
            if (IsLive)
            {
                Widgets.StatusDot(LastPollErrors > 0 ? Theme.Warn : Theme.Good);
                ImGui.TextUnformatted(LastPoll == DateTime.MinValue ? "Live" : $"Live  -  updated {(DateTime.Now - LastPoll).TotalSeconds:0.0} s ago");
                Widgets.Tooltip("Live reading pauses while you are on another tab.");
            }
            else if (!connected)
            {
                ImGui.TextDisabled("Not connected - showing what this ECU can report.");
            }
        }

        void DrawQuickFacts()
        {
            float height = ImGui.GetTextLineHeightWithSpacing() * 3.4f;
            if (!ImGui.BeginTable("facts", 4, ImGuiTableFlags.SizingStretchSame | ImGuiTableFlags.BordersInnerV | ImGuiTableFlags.BordersOuter))
            {
                return;
            }

            ImGui.TableNextColumn();
            ImGui.BeginChild("fact_ecu", new Vector2(0, height));
            ImGui.TextDisabled("Control unit");
            ImGui.TextUnformatted(Ctx.PrimaryEcu.Qualifier);
            string description = Tr.Ctf(Ctx.PrimaryEcu.EcuDescription_CTF, Tr.Kind.Sentence).Split('\n')[0].Trim();
            ImGui.TextDisabled(Ctx.PrimaryVariant != null ? App.VariantName(Ctx.PrimaryVariant) : description);
            Widgets.Tooltip(description);
            ImGui.EndChild();

            foreach (string id in new[] { "vin", "odometer" })
            {
                ImGui.TableNextColumn();
                ImGui.BeginChild($"fact_{id}", new Vector2(0, height));
                var vital = All.First(v => v.Id == id);
                ImGui.TextDisabled(vital.Label);
                var tile = InfoTiles.FirstOrDefault(t => t.Vital == vital);
                if (tile is null)
                {
                    Widgets.Dim("not available from this ECU");
                }
                else
                {
                    string value = Display(tile, out _, out string note);
                    string unit = tile.Last?.Number != null ? UnitLabel(tile) : "";
                    Widgets.BigText($"{value} {unit}".Trim(), 1.25f);
                    if (ImGui.BeginPopupContextItem($"copy_{id}"))
                    {
                        if (ImGui.MenuItem("Copy"))
                        {
                            ImGui.SetClipboardText(value);
                        }
                        ImGui.EndPopup();
                    }
                    if (note != null)
                    {
                        Widgets.Dim(note);
                    }
                    else if (tile.Last is null)
                    {
                        Widgets.Dim(App.Connected ? "press Read once" : "connect to read");
                    }
                }
                ImGui.EndChild();
            }

            ImGui.TableNextColumn();
            ImGui.BeginChild("fact_faults", new Vector2(0, height));
            ImGui.TextDisabled("Fault memory");
            if (Faults is null)
            {
                Widgets.Dim(App.Connected ? "not read yet" : "connect to read");
            }
            else if (Faults.Error != null)
            {
                Widgets.Colored(Theme.Warn, Faults.Error);
            }
            else if (Faults.Faults.Count == 0)
            {
                Widgets.StatusDot(Theme.Good);
                Widgets.Colored(Theme.Good, "No faults stored");
            }
            else
            {
                int active = Faults.Faults.Count(f => f.Active);
                var color = active > 0 ? Theme.Bad : Theme.Warn;
                Widgets.StatusDot(color);
                Widgets.Colored(color, $"{Faults.Faults.Count} stored" + (active > 0 ? $", {active} active now" : ""));
            }
            if (Widgets.Button(Faults is null ? "Read faults" : "Read again", App.Connected && !ReadFaultsPending, "Connect to the car first (step 3 at the top)"))
            {
                ReadFaultsPending = true;
            }
            if (Faults != null && Faults.Faults.Count > 0)
            {
                ImGui.SameLine();
                if (ImGui.Button(ShowFaultList ? "Hide list" : "Show list"))
                {
                    ShowFaultList = !ShowFaultList;
                }
            }
            ImGui.EndChild();

            ImGui.EndTable();

            if (ShowFaultList && Faults != null && Faults.Faults.Count > 0)
            {
                DrawFaultList();
            }
        }

        void DrawFaultList()
        {
            if (!ImGui.BeginTable("faultlist", 3, ImGuiTableFlags.RowBg | ImGuiTableFlags.BordersInnerH | ImGuiTableFlags.SizingStretchProp))
            {
                return;
            }
            ImGui.TableSetupColumn("Code", ImGuiTableColumnFlags.WidthFixed, Widgets.S(110));
            ImGui.TableSetupColumn("State", ImGuiTableColumnFlags.WidthFixed, Widgets.S(110));
            ImGui.TableSetupColumn("Meaning", ImGuiTableColumnFlags.WidthStretch);
            ImGui.TableHeadersRow();
            foreach (var f in Faults.Faults)
            {
                ImGui.TableNextRow();
                ImGui.TableNextColumn();
                ImGui.TextUnformatted(f.Code);
                ImGui.TableNextColumn();
                if (f.Active)
                {
                    Widgets.Colored(Theme.Bad, "Active now");
                }
                else if (f.Pending)
                {
                    Widgets.Colored(Theme.Warn, "Pending");
                }
                else
                {
                    Widgets.Dim("Stored");
                }
                Widgets.Tooltip($"Status byte 0x{f.Status:X2}");
                ImGui.TableNextColumn();
                if (f.Meaning is null)
                {
                    Widgets.Dim("Not listed in this CBF - search the code in the Fault codes tab or online.");
                }
                else
                {
                    ImGui.TextWrapped(f.Meaning);
                }
            }
            ImGui.EndTable();
            ImGui.TextDisabled("Clearing faults: use the Commands tab (search \"clear\" or \"löschen\"), after noting these down.");
        }

        void DrawTiles()
        {
            Widgets.Heading("Live readings");
            Widgets.Help("Found automatically in this CBF. Use the ... button on a card to pick a different source value or hide it. Colors: green OK, yellow worth watching, red act now. Limits are general guidance, check your car's specifications.");

            float cardWidth = Widgets.S(290);
            int columns = Math.Max(1, (int)(ImGui.GetContentRegionAvail().X / cardWidth));
            float line = ImGui.GetTextLineHeightWithSpacing();
            float cardHeight = line * 5.6f + Widgets.S(52);

            if (ImGui.BeginTable("tiles", columns, ImGuiTableFlags.SizingStretchSame))
            {
                for (int i = 0; i < Tiles.Count; i++)
                {
                    ImGui.TableNextColumn();
                    ImGui.PushID(i);
                    if (DrawTile(Tiles[i], cardHeight))
                    {
                        ImGui.PopID();
                        break;  // list changed
                    }
                    ImGui.PopID();
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
        bool DrawTile(Tile tile, float height)
        {
            bool changed = false;
            ImGui.BeginChild("card", new Vector2(0, height), ImGuiChildFlags.Border, ImGuiWindowFlags.NoScrollbar);

            string name = TileName(tile);
            ImGui.TextDisabled(name);
            Widgets.Tooltip($"{tile.Source.Name}\n{tile.Source.Original}\n{tile.Source.Service.Qualifier}" + (tile.Vital?.Advice is null ? "" : $"\n\n{tile.Vital.Advice}"));

            float menuWidth = ImGui.CalcTextSize("...").X + ImGui.GetStyle().FramePadding.X * 2;
            ImGui.SameLine(ImGui.GetWindowWidth() - menuWidth - ImGui.GetStyle().WindowPadding.X);
            if (ImGui.SmallButton("..."))
            {
                ImGui.OpenPopup("cardmenu");
            }

            string value = Display(tile, out Vector4 color, out string note);
            ImGui.PushStyleColor(ImGuiCol.Text, color);
            ImGui.SetWindowFontScale(2.2f);
            ImGui.TextUnformatted(value);
            ImGui.SetWindowFontScale(1f);
            ImGui.PopStyleColor();
            if (tile.Last?.Number != null)
            {
                ImGui.SameLine();
                ImGui.TextDisabled(UnitLabel(tile));
            }
            if (note != null)
            {
                ImGui.SameLine();
                ImGui.PushStyleColor(ImGuiCol.Text, tile.Last?.Error != null ? Theme.Dim : color);
                ImGui.TextUnformatted(note);
                ImGui.PopStyleColor();
            }

            // gauge bar
            float barHeight = Widgets.S(6);
            var draw = ImGui.GetWindowDrawList();
            var p = ImGui.GetCursorScreenPos();
            float width = ImGui.GetContentRegionAvail().X;
            draw.AddRectFilled(p, p + new Vector2(width, barHeight), ImGui.ColorConvertFloat4ToU32(new Vector4(1, 1, 1, 0.08f)), barHeight);
            if (GaugeFraction(tile) is double fraction)
            {
                draw.AddRectFilled(p, p + new Vector2(Math.Max(barHeight, width * (float)fraction), barHeight), ImGui.ColorConvertFloat4ToU32(color), barHeight);
            }
            ImGui.Dummy(new Vector2(width, barHeight));

            // history
            if (tile.HistoryCount > 1)
            {
                float lo = (float)tile.Min.Value, hi = (float)tile.Max.Value;
                if (hi - lo < 1e-3f)
                {
                    lo -= 1;
                    hi += 1;
                }
                ImGui.PushStyleColor(ImGuiCol.PlotLines, color);
                ImGui.PushStyleColor(ImGuiCol.FrameBg, new Vector4(0, 0, 0, 0.15f));
                int offset = tile.HistoryCount < HistoryLength ? 0 : tile.HistoryHead;
                ImGui.PlotLines("##history", ref tile.History[0], tile.HistoryCount, offset, null, lo, hi, new Vector2(-1, Widgets.S(40)));
                ImGui.PopStyleColor(2);
                ImGui.TextDisabled($"min {FormatNumber(tile.Min.Value)}   max {FormatNumber(tile.Max.Value)}");
            }
            else
            {
                ImGui.Dummy(new Vector2(0, Widgets.S(40)));
                if (tile.Last?.Text != null)
                {
                    ImGui.TextDisabled(" ");
                }
                else
                {
                    ImGui.TextDisabled(App.Connected ? (Live ? "waiting for data..." : "press Start live") : "connect to read");
                }
            }

            if (ImGui.BeginPopup("cardmenu"))
            {
                if (tile.Choices.Count > 1)
                {
                    ImGui.TextDisabled("Read this from:");
                    foreach (var choice in tile.Choices.Take(40))
                    {
                        string label = $"{choice.Name}" + (choice.UnitText.Length > 0 ? $"  [{choice.UnitText}]" : "") + $"   ({choice.Service.Qualifier})";
                        if (ImGui.Selectable(label, choice == tile.Source))
                        {
                            ChooseSource(tile, choice);
                        }
                        Widgets.Tooltip(choice.Original);
                    }
                    ImGui.Separator();
                }
                if (ImGui.MenuItem("Copy value"))
                {
                    ImGui.SetClipboardText($"{name}: {value} {(tile.Last?.Number != null ? UnitLabel(tile) : "")}".Trim());
                }
                if (ImGui.MenuItem("Reset min / max"))
                {
                    tile.ResetHistory();
                }
                if (ImGui.MenuItem(tile.Vital != null ? "Hide from dashboard" : "Remove from dashboard"))
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

            ImGui.EndChild();
            return changed;
        }

        void DrawAddCard(float height)
        {
            ImGui.BeginChild("addcard", new Vector2(0, height), ImGuiChildFlags.Border, ImGuiWindowFlags.NoScrollbar);
            ImGui.TextDisabled("Something else?");
            ImGui.TextWrapped("Put any value this ECU can report on the dashboard.");
            if (ImGui.Button("+ Add a value...", new Vector2(-1, 0)))
            {
                AddSearch = "";
                ImGui.OpenPopup("addvalue");
            }
            ImGui.TextDisabled($"{Sources.Count} readable values in this CBF");

            ImGui.SetNextWindowSize(new Vector2(Widgets.S(700), Widgets.S(520)));
            if (ImGui.BeginPopup("addvalue"))
            {
                Widgets.Heading("Add a value to the dashboard");
                ImGui.SetNextItemWidth(-1);
                if (ImGui.IsWindowAppearing())
                {
                    ImGui.SetKeyboardFocusHere();
                }
                ImGui.InputTextWithHint("##addsearch", "Search, e.g. pressure, Drehzahl, lambda...", ref AddSearch, 128);
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
            ImGui.EndChild();
        }

        void DrawMissing()
        {
            var missingLive = Missing.Where(v => !v.Info).ToList();
            if (missingLive.Count == 0 && Hidden.Count == 0)
            {
                return;
            }
            if (missingLive.Count > 0 && ImGui.CollapsingHeader($"Not available from this ECU ({missingLive.Count})"))
            {
                ImGui.TextWrapped("Each CBF file describes one control unit, so it only knows its own readings. Open the CBF for the ECU listed to see these.");
                if (ImGui.BeginTable("missing", 2, ImGuiTableFlags.RowBg | ImGuiTableFlags.BordersInnerH | ImGuiTableFlags.SizingStretchProp))
                {
                    ImGui.TableSetupColumn("Reading", ImGuiTableColumnFlags.WidthFixed, Widgets.S(240));
                    ImGui.TableSetupColumn("Usually found in", ImGuiTableColumnFlags.WidthStretch);
                    ImGui.TableHeadersRow();
                    foreach (var v in missingLive)
                    {
                        ImGui.TableNextRow();
                        ImGui.TableNextColumn();
                        ImGui.TextUnformatted(v.Label);
                        ImGui.TableNextColumn();
                        ImGui.TextDisabled(v.Usually);
                    }
                    ImGui.EndTable();
                }
            }
            if (Hidden.Count > 0 && ImGui.CollapsingHeader($"Hidden ({Hidden.Count})"))
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
