using System;
using System.Collections.Generic;
using System.Linq;
using Caesar;

namespace Diogenes.Translation
{
    // Groups CBF diagnostic services into a few plain-English categories
    public static class ServiceCategories
    {
        public enum Category
        {
            ReadValue,
            WriteValue,
            ReadCoding,
            WriteCoding,
            ActuatorTest,
            Routine,
            Session,
            Security,
            Script,
            Other,
        }

        public static readonly Category[] All = (Category[])Enum.GetValues(typeof(Category));

        public static string Label(Category c) => c switch
        {
            Category.ReadValue => "Read a value",
            Category.WriteValue => "Change a value",
            Category.ReadCoding => "Read coding",
            Category.WriteCoding => "Write coding",
            Category.ActuatorTest => "Test a part (actuator)",
            Category.Routine => "Run a routine",
            Category.Session => "Diagnostic session",
            Category.Security => "Security / unlock",
            Category.Script => "Scripted job",
            _ => "Other",
        };

        public static string Explanation(Category c) => c switch
        {
            Category.ReadValue => "Safe. Asks the ECU for a value and shows it. Nothing is changed.",
            Category.WriteValue => "Changes a stored value inside the ECU. Make sure you know what it does.",
            Category.ReadCoding => "Safe. Reads the ECU's configuration (variant coding).",
            Category.WriteCoding => "Changes the ECU's configuration. Write down the old value first.",
            Category.ActuatorTest => "Physically moves or switches something on the car (a motor, light, valve...).",
            Category.Routine => "Starts a built-in procedure, e.g. a calibration or a reset.",
            Category.Session => "Switches the ECU into another mode (e.g. extended / programming). Usually needed before other commands.",
            Category.Security => "Unlocks protected functions using a seed/key exchange.",
            Category.Script => "Runs a small program from the CBF that may send several requests.",
            _ => "No category information in the CBF.",
        };

        // prefixes used by Mercedes in qualifiers, longest first
        static readonly (string Prefix, Category Category)[] Prefixes =
        {
            ("DT_STO_", Category.ReadValue),
            ("DT_", Category.ReadValue),
            ("RVC_", Category.ReadCoding),
            ("WVC_", Category.WriteCoding),
            ("VC_", Category.ReadCoding),
            ("DL_", Category.WriteValue),
            ("WDBI_", Category.WriteValue),
            ("RDBI_", Category.ReadValue),
            ("IOC_", Category.ActuatorTest),
            ("IO_", Category.ActuatorTest),
            ("RT_", Category.Routine),
            ("RC_", Category.Routine),
            ("SES_", Category.Session),
            ("SEC_", Category.Security),
            ("DJ_", Category.Script),
            ("FN_", Category.Other),
            ("DNU_", Category.Other),
            ("ROE_", Category.Other),
            ("DIAG_", Category.Other),
        };

        public static string StripPrefix(string qualifier)
        {
            foreach (var (prefix, _) in Prefixes)
            {
                if (qualifier.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                {
                    return qualifier.Substring(prefix.Length);
                }
            }
            return qualifier;
        }

        public static Category Of(DiagService ds)
        {
            // security is spread over several service types, the name is the most reliable hint
            string q = ds.Qualifier ?? "";
            if (q.StartsWith("SEC_", StringComparison.OrdinalIgnoreCase) || q.IndexOf("SecurityAccess", StringComparison.OrdinalIgnoreCase) >= 0)
            {
                return Category.Security;
            }

            switch ((DiagService.ServiceType)ds.DataClass_ServiceType)
            {
                case DiagService.ServiceType.Data:
                case DiagService.ServiceType.StoredData:
                    return Category.ReadValue;
                case DiagService.ServiceType.Download:
                    return Category.WriteValue;
                case DiagService.ServiceType.DiagnosticJob:
                    return Category.Script;
                case DiagService.ServiceType.Session:
                    return Category.Session;
                case DiagService.ServiceType.Routine:
                    return Category.Routine;
                case DiagService.ServiceType.IoControl:
                    return Category.ActuatorTest;
                case DiagService.ServiceType.VariantCodingWrite:
                    return Category.WriteCoding;
                case DiagService.ServiceType.VariantCodingRead:
                    return Category.ReadCoding;
            }

            foreach (var (prefix, category) in Prefixes)
            {
                if (q.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                {
                    return category;
                }
            }
            if (ds.DiagServiceCode.Count > 0)
            {
                return Category.Script;
            }
            return Category.Other;
        }

        // UDS / KWP negative response codes in plain words
        public static string NegativeResponseMeaning(byte nrc) => nrc switch
        {
            0x10 => "General reject - the ECU refused the request",
            0x11 => "Service not supported by this ECU",
            0x12 => "Sub-function not supported",
            0x13 => "Wrong message length or format",
            0x14 => "Response too long",
            0x21 => "ECU busy - try again",
            0x22 => "Conditions not correct (e.g. wrong session, engine running, ignition off)",
            0x24 => "Request sequence error - another step must be done first",
            0x25 => "No response from a sub-component",
            0x26 => "A fault prevents this action",
            0x31 => "Request out of range - value or ID not accepted",
            0x33 => "Security access denied - the ECU must be unlocked first",
            0x35 => "Invalid key",
            0x36 => "Too many attempts - wait and try again later",
            0x37 => "Required time delay not expired - wait and try again",
            0x70 => "Upload/download not accepted",
            0x71 => "Transfer data suspended",
            0x72 => "General programming failure",
            0x73 => "Wrong block sequence counter",
            0x78 => "Response pending (ECU still working)",
            0x7E => "Sub-function not supported in the current session",
            0x7F => "Service not supported in the current session - switch session first",
            0x81 => "Engine RPM too high",
            0x82 => "Engine RPM too low",
            0x83 => "Engine is running",
            0x84 => "Engine is not running",
            0x85 => "Engine run time too low",
            0x86 => "Temperature too high",
            0x87 => "Temperature too low",
            0x88 => "Vehicle speed too high",
            0x89 => "Vehicle speed too low",
            0x8A => "Throttle/pedal too high",
            0x8B => "Throttle/pedal too low",
            0x8C => "Transmission not in neutral",
            0x8D => "Transmission not in gear",
            0x8F => "Brake not pressed",
            0x90 => "Shifter not in Park",
            0x91 => "Torque converter clutch locked",
            0x92 => "Voltage too high",
            0x93 => "Voltage too low",
            _ => "Unknown reason",
        };
    }
}
