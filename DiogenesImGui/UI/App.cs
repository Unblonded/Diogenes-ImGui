using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Numerics;
using System.Threading.Tasks;
using Caesar;
using CaesarConnection.VCI;
using Diogenes.Translation;
using ImGuiNET;

namespace Diogenes.UI
{
    // Main window. Replaces Forms/MainForm.
    public class App
    {
        static DiogenesSharedContext Ctx => DiogenesSharedContext.Singleton;
        static AppSettings Settings => AppSettings.Current;

        public readonly LogBuffer Log;
        public readonly LogBuffer Trace = new LogBuffer();

        readonly CommandsTab Commands;
        readonly CodingTab Coding;
        readonly FaultCodesTab FaultCodes;
        readonly MemoryTab Memory;
        readonly FlashTab Flash;

        BaseDevice[] Devices = new BaseDevice[] { };
        int SelectedDevice = -1;
        List<string> Interfaces = new List<string>();
        int SelectedInterface = -1;
        string LoadedCbfPath = null;

        // one background operation at a time (connecting, running a command, reading memory, flashing)
        volatile bool busy = false;
        string BusyLabel = "";
        readonly ConcurrentQueue<Action> MainThreadQueue = new ConcurrentQueue<Action>();

        // simple modal message box
        string MessageTitle = null;
        string MessageBody = null;
        bool OpenMessagePopup = false;

        // confirmation dialog
        string ConfirmTitle = null;
        string ConfirmBody = null;
        Action ConfirmAction = null;
        bool OpenConfirmPopup = false;

        bool PopupOpenDummy = true;
        bool ShowQuickStart = false;
        float LogHeight = -1;
        int LastLogVersion = -1;
        int LastTraceVersion = -1;
        string ConsoleInput = "";

        public bool Busy => busy;
        public bool Connected => Ctx.Channel != null;
        public bool CbfLoaded => Ctx.PrimaryContainer != null;

        public App(LogBuffer log, string commandLinePath)
        {
            Log = log;
            Commands = new CommandsTab(this);
            Coding = new CodingTab(this);
            FaultCodes = new FaultCodesTab(this);
            Memory = new MemoryTab(this);
            Flash = new FlashTab(this);

            Console.WriteLine("Diogenes II (ImGui edition). Start by opening a CBF file for the control unit you want to talk to.");
            RefreshDevices();

            if (commandLinePath == "--demo")
            {
                LoadContainer(DemoData.Create(), "(demo data)");
            }
            else if (!string.IsNullOrEmpty(commandLinePath))
            {
                LoadCbf(commandLinePath);
            }
            ShowQuickStart = !CbfLoaded && Settings.RecentCbfFiles.Count == 0;
        }

        public void Shutdown()
        {
            CloseConnection();
            Settings.Save();
        }

        // ---------------------------------------------------------------- background work

        public bool RunBackground(string label, Action work, Action onFinishedMainThread = null)
        {
            if (busy)
            {
                Console.WriteLine($"Please wait, '{BusyLabel}' is still running.");
                return false;
            }
            busy = true;
            BusyLabel = label;
            Task.Run(() =>
            {
                try
                {
                    work();
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"{label} failed: {ex.Message}");
                    ShowMessage($"{label} failed", ex.Message);
                }
                finally
                {
                    busy = false;
                    if (onFinishedMainThread != null)
                    {
                        MainThreadQueue.Enqueue(onFinishedMainThread);
                    }
                }
            });
            return true;
        }

        public void OnMainThread(Action action)
        {
            MainThreadQueue.Enqueue(action);
        }

        public void ShowMessage(string title, string body)
        {
            MainThreadQueue.Enqueue(() =>
            {
                MessageTitle = title;
                MessageBody = body;
                OpenMessagePopup = true;
            });
        }

        public void Confirm(string title, string body, Action onYes)
        {
            ConfirmTitle = title;
            ConfirmBody = body;
            ConfirmAction = onYes;
            OpenConfirmPopup = true;
        }

        // ---------------------------------------------------------------- CBF / connection logic

        public void OpenCbfDialog()
        {
            string path = FileDialogs.OpenFile("Select a CBF file (one file per control unit)", "CBF files (*.cbf)|*.cbf|All files (*.*)|*.*");
            if (path != null)
            {
                LoadCbf(path);
            }
        }

