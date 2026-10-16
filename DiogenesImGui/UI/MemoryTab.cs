using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Numerics;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using Caesar;
using ImGuiNET;

namespace Diogenes.UI
{
    // Replaces Forms/MemoryEditorView (and the Be.HexBox control) with an ImGui hex grid.
    public class MemoryTab
    {
        const int BytesPerRow = 16;
        const uint SizeLimitMB = 50;

        readonly App App;
        static DiogenesSharedContext Ctx => DiogenesSharedContext.Singleton;

        string StartText = "0";
        string LengthText = "100";

        // protocol options (advanced)
        int ChunkSize = 8;
        int AddressWidth = 4;
        int ReadCommand = 0x23;
        int WriteCommand = 0x3D;
        bool UseAlfid = true;

        byte[] Buffer = new byte[] { };
        byte[] OriginalBuffer = new byte[] { };
        uint BufferAddress = 0;
        bool DiffCacheAvailable = false;

        int SelectedByte = -1;
        string ByteEditText = "";
        bool FocusByteEdit = false;

        volatile float Progress = 0;
        volatile string ProgressText = "";

        public MemoryTab(App app)
        {
            App = app;
        }

        public void Draw()
        {
            ImGui.TextWrapped("Read or write a range of the ECU's memory directly. Most ECUs must be unlocked (security access) first. Writing the wrong bytes can break the ECU.");
            ImGui.Spacing();

            ImGui.SetNextItemWidth(Widgets.S(160));
            ImGui.InputText("Start address (hex)", ref StartText, 16, ImGuiInputTextFlags.CharsHexadecimal);
            ImGui.SameLine();
            ImGui.SetNextItemWidth(Widgets.S(120));
            ImGui.InputText("Length (hex bytes)", ref LengthText, 16, ImGuiInputTextFlags.CharsHexadecimal);

            bool canTransfer = App.Connected && !App.Busy;
            string reason = !App.Connected ? "Connect to the car first" : "Busy";
            if (Widgets.ColoredButton("Read from ECU", Theme.Good, new Vector2(Widgets.S(150), 0), canTransfer))
            {
                StartRead();
            }
            if (!canTransfer) Widgets.Tooltip(reason);
            ImGui.SameLine();
            if (Widgets.ColoredButton("Write to ECU", Theme.Bad, new Vector2(Widgets.S(150), 0), canTransfer && Buffer.Length > 0))
            {
                App.Confirm("Write memory?", $"This writes {Buffer.Length} bytes to the ECU at 0x{BufferAddress:X8}. Only changed {ChunkSize}-byte chunks are sent if the data was read from the ECU first.\n\nA wrong write can make the ECU stop working.", StartWrite);
            }
            if (!canTransfer) Widgets.Tooltip(reason);
            ImGui.SameLine();
            if (Widgets.Button("Save to file...", Buffer.Length > 0 && !App.Busy))
            {
                SaveToFile();
            }
            ImGui.SameLine();
            if (Widgets.Button("Load from file...", !App.Busy))
            {
                LoadFromFile();
            }

            if (ImGui.CollapsingHeader("Advanced options"))
            {
                ImGui.Indent();
                ImGui.SetNextItemWidth(Widgets.S(120));
                ImGui.InputInt("Bytes per request", ref ChunkSize);
                ChunkSize = Math.Clamp(ChunkSize, 1, 255);
                Widgets.Help("How many bytes are read or written in each request. Use 1 to step around protected bytes.");
                ImGui.SetNextItemWidth(Widgets.S(120));
                ImGui.InputInt("Address size (bytes)", ref AddressWidth);
                AddressWidth = Math.Clamp(AddressWidth, 1, 5);
                ImGui.SetNextItemWidth(Widgets.S(120));
                ImGui.InputInt("Read command", ref ReadCommand, 1, 1, ImGuiInputTextFlags.CharsHexadecimal);
                ReadCommand = Math.Clamp(ReadCommand, 0, 255);
                ImGui.SameLine();
                ImGui.TextDisabled($"0x{ReadCommand:X2}");
                ImGui.SetNextItemWidth(Widgets.S(120));
                ImGui.InputInt("Write command", ref WriteCommand, 1, 1, ImGuiInputTextFlags.CharsHexadecimal);
                WriteCommand = Math.Clamp(WriteCommand, 0, 255);
                ImGui.SameLine();
                ImGui.TextDisabled($"0x{WriteCommand:X2}");
                ImGui.Checkbox("Send address/length format byte (ALFID)", ref UseAlfid);
                Widgets.Help("Normally on for UDS ECUs, off for older KW2C3PE ECUs (e.g. KI211).");
                ImGui.TextDisabled($"Example read request: {PreviewRequest()}");
                ImGui.Unindent();
            }

            if (App.Busy && ProgressText.Length > 0)
            {
                ImGui.ProgressBar(Progress, new Vector2(-1, 0), ProgressText);
            }
            else if (ProgressText.Length > 0)
            {
                ImGui.TextDisabled(ProgressText);
            }

            ImGui.Separator();
            DrawByteEditor();
            DrawHexGrid();
        }

