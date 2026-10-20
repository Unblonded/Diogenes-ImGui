#region License
/*Copyright(c) 2018, Brian Humlicek
* https://github.com/BrianHumlicek
* 
*Permission is hereby granted, free of charge, to any person obtaining a copy
*of this software and associated documentation files (the "Software"), to deal
*in the Software without restriction, including without limitation the rights
*to use, copy, modify, merge, publish, distribute, sub-license, and/or sell
*copies of the Software, and to permit persons to whom the Software is
*furnished to do so, subject to the following conditions:
*The above copyright notice and this permission notice shall be included in all
*copies or substantial portions of the Software.
*
*THE SOFTWARE IS PROVIDED "AS IS", WITHOUT WARRANTY OF ANY KIND, EXPRESS OR
*IMPLIED, INCLUDING BUT NOT LIMITED TO THE WARRANTIES OF MERCHANTABILITY,
*FITNESS FOR A PARTICULAR PURPOSE AND NONINFRINGEMENT. IN NO EVENT SHALL THE
*AUTHORS OR COPYRIGHT HOLDERS BE LIABLE FOR ANY CLAIM, DAMAGES OR OTHER
*LIABILITY, WHETHER IN AN ACTION OF CONTRACT, TORT OR OTHERWISE, ARISING FROM,
*OUT OF OR IN CONNECTION WITH THE SOFTWARE OR THE USE OR OTHER DEALINGS IN THE
*SOFTWARE.
*/
#endregion License
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using Microsoft.Win32;

namespace SAE.J2534
{
    public class APIFactory : Common.ManagedDisposable
    {
        private static Dictionary<string, API> Cache = new Dictionary<string, API>();
        /// <summary>
        /// Loads an API, verifies its signature and returns the managed wrapper 
        /// </summary>
        /// <param name="Filename">DLL File to load</param>
        /// <returns>JManaged 2534API</returns>
        public static API GetAPI(string Filename)
        {
            return GetAPI(Filename, APIInfo.DefaultLongSize);
        }
        /// <summary>
        /// Loads an API whose 'unsigned long' is LongSize bytes, verifies its signature and returns the managed wrapper
        /// </summary>
        public static API GetAPI(string Filename, int LongSize)
        {
            lock (Cache)
            {
                if (!Cache.ContainsKey(Filename))
                {
                    if (!File.Exists(Filename)) throw new DllNotFoundException($"DLL Not Found! {Filename}");

                    var API = new API(Filename, LongSize);
                    if (API.APISignature.SAE_API != SAE_API.V202_SIGNATURE &&
                        API.APISignature.SAE_API != SAE_API.V404_SIGNATURE &&
                        API.APISignature.SAE_API != SAE_API.V500_SIGNATURE)
                    {
                        API.Dispose();
                        throw new MissingMemberException($"No compatible export signature was found in DLL {Filename}");
                    }

                    API.OnDisposing += () => Cache.Remove(Filename);
                    Cache.Add(Filename, API);
                }
                return Cache[Filename];
            }
        }

        /// <summary>
        /// Lists the J2534 API's installed on this computer: the registry on Windows, driver JSON files and
        /// known library locations on macOS and Linux
        /// </summary>
        /// <returns>APIInfo enumerable</returns>
        public static IEnumerable<APIInfo> GetAPIinfo()
        {
            return OperatingSystem.IsWindows() ? GetRegistryAPIinfo() : GetUnixAPIinfo();
        }

        // Libraries that install to a fixed place without a JSON file, e.g. the open source Tactrix OpenPort 2.0
        // driver (github.com/bisak/openport-j2534), which Homebrew puts in /opt/homebrew/lib on Apple silicon
        private static readonly (string Name, string Filename)[] KnownUnixLibraries =
        {
            ("Tactrix OpenPort 2.0", "/opt/homebrew/lib/libj2534.dylib"),
            ("Tactrix OpenPort 2.0", "/usr/local/lib/libj2534.dylib"),
            ("Tactrix OpenPort 2.0", "/usr/local/lib/libj2534.so"),
            ("Tactrix OpenPort 2.0", "/usr/lib/libj2534.so"),
        };