        public void LoadCbf(string path)
        {
            if (!File.Exists(path))
            {
                Console.WriteLine($"CBF file could not be found at {path}");
                Settings.RemoveRecentFile(path);
                return;
            }
            UnloadCbf();
            try
            {
                if (!Ctx.TryLoadCBF(path))
                {
                    ShowMessage("Could not open file", "This file failed its checksum test, so it is probably damaged or not a CBF file.");
                    return;
                }
            }
            catch (Exception ex)
            {
                Ctx.PrimaryContainer = null;
                ShowMessage("Could not open file", $"The file could not be read as a CBF file:\n{ex.Message}");
                return;
            }

            Settings.AddRecentFile(path);
            LoadContainer(Ctx.PrimaryContainer, path);
        }

        void LoadContainer(CaesarContainer container, string path)
        {
            Ctx.PrimaryContainer = container;
            LoadedCbfPath = path;
            Tr.SetContainer(Ctx.PrimaryContainer);
            RefreshInterfaces();

            var ecu = Ctx.PrimaryEcu;
            Console.WriteLine($"Loaded {Path.GetFileName(path)}: {ecu.Qualifier} - {Tr.Ctf(ecu.EcuDescription_CTF)} ({ecu.ECUVariants.Count} versions, {ecu.GlobalDiagServices.Count} commands)");
            ShowQuickStart = false;
            NotifyCbfOrVariantChange();
        }

        public void UnloadCbf()
        {
            CloseConnection();
            Ctx.PreConnectParameters.Clear();
            Ctx.PrimaryContainer = null;
            LoadedCbfPath = null;
            Tr.SetContainer(null);
            RefreshInterfaces();
            NotifyCbfOrVariantChange();
        }

        void RefreshDevices()
        {
            try
            {
                Devices = CaesarConnection.Connection.GetDevices();
            }
            catch (Exception ex)
            {
                Devices = new BaseDevice[] { };
                Console.WriteLine($"Could not list adapters: {ex.Message}");
            }
            SelectedDevice = Array.FindIndex(Devices, d => d.GetPersistentHash() == Settings.LastSelectedDevice);
            if (SelectedDevice < 0 && Devices.Length > 0)
            {
                SelectedDevice = 0;
            }
        }

        void RefreshInterfaces()
        {
            Interfaces.Clear();
            SelectedInterface = -1;
            if (!CbfLoaded)
            {
                return;
            }
            Interfaces.AddRange(Ctx.PrimaryEcu.ECUInterfaceSubtypes.Select(x => x.Qualifier));
            if (Interfaces.Count > 0)
            {
                SelectInterface(0);
            }
        }

        void SelectInterface(int index)
        {
            SelectedInterface = index;
            Ctx.PreConnectParameters.Clear();
            var iface = Ctx.PrimaryEcu.ECUInterfaceSubtypes.FirstOrDefault(x => x.Qualifier == Interfaces[index]);
            if (iface is null)
            {
                return;
            }
            var comParams = iface.CommunicationParameters.ToDictionary(t => t.ParamName, t => t.ComParamValue);

            // some ECUs need hand-tuned parameters, these override the CBF values
            foreach (var quirk in ComParamQuirks.GetQuirksForTarget(Ctx.PrimaryEcu.Qualifier, iface.Qualifier))
            {
                comParams[quirk.Key] = quirk.Value;
            }
            foreach (var kv in comParams)
            {
                Ctx.PreConnectParameters.Add(new DiogenesSharedContext.RawComParam { Name = kv.Key, Value = kv.Value });
            }
        }

