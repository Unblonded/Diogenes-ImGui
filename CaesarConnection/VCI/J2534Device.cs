using SAE.J2534;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using CaesarConnection.VCI.Channel;
using CaesarConnection.ComParam;
using System.IO;

namespace CaesarConnection.VCI
{
    public class J2534Device : BaseDevice
    {
        API Api;
        Device PhysicalDevice;
        // size of the driver's C 'unsigned long', 4 on Windows; 8 or 4 on macOS/Linux depending on the driver
        public int LongSize = APIInfo.DefaultLongSize;

        // driver libraries the user picked by hand (macOS/Linux have no registry to list them)
        public static List<string> ExtraDriverPaths = new List<string>();

        public void Connect()
        {
            APIFactory.GetAPI(Parameter, LongSize);
        }

        public static J2534Device[] GetJ2534Devices() 
        {
            List<J2534Device> result = new List<J2534Device>();
            foreach (APIInfo apiInfo in APIFactory.GetAPIinfo())
            {
                result.Add(new J2534Device
                {
                    Name = apiInfo.Name,
                    Parameter = apiInfo.Filename,
                    LongSize = apiInfo.LongSize,
                });
            }
            foreach (string path in ExtraDriverPaths)
            {
                if (File.Exists(path) && !result.Any(x => x.Parameter == path))
                {
                    result.Add(new J2534Device
                    {
                        Name = Path.GetFileName(path),
                        Parameter = path,
                    });
                }
            }
            return result.ToArray();
        }

        public override void OpenVCI()
        {
            Api = APIFactory.GetAPI(Parameter, LongSize); // parameter here refers to the dll path
            PhysicalDevice = Api.GetDevice();
            Console.WriteLine($"J2534 device {Path.GetFileName(Parameter)} opened");
        }

        public override BaseChannel CreateChannel(ParameterSet cp, System.IO.TextWriter traceWriter)
        {
            // J2534 adapters allow one ISO15765 channel at a time, so a second ECU on the same bus
            // shares the open channel with its own flow control filter
            int baudrate = (int)cp.GetParameter(CP.Baudrate);
            var shared = Channels.OfType<J2534Channel>().FirstOrDefault(x => x.Baudrate == baudrate);
            if (shared != null)
            {
                shared.AddEcu(cp);
                return shared;
            }
            J2534Channel newChannel = new J2534Channel(PhysicalDevice, cp);
            newChannel.TraceWriter = traceWriter;
            Channels.Add(newChannel);
            return newChannel;
        }

        public override void ReleaseChannel(BaseChannel channel, ParameterSet cp = null)
        {
            // keep a shared channel open while other ECUs still use it
            if (channel is J2534Channel j2534 && cp != null && j2534.RemoveEcu(cp))
            {
                return;
            }
            base.ReleaseChannel(channel, cp);
        }

        public override void Dispose()
        {
            foreach (var channel in Channels) 
            {
                try
                {
                    channel.Dispose();
                }
                catch 
                {
                    // nothing to do here
                }
            }
            Channels.Clear();

            PhysicalDevice?.Dispose();
            Api?.Dispose();
            Console.WriteLine($"Disposed J2534Device");
            base.Dispose();
        }

        public override string ToString()
        {
            return $"J2534 : {this.Name}";
        }

        public override int GetPersistentHash()
        {
            string uniqueHandle = $"{GetType().Name}-{Name}-{Parameter}";
            // lazy 32-bit hash to store as user pref
            byte[] chunks = Encoding.UTF8.GetBytes(uniqueHandle);
            int a = 0, b = 0;
            foreach (byte chunk in chunks)
            {
                a ^= chunk;
                b += chunk;
                if ((a & 1) != 0)
                {
                    a >>= 1;
                    a |= 0x800000;
                    b <<= 1;
                    b |= 1;
                }
            }
            return a ^ b;
        }
    }
}