        /// <summary>
        /// On macOS and Linux there is no registry. Drivers describe themselves in ~/.passthru/*.json
        /// (the convention of the Macchina and OpenVehicleDiag drivers, which use 32-bit integers), and a
        /// few libraries are looked for where their installers put them.
        /// </summary>
        private static IEnumerable<APIInfo> GetUnixAPIinfo()
        {
            var result = new List<APIInfo>();
            string home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            string jsonFolder = Path.Combine(home, ".passthru");
            if (Directory.Exists(jsonFolder))
            {
                foreach (string jsonPath in Directory.GetFiles(jsonFolder, "*.json").OrderBy(x => x))
                {
                    try
                    {
                        using var json = JsonDocument.Parse(File.ReadAllText(jsonPath));
                        var root = json.RootElement;
                        string library = root.TryGetProperty("FUNCTION_LIB", out var lib) ? lib.GetString() : null;
                        if (String.IsNullOrWhiteSpace(library)) continue;
                        if (library.StartsWith("~/")) library = Path.Combine(home, library.Substring(2));

                        string name = root.TryGetProperty("NAME", out var n) ? n.GetString() : Path.GetFileNameWithoutExtension(jsonPath);
                        var details = new StringBuilder();
                        if (root.TryGetProperty("VENDOR", out var vendor)) details.AppendLine($"Vendor: {vendor.GetString()}");
                        // optional: "LONG_SIZE": 8 for a driver built with C 'unsigned long'
                        int longSize = root.TryGetProperty("LONG_SIZE", out var size) && size.TryGetInt32(out int s) && (s == 4 || s == 8) ? s : 4;
                        result.Add(new APIInfo(name, library, details.ToString(), longSize));
                    }
                    catch (Exception)
                    {
                        //a broken file only hides that one driver
                    }
                }
            }
            foreach (var known in KnownUnixLibraries)
            {
                if (File.Exists(known.Filename) && !result.Any(x => x.Filename == known.Filename))
                {
                    result.Add(new APIInfo(known.Name, known.Filename, "", APIInfo.DefaultLongSize));
                    break;
                }
            }
            return result;
        }

        /// <summary>
        /// Queries the registry for registered J2534 API's
        /// </summary>
        [System.Runtime.Versioning.SupportedOSPlatform("windows")]
        private static IEnumerable<APIInfo> GetRegistryAPIinfo()
        {
            Dictionary<string, string> DetailOptions = new Dictionary<string, string>();
            DetailOptions.Add("CAN", "CAN Bus");
            DetailOptions.Add("ISO15765", "ISO15765");
            DetailOptions.Add("J1850PWM", "J1850PWM");
            DetailOptions.Add("J1850VPW", "J1850VPW");
            DetailOptions.Add("ISO9141", "ISO9141");
            DetailOptions.Add("ISO14230", "ISO14230");
            DetailOptions.Add("SCI_A_ENGINE", "SCI-A Engine");
            DetailOptions.Add("SCI_A_TRANS", "SCI-A Transmission");
            DetailOptions.Add("SCI_B_ENGINE", "SCI-B Engine");
            DetailOptions.Add("SCI_B_TRANS", "SCI-B Transmission");

            const string PASSTHRU_REGISTRY_PATH = @"Software\PassThruSupport.04.04";
            const string PASSTHRU_REGISTRY_PATH_6432 = @"Software\Wow6432Node\PassThruSupport.04.04";

            RegistryKey RootKey = Registry.LocalMachine.OpenSubKey(PASSTHRU_REGISTRY_PATH, false);
            if (RootKey == null)
            {
                RootKey = Registry.LocalMachine.OpenSubKey(PASSTHRU_REGISTRY_PATH_6432, false);
            }

            if (RootKey == null)
                yield break;

            string[] RegistryEntries = RootKey.GetSubKeyNames();
            foreach (string Entry in RegistryEntries)
            {
                RegistryKey deviceKey = RootKey.OpenSubKey(Entry);
                if (deviceKey == null) continue;

                StringBuilder DetailsBuilder = new StringBuilder();

                var Vendor = (string)deviceKey.GetValue("Vendor", "");
                if (!String.IsNullOrWhiteSpace(Vendor))
                    DetailsBuilder.AppendLine($"Vendor: {Vendor}");
                var ConfigApplication = (string)deviceKey.GetValue("ConfigApplication", "");
                if (!String.IsNullOrWhiteSpace(ConfigApplication))
                    DetailsBuilder.AppendLine($"Configuration Application: {ConfigApplication}");

                foreach (var Option in DetailOptions)
                {
                    if (((int)deviceKey.GetValue(Option.Key, 0)) != 0)
                    {
                        DetailsBuilder.AppendLine(Option.Value);
                    }
                }

                yield return new APIInfo((string)deviceKey.GetValue("Name", ""),
                                         (string)deviceKey.GetValue("FunctionLibrary", ""),
                                         DetailsBuilder.ToString());
            }
        }
        public static void StaticDispose()
        {
            foreach (var API in Cache.Values.ToArray())
            {
                API.Dispose();
            }
        }

        protected override void DisposeManaged()
        {
            StaticDispose();
        }
    }
}
