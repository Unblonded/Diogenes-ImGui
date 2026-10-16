using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Numerics;
using Caesar;
using Diogenes.Translation;
using ImGuiNET;
using static Diogenes.Translation.ServiceCategories;

namespace Diogenes.UI
{
    // Replaces Forms/DiagServicesView and Forms/PresentationEditor.
    // Left: searchable list of commands in plain English. Right: inputs, Run button and the decoded answer.
    public class CommandsTab
    {
        class ServiceRow
        {
            public DiagService Service;
            public Category Category;
            public string Name;
            public string Original;
            public string SearchText;
            public bool DuplicateName;
        }

        class ResultRow
        {
            public string Label;
            public string Value;
            public string OriginalValue;
            public string Raw;
        }

        readonly App App;
        static DiogenesSharedContext Ctx => DiogenesSharedContext.Singleton;
        static AppSettings Settings => AppSettings.Current;

        List<ServiceRow> AllRows = new List<ServiceRow>();
        List<ServiceRow> VisibleRows = new List<ServiceRow>();
        bool RowsDirty = true;
        bool FilterDirty = true;
        int RowsTranslationVersion = -1;
        DateTime LastTranslationRefresh = DateTime.MinValue;

        string Search = "";
        int CategoryFilter = -1;
        bool VariantOnly = true;
        bool SafeOnly = false;

        ServiceRow Selected = null;
        DiagServiceRunner Runner = null;
        readonly Dictionary<int, string> EditText = new Dictionary<int, string>();
        readonly HashSet<int> InvalidInputs = new HashSet<int>();

        List<ResultRow> Results = null;
        string ResultStatus = null;
        Vector4 ResultColor;
        string ResultRaw = null;
        bool SkipConfirmThisSession = false;

        public CommandsTab(App app)
        {
            App = app;
        }

        public void NotifyCbfOrVariantChange()
        {
            RowsDirty = true;
            Select(null);
        }

        static bool IsSafe(Category c) => c == Category.ReadValue || c == Category.ReadCoding;

        static Vector4 CategoryColor(Category c) => c switch
        {
            Category.ReadValue or Category.ReadCoding => Theme.Good,
            Category.WriteValue or Category.WriteCoding or Category.ActuatorTest => Theme.Bad,
            Category.Routine or Category.Security or Category.Script => Theme.Warn,
            _ => Theme.Dim,
        };

        // ------------------------------------------------------------ list building

        void RebuildRows()
        {
            RowsDirty = false;
            FilterDirty = true;
            RowsTranslationVersion = OnlineTranslator.Version;
            LastTranslationRefresh = DateTime.Now;
            AllRows.Clear();

            if (!App.CbfLoaded)
            {
                return;
            }
            IEnumerable<DiagService> source = (VariantOnly && Ctx.PrimaryVariant != null)
                ? Ctx.PrimaryVariant.DiagServices
                : Ctx.PrimaryEcu.GlobalDiagServices;

            // the ECU-wide list repeats a command once per ECU version, show each one once
            var seenQualifiers = new HashSet<string>();
            foreach (var ds in source)
            {
                if (ds is null || !seenQualifiers.Add(ds.Qualifier ?? ""))
                {
                    continue;
                }
                string original = Tr.Original(ds.Name_CTF);
                string name = string.IsNullOrWhiteSpace(original) ? Tr.Qualifier(ds.Qualifier) : Tr.Ctf(ds.Name_CTF, Tr.Kind.Title);
                if (string.IsNullOrWhiteSpace(original))
                {
                    original = ds.Qualifier;
                }
                AllRows.Add(new ServiceRow
                {
                    Service = ds,
                    Category = Of(ds),
                    Name = name,
                    Original = original,
                    SearchText = $"{name} {original} {ds.Qualifier}".ToLowerInvariant(),
                });
            }
            AllRows.Sort((a, b) => string.Compare(a.Name, b.Name, StringComparison.OrdinalIgnoreCase));

            // several commands can share a display name, the qualifier tells them apart
            foreach (var group in AllRows.GroupBy(r => r.Name).Where(g => g.Count() > 1))
            {
                foreach (var row in group)
                {
                    row.DuplicateName = true;
                }
            }
        }

