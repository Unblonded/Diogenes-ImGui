using System.Numerics;
using ImGuiNET;

namespace Diogenes.UI
{
    public static class Theme
    {
        public static readonly Vector4 Accent = new Vector4(0.27f, 0.55f, 0.95f, 1f);
        public static readonly Vector4 Good = new Vector4(0.35f, 0.80f, 0.45f, 1f);
        public static readonly Vector4 Warn = new Vector4(0.95f, 0.75f, 0.25f, 1f);
        public static readonly Vector4 Bad = new Vector4(0.95f, 0.38f, 0.35f, 1f);
        public static readonly Vector4 Dim = new Vector4(0.60f, 0.62f, 0.66f, 1f);
        public static readonly Vector4 Modified = new Vector4(1.00f, 0.55f, 0.20f, 1f);

        public static void Apply()
        {
            ImGui.StyleColorsDark();
            var style = ImGui.GetStyle();
            style.WindowRounding = 0;
            style.ChildRounding = 6;
            style.FrameRounding = 5;
            style.PopupRounding = 6;
            style.GrabRounding = 5;
            style.TabRounding = 5;
            style.ScrollbarRounding = 6;
            style.FramePadding = new Vector2(8, 5);
            style.ItemSpacing = new Vector2(8, 7);
            style.CellPadding = new Vector2(6, 4);
            style.WindowPadding = new Vector2(12, 10);
            style.ScrollbarSize = 14;

            // spacing, rounding and scrollbars grow with the screen like the font does
            style.ScaleAllSizes(Widgets.UiScale);

            var c = style.Colors;
            c[(int)ImGuiCol.WindowBg] = new Vector4(0.10f, 0.11f, 0.12f, 1f);
            c[(int)ImGuiCol.ChildBg] = new Vector4(0.13f, 0.14f, 0.16f, 1f);
            c[(int)ImGuiCol.PopupBg] = new Vector4(0.12f, 0.13f, 0.15f, 0.98f);
            c[(int)ImGuiCol.FrameBg] = new Vector4(0.19f, 0.20f, 0.23f, 1f);
            c[(int)ImGuiCol.FrameBgHovered] = new Vector4(0.24f, 0.26f, 0.30f, 1f);
            c[(int)ImGuiCol.FrameBgActive] = new Vector4(0.28f, 0.30f, 0.35f, 1f);
            c[(int)ImGuiCol.Button] = new Vector4(0.22f, 0.24f, 0.28f, 1f);
            c[(int)ImGuiCol.ButtonHovered] = new Vector4(0.29f, 0.32f, 0.38f, 1f);
            c[(int)ImGuiCol.ButtonActive] = Accent;
            c[(int)ImGuiCol.Header] = new Vector4(0.22f, 0.36f, 0.58f, 0.70f);
            c[(int)ImGuiCol.HeaderHovered] = new Vector4(0.26f, 0.44f, 0.70f, 0.80f);
            c[(int)ImGuiCol.HeaderActive] = Accent;
            c[(int)ImGuiCol.Tab] = new Vector4(0.16f, 0.17f, 0.20f, 1f);
            c[(int)ImGuiCol.TabHovered] = new Vector4(0.26f, 0.44f, 0.70f, 1f);
            c[(int)ImGuiCol.TabActive] = new Vector4(0.22f, 0.38f, 0.62f, 1f);
            c[(int)ImGuiCol.TitleBgActive] = new Vector4(0.16f, 0.17f, 0.20f, 1f);
            c[(int)ImGuiCol.CheckMark] = Accent;
            c[(int)ImGuiCol.SliderGrab] = Accent;
            c[(int)ImGuiCol.PlotHistogram] = Accent;
            c[(int)ImGuiCol.TableHeaderBg] = new Vector4(0.17f, 0.18f, 0.21f, 1f);
            c[(int)ImGuiCol.TableRowBgAlt] = new Vector4(1f, 1f, 1f, 0.03f);
        }
    }
}
