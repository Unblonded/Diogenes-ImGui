using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using Caesar;
using Diogenes.Translation;
using ImGuiNET;

namespace Diogenes.UI
{
    // Replaces Forms/VarcodingView. Shows each coding setting with its possible options in plain English,
    // and can read the ECU's current coding to show which option is active.
    public class CodingTab
    {
        readonly App App;
        static DiogenesSharedContext Ctx => DiogenesSharedContext.Singleton;
        static AppSettings Settings => AppSettings.Current;

        List<VCDomain> Domains = new List<VCDomain>();
        int SelectedDomain = -1;
        string Search = "";

        // coding bytes read from the ECU for the selected domain
        byte[] CurrentCoding = null;
        VCDomain CurrentCodingDomain = null;

        public CodingTab(App app)
        {
            App = app;
        }

        public void NotifyCbfOrVariantChange()
        {
            Domains.Clear();
            SelectedDomain = -1;
            CurrentCoding = null;
            CurrentCodingDomain = null;
            if (!App.CbfLoaded)
            {
                return;
            }
            // the variant knows exactly which coding blocks apply; without one, show the whole catalog
            Domains = Ctx.PrimaryVariant != null && Ctx.PrimaryVariant.VCDomains.Count > 0
                ? Ctx.PrimaryVariant.VCDomains.ToList()
                : Ctx.PrimaryEcu.GlobalVCDs.ToList();
            if (Domains.Count > 0)
            {
                SelectedDomain = 0;
            }
        }

        static string DomainName(VCDomain d)
        {
            string original = Tr.Original(d.Name_CTF);
            return string.IsNullOrWhiteSpace(original) ? Tr.Qualifier(d.Qualifier) : Tr.Ctf(d.Name_CTF, Tr.Kind.Title);
        }

        static string FragmentName(VCFragment f, out string original)
        {
            original = Tr.Original(f.Name_CTF);
            if (string.IsNullOrWhiteSpace(original))
            {
                original = f.Qualifier ?? "";
                return Tr.Qualifier(f.Qualifier);
            }
            return Tr.Ctf(f.Name_CTF, Tr.Kind.Title);
        }

        static string OptionName(VCSubfragment s, out string original)
        {
            // NameResolved in the library reads Description_CTF, fall back to Name_CTF
            int id = string.IsNullOrWhiteSpace(Tr.Original(s.Description_CTF)) ? s.Name_CTF : s.Description_CTF;
            original = Tr.Original(id);
            if (string.IsNullOrWhiteSpace(original))
            {
                original = s.QualifierUsuallyDisabled ?? BitUtility.BytesToHex(s.Dump, true);
                return original;
            }
            return Tr.Ctf(id, Tr.Kind.Sentence);
        }