        void RefilterRows()
        {
            FilterDirty = false;
            string[] terms = Search.ToLowerInvariant().Split(' ', StringSplitOptions.RemoveEmptyEntries);
            VisibleRows = AllRows.Where(r =>
                (CategoryFilter < 0 || (int)r.Category == CategoryFilter) &&
                (!SafeOnly || IsSafe(r.Category)) &&
                terms.All(t => r.SearchText.Contains(t))).ToList();
        }

        // ------------------------------------------------------------ selection

        void Select(ServiceRow row)
        {
            Selected = row;
            Runner = row is null ? null : new DiagServiceRunner(row.Service);
            EditText.Clear();
            InvalidInputs.Clear();
            Results = null;
            ResultStatus = null;
            ResultRaw = null;
            Runner?.GenerateRequestBitArray();
        }

        // ------------------------------------------------------------ drawing

        public void Draw()
        {
            // pick up new online translations, at most once a second
            if (!RowsDirty && OnlineTranslator.Version != RowsTranslationVersion && (DateTime.Now - LastTranslationRefresh).TotalSeconds > 1)
            {
                var keep = Selected?.Service;
                RebuildRows();
                Selected = AllRows.FirstOrDefault(r => r.Service == keep) ?? Selected;
            }
            if (RowsDirty)
            {
                RebuildRows();
            }
            if (FilterDirty)
            {
                RefilterRows();
            }

            float leftWidth = Math.Max(Widgets.S(320), ImGui.GetContentRegionAvail().X * 0.38f);
            ImGui.BeginChild("cmdlist", new Vector2(leftWidth, 0), ImGuiChildFlags.Border | ImGuiChildFlags.ResizeX);
            DrawList();
            ImGui.EndChild();

            ImGui.SameLine();
            ImGui.BeginChild("cmddetail", Vector2.Zero, ImGuiChildFlags.Border);
            DrawDetail();
            ImGui.EndChild();
        }

        void DrawList()
        {
            ImGui.SetNextItemWidth(Widgets.S(-1));
            if (ImGui.InputTextWithHint("##search", "Search commands (English or German)...", ref Search, 256))
            {
                FilterDirty = true;
            }

            ImGui.SetNextItemWidth(ImGui.GetContentRegionAvail().X * 0.55f);
            if (ImGui.BeginCombo("##cat", CategoryFilter < 0 ? "All types" : Label((Category)CategoryFilter)))
            {
                if (ImGui.Selectable("All types", CategoryFilter < 0))
                {
                    CategoryFilter = -1;
                    FilterDirty = true;
                }
                foreach (var c in All)
                {
                    int count = AllRows.Count(r => r.Category == c);
                    if (count == 0)
                    {
                        continue;
                    }
                    ImGui.PushStyleColor(ImGuiCol.Text, CategoryColor(c));
                    if (ImGui.Selectable($"{Label(c)} ({count})", CategoryFilter == (int)c))
                    {
                        CategoryFilter = (int)c;
                        FilterDirty = true;
                    }
                    ImGui.PopStyleColor();
                    Widgets.Tooltip(Explanation(c));
                }
                ImGui.EndCombo();
            }
            ImGui.SameLine();
            if (ImGui.Checkbox("Read-only", ref SafeOnly))
            {
                FilterDirty = true;
            }
            Widgets.Tooltip("Only show commands that read data and can't change anything in the car");

            if (Ctx.PrimaryVariant != null)
            {
                if (ImGui.Checkbox("Only commands for this ECU version", ref VariantOnly))
                {
                    RowsDirty = true;
                }
                Widgets.Tooltip("The connected ECU's exact version was detected, so commands that don't apply to it can be hidden.");
            }

            ImGui.TextDisabled($"{VisibleRows.Count} of {AllRows.Count} commands");
            ImGui.Separator();

            ImGui.BeginChild("rows");
            unsafe
            {
                var clipper = new ImGuiListClipperPtr(ImGuiNative.ImGuiListClipper_ImGuiListClipper());
                clipper.Begin(VisibleRows.Count);
                while (clipper.Step())
                {
                    for (int i = clipper.DisplayStart; i < clipper.DisplayEnd; i++)
                    {
                        var row = VisibleRows[i];
                        ImGui.PushID(i);
                        Widgets.StatusDot(CategoryColor(row.Category));
                        string label = Settings.ShowOriginalNames && Tr.IsTranslated(row.Original, row.Name) ? $"{row.Name}   [{row.Original}]" : row.Name;
                        if (row.DuplicateName)
                        {
                            label += $"   ({row.Service.Qualifier})";
                        }
                        if (ImGui.Selectable(label, row == Selected))
                        {
                            Select(row);
                        }
                        Widgets.Tooltip($"{Label(row.Category)}\n{row.Service.Qualifier}");
                        ImGui.PopID();
                    }
                }
                clipper.End();
                clipper.Destroy();
            }
            if (AllRows.Count == 0)
            {
                ImGui.TextDisabled("No commands in this file.");
            }
            ImGui.EndChild();
        }

