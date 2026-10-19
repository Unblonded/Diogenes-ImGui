using System;
using System.Collections.Generic;
using System.Linq;
using Caesar;
using Diogenes.Translation;

namespace Diogenes
{
    // Reads the ECU's stored fault codes with the standard request (UDS 19 02 / KWP 18 02)
    // and looks their meaning up in the CBF. Read only, never clears anything.
    public static class FaultMemoryReader
    {
        public class Fault
        {
            public string Code;
            public string Meaning;      // filled in on the main thread from Dtc, see VitalsTab
            public DTC Dtc;             // matching code in the CBF, null when it isn't listed
            public string EcuName;
            public byte Status;
            public bool Active;
            public bool Pending;
        }

        public class Result
        {
            public List<Fault> Faults = new List<Fault>();
            public string Error;
        }

        // KWP ECUs (older Mercedes) list 0x18 services in their CBF, UDS ones list 0x19
        static bool UsesKwp(ECU ecu)
        {
            bool kwp = false;
            foreach (var ds in ecu.GlobalDiagServices)
            {
                if (ds?.RequestBytes is null || ds.RequestBytes.Length == 0)
                {
                    continue;
                }
                if (ds.RequestBytes[0] == 0x19)
                {
                    return false;
                }
                if (ds.RequestBytes[0] == 0x18)
                {
                    kwp = true;
                }
            }
            return kwp;
        }

        // runs on a background thread
        public static Result Read(CaesarConnection.Protocol.BaseProtocol channel, ECU ecu, IEnumerable<DTC> knownCodes)
        {
            var result = new Result();
            if (channel is null)
            {
                result.Error = "Not connected";
                return result;
            }
            bool kwp = UsesKwp(ecu);
            byte[] request = kwp ? new byte[] { 0x18, 0x02, 0xFF, 0x00 } : new byte[] { 0x19, 0x02, 0xFF };
            byte[] response = channel.Send(request, true);

            if (response is null || response.Length == 0)
            {
                result.Error = "No answer from the ECU";
                return result;
            }
            if (response[0] == 0x7F)
            {
                byte code = response.Length > 2 ? response[2] : (byte)0;
                result.Error = $"The ECU refused: {ServiceCategories.NegativeResponseMeaning(code)}";
                return result;
            }

            var lookup = BuildLookup(knownCodes);
            if (!kwp && response[0] == 0x59 && response.Length >= 3)
            {
                // 59 02 <availability mask> then <3 byte code><status> per fault
                for (int i = 3; i + 3 < response.Length; i += 4)
                {
                    byte status = response[i + 3];
                    // many ECUs list every code they support with only "test not completed" bits set,
                    // only keep codes that actually failed: now, this cycle, pending or confirmed
                    if ((status & 0x0F) == 0)
                    {
                        continue;
                    }
                    string sae = Sae(response[i], response[i + 1]);
                    var fault = new Fault
                    {
                        Code = $"{sae}{response[i + 2]:X2}",
                        Status = status,
                        Active = (status & 0x01) != 0,
                        Pending = (status & 0x04) != 0 && (status & 0x08) == 0,
                    };
                    fault.Dtc = Lookup(lookup, fault.Code, sae,$"{response[i]:X2}{response[i + 1]:X2}{response[i + 2]:X2}", $"{response[i]:X2}{response[i + 1]:X2}");
                    fault.EcuName = ecu.Qualifier;
                    result.Faults.Add(fault);
                }
            }
            else if (kwp && response[0] == 0x58 && response.Length >= 2)
            {
                // 58 <count> then <2 byte code><status> per fault
                for (int i = 2; i + 2 < response.Length; i += 3)
                {
                    byte status = response[i + 2];
                    // KWP storage state in bits 5-6: 00 nothing detected, 01 stored but not present,
                    // 10 maturing (intermittent), 11 present right now
                    int storage = (status >> 5) & 0x03;
                    if (storage == 0)
                    {
                        continue;
                    }
                    string hex = $"{response[i]:X2}{response[i + 1]:X2}";
                    string sae = Sae(response[i], response[i + 1]);
                    var fault = new Fault
                    {
                        Code = sae,
                        Status = status,
                        Active = storage == 0x03,
                        Pending = storage == 0x02,
                    };
                    fault.Dtc = Lookup(lookup, sae, hex);
                    fault.EcuName = ecu.Qualifier;
                    result.Faults.Add(fault);
                }
            }
            else
            {
                result.Error = $"Unexpected answer: {BitUtility.BytesToHex(response, true)}";
            }
            return result;
        }

        // first two bytes as an SAE code, e.g. 01 28 -> P0128
        static string Sae(byte high, byte low)
        {
            char letter = "PCBU"[high >> 6];
            return $"{letter}{(high >> 4) & 0x03}{high & 0x0F:X1}{low:X2}";
        }

        static Dictionary<string, DTC> BuildLookup(IEnumerable<DTC> codes)
        {
            var lookup = new Dictionary<string, DTC>(StringComparer.OrdinalIgnoreCase);
            foreach (var dtc in codes)
            {
                if (dtc?.Qualifier is null)
                {
                    continue;
                }
                string q = dtc.Qualifier.Trim();
                lookup.TryAdd(q, dtc);
                foreach (string prefix in new[] { "DTC_", "D_", "0x" })
                {
                    if (q.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                    {
                        lookup.TryAdd(q.Substring(prefix.Length), dtc);
                    }
                }
            }
            return lookup;
        }

        static DTC Lookup(Dictionary<string, DTC> lookup, params string[] candidates)
        {
            foreach (string c in candidates)
            {
                if (lookup.TryGetValue(c, out var dtc))
                {
                    return dtc;
                }
            }
            return null;
        }
    }
}
