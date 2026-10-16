using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.CompilerServices;
using Caesar;

namespace Diogenes
{
    // A small made-up ECU with German texts, built from the library's own classes.
    // Started with "DiogenesImGui.exe --demo" to try the interface (and its translation) without a real CBF file.
    public static class DemoData
    {
        static List<string> Strings;

        static int S(string text)
        {
            Strings.Add(text);
            return Strings.Count - 1;
        }

        static DiagPresentation Pres(string qualifier, string description, string unit = "")
        {
            return new DiagPresentation
            {
                Qualifier = qualifier,
                Description_CTF = S(description),
                DisplayedUnit_CTF = S(unit),
                Description2_CTF = -1,
                Unk14 = -1,
                ScaleTableOffset = -1,
                BasicEnumOffset = -1,
                Unk17 = -1,
                Unk19 = -1,
                Unk22 = -1,
                InternalDataType = -1,
                TypeLength_1A = 1,
                Type_1C = 0,
            };
        }

        static DiagPresentation EnumPres(string qualifier, string description, params (int value, string name)[] options)
        {
            var pres = Pres(qualifier, description);
            foreach (var (value, name) in options)
            {
                var e = (BasicEnum)RuntimeHelpers.GetUninitializedObject(typeof(BasicEnum));
                e.EnumValue = value;
                e.EnumName = name;
                e.EnumName_CTF = S(name);
                pres.BasicEnums.Add(e);
            }
            pres.BasicEnumCount = options.Length;
            return pres;
        }

        static DiagPresentation ScaledPres(string qualifier, string description, string unit, float factor, float offset)
        {
            var pres = Pres(qualifier, description, unit);
            pres.Unk14 = 0;
            pres.Scales.Add(new Scale { MultiplyFactor = factor, AddConstOffset = offset, EnumDescription = S(description) });
            pres.ScaleCount = 1;
            return pres;
        }

        static DiagPresentation UIntPres(string qualifier, string description, string unit = "")
        {
            var pres = Pres(qualifier, description, unit);
            pres.InternalDataType = 5;
            return pres;
        }

        static DiagPresentation StringPres(string qualifier, string description)
        {
            var pres = Pres(qualifier, description);
            pres.InternalDataType = 6;
            return pres;
        }

        static DiagPreparation Prep(string qualifier, string name, int bitPosition, int sizeInBits, int presIndex, bool input, byte[] defaultValue = null)
        {
            var bits = new BitArray(sizeInBits);
            if (defaultValue != null)
            {
                BitArrayExtension.CopyBitsClamped(bits, new BitArray(defaultValue));
            }
            return new DiagPreparation
            {
                Qualifier = qualifier,
                Name_CTF = name is null ? -1 : S(name),
                BitPosition = bitPosition,
                SizeInBits = sizeInBits,
                FieldType = input ? DiagPreparation.InferredDataType.PrepPresentationType : DiagPreparation.InferredDataType.NativePresentationType,
                PrepPresPoolIndex = input ? presIndex : -1,
                PresPoolIndex = input ? -1 : presIndex,
                Dump = defaultValue ?? new byte[(sizeInBits + 7) / 8],
                Content = bits,
            };
        }

        static DiagService Service(string qualifier, string name, string description, DiagService.ServiceType type, byte[] request)
        {
            return new DiagService
            {
                Qualifier = qualifier,
                Name_CTF = name is null ? -1 : S(name),
                Description_CTF = description is null ? -1 : S(description),
                DataClass_ServiceType = (ushort)type,
                RequestBytes = request,
            };
        }