        public void Draw()
        {
            if (Domains.Count == 0)
            {
                Widgets.BigText("No coding information", 1.3f);
                ImGui.TextWrapped("This CBF file has no variant coding blocks" + (Ctx.PrimaryVariant is null ? ", or they are only listed per ECU version. Connect to the car so the version can be detected, or pick one under View > Advanced mode > Settings." : "."));
                return;
            }

            ImGui.TextWrapped("Coding (\"variant coding\") is the ECU's configuration: which equipment is fitted, country settings, and so on. Each row below is one setting and the options it can have.");
            ImGui.Spacing();

            ImGui.SetNextItemWidth(Widgets.S(420));
            if (ImGui.BeginCombo("Coding block", SelectedDomain >= 0 ? DomainName(Domains[SelectedDomain]) : ""))
            {
                for (int i = 0; i < Domains.Count; i++)
                {
                    if (ImGui.Selectable($"{DomainName(Domains[i])}##{i}", i == SelectedDomain))
                    {
                        SelectedDomain = i;
                    }
                }
                ImGui.EndCombo();
            }
            Widgets.Help("Large ECUs split their coding into several blocks. Each block is read and written as one piece.");

            var domain = Domains[SelectedDomain];
            ImGui.SameLine();
            bool canRead = App.Connected && !App.Busy && !string.IsNullOrEmpty(domain.ReadServiceName);
            if (Widgets.ColoredButton("Read current coding from car", Theme.Good, default, canRead))
            {
                ReadCoding(domain);
            }
            if (!canRead)
            {
                Widgets.Tooltip(!App.Connected ? "Connect to the car first" : string.IsNullOrEmpty(domain.ReadServiceName) ? "This block has no read command" : "Busy");
            }

            if (Settings.AdvancedMode)
            {
                Widgets.Dim($"{domain.Qualifier}   read: {domain.ReadServiceName}   write: {domain.WriteServiceName}   size: {domain.DumpSize} bytes");
                if (CurrentCoding != null && CurrentCodingDomain == domain)
                {
                    string hex = BitUtility.BytesToHex(CurrentCoding, true);
                    ImGui.TextUnformatted($"Current coding: {hex}");
                    ImGui.SameLine();
                    if (ImGui.SmallButton("Copy"))
                    {
                        ImGui.SetClipboardText(hex);
                    }
                }
            }

            ImGui.SetNextItemWidth(Widgets.S(420));
            ImGui.InputTextWithHint("##vcsearch", "Filter settings (English or German)...", ref Search, 128);
            ImGui.SameLine();
            ImGui.TextDisabled("Writing coding is not supported in this version (read-only).");

            bool haveCurrent = CurrentCoding != null && CurrentCodingDomain == domain;
            string filter = Search.ToLowerInvariant();

            if (ImGui.BeginTable("fragments", 3, ImGuiTableFlags.RowBg | ImGuiTableFlags.BordersInnerH | ImGuiTableFlags.ScrollY | ImGuiTableFlags.Resizable, ImGui.GetContentRegionAvail()))
            {
                ImGui.TableSetupScrollFreeze(0, 1);
                ImGui.TableSetupColumn("Setting", ImGuiTableColumnFlags.WidthStretch, 1.2f);
                ImGui.TableSetupColumn("Current value", ImGuiTableColumnFlags.WidthStretch, 1f);
                ImGui.TableSetupColumn("Possible values", ImGuiTableColumnFlags.WidthStretch, 1.4f);
                ImGui.TableHeadersRow();

                for (int i = 0; i < domain.VCFragments.Count; i++)
                {
                    var fragment = domain.VCFragments[i];
                    string name = FragmentName(fragment, out string originalName);
                    if (filter.Length > 0 && !name.ToLowerInvariant().Contains(filter) && !originalName.ToLowerInvariant().Contains(filter))
                    {
                        continue;
                    }

                    ImGui.PushID(i);
                    ImGui.TableNextRow();

                    ImGui.TableNextColumn();
                    ImGui.TextWrapped(name);
                    if (Settings.ShowOriginalNames && Tr.IsTranslated(originalName, name))
                    {
                        Widgets.Dim(originalName);
                    }
                    Widgets.Tooltip($"{originalName}\n{fragment.Qualifier}" + (Settings.AdvancedMode ? $"\nbit {fragment.ByteBitPos}, {fragment.BitLength} bits" : ""));

                    ImGui.TableNextColumn();
                    if (haveCurrent)
                    {
                        VCSubfragment active = null;
                        try
                        {
                            active = fragment.GetSubfragmentConfiguration(CurrentCoding);
                        }
                        catch
                        {
                            // fragment lies outside the bytes that were read
                        }
                        if (active != null)
                        {
                            Widgets.Colored(Theme.Good, OptionName(active, out _));
                        }
                        else
                        {
                            ImGui.TextDisabled("(unknown value)");
                        }
                    }
                    else
                    {
                        ImGui.TextDisabled("-");
                    }

                    ImGui.TableNextColumn();
                    if (fragment.Subfragments.Count == 0)
                    {
                        ImGui.TextDisabled("(free value)");
                    }
                    else
                    {
                        string summary = string.Join(", ", fragment.Subfragments.Take(3).Select(s => OptionName(s, out _)));
                        if (fragment.Subfragments.Count > 3)
                        {
                            summary += $", ... ({fragment.Subfragments.Count} options)";
                        }
                        if (ImGui.TreeNodeEx($"{summary}##opts", ImGuiTreeNodeFlags.SpanAvailWidth))
                        {
                            foreach (var option in fragment.Subfragments)
                            {
                                string optionName = OptionName(option, out string optionOriginal);
                                ImGui.BulletText(optionName);
                                if (Tr.IsTranslated(optionOriginal, optionName))
                                {
                                    Widgets.Tooltip($"Original: {optionOriginal}");
                                }
                                if (Settings.AdvancedMode)
                                {
                                    ImGui.SameLine();
                                    ImGui.TextDisabled($"[{BitUtility.BytesToHex(option.Dump, true)}]");
                                }
                            }
                            ImGui.TreePop();
                        }
                    }
                    ImGui.PopID();
                }
                ImGui.EndTable();
            }
        }

        void ReadCoding(VCDomain domain)
        {
            var service = Ctx.PrimaryVariant?.DiagServices.FirstOrDefault(x => x?.Qualifier == domain.ReadServiceName)
                          ?? Ctx.PrimaryEcu.GlobalDiagServices.FirstOrDefault(x => x.Qualifier == domain.ReadServiceName);
            if (service is null)
            {
                App.ShowMessage("Can't read coding", $"The read command {domain.ReadServiceName} is not in this CBF file.");
                return;
            }
            var runner = new DiagServiceRunner(service);
            App.RunBackground("Reading coding", () =>
            {
                runner.DoDiagService();
                byte[] response = runner.Response ?? new byte[] { };
                if (response.Length == 0 || response[0] == 0x7F)
                {
                    string reason = response.Length > 2 ? ServiceCategories.NegativeResponseMeaning(response[2]) : "no answer";
                    App.ShowMessage("Could not read coding", $"The ECU did not return its coding: {reason}.");
                    return;
                }
                // the coding bytes are the tail of the answer, after the response header (62 xx xx / 61 xx)
                int size = domain.DumpSize > 0 && domain.DumpSize <= response.Length ? domain.DumpSize : response.Length;
                byte[] coding = response.Skip(response.Length - size).ToArray();
                Console.WriteLine($"Read coding block {domain.Qualifier}: {BitUtility.BytesToHex(coding, true)}");
                App.OnMainThread(() =>
                {
                    CurrentCoding = coding;
                    CurrentCodingDomain = domain;
                });
            });
        }
    }
}