        void DrawDetail()
        {
            if (Selected is null)
            {
                ImGui.Spacing();
                Widgets.BigText("Pick a command on the left", 1.3f);
                ImGui.Spacing();
                ImGui.TextWrapped("Use the search box to find what you want, e.g. \"mileage\", \"VIN\", \"reset\" or \"fault\".");
                ImGui.Spacing();
                Widgets.Heading("Colors");
                foreach (var c in new[] { Category.ReadValue, Category.Routine, Category.WriteValue })
                {
                    Widgets.StatusDot(CategoryColor(c));
                    ImGui.TextWrapped(c == Category.ReadValue ? "Green: only reads information, always safe."
                        : c == Category.Routine ? "Yellow: runs a procedure (session change, unlock, script, routine)."
                        : "Red: changes something in the car (values, coding, moving parts).");
                }
                return;
            }

            var ds = Selected.Service;
            Widgets.BigText(Selected.Name, 1.4f);
            if (Tr.IsTranslated(Selected.Original, Selected.Name) || Settings.ShowOriginalNames)
            {
                Widgets.Dim($"Original: {Selected.Original}");
            }

            Widgets.StatusDot(CategoryColor(Selected.Category));
            ImGui.PushStyleColor(ImGuiCol.Text, CategoryColor(Selected.Category));
            ImGui.TextUnformatted(Label(Selected.Category));
            ImGui.PopStyleColor();
            ImGui.SameLine();
            ImGui.TextDisabled($"- {Explanation(Selected.Category)}");

            string description = Tr.Ctf(ds.Description_CTF);
            if (!string.IsNullOrWhiteSpace(description))
            {
                ImGui.Spacing();
                ImGui.TextWrapped(description);
            }

            if (Settings.AdvancedMode)
            {
                Widgets.Dim($"Qualifier: {ds.Qualifier}" + (ds.DiagServiceCode.Count > 0 ? $"   Scripts: {string.Join(", ", ds.DiagServiceCode.Select(x => x.Qualifier))}" : ""));
            }

            ImGui.Spacing();
            ImGui.Separator();
            DrawInputs();

            ImGui.Spacing();
            bool canRun = App.Connected && !App.Busy;
            string why = !App.Connected ? "Connect to the car first (step 3 at the top)" : "Another operation is still running";
            if (Widgets.ColoredButton("Run", IsSafe(Selected.Category) ? Theme.Good : Theme.Warn, new Vector2(Widgets.S(160), Widgets.S(40)), canRun))
            {
                RunSelected();
            }
            if (!canRun)
            {
                Widgets.Tooltip(why);
                ImGui.SameLine();
                ImGui.TextDisabled(why);
            }

            ImGui.Spacing();
            DrawResults();
        }

