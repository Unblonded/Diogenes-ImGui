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
using System.Runtime.InteropServices;

namespace SAE.J2534
{
    internal class HeapMessage : Common.UnmanagedDisposable
    {
        private readonly int longSize;
        public IntPtr Ptr { get; }

        public HeapMessage(Protocol ProtocolID, int LongSize)
        {
            longSize = LongSize;
            Ptr = Marshal.AllocHGlobal(NativeLong.MessageSize(LongSize));
            this.ProtocolID = ProtocolID;
        }
        public HeapMessage(Protocol ProtocolID, Message Message, int LongSize) : this(ProtocolID, Message.TxFlags, Message.Data, LongSize)
        {
        }
        public HeapMessage(Protocol ProtocolID, TxFlag TxFlags, IEnumerable<byte> Data, int LongSize) : this(ProtocolID, LongSize)
        {
            this.TxFlags = TxFlags;
            this.Data = Data;
        }

        public Message ToMessage()
        {
            return new Message((byte[])Data, RxStatus, Timestamp);
        }

        public Protocol ProtocolID
        {
            get { return (Protocol)NativeLong.Read(Ptr, 0, longSize); }
            set { NativeLong.Write(Ptr, 0, longSize, (int)value); }
        }
        public RxFlag RxStatus
        {
            get { return (RxFlag)NativeLong.Read(Ptr, longSize, longSize); }
            set { NativeLong.Write(Ptr, longSize, longSize, (int)value); }
        }
        public TxFlag TxFlags
        {
            get { return (TxFlag)NativeLong.Read(Ptr, longSize * 2, longSize); }
            set { NativeLong.Write(Ptr, longSize * 2, longSize, (int)value); }
        }
        public uint Timestamp
        {
            get { return (uint)NativeLong.Read(Ptr, longSize * 3, longSize); }
            set { NativeLong.Write(Ptr, longSize * 3, longSize, (int)value); }
        }
        public uint ExtraDataIndex
        {
            get { return (uint)NativeLong.Read(Ptr, longSize * 5, longSize); }
            set { NativeLong.Write(Ptr, longSize * 5, longSize, (int)value); }
        }
        public int Length
        {
            get { return NativeLong.Read(Ptr, longSize * 4, longSize); }
            private set
            {
                if (value > NativeLong.MessageSize(longSize)) throw new ArgumentException("Message Data.Length is greater than fixed maximum");
                NativeLong.Write(Ptr, longSize * 4, longSize, value);
            }
        }
        public IEnumerable<byte> Data
        {
            get
            {
                byte[] data = new byte[Length];
                Marshal.Copy(IntPtr.Add(Ptr, NativeLong.MessageDataOffset(longSize)), data, 0, data.Length);
                return data;
            }
            set
            {
                IntPtr DataPtr = IntPtr.Add(Ptr, NativeLong.MessageDataOffset(longSize));  //Offset to data array
                if (value is byte[])  //Byte[] is fastest
                {
                    var ValueAsArray = (byte[])value;
                    Length = ValueAsArray.Length;
                    Marshal.Copy(ValueAsArray, 0, DataPtr, ValueAsArray.Length);
                }
                else if (value is IList<byte>)   //Collection with indexer is second best
                {
                    var ValueAsList = (IList<byte>)value;
                    int length = ValueAsList.Count;
                    Length = length;
                    for (int indexer = 0; indexer < length; indexer++)
                    {
                        Marshal.WriteByte(DataPtr, indexer, ValueAsList[indexer]);
                    }
                }
                else if (value is IEnumerable<byte>)    //Enumerator is third
                {
                    int index_count = 0;
                    foreach (byte b in value)
                    {
                        Marshal.WriteByte(DataPtr, index_count, b);
                        index_count++;
                    }
                    Length = index_count;  //Set length
                }
            }
        }
        public static explicit operator IntPtr(HeapMessage HeapMessage)
        {
            return HeapMessage.Ptr;
        }
        protected override void DisposeUnmanaged()
        {
            Marshal.FreeHGlobal(Ptr);
        }
    }
}