        void Connect()
        {
            if (!CbfLoaded)
            {
                ShowMessage("Open a CBF file first", "Step 1: open the CBF file for the control unit you want to talk to.");
                return;
            }
            if (SelectedDevice < 0 || SelectedDevice >= Devices.Length)
            {
                ShowMessage("No adapter selected", "Step 2: plug in your J2534 adapter, install its driver, then press Refresh.");
                return;
            }
            if (SelectedInterface < 0)
            {
                ShowMessage("No connection type", "This CBF file does not list any connection types.");
                return;
            }

            var ecu = Ctx.PrimaryEcu;
            var iface = ecu.ECUInterfaceSubtypes.First(x => x.Qualifier == Interfaces[SelectedInterface]);
            string physicalProtocol = $"{iface.PhysicalProtocol}";
            string diagProtocol = ecu.ECUInterfaces[iface.ParentInterfaceIndex].Qualifier;
            var device = Devices[SelectedDevice];
            var comParams = Ctx.PreConnectParameters.ToDictionary(x => x.Name, x => x.Value);
            bool identify = Settings.AutoIdentifyVariant;

            RunBackground("Connecting", () =>
            {
                try
                {
                    Ctx.Connection = CaesarConnection.Connection.Create(device);
                    Ctx.Channel = Ctx.Connection.OpenChannel(physicalProtocol, diagProtocol, comParams, Trace);
                    Console.WriteLine($"Connected to {ecu.Qualifier} using {device.Name} ({physicalProtocol} / {diagProtocol})");
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"Failed to connect: {ex.Message}");
                    CloseConnection();
                    ShowMessage("Could not connect", $"{ex.Message}\n\nCheck that the adapter is plugged into the car, the ignition is on, and the right connection type is selected.");
                    return;
                }
                if (identify)
                {
                    IdentifyEcuVariant();
                }
            }, NotifyCbfOrVariantChange);
        }

        void IdentifyEcuVariant()
        {
            try
            {
                int variantId = Ctx.Channel.GetEcuVariant();
                Console.WriteLine($"ECU reports version ID {variantId:X4}");
                var match = Ctx.PrimaryEcu.ECUVariants.FirstOrDefault(v => v.VariantPatterns.Any(p => (p.VariantID & 0xFFFF) == variantId));
                if (match is null)
                {
                    Console.WriteLine($"No version in this CBF matches {variantId:X4}. You can pick one manually under Settings.");
                }
                else
                {
                    Console.WriteLine($"Identified ECU version: {match.Qualifier}");
                }
                Ctx.PrimaryVariant = match;
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Could not identify the ECU version: {ex.Message}");
                Ctx.PrimaryVariant = null;
            }
        }

        public void CloseConnection()
        {
            bool wasConnected = Ctx.Channel != null;
            Ctx.PrimaryVariant = null;
            try
            {
                Ctx.Channel?.Dispose();
                Ctx.Connection?.Dispose();
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Error while disconnecting: {ex.Message}");
            }
            Ctx.Channel = null;
            Ctx.Connection = null;
            if (wasConnected)
            {
                Console.WriteLine("Disconnected");
                OnMainThread(NotifyCbfOrVariantChange);
            }
        }

        public void SetVariant(ECUVariant variant)
        {
            Ctx.PrimaryVariant = variant;
            NotifyCbfOrVariantChange();
        }

        void NotifyCbfOrVariantChange()
        {
            Commands.NotifyCbfOrVariantChange();
            Coding.NotifyCbfOrVariantChange();
            FaultCodes.NotifyCbfOrVariantChange();
        }

        public static string VariantName(ECUVariant v)
        {
            if (v is null)
            {
                return "";
            }
            string name = Tr.Ctf(v.Name_CTF, Tr.Kind.Title);
            return string.IsNullOrWhiteSpace(name) ? v.Qualifier : $"{name} ({v.Qualifier})";
        }

        // ---------------------------------------------------------------- drawing

        public void Draw()
        {
            while (MainThreadQueue.TryDequeue(out var action))
            {
                action();
            }

            ImGui.GetIO().FontGlobalScale = Settings.FontSize / 18f;

            var viewport = ImGui.GetMainViewport();
            ImGui.SetNextWindowPos(viewport.WorkPos);
            ImGui.SetNextWindowSize(viewport.WorkSize);
            var flags = ImGuiWindowFlags.NoDecoration | ImGuiWindowFlags.NoMove | ImGuiWindowFlags.NoResize |
                        ImGuiWindowFlags.NoSavedSettings | ImGuiWindowFlags.MenuBar | ImGuiWindowFlags.NoBringToFrontOnFocus;
            ImGui.Begin("Diogenes", flags);

            DrawMenuBar();
            DrawSetupBar();
            ImGui.Spacing();

            if (LogHeight < 0)
            {
                LogHeight = Widgets.S(220);
            }
            float splitterHeight = Widgets.S(6);
            float workHeight = ImGui.GetContentRegionAvail().Y - LogHeight - splitterHeight;
            ImGui.BeginChild("work", new Vector2(0, Math.Max(Widgets.S(120), workHeight)), ImGuiChildFlags.None, ImGuiWindowFlags.NoScrollbar);
            if (ShowQuickStart || !CbfLoaded)
            {
                DrawQuickStart();
            }
            else
            {
                DrawTabs();
            }
            ImGui.EndChild();

            // drag handle between the work area and the log
            ImGui.InvisibleButton("splitter", new Vector2(-1, splitterHeight));
            if (ImGui.IsItemHovered() || ImGui.IsItemActive())
            {
                ImGui.SetMouseCursor(ImGuiMouseCursor.ResizeNS);
            }
            if (ImGui.IsItemActive())
            {
                LogHeight = Math.Clamp(LogHeight - ImGui.GetIO().MouseDelta.Y, Widgets.S(80), viewport.WorkSize.Y - Widgets.S(250));
            }

            DrawLogPanel();
            ImGui.End();

            DrawPopups();
        }

        void DrawMenuBar()
        {
            if (!ImGui.BeginMenuBar())
            {
                return;
            }
            if (ImGui.BeginMenu("File"))
            {
                if (ImGui.MenuItem("Open CBF file...", "Ctrl+O"))
                {
                    OpenCbfDialog();
                }
                if (ImGui.BeginMenu("Open recent", Settings.RecentCbfFiles.Count > 0))
                {
                    foreach (string recent in Settings.RecentCbfFiles.ToList())
                    {
                        if (ImGui.MenuItem(recent))
                        {
                            LoadCbf(recent);
                        }
                    }
                    ImGui.Separator();
                    if (ImGui.MenuItem("Clear recent files"))
                    {
                        Settings.RecentCbfFiles.Clear();
                        Settings.Save();
                    }
                    ImGui.EndMenu();
                }
                if (ImGui.MenuItem("Close CBF file", null, false, CbfLoaded))
                {
                    UnloadCbf();
                }
                ImGui.Separator();
                if (ImGui.MenuItem("Open program folder"))
                {
                    System.Diagnostics.Process.Start("explorer.exe", AppContext.BaseDirectory);
                }
                if (ImGui.MenuItem("Exit"))
                {
                    Environment.Exit(0);
                }
                ImGui.EndMenu();
            }
            if (ImGui.BeginMenu("View"))
            {
                bool translate = Settings.TranslateToEnglish;
                if (ImGui.MenuItem("Translate German to English", null, ref translate))
                {
                    Settings.TranslateToEnglish = translate;
                    Settings.Save();
                    NotifyCbfOrVariantChange();
                }
                bool online = Settings.UseOnlineTranslation;
                if (ImGui.MenuItem("Use online translation (better quality)", null, ref online, Settings.TranslateToEnglish))
                {
                    Settings.UseOnlineTranslation = online;
                    Settings.Save();
                    NotifyCbfOrVariantChange();
                }
                bool originals = Settings.ShowOriginalNames;
                if (ImGui.MenuItem("Show original German names", null, ref originals))
                {
                    Settings.ShowOriginalNames = originals;
                    Settings.Save();
                }
                ImGui.Separator();
                bool advanced = Settings.AdvancedMode;
                if (ImGui.MenuItem("Advanced mode (raw bytes, bit positions, extra tabs)", null, ref advanced))
                {
                    Settings.AdvancedMode = advanced;
                    Settings.Save();
                }
                ImGui.Separator();
                float size = Settings.FontSize;
                ImGui.SetNextItemWidth(Widgets.S(160));
                if (ImGui.SliderFloat("Text size", ref size, 13f, 28f, "%.0f"))
                {
                    Settings.FontSize = size;
                }
                if (ImGui.IsItemDeactivatedAfterEdit())
                {
                    Settings.Save();
                }
                ImGui.EndMenu();
            }
            if (ImGui.BeginMenu("Help"))
            {
                if (ImGui.MenuItem("Quick start guide"))
                {
                    ShowQuickStart = true;
                }
                ImGui.EndMenu();
            }

            if (ImGui.IsKeyChordPressed(ImGuiKey.ModCtrl | ImGuiKey.O))
            {
                OpenCbfDialog();
            }

            // right side: translation status
            if (Settings.TranslateToEnglish)
            {
                string status = Tr.HasOfficialEnglish ? "English text from CBF"
                    : !Settings.UseOnlineTranslation ? "Translating (offline dictionary)"
                    : OnlineTranslator.Unavailable ? "Translating (offline - online unavailable)"
                    : OnlineTranslator.PendingCount > 0 ? $"Translating... {OnlineTranslator.PendingCount} left"
                    : "Translated to English";
                float w = ImGui.CalcTextSize(status).X + Widgets.S(20);
                ImGui.SameLine(ImGui.GetWindowWidth() - w);
                ImGui.TextDisabled(status);
            }
            ImGui.EndMenuBar();
        }

        void DrawSetupBar()
        {
            float cardHeight = ImGui.GetTextLineHeightWithSpacing() * 4.2f;
            if (!ImGui.BeginTable("setup", 3, ImGuiTableFlags.SizingStretchSame | ImGuiTableFlags.BordersInnerV))
            {
                return;
            }

            // step 1: CBF
            ImGui.TableNextColumn();
            ImGui.BeginChild("step1", new Vector2(0, cardHeight));
            StepTitle(1, "Control unit file (CBF)", CbfLoaded);
            Widgets.Help("A CBF file describes one control unit (ECU) in the car: which commands it understands and how to read its answers. You need the one that matches the ECU you want to talk to (e.g. IC_204.CBF for the instrument cluster).");
            if (CbfLoaded)
            {
                var ecu = Ctx.PrimaryEcu;
                // some CBFs put long technical notes in the description, only show its first line here
                string description = Tr.Ctf(ecu.EcuDescription_CTF, Tr.Kind.Sentence);
                string firstLine = description.Split('\n')[0].Trim();
                ImGui.TextUnformatted(ecu.Qualifier + (firstLine.Length == 0 ? "" : $"  -  {firstLine}"));
                Widgets.Tooltip($"{LoadedCbfPath}\n\n{description}");
                if (ImGui.Button("Open a different file..."))
                {
                    OpenCbfDialog();
                }
            }
            else
            {
                if (Widgets.ColoredButton("Open CBF file...", Theme.Accent))
                {
                    OpenCbfDialog();
                }
            }
            ImGui.EndChild();

            // step 2: adapter
            ImGui.TableNextColumn();
            ImGui.BeginChild("step2", new Vector2(0, cardHeight));
            StepTitle(2, "Adapter", SelectedDevice >= 0);
            Widgets.Help("The interface cable between this PC and the car's OBD port. Any J2534 pass-thru device should work (e.g. OpenPort 2.0). Its driver must be installed.");
            ImGui.BeginDisabled(Connected || busy);
            if (Devices.Length == 0)
            {
                Widgets.Colored(Theme.Warn, "No J2534 adapter found. Install its driver, plug it in, then refresh.");
            }
            else
            {
                ImGui.SetNextItemWidth(Widgets.S(-90));
                string preview = SelectedDevice >= 0 ? Devices[SelectedDevice].Name : "Choose...";
                if (ImGui.BeginCombo("##device", preview))
                {
                    for (int i = 0; i < Devices.Length; i++)
                    {
                        if (ImGui.Selectable(Devices[i].Name, i == SelectedDevice))
                        {
                            SelectedDevice = i;
                            Settings.LastSelectedDevice = Devices[i].GetPersistentHash();
                            Settings.Save();
                        }
                    }
                    ImGui.EndCombo();
                }
                ImGui.SameLine();
            }
            if (ImGui.Button("Refresh"))
            {
                RefreshDevices();
            }
            ImGui.EndDisabled();
            ImGui.EndChild();

            // step 3: connect
            ImGui.TableNextColumn();
            ImGui.BeginChild("step3", new Vector2(0, cardHeight));
            StepTitle(3, "Connect to the car", Connected);
            Widgets.Help("Turn the ignition on, then connect. 'Connection type' comes from the CBF file; the first one is usually right.");
            ImGui.BeginDisabled(Connected || busy || !CbfLoaded);
            ImGui.SetNextItemWidth(Widgets.S(-130));
            if (ImGui.BeginCombo("##iface", SelectedInterface >= 0 ? Interfaces[SelectedInterface] : "Connection type"))
            {
                for (int i = 0; i < Interfaces.Count; i++)
                {
                    if (ImGui.Selectable(Interfaces[i], i == SelectedInterface))
                    {
                        SelectInterface(i);
                    }
                }
                ImGui.EndCombo();
            }
            Widgets.Tooltip("Connection type (bus and protocol) from the CBF file");
            ImGui.EndDisabled();
            ImGui.SameLine();
            if (Connected)
            {
                if (Widgets.ColoredButton("Disconnect", Theme.Bad, new Vector2(-1, 0), !busy))
                {
                    CloseConnection();
                }
            }
            else
            {
                if (Widgets.ColoredButton("Connect", Theme.Good, new Vector2(-1, 0), !busy && CbfLoaded && SelectedDevice >= 0))
                {
                    Connect();
                }
            }

            if (busy)
            {
                Widgets.StatusDot(Theme.Warn);
                ImGui.TextUnformatted($"{BusyLabel}...");
            }
            else if (Connected)
            {
                Widgets.StatusDot(Theme.Good);
                string variant = Ctx.PrimaryVariant is null ? "version unknown" : VariantName(Ctx.PrimaryVariant);
                ImGui.TextUnformatted($"Connected  |  {variant}");
                Widgets.Tooltip("The 'version' (variant) is the exact software version of the ECU. Commands for the right version are shown when it is known.");
            }
            else
            {
                Widgets.StatusDot(Theme.Dim);
                ImGui.TextDisabled("Not connected");
            }
            ImGui.EndChild();

            ImGui.EndTable();
        }

        static void StepTitle(int number, string title, bool done)
        {
            Widgets.StatusDot(done ? Theme.Good : Theme.Dim);
            ImGui.PushStyleColor(ImGuiCol.Text, done ? Theme.Good : Theme.Accent);
            ImGui.TextUnformatted($"Step {number}: {title}");
            ImGui.PopStyleColor();
        }

        void DrawQuickStart()
        {
            ImGui.Spacing();
            Widgets.BigText("Welcome to Diogenes", 1.6f);
            ImGui.Spacing();
            ImGui.TextWrapped("This tool talks to Mercedes-Benz control units (ECUs) through a J2534 adapter, using the ECU's CBF file. Everything shown from the CBF file is translated from German to English automatically.");
            ImGui.Spacing();
            Widgets.Heading("How to use it");
            ImGui.BulletText("Step 1 - open the CBF file for the ECU you want (top left).");
            ImGui.BulletText("Step 2 - pick your adapter (top middle).");
            ImGui.BulletText("Step 3 - switch the ignition on and press Connect (top right).");
            ImGui.BulletText("Then use the Commands tab: search for what you want, pick it, and press Run.");
            ImGui.Spacing();
            Widgets.Heading("Good to know");
            ImGui.BulletText("You can browse a CBF file without a car connected: commands, coding options and fault codes are all visible.");
            ImGui.BulletText("Green commands only read data. Yellow/red ones change something in the car and will ask before running.");
            ImGui.BulletText("View > Show original German names shows the untranslated names next to the English ones.");
            ImGui.BulletText("View > Advanced mode shows raw bytes, bit positions, connection parameters and a raw command console.");
            ImGui.Spacing();

            if (Settings.RecentCbfFiles.Count > 0)
            {
                Widgets.Heading("Recently opened");
                foreach (string recent in Settings.RecentCbfFiles.Take(6).ToList())
                {
                    if (ImGui.Selectable($"{Path.GetFileName(recent)}   ({Path.GetDirectoryName(recent)})"))
                    {
                        LoadCbf(recent);
                    }
                }
                ImGui.Spacing();
            }

            if (Widgets.ColoredButton("Open CBF file...", Theme.Accent, new Vector2(Widgets.S(220), 0)))
            {
                OpenCbfDialog();
            }
            if (CbfLoaded)
            {
                ImGui.SameLine();
                if (ImGui.Button("Close this guide", new Vector2(Widgets.S(220), 0)))
                {
                    ShowQuickStart = false;
                }
            }
        }

        void DrawTabs()
        {
            if (!ImGui.BeginTabBar("tabs"))
            {
                return;
            }
            TabItem(0, "Commands", Commands.Draw);
            TabItem(1, "Coding", Coding.Draw);
            TabItem(2, "Fault codes", FaultCodes.Draw);
            TabItem(3, "Memory", Memory.Draw);
            TabItem(4, "Flash", Flash.Draw);
            if (Settings.AdvancedMode)
            {
                TabItem(5, "Settings", DrawSettingsTab);
            }
            ImGui.EndTabBar();
        }

        void TabItem(int index, string label, Action draw)
        {
            if (ImGui.BeginTabItem(label))
            {
                ImGui.BeginChild($"tab{index}", Vector2.Zero);
                draw();
                ImGui.EndChild();
                ImGui.EndTabItem();
            }
        }

        void DrawSettingsTab()
        {
            Widgets.Heading("ECU version (variant)");
            Widgets.Help("Normally detected automatically when connecting. If detection fails, you can pick it here so the matching commands and coding are shown.");
            bool auto = Settings.AutoIdentifyVariant;
            if (ImGui.Checkbox("Detect the ECU version automatically when connecting", ref auto))
            {
                Settings.AutoIdentifyVariant = auto;
                Settings.Save();
            }
            ImGui.SetNextItemWidth(Widgets.S(500));
            string current = Ctx.PrimaryVariant is null ? "(none)" : VariantName(Ctx.PrimaryVariant);
            if (ImGui.BeginCombo("Version", current))
            {
                if (ImGui.Selectable("(none)", Ctx.PrimaryVariant is null))
                {
                    SetVariant(null);
                }
                foreach (var variant in Ctx.PrimaryEcu.ECUVariants)
                {
                    if (ImGui.Selectable(VariantName(variant), variant == Ctx.PrimaryVariant))
                    {
                        SetVariant(variant);
                    }
                }
                ImGui.EndCombo();
            }

            ImGui.Spacing();
            Widgets.Heading("Connection parameters");
            Widgets.Help("Timing and addressing values from the CBF file, used when connecting. Only change these if you know why.");
            ImGui.BeginDisabled(Connected);
            if (ImGui.BeginTable("comparams", 2, ImGuiTableFlags.RowBg | ImGuiTableFlags.BordersInnerH | ImGuiTableFlags.ScrollY, new Vector2(0, Widgets.S(300))))
            {
                ImGui.TableSetupColumn("Parameter", ImGuiTableColumnFlags.WidthStretch);
                ImGui.TableSetupColumn("Value", ImGuiTableColumnFlags.WidthFixed, Widgets.S(160));
                ImGui.TableSetupScrollFreeze(0, 1);
                ImGui.TableHeadersRow();
                for (int i = 0; i < Ctx.PreConnectParameters.Count; i++)
                {
                    var p = Ctx.PreConnectParameters[i];
                    ImGui.TableNextRow();
                    ImGui.TableNextColumn();
                    ImGui.TextUnformatted(p.Name);
                    ImGui.TableNextColumn();
                    int value = p.Value;
                    ImGui.SetNextItemWidth(Widgets.S(-1));
                    if (ImGui.InputInt($"##cp{i}", ref value, 0))
                    {
                        p.Value = value;
                    }
                }
                ImGui.EndTable();
            }
            ImGui.EndDisabled();
            if (Connected)
            {
                ImGui.TextDisabled("Disconnect to change connection parameters.");
            }
        }

        void DrawLogPanel()
        {
            ImGui.BeginChild("log", Vector2.Zero, ImGuiChildFlags.Border);
            if (ImGui.BeginTabBar("logtabs"))
            {
                if (ImGui.BeginTabItem("Messages"))
                {
                    DrawLogLines(Log, ref LastLogVersion, translate: true);
                    ImGui.EndTabItem();
                }
                if (Settings.AdvancedMode && ImGui.BeginTabItem("Bus trace"))
                {
                    DrawLogLines(Trace, ref LastTraceVersion, translate: false);
                    ImGui.EndTabItem();
                }
                ImGui.EndTabBar();
            }
            ImGui.EndChild();
        }

        void DrawLogLines(LogBuffer buffer, ref int lastVersion, bool translate)
        {
            bool showConsole = Settings.AdvancedMode && buffer == Log;
            float footer = showConsole ? ImGui.GetFrameHeightWithSpacing() : 0;

            if (ImGui.SmallButton("Copy all"))
            {
                ImGui.SetClipboardText(string.Join(Environment.NewLine, buffer.Snapshot()));
            }
            ImGui.SameLine();
            if (ImGui.SmallButton("Clear"))
            {
                buffer.Clear();
            }

            ImGui.BeginChild("lines", new Vector2(0, -footer), ImGuiChildFlags.None, ImGuiWindowFlags.HorizontalScrollbar);
            var lines = buffer.Snapshot();
            unsafe
            {
                var clipper = new ImGuiListClipperPtr(ImGuiNative.ImGuiListClipper_ImGuiListClipper());
                clipper.Begin(lines.Count);
                while (clipper.Step())
                {
                    for (int i = clipper.DisplayStart; i < clipper.DisplayEnd; i++)
                    {
                        string line = lines[i];
                        if (translate && Settings.TranslateToEnglish && GermanText.LooksGerman(line))
                        {
                            string english = Tr.Text(line);
                            ImGui.TextUnformatted(english);
                            Widgets.Tooltip($"Original: {line}");
                        }
                        else
                        {
                            ImGui.TextUnformatted(line);
                        }
                    }
                }
                clipper.End();
                clipper.Destroy();
            }
            if (buffer.Version != lastVersion)
            {
                lastVersion = buffer.Version;
                ImGui.SetScrollHereY(1.0f);
            }
            ImGui.EndChild();

            if (showConsole)
            {
                ImGui.SetNextItemWidth(Widgets.S(-1));
                bool enterPressed = ImGui.InputTextWithHint("##console", Connected ? "Send raw bytes to the ECU: type hex like 22 F1 90 and press Enter" : "Raw command console (connect first)", ref ConsoleInput, 512, ImGuiInputTextFlags.EnterReturnsTrue);
                if (enterPressed && Connected)
                {
                    string text = ConsoleInput;
                    ConsoleInput = "";
                    ImGui.SetKeyboardFocusHere(-1);
                    if (BitUtility.TryParseHex(text, out byte[] request))
                    {
                        RunBackground("Sending", () =>
                        {
                            Console.WriteLine($"REQ: {BitUtility.BytesToHex(request, true)}");
                            byte[] response = Ctx.Channel.Send(request, true);
                            Console.WriteLine($"ECU: {BitUtility.BytesToHex(response, true)}");
                        });
                    }
                    else
                    {
                        Console.WriteLine($"Could not understand '{text}' as hex bytes");
                    }
                }
            }
        }

        void DrawPopups()
        {
            if (OpenMessagePopup)
            {
                ImGui.OpenPopup("##message");
                OpenMessagePopup = false;
            }
            ImGui.SetNextWindowSize(new Vector2(Widgets.S(520), 0));
            if (ImGui.BeginPopupModal("##message", ref PopupOpenDummy, ImGuiWindowFlags.AlwaysAutoResize | ImGuiWindowFlags.NoTitleBar))
            {
                Widgets.BigText(MessageTitle ?? "", 1.2f);
                ImGui.Separator();
                ImGui.TextWrapped(MessageBody ?? "");
                ImGui.Spacing();
                if (ImGui.Button("OK", new Vector2(Widgets.S(120), 0)) || ImGui.IsKeyPressed(ImGuiKey.Enter) || ImGui.IsKeyPressed(ImGuiKey.Escape))
                {
                    ImGui.CloseCurrentPopup();
                }
                ImGui.EndPopup();
            }

            if (OpenConfirmPopup)
            {
                ImGui.OpenPopup("##confirm");
                OpenConfirmPopup = false;
            }
            ImGui.SetNextWindowSize(new Vector2(Widgets.S(560), 0));
            if (ImGui.BeginPopupModal("##confirm", ref PopupOpenDummy, ImGuiWindowFlags.AlwaysAutoResize | ImGuiWindowFlags.NoTitleBar))
            {
                Widgets.BigText(ConfirmTitle ?? "", 1.2f);
                ImGui.Separator();
                ImGui.TextWrapped(ConfirmBody ?? "");
                ImGui.Spacing();
                if (Widgets.ColoredButton("Yes, continue", Theme.Warn, new Vector2(Widgets.S(160), 0)))
                {
                    ImGui.CloseCurrentPopup();
                    var action = ConfirmAction;
                    ConfirmAction = null;
                    action?.Invoke();
                }
                ImGui.SameLine();
                if (ImGui.Button("Cancel", new Vector2(Widgets.S(120), 0)) || ImGui.IsKeyPressed(ImGuiKey.Escape))
                {
                    ConfirmAction = null;
                    ImGui.CloseCurrentPopup();
                }
                ImGui.EndPopup();
            }
        }
    }
}
