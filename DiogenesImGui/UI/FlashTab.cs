using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Numerics;
using Caesar;
using CaesarConnection.Target;
using Diogenes.Translation;
using ImGuiNET;

namespace Diogenes.UI
{
    // Replaces Forms/FlashView
    public class FlashTab
    {
        readonly App App;
        static DiogenesSharedContext Ctx => DiogenesSharedContext.Singleton;

        readonly List<FlashItem> FlashItems = new List<FlashItem>();
        List<SoftwareBlock> EcuBlocks = new List<SoftwareBlock>();
        int SelectedItem = -1;

        volatile float Progress = 0;
        volatile string ProgressText = "";

        public FlashTab(App app)
        {
            App = app;
        }

        public void Draw()
        {
            Widgets.Colored(Theme.Bad, "Flashing replaces the ECU's software. If it is interrupted the ECU can stop working. This feature is experimental and only works on UDS ECUs.");
            ImGui.Spacing();

            // ---------------- files to flash
            Widgets.Heading("1. Flash files (CFF)");
            Widgets.Help("CFF files contain the new software. The ECU name inside each file must match the loaded CBF.");
            if (ImGui.Button("Add CFF files..."))
            {
                AddFiles();
            }
            ImGui.SameLine();
            if (Widgets.Button("Remove selected", SelectedItem >= 0 && !App.Busy))
            {
                FlashItems.RemoveAt(SelectedItem);
                SelectedItem = -1;
            }

            float tableHeight = Math.Min(Widgets.S(200), ImGui.GetTextLineHeightWithSpacing() * (FlashItems.Count + 2) + 8);
            if (ImGui.BeginTable("flashfiles", 6, ImGuiTableFlags.RowBg | ImGuiTableFlags.BordersInnerH | ImGuiTableFlags.ScrollY, new Vector2(0, tableHeight)))
            {
                ImGui.TableSetupColumn("Name", ImGuiTableColumnFlags.WidthStretch);
                ImGui.TableSetupColumn("For ECU");
                ImGui.TableSetupColumn("Order");
                ImGui.TableSetupColumn("Blocks");
                ImGui.TableSetupColumn("Segments");
                ImGui.TableSetupColumn("Matches");
                ImGui.TableHeadersRow();
                for (int i = 0; i < FlashItems.Count; i++)
                {
                    var item = FlashItems[i];
                    bool matches = App.CbfLoaded && item.AllowedECU == Ctx.PrimaryEcu.Qualifier;
                    ImGui.TableNextRow();
                    ImGui.TableNextColumn();
                    if (ImGui.Selectable($"{item.Name}##{i}", i == SelectedItem, ImGuiSelectableFlags.SpanAllColumns))
                    {
                        SelectedItem = i;
                    }
                    ImGui.TableNextColumn();
                    ImGui.TextUnformatted(item.AllowedECU);
                    ImGui.TableNextColumn();
                    ImGui.TextUnformatted(item.Priority.ToString());
                    ImGui.TableNextColumn();
                    ImGui.TextUnformatted(item.Blocks.ToString());
                    ImGui.TableNextColumn();
                    ImGui.TextUnformatted(item.Segments.ToString());
                    ImGui.TableNextColumn();
                    Widgets.Colored(matches ? Theme.Good : Theme.Bad, matches ? "yes" : "no");
                }
                ImGui.EndTable();
            }

            // ---------------- ECU state
            ImGui.Spacing();
            Widgets.Heading("2. Software currently in the ECU (optional)");
            if (Widgets.Button("Ask the ECU", App.Connected && !App.Busy, App.Connected ? "Busy" : "Connect to the car first"))
            {
                QueryBlocks();
            }
            if (EcuBlocks.Count > 0 && ImGui.BeginTable("ecublocks", 5, ImGuiTableFlags.RowBg | ImGuiTableFlags.BordersInnerH))
            {
                ImGui.TableSetupColumn("#");
                ImGui.TableSetupColumn("Part number", ImGuiTableColumnFlags.WidthStretch);
                ImGui.TableSetupColumn("Version");
                ImGui.TableSetupColumn("Valid");
                ImGui.TableSetupColumn("Flashed on");
                ImGui.TableHeadersRow();
                foreach (var block in EcuBlocks)
                {
                    ImGui.TableNextRow();
                    ImGui.TableNextColumn();
                    ImGui.TextUnformatted(block.Index.ToString());
                    ImGui.TableNextColumn();
                    ImGui.TextUnformatted(block.PartNumber ?? "");
                    ImGui.TableNextColumn();
                    ImGui.TextUnformatted(block.VersionString ?? "");
                    ImGui.TableNextColumn();
                    Widgets.Colored(block.Valid ? Theme.Good : Theme.Bad, block.Valid ? "yes" : "no");
                    ImGui.TableNextColumn();
                    ImGui.TextUnformatted($"{block.FlashYear:D2}-{block.FlashMonth:D2}-{block.FlashDay:D2}");
                }
                ImGui.EndTable();
            }

            // ---------------- flash
            ImGui.Spacing();
            Widgets.Heading("3. Flash");
            bool ready = App.Connected && !App.Busy && FlashItems.Count > 0 && Ctx.PrimaryVariant != null;
            string why = !App.Connected ? "Connect to the car first"
                : Ctx.PrimaryVariant is null ? "The ECU version must be known (detected on connect)"
                : FlashItems.Count == 0 ? "Add at least one CFF file"
                : "Busy";
            if (Widgets.ColoredButton("Start flashing", Theme.Bad, new Vector2(Widgets.S(200), Widgets.S(36)), ready))
            {
                App.Confirm("Flash the ECU?",
                    "Flashing is experimental and can fail. If it is interrupted the ECU may stop working.\n\n" +
                    "- Keep the battery charged (use a charger if you can)\n" +
                    "- Have another recovery tool ready (e.g. Vediamo)\n" +
                    "- Back up the coding first, some ECUs reset it after flashing\n\n" +
                    "Do you want to start?", StartFlash);
            }
            if (!ready)
            {
                Widgets.Tooltip(why);
                ImGui.SameLine();
                ImGui.TextDisabled(why);
            }
            if (ProgressText.Length > 0)
            {
                ImGui.ProgressBar(Progress, new Vector2(-1, 0), ProgressText);
            }
        }