        void DrawInputs()
        {
            Widgets.Heading("Inputs");
            var ecu = Ctx.PrimaryEcu;
            int shown = 0;

            if (ImGui.BeginTable("inputs", Settings.AdvancedMode ? 3 : 2, ImGuiTableFlags.SizingStretchProp | ImGuiTableFlags.RowBg))
            {
                for (int i = 0; i < Runner.VisiblePreparations.Count; i++)
                {
                    var prep = Runner.VisiblePreparations[i];
                    bool editable = prep.FieldType == DiagPreparation.InferredDataType.PrepPresentationType
                        && prep.PrepPresPoolIndex >= 0 && prep.PrepPresPoolIndex < ecu.GlobalPrepPresentations.Count
                        && (prep.BitPosition + prep.SizeInBits) <= Runner.DiagBits.Length;

                    // fixed bytes are part of the command itself, only interesting in advanced mode
                    if (!editable && !Settings.AdvancedMode)
                    {
                        continue;
                    }
                    shown++;
                    ImGui.PushID(i);
                    ImGui.TableNextRow();

                    ImGui.TableNextColumn();
                    string original = Tr.Original(prep.Name_CTF);
                    string label = string.IsNullOrWhiteSpace(original) ? Tr.Qualifier(prep.Qualifier) : Tr.Ctf(prep.Name_CTF, Tr.Kind.Title);
                    ImGui.TextUnformatted(label);
                    Widgets.Tooltip($"{(string.IsNullOrWhiteSpace(original) ? "" : original + "\n")}{prep.Qualifier}");

                    ImGui.TableNextColumn();
                    if (editable)
                    {
                        DrawPresentationEditor(i, prep, ecu.GlobalPrepPresentations[prep.PrepPresPoolIndex]);
                    }
                    else
                    {
                        ImGui.TextDisabled($"fixed: {BitUtility.BytesToHex(BitArrayExtension.ToBytes(prep.Content), true)}");
                    }

                    if (Settings.AdvancedMode)
                    {
                        ImGui.TableNextColumn();
                        ImGui.TextDisabled($"bit {prep.BitPosition}, {prep.SizeInBits} bits");
                    }
                    ImGui.PopID();
                }
                ImGui.EndTable();
            }
            if (shown == 0)
            {
                ImGui.TextDisabled("No inputs needed.");
            }

            if (Settings.AdvancedMode)
            {
                string requestHex = BitUtility.BytesToHex(BitArrayExtension.ToBytes(Runner.DiagBits), true);
                ImGui.TextDisabled("Request bytes:");
                ImGui.SameLine();
                ImGui.TextUnformatted(requestHex.Length == 0 ? "(none - this command is a script)" : requestHex);
            }
        }