        string PreviewRequest()
        {
            try
            {
                if (!ParseRange(out uint start, out uint length, quiet: true))
                {
                    return "-";
                }
                int readSize = (int)Math.Min(length, (uint)ChunkSize);
                byte alfid = Alfid(AddressWidth, DigitCount(ChunkSize));
                return BitUtility.BytesToHex(CreateReadCommand((byte)ReadCommand, alfid, start, AddressWidth, readSize, UseAlfid).ToArray(), true);
            }
            catch
            {
                return "-";
            }
        }

        void DrawByteEditor()
        {
            if (SelectedByte < 0 || SelectedByte >= Buffer.Length)
            {
                ImGui.TextDisabled(Buffer.Length == 0 ? "No data yet. Read from the ECU or load a file." : "Click a byte to change it.");
                return;
            }
            ImGui.TextUnformatted($"Byte at 0x{BufferAddress + SelectedByte:X8}:");
            ImGui.SameLine();
            ImGui.SetNextItemWidth(Widgets.S(60));
            if (FocusByteEdit)
            {
                ImGui.SetKeyboardFocusHere();
                FocusByteEdit = false;
            }
            if (ImGui.InputText("##byte", ref ByteEditText, 3, ImGuiInputTextFlags.CharsHexadecimal | ImGuiInputTextFlags.AutoSelectAll | ImGuiInputTextFlags.EnterReturnsTrue))
            {
                if (Widgets.TryParseHex(ByteEditText, out uint v) && v <= 0xFF)
                {
                    Buffer[SelectedByte] = (byte)v;
                    // move on to the next byte, like a hex editor
                    if (SelectedByte + 1 < Buffer.Length)
                    {
                        SelectedByte++;
                        ByteEditText = $"{Buffer[SelectedByte]:X2}";
                        FocusByteEdit = true;
                    }
                }
            }
            ImGui.SameLine();
            ImGui.TextDisabled("Type a hex value and press Enter.  Changed bytes are shown in orange.");
        }

        void DrawHexGrid()
        {
            int rows = (Buffer.Length + BytesPerRow - 1) / BytesPerRow;
            ImGui.BeginChild("hex", Vector2.Zero, ImGuiChildFlags.Border, ImGuiWindowFlags.HorizontalScrollbar);
            float charWidth = ImGui.CalcTextSize("F").X;
            float cellWidth = charWidth * 2 + 6;

            unsafe
            {
                var clipper = new ImGuiListClipperPtr(ImGuiNative.ImGuiListClipper_ImGuiListClipper());
                clipper.Begin(rows);
                while (clipper.Step())
                {
                    for (int row = clipper.DisplayStart; row < clipper.DisplayEnd; row++)
                    {
                        int rowStart = row * BytesPerRow;
                        ImGui.TextDisabled($"{BufferAddress + rowStart:X8}");
                        float x = ImGui.GetCursorPosX();
                        for (int col = 0; col < BytesPerRow; col++)
                        {
                            int index = rowStart + col;
                            ImGui.SameLine(0, Widgets.S(col == 8 ? 14 : 6));
                            if (index >= Buffer.Length)
                            {
                                ImGui.TextUnformatted("  ");
                                continue;
                            }
                            bool changed = index < OriginalBuffer.Length && OriginalBuffer[index] != Buffer[index];
                            if (changed)
                            {
                                ImGui.PushStyleColor(ImGuiCol.Text, Theme.Modified);
                            }
                            ImGui.PushID(index);
                            if (ImGui.Selectable($"{Buffer[index]:X2}", index == SelectedByte, ImGuiSelectableFlags.None, new Vector2(charWidth * 2, 0)))
                            {
                                SelectedByte = index;
                                ByteEditText = $"{Buffer[index]:X2}";
                                FocusByteEdit = true;
                            }
                            ImGui.PopID();
                            if (changed)
                            {
                                ImGui.PopStyleColor();
                            }
                        }

                        // ASCII column
                        ImGui.SameLine(0, Widgets.S(20));
                        var sb = new StringBuilder(BytesPerRow);
                        for (int col = 0; col < BytesPerRow && rowStart + col < Buffer.Length; col++)
                        {
                            byte b = Buffer[rowStart + col];
                            sb.Append(b >= 0x20 && b < 0x7F ? (char)b : '.');
                        }
                        ImGui.TextDisabled(sb.ToString());
                    }
                }
                clipper.End();
                clipper.Destroy();
            }
            ImGui.EndChild();
        }

