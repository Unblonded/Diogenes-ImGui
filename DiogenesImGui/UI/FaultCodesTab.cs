using System;
using System.Collections.Generic;
using System.Linq;
using Caesar;
using Diogenes.Translation;
using ImGuiNET;

namespace Diogenes.UI
{
    // New: a searchable list of the fault codes defined in the CBF, with English descriptions.
    // Useful to look up what a code means. It does not read fault memory from the car.
    public class FaultCodesTab
    {
        class Row
        {
            public string Code;
            public string Meaning;
            public string Original;
        }

        readonly App App;
        static DiogenesSharedContext Ctx => DiogenesSharedContext.Singleton;
        static AppSettings Settings => AppSettings.Current;

        List<DTC> Codes = new List<DTC>();
        List<Row> Visible = new List<Row>();
        string Search = "";
        bool Dirty = true;
        int BuiltTranslationVersion = -1;
        DateTime LastBuild = DateTime.MinValue;

        public FaultCodesTab(App app)
        {
            App = app;
        }

        public void NotifyCbfOrVariantChange()
        {
            Codes.Clear();
            Dirty = true;
            if (!App.CbfLoaded)
            {
                return;
            }
            var source = Ctx.PrimaryVariant != null && Ctx.PrimaryVariant.DTCs.Length > 0
                ? Ctx.PrimaryVariant.DTCs
                : Ctx.PrimaryEcu.GlobalDTCs.ToArray();
            Codes = source.Where(x => x != null).OrderBy(x => x.Qualifier).ToList();
        }

        void Rebuild()
        {
            Dirty = false;
            BuiltTranslationVersion = OnlineTranslator.Version;
            LastBuild = DateTime.Now;
            string[] terms = Search.ToLowerInvariant().Split(' ', StringSplitOptions.RemoveEmptyEntries);
            Visible = new List<Row>();
            var seen = new HashSet<string>();
            foreach (var dtc in Codes)
            {
                var row = new Row { Code = dtc.Qualifier, Original = Tr.Original(dtc.Description_CTF), Meaning = Tr.Ctf(dtc.Description_CTF) };
                // the same code is often listed once per ECU version
                if (!seen.Add($"{row.Code}|{row.Original}"))
                {
                    continue;
                }
                string haystack = $"{row.Code} {row.Meaning} {row.Original}".ToLowerInvariant();
                if (terms.All(t => haystack.Contains(t)))
                {
                    Visible.Add(row);
                }
            }
        }

        public void Draw()
        {
            if (Dirty || (OnlineTranslator.Version != BuiltTranslationVersion && (DateTime.Now - LastBuild).TotalSeconds > 1))
            {
                Rebuild();
            }

            ImGui.TextWrapped("All fault codes this ECU can report, with their meaning. Use this to look up a code (e.g. from another scan tool). This list comes from the CBF file and does not read the car.");
            ImGui.SetNextItemWidth(Widgets.S(420));
            if (ImGui.InputTextWithHint("##dtcsearch", "Search code or description...", ref Search, 128))
            {
                Dirty = true;
            }
            ImGui.SameLine();
            ImGui.TextDisabled($"{Visible.Count} codes");

            if (!ImGui.BeginTable("dtcs", 2, ImGuiTableFlags.RowBg | ImGuiTableFlags.BordersInnerH | ImGuiTableFlags.ScrollY | ImGuiTableFlags.Resizable, ImGui.GetContentRegionAvail()))
            {
                return;
            }
            ImGui.TableSetupScrollFreeze(0, 1);
            ImGui.TableSetupColumn("Code", ImGuiTableColumnFlags.WidthFixed, Widgets.S(140));
            ImGui.TableSetupColumn("Meaning", ImGuiTableColumnFlags.WidthStretch);
            ImGui.TableHeadersRow();

            unsafe
            {
                var clipper = new ImGuiListClipperPtr(ImGuiNative.ImGuiListClipper_ImGuiListClipper());
                clipper.Begin(Visible.Count);
                while (clipper.Step())
                {
                    for (int i = clipper.DisplayStart; i < clipper.DisplayEnd; i++)
                    {
                        var row = Visible[i];
                        ImGui.TableNextRow();
                        ImGui.TableNextColumn();
                        ImGui.TextUnformatted(row.Code);
                        ImGui.TableNextColumn();
                        bool translated = Tr.IsTranslated(row.Original, row.Meaning);
                        ImGui.TextUnformatted(Settings.ShowOriginalNames && translated ? $"{row.Meaning}   [{row.Original}]" : row.Meaning);
                        if (translated)
                        {
                            Widgets.Tooltip($"Original: {row.Original}");
                        }
                    }
                }
                clipper.End();
                clipper.Destroy();
            }
            ImGui.EndTable();
        }
    }
}