        // port of PresentationEditor: edits one preparation through its presentation (enum, number, bytes...)
        void DrawPresentationEditor(int index, DiagPreparation prep, DiagPresentation pres)
        {
            BitArray bits = BitArrayExtension.Slice(Runner.DiagBits, prep.BitPosition, prep.SizeInBits);
            DiagPresentation.PresentationTypes type;
            try
            {
                // the library throws for presentation types it doesn't understand yet
                type = pres.GetDataType();
            }
            catch (Exception)
            {
                ImGui.TextDisabled($"(unsupported value type: {BitUtility.BytesToHex(BitArrayExtension.ToBytes(bits), true)})");
                return;
            }
            string unit = Tr.Ctf(pres.DisplayedUnit_CTF);
            ImGui.SetNextItemWidth(Widgets.S(-1));

            switch (type)
            {
                case DiagPresentation.PresentationTypes.PresBasicEnum:
                    {
                        int current = BitArrayExtension.PromoteToInt32(bits, bigEndian: true);
                        var match = pres.BasicEnums.FirstOrDefault(e => e.EnumValue == current);
                        string preview = match is null ? $"(value {current})" : Tr.Text(match.EnumName, Tr.Kind.Title);
                        if (ImGui.BeginCombo("##enum", preview))
                        {
                            for (int optionIndex = 0; optionIndex < pres.BasicEnums.Count; optionIndex++)
                            {
                                var option = pres.BasicEnums[optionIndex];
                                string optionLabel = Tr.Text(option.EnumName, Tr.Kind.Title);
                                if (Settings.ShowOriginalNames && Tr.IsTranslated(option.EnumName, optionLabel))
                                {
                                    optionLabel += $"   [{option.EnumName}]";
                                }
                                // the index keeps options with the same name from sharing an ImGui ID
                                if (ImGui.Selectable($"{optionLabel}##opt{optionIndex}", option == match))
                                {
                                    // sized to the field, same as the number editors, so the value lands in the right bits
                                    BitArrayExtension.CopyBitsClamped(bits, BitArrayExtension.ToBitArray(option.EnumValue, bigEndian: true, bitArraySize: bits.Length));
                                    Commit(prep, bits);
                                }
                            }
                            ImGui.EndCombo();
                        }
                        break;
                    }
                case DiagPresentation.PresentationTypes.PresBytes:
                    TextEditor(index, prep, bits, BitUtility.BytesToHex(BitArrayExtension.ToBytes(bits), true), "hex bytes", text =>
                    {
                        if (BitUtility.TryParseHex(text, out byte[] hex) && hex.Length * 8 == bits.Length)
                        {
                            BitArrayExtension.CopyBitsClamped(bits, new BitArray(hex));
                            return true;
                        }
                        return false;
                    });
                    break;
                case DiagPresentation.PresentationTypes.PresIEEEFloat:
                    TextEditor(index, prep, bits, BitArrayExtension.ToFloat(bits, true).ToString(CultureInfo.InvariantCulture), unit, text =>
                    {
                        if (float.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out float f))
                        {
                            BitArrayExtension.CopyBitsClamped(bits, BitArrayExtension.ToBitArray(f, bigEndian: true));
                            return true;
                        }
                        return false;
                    });
                    break;
                case DiagPresentation.PresentationTypes.PresInt:
                    TextEditor(index, prep, bits, BitArrayExtension.PromoteToInt32(bits, bigEndian: true).ToString(), unit, text =>
                    {
                        if (int.TryParse(text, out int v))
                        {
                            BitArrayExtension.CopyBitsClamped(bits, BitArrayExtension.ToBitArray(v, bigEndian: true, bitArraySize: bits.Length));
                            return true;
                        }
                        return false;
                    });
                    break;
                case DiagPresentation.PresentationTypes.PresUInt:
                    TextEditor(index, prep, bits, BitArrayExtension.PromoteToUInt32(bits, bigEndian: true).ToString(), unit, text =>
                    {
                        if (uint.TryParse(text, out uint v))
                        {
                            BitArrayExtension.CopyBitsClamped(bits, BitArrayExtension.ToBitArray(v, bigEndian: true, bitArraySize: bits.Length));
                            return true;
                        }
                        return false;
                    });
                    break;
                case DiagPresentation.PresentationTypes.PresScaledDecimal when pres.Scales.Count == 1:
                    {
                        // same formula as DiagPresentation.Interpret: shown = raw * factor + offset
                        var scale = pres.Scales[0];
                        decimal raw = BitArrayExtension.PromoteToInt32(bits, bigEndian: true);
                        decimal shownValue = raw * (decimal)scale.MultiplyFactor + (decimal)scale.AddConstOffset;
                        TextEditor(index, prep, bits, shownValue.ToString(CultureInfo.InvariantCulture), unit, text =>
                        {
                            if (scale.MultiplyFactor != 0 && decimal.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out decimal v))
                            {
                                int newRaw = (int)Math.Round((v - (decimal)scale.AddConstOffset) / (decimal)scale.MultiplyFactor);
                                BitArrayExtension.CopyBitsClamped(bits, BitArrayExtension.ToBitArray(newRaw, bigEndian: true, bitArraySize: bits.Length));
                                return true;
                            }
                            return false;
                        });
                        break;
                    }
                default:
                    ImGui.TextDisabled($"(can't edit this type yet: {BitUtility.BytesToHex(BitArrayExtension.ToBytes(bits), true)})");
                    break;
            }
        }

        void TextEditor(int index, DiagPreparation prep, BitArray bits, string currentText, string hint, Func<string, bool> tryApply)
        {
            if (!EditText.TryGetValue(index, out string text))
            {
                text = currentText;
            }
            bool invalid = InvalidInputs.Contains(index);
            if (invalid)
            {
                ImGui.PushStyleColor(ImGuiCol.FrameBg, new Vector4(0.45f, 0.20f, 0.18f, 1f));
            }
            if (!string.IsNullOrWhiteSpace(hint))
            {
                ImGui.SetNextItemWidth(-ImGui.CalcTextSize(hint).X - 12);
            }
            if (ImGui.InputText("##value", ref text, 256))
            {
                EditText[index] = text;
                if (tryApply(text))
                {
                    InvalidInputs.Remove(index);
                    Commit(prep, bits);
                }
                else
                {
                    InvalidInputs.Add(index);
                }
            }
            if (invalid)
            {
                ImGui.PopStyleColor();
                Widgets.Tooltip("This value can't be used. Check the format.");
            }
            if (!string.IsNullOrWhiteSpace(hint))
            {
                ImGui.SameLine();
                ImGui.TextDisabled(hint);
            }
        }

        void Commit(DiagPreparation prep, BitArray bits)
        {
            prep.Content = bits;
            Runner.GenerateRequestBitArray();
        }

        // ------------------------------------------------------------ running

        void RunSelected()
        {
            if (InvalidInputs.Count > 0)
            {
                App.ShowMessage("Check the inputs", "One or more inputs are not valid (shown in red). Fix them before running.");
                return;
            }
            if (IsSafe(Selected.Category) || SkipConfirmThisSession)
            {
                Execute();
                return;
            }
            App.Confirm($"Run \"{Selected.Name}\"?",
                $"{Label(Selected.Category)}: {Explanation(Selected.Category)}\n\nOnly continue if you know what this command does to your car.",
                Execute);
        }

        void Execute()
        {
            var runner = Runner;
            var row = Selected;
            Results = null;
            ResultStatus = null;
            App.RunBackground($"Running {row.Name}", () =>
            {
                if (!Ctx.StartUserInitiatedRequest())
                {
                    Console.WriteLine("Another request is still being processed, please try again.");
                    return;
                }
                try
                {
                    runner.Response = new byte[] { };
                    Console.WriteLine($"Running: {row.Name} ({row.Service.Qualifier})");
                    runner.DoDiagService();
                }
                finally
                {
                    Ctx.EndUserInitiatedRequest();
                }
            }, () =>
            {
                if (runner == Runner)
                {
                    BuildResults(runner);
                }
            });
        }

        void BuildResults(DiagServiceRunner runner)
        {
            byte[] response = runner.Response ?? new byte[] { };
            ResultRaw = BitUtility.BytesToHex(response, true);
            Results = new List<ResultRow>();

            if (response.Length == 0)
            {
                ResultStatus = runner.Service.DiagServiceCode.Count > 0
                    ? "Script finished (see Messages below for details)."
                    : "No answer from the ECU.";
                ResultColor = runner.Service.DiagServiceCode.Count > 0 ? Theme.Good : Theme.Warn;
                return;
            }
            if (response[0] == 0x7F)
            {
                byte code = response.Length > 2 ? response[2] : (byte)0;
                ResultStatus = $"The ECU refused: {NegativeResponseMeaning(code)} (code 0x{code:X2})";
                ResultColor = Theme.Bad;
                Console.WriteLine(ResultStatus);
                return;
            }

            ResultStatus = "The ECU accepted the command.";
            ResultColor = Theme.Good;

            var ecu = runner.Service.ParentECU;
            var responseBits = new BitArray(response);
            foreach (var prepSet in runner.Service.OutputPreparations)
            {
                foreach (var prep in prepSet)
                {
                    var result = new ResultRow();
                    try
                    {
                        DiagPresentation pres = (prep.PresPoolIndex >= 0 && prep.PresPoolIndex < ecu.GlobalPresentations.Count)
                            ? ecu.GlobalPresentations[prep.PresPoolIndex] : null;

                        string labelOriginal = pres is null ? "" : Tr.Original(pres.Description_CTF);
                        result.Label = string.IsNullOrWhiteSpace(labelOriginal) ? Tr.Qualifier(prep.Qualifier) : Tr.Ctf(pres.Description_CTF, Tr.Kind.Title);

                        if (prep.BitPosition + prep.SizeInBits > responseBits.Length || pres is null)
                        {
                            result.Value = "(not in the answer)";
                        }
                        else
                        {
                            var sliced = BitArrayExtension.Slice(responseBits, prep.BitPosition, prep.SizeInBits);
                            result.OriginalValue = pres.Interpret(sliced);
                            result.Value = Tr.Value(result.OriginalValue);
                            result.Raw = BitUtility.BytesToHex(BitArrayExtension.ToBytes(sliced), true);
                        }
                    }
                    catch (Exception ex)
                    {
                        result.Label ??= prep.Qualifier;
                        result.Value = $"(could not decode: {ex.Message})";
                    }
                    Results.Add(result);
                    Console.WriteLine($"  {result.Label}: {result.Value}");
                }
            }
        }

        void DrawResults()
        {
            if (ResultStatus is null)
            {
                return;
            }
            Widgets.Heading("Result");
            Widgets.StatusDot(ResultColor);
            Widgets.Colored(ResultColor, ResultStatus);

            if (Results != null && Results.Count > 0 && ImGui.BeginTable("results", Settings.AdvancedMode ? 3 : 2, ImGuiTableFlags.RowBg | ImGuiTableFlags.BordersInnerH | ImGuiTableFlags.SizingStretchProp))
            {
                ImGui.TableSetupColumn("What");
                ImGui.TableSetupColumn("Value");
                if (Settings.AdvancedMode)
                {
                    ImGui.TableSetupColumn("Raw");
                }
                ImGui.TableHeadersRow();
                for (int i = 0; i < Results.Count; i++)
                {
                    var r = Results[i];
                    ImGui.TableNextRow();
                    ImGui.TableNextColumn();
                    ImGui.TextWrapped(r.Label ?? "");
                    ImGui.TableNextColumn();
                    ImGui.TextWrapped(r.Value ?? "");
                    if (r.OriginalValue != null && Tr.IsTranslated(r.OriginalValue, r.Value))
                    {
                        Widgets.Tooltip($"Original: {r.OriginalValue}");
                    }
                    if (ImGui.BeginPopupContextItem($"copy{i}"))
                    {
                        if (ImGui.MenuItem("Copy value"))
                        {
                            ImGui.SetClipboardText(r.Value ?? "");
                        }
                        ImGui.EndPopup();
                    }
                    if (Settings.AdvancedMode)
                    {
                        ImGui.TableNextColumn();
                        ImGui.TextDisabled(r.Raw ?? "");
                    }
                }
                ImGui.EndTable();
            }

            if (Settings.AdvancedMode && !string.IsNullOrEmpty(ResultRaw))
            {
                ImGui.TextDisabled("Full answer:");
                ImGui.SameLine();
                ImGui.TextWrapped(ResultRaw);
            }
            if (!IsSafe(Selected.Category) && App.Connected)
            {
                ImGui.Checkbox("Don't ask again before running commands (until restart)", ref SkipConfirmThisSession);
            }
        }
    }
}
