using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;

namespace Diogenes.UI
{
    // Native Windows open/save dialogs (comdlg32). Used instead of WinForms so the app doesn't need
    // the whole Windows Desktop runtime just to pick a file.
    public static class FileDialogs
    {
        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        struct OpenFileName
        {
            public int lStructSize;
            public IntPtr hwndOwner;
            public IntPtr hInstance;
            public string lpstrFilter;
            public string lpstrCustomFilter;
            public int nMaxCustFilter;
            public int nFilterIndex;
            public IntPtr lpstrFile;
            public int nMaxFile;
            public IntPtr lpstrFileTitle;
            public int nMaxFileTitle;
            public string lpstrInitialDir;
            public string lpstrTitle;
            public int Flags;
            public short nFileOffset;
            public short nFileExtension;
            public string lpstrDefExt;
            public IntPtr lCustData;
            public IntPtr lpfnHook;
            public string lpTemplateName;
            public IntPtr pvReserved;
            public int dwReserved;
            public int FlagsEx;
        }

        [DllImport("comdlg32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        static extern bool GetOpenFileNameW(ref OpenFileName ofn);

        [DllImport("comdlg32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        static extern bool GetSaveFileNameW(ref OpenFileName ofn);

        [DllImport("user32.dll")]
        static extern IntPtr GetActiveWindow();

        const int OFN_OVERWRITEPROMPT = 0x00000002;
        const int OFN_HIDEREADONLY = 0x00000004;
        const int OFN_NOCHANGEDIR = 0x00000008;
        const int OFN_ALLOWMULTISELECT = 0x00000200;
        const int OFN_PATHMUSTEXIST = 0x00000800;
        const int OFN_FILEMUSTEXIST = 0x00001000;
        const int OFN_EXPLORER = 0x00080000;

        const int BufferChars = 32 * 1024;

        // filter uses the WinForms style: "CBF files (*.cbf)|*.cbf|All files (*.*)|*.*"
        public static string OpenFile(string title, string filter)
        {
            var files = Show(title, filter, null, save: false, multi: false);
            return files.Count > 0 ? files[0] : null;
        }

        public static List<string> OpenFiles(string title, string filter)
        {
            return Show(title, filter, null, save: false, multi: true);
        }

        public static string SaveFile(string title, string filter, string defaultName)
        {
            var files = Show(title, filter, defaultName, save: true, multi: false);
            return files.Count > 0 ? files[0] : null;
        }

        static List<string> Show(string title, string filter, string defaultName, bool save, bool multi)
        {
            var result = new List<string>();
            IntPtr buffer = Marshal.AllocCoTaskMem(BufferChars * 2);
            try
            {
                // zero the buffer, then put the default file name in it
                Marshal.Copy(new byte[BufferChars * 2], 0, buffer, BufferChars * 2);
                if (!string.IsNullOrEmpty(defaultName))
                {
                    char[] name = (defaultName + "\0").ToCharArray();
                    Marshal.Copy(name, 0, buffer, Math.Min(name.Length, BufferChars - 1));
                }

                var ofn = new OpenFileName
                {
                    lStructSize = Marshal.SizeOf<OpenFileName>(),
                    hwndOwner = GetActiveWindow(),
                    lpstrFilter = filter.Replace('|', '\0') + "\0\0",
                    nFilterIndex = 1,
                    lpstrFile = buffer,
                    nMaxFile = BufferChars,
                    lpstrTitle = title,
                    Flags = OFN_EXPLORER | OFN_NOCHANGEDIR | OFN_HIDEREADONLY | OFN_PATHMUSTEXIST |
                            (save ? OFN_OVERWRITEPROMPT : OFN_FILEMUSTEXIST) |
                            (multi ? OFN_ALLOWMULTISELECT : 0),
                };

                bool ok = save ? GetSaveFileNameW(ref ofn) : GetOpenFileNameW(ref ofn);
                if (!ok)
                {
                    return result;
                }

                // multi-select returns "folder\0file1\0file2\0\0", single select returns "fullpath\0"
                var parts = new List<string>();
                int offset = 0;
                while (true)
                {
                    string part = Marshal.PtrToStringUni(buffer + offset * 2);
                    if (string.IsNullOrEmpty(part))
                    {
                        break;
                    }
                    parts.Add(part);
                    offset += part.Length + 1;
                    if (!multi)
                    {
                        break;
                    }
                }
                if (parts.Count == 1)
                {
                    result.Add(parts[0]);
                }
                else if (parts.Count > 1)
                {
                    for (int i = 1; i < parts.Count; i++)
                    {
                        result.Add(Path.Combine(parts[0], parts[i]));
                    }
                }
                return result;
            }
            finally
            {
                Marshal.FreeCoTaskMem(buffer);
            }
        }
    }
}