        public static CaesarContainer Create()
        {
            Strings = new List<string>();
            var ecu = new ECU
            {
                Qualifier = "DEMO_KI",
                EcuName_CTF = -1,
                EcuDescription_CTF = S("Kombiinstrument (Demo, keine echten Daten)"),
            };

            // presentations
            ecu.GlobalPresentations.Add(StringPres("PRES_FIN", "Fahrzeugidentifizierungsnummer"));                                    // 0
            ecu.GlobalPresentations.Add(ScaledPres("PRES_KM", "Kilometerstand", "km", 1f, 0f));                                         // 1
            ecu.GlobalPresentations.Add(ScaledPres("PRES_SPANNUNG", "Versorgungsspannung", "V", 0.1f, 0f));                              // 2
            ecu.GlobalPresentations.Add(EnumPres("PRES_TUER", "Zustand Fahrertür", (0, "geschlossen"), (1, "geöffnet")));               // 3
            ecu.GlobalPresentations.Add(ScaledPres("PRES_AUSSENTEMP", "Außentemperatur", "°C", 0.5f, -40f));                           // 4

            ecu.GlobalPrepPresentations.Add(EnumPres("PREP_LAMPE", "Leuchte", (0, "Abblendlicht links"), (1, "Abblendlicht rechts"), (2, "Nebelscheinwerfer"), (3, "Blinker links")));  // 0
            ecu.GlobalPrepPresentations.Add(EnumPres("PREP_EINAUS", "Ein/Aus", (0, "Aus"), (1, "Ein")));                                // 1
            ecu.GlobalPrepPresentations.Add(ScaledPres("PREP_INTERVALL", "Wartungsintervall", "km", 100f, 0f));                         // 2
            ecu.GlobalPrepPresentations.Add(UIntPres("PREP_DAUER", "Dauer", "s"));                                                     // 3

            var services = new List<DiagService>();

            var vin = Service("DT_STO_Fahrzeugidentnummer", "Fahrzeugidentnummer lesen", "Liest die im Steuergerät gespeicherte Fahrgestellnummer aus.", DiagService.ServiceType.StoredData, new byte[] { 0x22, 0xF1, 0x90 });
            vin.OutputPreparations.Add(new List<DiagPreparation> { Prep("FIN", null, 24, 17 * 8, 0, false) });
            services.Add(vin);

            var km = Service("DT_Kilometerstand", "Kilometerstand lesen", null, DiagService.ServiceType.Data, new byte[] { 0x22, 0x01, 0x02 });
            km.OutputPreparations.Add(new List<DiagPreparation> { Prep("KM", null, 24, 24, 1, false) });
            services.Add(km);

            var status = Service("DT_Status_Fahrzeug", "Fahrzeugzustand lesen", "Spannung, Türen und Außentemperatur", DiagService.ServiceType.Data, new byte[] { 0x22, 0x01, 0x10 });
            status.OutputPreparations.Add(new List<DiagPreparation>
            {
                Prep("Spannung", null, 24, 8, 2, false),
                Prep("Tuer_FA", null, 32, 8, 3, false),
                Prep("Aussentemperatur", null, 40, 8, 4, false),
            });
            services.Add(status);

            var lamp = Service("IOC_Leuchtenansteuerung", "Leuchte ansteuern", "Schaltet eine Leuchte für den Test ein oder aus.", DiagService.ServiceType.IoControl, new byte[] { 0x2F, 0x02, 0x00, 0x00, 0x00 });
            lamp.InputPreparations.Add(Prep("Leuchte", "Leuchte", 24, 8, 0, true));
            lamp.InputPreparations.Add(Prep("Zustand", "Zustand", 32, 8, 1, true));
            lamp.InputPreparationPresentations.AddRange(lamp.InputPreparations);
            services.Add(lamp);

            var interval = Service("DL_Wartungsintervall_schreiben", "Wartungsintervall schreiben", "Ändert das Serviceintervall im Kombiinstrument.", DiagService.ServiceType.Download, new byte[] { 0x2E, 0x01, 0x20, 0x00, 0x96 });
            interval.InputPreparations.Add(Prep("Intervall", "Wartungsintervall", 24, 16, 2, true, new byte[] { 0x00, 0x96 }));
            interval.InputPreparationPresentations.AddRange(interval.InputPreparations);
            services.Add(interval);

            services.Add(Service("FN_Fehlerspeicher_loeschen", "Fehlerspeicher löschen", "Löscht alle gespeicherten Fehler.", DiagService.ServiceType.DiagnosticFunction, new byte[] { 0x14, 0xFF, 0xFF, 0xFF }));
            services.Add(Service("SES_Erweiterte_Diagnosesitzung", "Erweiterte Diagnosesitzung starten", null, DiagService.ServiceType.Session, new byte[] { 0x10, 0x03 }));
            services.Add(Service("SEC_SecurityAccess_RequestSeed", null, null, DiagService.ServiceType.DiagnosticFunction, new byte[] { 0x27, 0x01 }));
            services.Add(Service("RT_Lenkwinkelsensor_Kalibrierung", "Lenkwinkelsensor kalibrieren", "Nur bei Stillstand und geradeaus stehenden Rädern durchführen.", DiagService.ServiceType.Routine, new byte[] { 0x31, 0x01, 0x02, 0x01 }));
            services.Add(Service("DT_Memory_nur_Entwicklung_Length", null, null, DiagService.ServiceType.Data, new byte[] { 0x22, 0xEF, 0x00 }));

            ecu.GlobalDiagServices.AddRange(services);

            // coding
            var domain = new VCDomain
            {
                Qualifier = "CODING_KI",
                Name_CTF = S("Grundcodierung Kombiinstrument"),
                Description_CTF = -1,
                ReadServiceName = "DT_STO_Fahrzeugidentnummer",
                WriteServiceName = "",
                DumpSize = 4,
            };
            VCFragment Fragment(string qualifier, string name, int bitPos, int bitLength, params (byte value, string option)[] options)
            {
                var f = new VCFragment { Qualifier = qualifier, Name_CTF = S(name), Description_CTF = -1, ByteBitPos = bitPos, BitLength = bitLength };
                foreach (var (value, option) in options)
                {
                    f.Subfragments.Add(new VCSubfragment { Name_CTF = -1, Description_CTF = S(option), Dump = new[] { value } });
                }
                return f;
            }
            domain.VCFragments.Add(Fragment("Laendervariante", "Länderversion", 0, 8, (0, "Europa"), (1, "USA"), (2, "Japan"), (3, "Rest der Welt")));
            domain.VCFragments.Add(Fragment("Tagfahrlicht", "Tagfahrlicht", 8, 1, (0, "nicht aktiv"), (1, "aktiv")));
            domain.VCFragments.Add(Fragment("Anhaengerkupplung", "Anhängerkupplung", 9, 1, (0, "nicht verbaut"), (1, "verbaut")));
            domain.VCFragments.Add(Fragment("Einheit", "Einheit Wegstrecke", 10, 1, (0, "Kilometer"), (1, "Meilen")));
            domain.VCFragments.Add(Fragment("Sprache", "Sprache Anzeige", 16, 8, (0, "Deutsch"), (1, "Englisch"), (2, "Französisch")));
            ecu.GlobalVCDs.Add(domain);

            // fault codes
            void Dtc(string code, string text) => ecu.GlobalDTCs.Add(new DTC { Qualifier = code, Description_CTF = S(text), Reference_CTF = -1 });
            Dtc("B1000", "Steuergerät ist defekt");
            Dtc("B1001", "Versorgungsspannung zu niedrig");
            Dtc("B1002", "Versorgungsspannung zu hoch");
            Dtc("B1203", "Außentemperaturfühler: Kurzschluss nach Masse");
            Dtc("B1204", "Außentemperaturfühler: Unterbrechung");
            Dtc("U0100", "Keine Kommunikation mit Motorsteuerung");
            Dtc("U0155", "CAN-Botschaft vom Kombiinstrument fehlt");
            Dtc("B1A21", "Tankfüllstandsgeber: Signal unplausibel");

            // connection
            var subtype = new ECUInterfaceSubtype { Qualifier = "HSCAN_UDS_500", Name_CTF = -1, Description_CTF = -1, ParentInterfaceIndex = 0, PhysicalProtocolRaw = 0 };
            ecu.ECUInterfaceSubtypes.Add(subtype);
            ecu.ECUInterfaces.Add(new ECUInterface { Qualifier = "UDS", Name_CTF = -1, Description_CTF = -1 });

            // two versions of the same ECU
            var variantA = new ECUVariant { Qualifier = "KI_DEMO_Version_A", Name_CTF = S("Kombiinstrument Softwarestand 12/2019"), Description_CTF = -1 };
            variantA.DiagServices = services.ToArray();
            variantA.VCDomains.Add(domain);
            variantA.DTCs = ecu.GlobalDTCs.ToArray();
            variantA.ParentECU = ecu;

            var container = new CaesarContainer();
            container.CaesarCTFHeader = new CTFHeader { CtfLanguages = new List<CTFLanguage> { new CTFLanguage { Qualifier = "Deutsch", StringEntries = Strings } } };
            container.CaesarECUs.Add(ecu);
            ecu.Restore(container.CaesarCTFHeader.CtfLanguages[0], container);

            // added after Restore, which would otherwise rebuild the variant's lists from file offsets
            ecu.ECUVariants.Add(variantA);
            return container;
        }
    }
}
