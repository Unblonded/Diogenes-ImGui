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
using System.Runtime.InteropServices;

namespace SAE.J2534
{
    internal class HeapInt : Common.UnmanagedDisposable
    {
        public IntPtr Ptr { get; }
        public HeapInt() 
        {
            //8 zeroed bytes fit an 'unsigned long' of either size: a driver writing 4 bytes leaves the top half zero,
            //and a driver reading 4 bytes sees the low half (little endian)
            Ptr = Marshal.AllocHGlobal(8);
            Marshal.WriteInt64(Ptr, 0);
        }
        public HeapInt(int i) : this()
        {
            Value = i;
        }
        public int Value
        {
            get { return (int)Marshal.ReadInt64(Ptr); }
            set { Marshal.WriteInt64(Ptr, (uint)value); }
        }
        public static explicit operator IntPtr(HeapInt HeapIntPtr)
        {
            return HeapIntPtr.Ptr;
        }
        protected override void DisposeUnmanaged()
        {
            Marshal.FreeHGlobal(Ptr);
        }
    }
}