        // ------------------------------------------------------------ protocol helpers (from MemoryEditorView)

        bool ParseRange(out uint start, out uint length, bool quiet = false)
        {
            length = 0;
            if (!Widgets.TryParseHex(StartText, out start))
            {
                if (!quiet) App.ShowMessage("Check the start address", $"Could not understand '{StartText}' as a hex address.");
                return false;
            }
            if (!Widgets.TryParseHex(LengthText, out length) || length == 0)
            {
                if (!quiet) App.ShowMessage("Check the length", $"Could not understand '{LengthText}' as a hex length.");
                return false;
            }
            if (length > SizeLimitMB * 1024 * 1024)
            {
                if (!quiet) App.ShowMessage("Range too large", $"Please read less than {SizeLimitMB}MB at a time.");
                return false;
            }
            if ((ulong)start + length > uint.MaxValue)
            {
                if (!quiet) App.ShowMessage("Range too large", "The range goes past the end of the address space.");
                return false;
            }
            return true;
        }

        static List<byte> CreateReadCommand(byte readCmd, byte alfid, long address, int addressWidth, int ioWidth, bool includeAlfid)
        {
            var command = new List<byte> { readCmd };
            if (includeAlfid)
            {
                command.Add(alfid);
            }
            command.AddRange(ToBigEndian(address, addressWidth));
            command.AddRange(ToBigEndian(ioWidth));
            return command;
        }

        static List<byte> CreateWriteCommand(byte writeCmd, long address, int addressWidth, byte[] payload, bool includeAlfid)
        {
            var command = new List<byte> { writeCmd };
            if (includeAlfid)
            {
                command.Add(Alfid(addressWidth, DigitCount(payload.Length)));
            }
            command.AddRange(ToBigEndian(address, addressWidth));
            command.AddRange(ToBigEndian(payload.Length));
            command.AddRange(payload);
            return command;
        }

        static List<byte> ToBigEndian(long value, int size = -1)
        {
            var result = new List<byte>();
            if (size == -1)
            {
                while (value > 0)
                {
                    result.Insert(0, (byte)(value & 0xFF));
                    value >>= 8;
                }
            }
            else
            {
                for (int i = 0; i < size; i++)
                {
                    result.Insert(0, (byte)(value & 0xFF));
                    value >>= 8;
                }
            }
            return result;
        }

        static byte Alfid(int addressSize, int memorySize)
        {
            if (addressSize < 1 || addressSize > 5 || memorySize < 1 || memorySize > 5)
            {
                throw new ArgumentOutOfRangeException("Invalid size for ALFID");
            }
            return (byte)((memorySize << 4) | addressSize);
        }

        static int DigitCount(int value)
        {
            int count = 0;
            while (value > 0)
            {
                count++;
                value >>= 8;
            }
            return count;
        }

        // ------------------------------------------------------------ read / write

        void StartRead()
        {
            if (!ParseRange(out uint start, out uint length))
            {
                return;
            }
            int chunk = ChunkSize;
            int addressWidth = AddressWidth;
            byte readCmd = (byte)ReadCommand;
            bool useAlfid = UseAlfid;

            App.RunBackground("Reading memory", () =>
            {
                byte positive = (byte)(readCmd + 0x40);
                byte alfid = Alfid(addressWidth, DigitCount(chunk));
                byte[] memory = new byte[length];
                uint end = start + length;
                uint cursor = start;
                bool badReads = false;

                try
                {
                    while (cursor < end)
                    {
                        Progress = (float)(cursor - start) / length;
                        ProgressText = $"Reading 0x{cursor:X8}";
                        int size = (int)Math.Min((uint)chunk, end - cursor);
                        byte[] response = Ctx.Channel.Send(CreateReadCommand(readCmd, alfid, cursor, addressWidth, size, useAlfid).ToArray());
                        if (response.Length > size && response[0] == positive)
                        {
                            Array.ConstrainedCopy(response, 1, memory, (int)(cursor - start), size);
                        }
                        else
                        {
                            badReads = true;
                        }
                        cursor += (uint)size;
                    }
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"Error during read: {ex.Message}");
                    badReads = true;
                }

                ProgressText = badReads ? "Read finished with errors" : "Read finished";
                if (badReads)
                {
                    App.ShowMessage("Some reads failed", "One or more blocks were refused by the ECU and were filled with zeros.\n\nCheck the address is valid and the ECU is unlocked. If the range has protected bytes, try 'Bytes per request' = 1 under Advanced options.");
                }
                App.OnMainThread(() =>
                {
                    Buffer = memory;
                    OriginalBuffer = memory.ToArray();
                    BufferAddress = start;
                    // only skip unchanged chunks on write if this read was clean
                    DiffCacheAvailable = !badReads;
                    SelectedByte = -1;
                });
            });
        }

