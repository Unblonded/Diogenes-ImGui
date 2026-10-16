using System;
using System.Globalization;
using System.Numerics;
using ImGuiNET;

namespace Diogenes.UI
{
    // small helpers so the tab code reads like a description of the screen
    public static class Widgets
    {
        // screen scale picked at startup (1.0 on a 1080p screen at 100%, about 2.0 on 4K), see Program.DetectUiScale
        public static float UiScale = 1f;

        // converts a size designed for 1080p into pixels for this screen and the chosen text size.
        // 0 and -1 keep their special ImGui meanings (auto / fill) and are not scaled.
        public static float S(float designPixels)
        {
            if (Math.Abs(designPixels) <= 1)
            {
                return designPixels;
            }
            return designPixels * UiScale * (AppSettings.Current.FontSize / 18f);
        }

        // a "(?)" that explains jargon on hover
        public static void Help(string text)
        {
            ImGui.SameLine();
            ImGui.TextDisabled("(?)");
            Tooltip(text);
        }

        public static void Tooltip(string text)
        {
            if (!string.IsNullOrEmpty(text) && ImGui.IsItemHovered(ImGuiHoveredFlags.AllowWhenDisabled | ImGuiHoveredFlags.DelayShort))
            {
                ImGui.BeginTooltip();
                ImGui.PushTextWrapPos(ImGui.GetFontSize() * 30);
                ImGui.TextUnformatted(text);
                ImGui.PopTextWrapPos();
                ImGui.EndTooltip();
            }
        }

        public static bool ColoredButton(string label, Vector4 color, Vector2 size = default, bool enabled = true)
        {
            ImGui.BeginDisabled(!enabled);
            ImGui.PushStyleColor(ImGuiCol.Button, color * new Vector4(0.75f, 0.75f, 0.75f, 1f));
            ImGui.PushStyleColor(ImGuiCol.ButtonHovered, color * new Vector4(0.90f, 0.90f, 0.90f, 1f));
            ImGui.PushStyleColor(ImGuiCol.ButtonActive, color);
            bool clicked = ImGui.Button(label, size);
            ImGui.PopStyleColor(3);
            ImGui.EndDisabled();
            return clicked;
        }

        public static bool Button(string label, bool enabled, string disabledReason = null, Vector2 size = default)
        {
            ImGui.BeginDisabled(!enabled);
            bool clicked = ImGui.Button(label, size);
            ImGui.EndDisabled();
            if (!enabled)
            {
                Tooltip(disabledReason);
            }
            return clicked;
        }

        public static void Heading(string text)
        {
            ImGui.PushStyleColor(ImGuiCol.Text, Theme.Accent);
            ImGui.TextUnformatted(text);
            ImGui.PopStyleColor();
        }

        public static void BigText(string text, float scale = 1.35f)
        {
            ImGui.SetWindowFontScale(scale);
            ImGui.TextWrapped(text);
            ImGui.SetWindowFontScale(1f);
        }

        public static void Colored(Vector4 color, string text)
        {
            ImGui.PushStyleColor(ImGuiCol.Text, color);
            ImGui.TextWrapped(text);
            ImGui.PopStyleColor();
        }

        public static void Dim(string text)
        {
            Colored(Theme.Dim, text);
        }

        // a filled circle used as a status light
        public static void StatusDot(Vector4 color)
        {
            var draw = ImGui.GetWindowDrawList();
            var pos = ImGui.GetCursorScreenPos();
            float h = ImGui.GetTextLineHeight();
            draw.AddCircleFilled(pos + new Vector2(h * 0.5f, h * 0.55f), h * 0.32f, ImGui.ColorConvertFloat4ToU32(color));
            ImGui.Dummy(new Vector2(h, h));
            ImGui.SameLine();
        }

        public static bool TryParseHex(string text, out uint value)
        {
            text = (text ?? "").Trim();
            if (text.StartsWith("0x", StringComparison.OrdinalIgnoreCase))
            {
                text = text.Substring(2);
            }
            return uint.TryParse(text, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out value);
        }
    }
}