        void AddFiles()
        {
            foreach (string file in FileDialogs.OpenFiles("Select CFF flash files", "CFF files (*.cff)|*.cff|All files (*.*)|*.*"))
            {
                try
                {
                    var container = new CaesarFlashContainer(File.ReadAllBytes(file));
                    if (FlashItems.Any(x => x.Container.ProvidedChecksum == container.ProvidedChecksum))
                    {
                        Console.WriteLine($"{Path.GetFileName(file)} is already in the list");
                        continue;
                    }
                    FlashItems.Add(new FlashItem(container));
                    Console.WriteLine($"Added {container.CaesarFlashHeader.FlashName}");
                }
                catch (Exception ex)
                {
                    App.ShowMessage("Could not add flash file", $"{Path.GetFileName(file)}: {ex.Message}");
                }
            }
            FlashItems.Sort((a, b) => a.Priority.CompareTo(b.Priority));
        }

        void QueryBlocks()
        {
            App.RunBackground("Asking the ECU for its software", () =>
            {
                var blocks = Ctx.Channel.GetSoftwareBlocks().ToList();
                App.OnMainThread(() => EcuBlocks = blocks);
            });
        }

        void StartFlash()
        {
            var items = FlashItems.ToList();
            App.RunBackground("Flashing", () => Flash(items));
        }

        // port of FlashView.Flash
        void Flash(List<FlashItem> items)
        {
            if (Ctx.Channel.GetType() != typeof(CaesarConnection.Protocol.UDS))
            {
                App.ShowMessage("Not supported", "Flashing currently only works on UDS ECUs.");
                return;
            }

            var toDownload = items.Where(x => x.AllowedECU == Ctx.PrimaryEcu.Qualifier).OrderBy(x => x.Priority).ToList();
            if (toDownload.Count == 0)
            {
                App.ShowMessage("No matching files", $"None of the CFF files are meant for this ECU ({Ctx.PrimaryEcu.Qualifier}).");
                return;
            }

            int totalSegments = toDownload.Sum(x => x.Segments);
            int completedSegments = 0;

            if (!Ctx.StartUserInitiatedRequest())
            {
                Console.WriteLine("Another request is being processed. Please try again after it has completed.");
                return;
            }

            // speed up the transfer -- the flash scripts don't do this themselves
            decimal priorStMin = Ctx.Channel.ComParameters.GetParameter(CaesarConnection.ComParam.CP.StMinOverride);
            Ctx.Channel.ComParameters.SetParameter(CaesarConnection.ComParam.CP.StMinOverride, 65535);
            Ctx.Channel.Channel.ReloadIsoTpTimings();

            try
            {
                foreach (var block in toDownload)
                {
                    var flashService = Ctx.PrimaryVariant.DiagServices.FirstOrDefault(x => x.Qualifier == block.Job);
                    if (flashService is null)
                    {
                        throw new Exception($"The flash command {block.Job} is not in this CBF file");
                    }

                    Console.WriteLine($"Running {flashService.Qualifier}");
                    var runner = new DiagServiceRunner(flashService);
                    runner.AttachFlashHost(block);
                    runner.FlashProgressChanged = () =>
                    {
                        var fh = runner.FlashHost;
                        int done = completedSegments + fh.SegmentsDownloaded;
                        Progress = (float)done / totalSegments;
                        ProgressText = $"{fh.BlockName} ({fh.SegmentName}) {fh.SegmentsDownloaded}/{fh.SegmentsTotal}  -  {(int)(Progress * 100)}%";
                    };
                    runner.DoDiagService();
                    completedSegments += runner.FlashHost.SegmentsTotal;
                }
                ProgressText = "Flash completed";
                Progress = 1;
            }
            finally
            {
                Ctx.Channel.ComParameters.SetParameter(CaesarConnection.ComParam.CP.StMinOverride, priorStMin);
                Ctx.Channel.Channel.ReloadIsoTpTimings();
                Ctx.EndUserInitiatedRequest();
            }
        }
    }
}