        void StartWrite()
        {
            if (!ParseRange(out uint start, out _))
            {
                return;
            }
            byte[] data = Buffer.ToArray();
            byte[] original = OriginalBuffer.ToArray();
            bool useCache = DiffCacheAvailable && original.Length == data.Length && BufferAddress == start;
            int chunk = ChunkSize;
            int addressWidth = AddressWidth;
            byte writeCmd = (byte)WriteCommand;
            bool useAlfid = UseAlfid;

            App.RunBackground("Writing memory", () =>
            {
                byte positive = (byte)(writeCmd + 0x40);
                bool badWrites = false;
                int written = 0;
                int offset = 0;
                try
                {
                    while (offset < data.Length)
                    {
                        Progress = (float)offset / data.Length;
                        ProgressText = $"Writing 0x{start + offset:X8}";
                        int size = Math.Min(chunk, data.Length - offset);
                        byte[] piece = new byte[size];
                        Array.Copy(data, offset, piece, 0, size);

                        bool unchanged = useCache && piece.SequenceEqual(original.Skip(offset).Take(size));
                        if (!unchanged)
                        {
                            byte[] response = Ctx.Channel.Send(CreateWriteCommand(writeCmd, start + offset, addressWidth, piece, useAlfid).ToArray());
                            if (response.Length == 0 || response[0] != positive)
                            {
                                badWrites = true;
                            }
                            written++;
                        }
                        offset += size;
                    }
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"Error during write: {ex.Message}");
                    badWrites = true;
                }

                Console.WriteLine($"Memory write sent {written} request(s)" + (badWrites ? " with errors" : ""));
                ProgressText = badWrites ? "Write finished with errors" : "Write finished";
                if (badWrites)
                {
                    App.ShowMessage("Some writes failed", "One or more write requests were refused by the ECU.\n\nCheck the address is valid and the ECU is unlocked.");
                }
                App.OnMainThread(() =>
                {
                    OriginalBuffer = data;
                    BufferAddress = start;
                    DiffCacheAvailable = !badWrites;
                });
            });
        }

        void SaveToFile()
        {
            string fileName = Ctx.PrimaryEcu != null ? $"{Ctx.PrimaryEcu.Qualifier}_0x{BufferAddress:X8}.bin" : $"0x{BufferAddress:X8}.bin";
            string path = FileDialogs.SaveFile("Save memory to a file", "BIN files (*.bin)|*.bin|All files (*.*)|*.*", fileName);
            if (path != null)
            {
                File.WriteAllBytes(path, Buffer);
                Console.WriteLine($"Saved {Buffer.Length} bytes to {path}");
            }
        }

        void LoadFromFile()
        {
            string path = FileDialogs.OpenFile("Load a binary file", "BIN files (*.bin)|*.bin|All files (*.*)|*.*");
            if (path is null)
            {
                return;
            }
            try
            {
                byte[] bytes = File.ReadAllBytes(path);

                // files saved by this tool end in _0x<address>, use it as the start address
                var match = Regex.Match(Path.GetFileNameWithoutExtension(path), @"0[xX]([0-9a-fA-F]+)$");
                if (match.Success && Widgets.TryParseHex(match.Groups[1].Value, out uint address))
                {
                    StartText = $"{address:X}";
                    Console.WriteLine($"Start address set to 0x{address:X} from the file name");
                }
                Widgets.TryParseHex(StartText, out uint start);

                Buffer = bytes;
                OriginalBuffer = bytes.ToArray();
                BufferAddress = start;
                LengthText = $"{bytes.Length:X}";
                // the ECU contents are unknown, so every chunk must be written
                DiffCacheAvailable = false;
                SelectedByte = -1;
                Console.WriteLine($"Loaded {bytes.Length} bytes from {path}");
            }
            catch (Exception ex)
            {
                App.ShowMessage("Could not load file", ex.Message);
            }
        }
    }
}
