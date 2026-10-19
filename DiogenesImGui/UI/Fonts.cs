using System;
using System.IO;
using System.Runtime.InteropServices;
using ImGuiNET;

namespace Diogenes.UI
{
    // Extra fonts on top of the main Segoe UI one: a semibold for headings, Bahnschrift (a DIN style
    // font, like car instrument clusters use) for big readings, Cascadia Mono for bytes and logs,
    // and the Windows icon font merged into the text fonts so icons can sit inside labels.
    public static class Fonts
    {
        public static ImFontPtr Semibold;
        public static ImFontPtr Display;      // gauge readings
        public static ImFontPtr DisplaySmall; // quick facts, card values
        public static ImFontPtr Title;        // page headings
        public static ImFontPtr Mono;

        static string FontFolder => Environment.GetFolderPath(Environment.SpecialFolder.Fonts);

        // called from ImGuiController's configure callback, after it added the main font and before the atlas is built
        public static void Load(float baseSize)
        {
            var io = ImGui.GetIO();
            string icons = Path.Combine(FontFolder, "segmdl2.ttf");

            // the main font was just added by the controller, so this merges the icons into it
            MergeIcons(io, icons, baseSize);

            Semibold = Add(io, "seguisb.ttf", baseSize);
            MergeIcons(io, icons, baseSize);
            Title = Add(io, "seguisb.ttf", baseSize * 1.55f);
            MergeIcons(io, icons, baseSize * 1.4f);
            Display = Add(io, "bahnschrift.ttf", baseSize * 2.6f);
            DisplaySmall = Add(io, "bahnschrift.ttf", baseSize * 1.45f);
            Mono = Add(io, "CascadiaMono.ttf", baseSize * 0.9f);
        }

        static ImFontPtr Add(ImGuiIOPtr io, string file, float size)
        {
            string path = Path.Combine(FontFolder, file);
            if (!File.Exists(path))
            {
                return default;
            }
            return io.Fonts.AddFontFromFileTTF(path, size, null, io.Fonts.GetGlyphRangesDefault());
        }

        static unsafe void MergeIcons(ImGuiIOPtr io, string path, float size)
        {
            if (!File.Exists(path))
            {
                return;
            }
            var config = new ImFontConfigPtr(ImGuiNative.ImFontConfig_ImFontConfig());
            config.MergeMode = true;
            config.PixelSnapH = true;
            config.GlyphOffset = new System.Numerics.Vector2(0, size * 0.12f);
            config.GlyphMinAdvanceX = size;
            io.Fonts.AddFontFromFileTTF(path, size * 0.95f, config, IconRange);
            config.Destroy();
        }

        // the atlas reads the range when it is built, so it lives for the whole program
        static readonly IntPtr IconRange = MakeRange(0xE700, 0xEDFF);

        static IntPtr MakeRange(ushort first, ushort last)
        {
            IntPtr range = Marshal.AllocHGlobal(sizeof(ushort) * 3);
            Marshal.WriteInt16(range, 0, unchecked((short)first));
            Marshal.WriteInt16(range, 2, unchecked((short)last));
            Marshal.WriteInt16(range, 4, 0);
            return range;
        }

        static unsafe bool Has(ImFontPtr font) => font.NativePtr != null;

        // PushFont that quietly does nothing when the font file wasn't on this PC
        public static void Push(ImFontPtr font)
        {
            ImGui.PushFont(Has(font) ? font : ImGui.GetFont());
        }

        public static void Pop()
        {
            ImGui.PopFont();
        }
    }

    // glyphs from Segoe MDL2 Assets (ships with Windows 10 and 11)
    public static class Icons
    {
        public const string Heart = "\uE95E";
        public const string Thermometer = "\uE9CA";
        public const string Bolt = "\uE945";
        public const string Battery = "\uEBA7";
        public const string Speedometer = "\uEC4A";
        public const string Gauge = "\uEC48";
        public const string Drop = "\uEB42";
        public const string Flame = "\uECAD";
        public const string Sun = "\uE706";
        public const string Cog = "\uE713";
        public const string Pulse = "\uE9D9";
        public const string Car = "\uE804";
        public const string Key = "\uE8D7";
        public const string Chip = "\uE950";
        public const string Warning = "\uE7BA";
        public const string Error = "\uE783";
        public const string Info = "\uE946";
        public const string Check = "\uE73E";
        public const string CheckCircle = "\uEC61";
        public const string Play = "\uE768";
        public const string Stop = "\uE71A";
        public const string Refresh = "\uE895";
        public const string Open = "\uE8E5";
        public const string Plug = "\uE703";
        public const string Close = "\uE711";
        public const string Add = "\uE710";
        public const string More = "\uE712";
        public const string Search = "\uE721";
        public const string Copy = "\uE8C8";
        public const string Terminal = "\uE756";
        public const string Code = "\uE943";
        public const string Chart = "\uE9D2";
        public const string Road = "\uEC49";
    }
}
