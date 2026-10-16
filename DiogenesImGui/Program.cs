using System;
using System.Drawing;
using System.IO;
using Diogenes.Translation;
using Diogenes.UI;
using ImGuiNET;
using Silk.NET.Input;
using Silk.NET.Maths;
using Silk.NET.OpenGL;
using Silk.NET.OpenGL.Extensions.ImGui;
using Silk.NET.Windowing;

namespace Diogenes
{
    static class Program
    {
        [STAThread]
        static void Main(string[] args)
        {
            // "DiogenesImGui --translate <text>" prints the offline translation and exits (handy for checking the dictionary)
            if (args.Length >= 2 && args[0] == "--translate")
            {
                for (int i = 1; i < args.Length; i++)
                {
                    Console.WriteLine($"{args[i]}  ->  {OfflineTranslator.Translate(args[i])}  |  title: {Tr.Text(args[i], Tr.Kind.Title)}");
                }
                return;
            }

            AppDomain.CurrentDomain.UnhandledException += (sender, e) => ReportCrash(e.ExceptionObject as Exception);

            var log = new LogBuffer();
            Console.SetOut(log);

            // Silk.NET normally finds GLFW by scanning the app folder, which doesn't work in a single-file exe
            Silk.NET.Windowing.Glfw.GlfwWindowing.RegisterPlatform();
            Silk.NET.Input.Glfw.GlfwInput.RegisterPlatform();

            OnlineTranslator.Start();

            string commandLinePath = args.Length > 0 ? args[0] : null;

            // open at three quarters of the screen, centered, with the UI scaled to match the screen
            var (screenWidth, screenHeight) = DetectUiScale();
            int width = Math.Max(1000, screenWidth * 3 / 4);
            int height = Math.Max(700, screenHeight * 3 / 4);

            var options = WindowOptions.Default;
            options.Size = new Vector2D<int>(width, height);
            options.Position = new Vector2D<int>((screenWidth - width) / 2, (screenHeight - height) / 2);
            options.Title = "Diogenes II";
            options.VSync = true;

            using var window = Window.Create(options);

            GL gl = null;
            IInputContext input = null;
            ImGuiController controller = null;
            App app = null;

            window.Load += () =>
            {
                gl = window.CreateOpenGL();
                input = window.CreateInput();

                // Segoe UI reads much better than the built-in pixel font and covers German umlauts
                string fontPath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Fonts), "segoeui.ttf");
                if (File.Exists(fontPath))
                {
                    controller = new ImGuiController(gl, window, input, new ImGuiFontConfig(fontPath, (int)Math.Round(18 * Widgets.UiScale)), ConfigureIO);
                }
                else
                {
                    controller = new ImGuiController(gl, window, input, ConfigureIO);
                }
                Theme.Apply();
                app = new App(log, commandLinePath);
            };

            window.FramebufferResize += size => gl?.Viewport(size);

            window.Render += delta =>
            {
                controller.Update((float)delta);
                gl.ClearColor(Color.FromArgb(255, 24, 26, 30));
                gl.Clear(ClearBufferMask.ColorBufferBit);
                app.Draw();
                controller.Render();
            };

            window.Closing += () =>
            {
                app?.Shutdown();
                OnlineTranslator.Flush();

                // GL objects must be freed while the window's OpenGL context still exists
                controller?.Dispose();
                input?.Dispose();
                gl?.Dispose();
                controller = null;
            };

            window.Run();
        }

        [System.Runtime.InteropServices.DllImport("user32.dll")]
        static extern bool SetProcessDpiAwarenessContext(IntPtr value);

        [System.Runtime.InteropServices.DllImport("user32.dll")]
        static extern uint GetDpiForSystem();

        [System.Runtime.InteropServices.DllImport("user32.dll")]
        static extern int GetSystemMetrics(int index);

        // Picks the UI scale from the Windows display scaling and the screen resolution, whichever is larger,
        // so a 4K screen gets big enough text even when Windows scaling is left at 100%.
        static (int width, int height) DetectUiScale()
        {
            int width = 1920;
            int height = 1080;
            float dpiScale = 1f;
            try
            {
                // per-monitor v2: real pixel sizes instead of Windows stretching a blurry bitmap
                SetProcessDpiAwarenessContext(new IntPtr(-4));
                dpiScale = GetDpiForSystem() / 96f;
                width = GetSystemMetrics(0);
                height = GetSystemMetrics(1);
            }
            catch
            {
                // older Windows: keep defaults
            }
            float resolutionScale = height / 1200f;
            Widgets.UiScale = Math.Clamp(Math.Max(dpiScale, resolutionScale), 1f, 4f);
            Console.WriteLine($"Screen {width}x{height}, Windows scaling {dpiScale * 100:0}%, UI scale {Widgets.UiScale:0.##}");
            return (width, height);
        }

        [System.Runtime.InteropServices.DllImport("user32.dll", CharSet = System.Runtime.InteropServices.CharSet.Unicode)]
        static extern int MessageBoxW(IntPtr hWnd, string text, string caption, uint type);

        static void ReportCrash(Exception ex)
        {
            string path = Path.Combine(AppSettings.SettingsFolder, "crash.log");
            try
            {
                Directory.CreateDirectory(AppSettings.SettingsFolder);
                File.WriteAllText(path, $"{DateTime.Now}\n{ex}");
            }
            catch
            {
                // nothing else we can do
            }
            MessageBoxW(IntPtr.Zero, $"Diogenes crashed:\n\n{ex?.Message}\n\nDetails were saved to {path}", "Diogenes", 0x10);
        }

        static void ConfigureIO()
        {
            var io = ImGui.GetIO();
            io.ConfigFlags |= ImGuiConfigFlags.NavEnableKeyboard;
            // don't litter the working directory with imgui.ini
            unsafe { io.NativePtr->IniFilename = null; }
        }
    }
}
