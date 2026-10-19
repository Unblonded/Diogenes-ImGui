using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Caesar;
using CaesarConnection.Protocol;

namespace Diogenes
{
    // One control unit the app can talk to: the main CBF (shared with the other tabs) or an extra CBF
    // loaded so the Vitals tab can read more of the car at once, e.g. engine + transmission.
    // Extra ECUs share the adapter's connection with the main one.
    public class EcuSlot
    {
        static DiogenesSharedContext Ctx => DiogenesSharedContext.Singleton;

        public readonly bool IsPrimary;
        public readonly string Path;
        readonly CaesarContainer OwnContainer;
        BaseProtocol OwnChannel;
        ECUVariant OwnVariant;

        // why the last connection attempt failed, null when fine
        public string Error;

        public CaesarContainer Container => IsPrimary ? Ctx.PrimaryContainer : OwnContainer;
        public ECU Ecu => Container.CaesarECUs[0];
        public BaseProtocol Channel => IsPrimary ? Ctx.Channel : OwnChannel;
        public ECUVariant Variant => IsPrimary ? Ctx.PrimaryVariant : OwnVariant;
        public bool Connected => Channel != null;
        public string Name => Ecu.Qualifier;

        public IEnumerable<DiagService> Services => Variant != null ? Variant.DiagServices : Ecu.GlobalDiagServices;

        public List<DTC> Codes => (Variant != null && Variant.DTCs.Length > 0 ? Variant.DTCs : Ecu.GlobalDTCs.ToArray())
            .Where(x => x != null).ToList();

        EcuSlot(bool primary, string path, CaesarContainer container)
        {
            IsPrimary = primary;
            Path = path;
            OwnContainer = container;
        }

        public static EcuSlot ForPrimary(string path) => new EcuSlot(true, path, null);

        public static EcuSlot Load(string path)
        {
            byte[] bytes = File.ReadAllBytes(path);
            if (!CaesarContainer.VerifyChecksum(bytes, out _))
            {
                throw new Exception("This file failed its checksum test, so it is probably damaged or not a CBF file.");
            }
            return new EcuSlot(false, path, new CaesarContainer(bytes));
        }

        // CBF connection values for an interface, with the hand-tuned overrides some ECUs need
        public static Dictionary<string, int> ComParamsFor(ECU ecu, ECUInterfaceSubtype iface)
        {
            var comParams = iface.CommunicationParameters.ToDictionary(t => t.ParamName, t => t.ComParamValue);
            foreach (var quirk in ComParamQuirks.GetQuirksForTarget(ecu.Qualifier, iface.Qualifier))
            {
                comParams[quirk.Key] = quirk.Value;
            }
            return comParams;
        }

        public static ECUVariant IdentifyVariant(BaseProtocol channel, ECU ecu)
        {
            int variantId = channel.GetEcuVariant();
            return ecu.ECUVariants.FirstOrDefault(v => v.VariantPatterns.Any(p => (p.VariantID & 0xFFFF) == variantId));
        }

        // extra ECUs only, runs on a background thread. Uses the CBF's first connection type, like the main one defaults to.
        public void Connect(CaesarConnection.Connection connection, TextWriter trace)
        {
            Error = null;
            var iface = Ecu.ECUInterfaceSubtypes.FirstOrDefault();
            if (iface is null)
            {
                Error = "This CBF file does not list any connection types.";
                return;
            }
            string physicalProtocol = $"{iface.PhysicalProtocol}";
            string diagProtocol = Ecu.ECUInterfaces[iface.ParentInterfaceIndex].Qualifier;
            try
            {
                OwnChannel = connection.OpenChannel(physicalProtocol, diagProtocol, ComParamsFor(Ecu, iface), trace);
            }
            catch (Exception ex)
            {
                OwnChannel = null;
                Error = ex.Message;
                return;
            }
            try
            {
                OwnVariant = IdentifyVariant(OwnChannel, Ecu);
            }
            catch (Exception)
            {
                // not fatal: the ECU-wide command list is used instead
                OwnVariant = null;
            }
        }

        public void Disconnect()
        {
            try
            {
                OwnChannel?.Dispose();
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Error while disconnecting {Name}: {ex.Message}");
            }
            OwnChannel = null;
            OwnVariant = null;
        }
    }
}
