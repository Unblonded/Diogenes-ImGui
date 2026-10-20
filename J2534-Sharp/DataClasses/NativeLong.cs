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
    /// <summary>
    /// J2534 structs are built from C 'unsigned long', which is 4 bytes on Windows and on drivers that use u32,
    /// but 8 bytes for C drivers on 64-bit macOS and Linux. These helpers read and write fields of either size.
    /// </summary>
    internal static class NativeLong
    {
        public static int Read(IntPtr Ptr, int Offset, int LongSize)
        {
            return LongSize == 8 ? (int)Marshal.ReadInt64(Ptr, Offset) : Marshal.ReadInt32(Ptr, Offset);
        }
        public static void Write(IntPtr Ptr, int Offset, int LongSize, int Value)
        {
            if (LongSize == 8)
                Marshal.WriteInt64(Ptr, Offset, (uint)Value);
            else
                Marshal.WriteInt32(Ptr, Offset, Value);
        }
        /// <summary>
        /// PASSTHRU_MSG: ProtocolID, RxStatus, TxFlags, Timestamp, DataSize, ExtraDataIndex, then 4128 data bytes
        /// </summary>
        public static int MessageSize(int LongSize)
        {
            return LongSize * 6 + 4128;
        }
        public static int MessageDataOffset(int LongSize)
        {
            return LongSize * 6;
        }
        /// <summary>
        /// Offset of the pointer in {unsigned long count; T* items} lists, which is aligned to the pointer size
        /// </summary>
        public static int ListPointerOffset(int LongSize)
        {
            return Math.Max(LongSize, IntPtr.Size);
        }
    }
}
