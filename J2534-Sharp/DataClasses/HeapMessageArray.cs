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
using System.Runtime.CompilerServices;

namespace SAE.J2534
{
    public class HeapMessageArray : Common.UnmanagedDisposable
    {
        private int protocolID;
        private int array_max_length;
        private readonly int longSize;
        private readonly int messageSize;
        private readonly int dataOffset;
        public IntPtr MessagePtr { get; }
        public IntPtr LengthPtr { get; }

        public IntPtr Ptr { get { return MessagePtr; } }

        public HeapMessageArray(Protocol ProtocolID, int Length, int LongSize)
        {
            if (Length < 1) throw new ArgumentException("Length must be at least 1 (HEAPMessageArray");
            array_max_length = Length;
            longSize = LongSize;
            messageSize = NativeLong.MessageSize(LongSize);
            dataOffset = NativeLong.MessageDataOffset(LongSize);
            //The count is an 'unsigned long' of either size, so it gets 8 bytes ahead of the (8 byte aligned) messages
            LengthPtr = Marshal.AllocHGlobal(messageSize * Length + 8);
            Marshal.WriteInt64(LengthPtr, 0);
            MessagePtr = IntPtr.Add(LengthPtr, 8);
            protocolID = (int)ProtocolID;
        }
        public int Length
        {
            get
            {
                return (int)Marshal.ReadInt64(LengthPtr);
            }
            set
            {
                if (value > array_max_length) throw new IndexOutOfRangeException("Length is greater than array bound (HEAPMessageArray)");
                Marshal.WriteInt64(LengthPtr, (uint)value);
            }
        }
        public Message this[int index]
        {
            get
            {
                if (index >= array_max_length || index < 0) throw new IndexOutOfRangeException("Index out of range in J2534HeapMessageArray get[]");
                return getIndex(index);
            }
            set
            {
                if (index >= array_max_length || index < 0) throw new IndexOutOfRangeException("Index out of range in J2534HeapMessageArray set[]");
                setIndex(index, value);
            }
        }
        private Message getIndex(int index)
        {
            IntPtr pMessage = IntPtr.Add(MessagePtr, index * messageSize);
            return new Message(marshalHeapDataToArray(pMessage),
                                    (RxFlag)NativeLong.Read(pMessage, longSize, longSize),
                                    (uint)NativeLong.Read(pMessage, longSize * 3, longSize));
        }
        private void setIndex(int index, Message Message)
        {
            IntPtr pMessage = IntPtr.Add(MessagePtr, index * messageSize);
            NativeLong.Write(pMessage, 0, longSize, protocolID);
            NativeLong.Write(pMessage, longSize * 2, longSize, Message.FlagsAsInt);
            marshalIEnumerableToHeapData(pMessage, Message.Data);
        }
        private byte[] marshalHeapDataToArray(IntPtr pData)
        {
            int Length = NativeLong.Read(pData, longSize * 4, longSize);
            byte[] data = new byte[Length];
            Marshal.Copy(IntPtr.Add(pData, dataOffset), data, 0, Length);
            return data;
        }
        private void marshalIEnumerableToHeapData(IntPtr pMessage, IEnumerable<byte> Data)
        {
            if (Data is byte[])  //Byte[] is fastest
            {
                var DataAsArray = (byte[])Data;
                NativeLong.Write(pMessage, longSize * 4, longSize, DataAsArray.Length);
                Marshal.Copy(DataAsArray, 0, IntPtr.Add(pMessage, dataOffset), DataAsArray.Length);
            }
            else if (Data is ArraySegment<byte>)
            {
                var DataAsArraySegment = (ArraySegment<byte>)Data;
                NativeLong.Write(pMessage, longSize * 4, longSize, DataAsArraySegment.Count);
                Marshal.Copy(DataAsArraySegment.Array, DataAsArraySegment.Offset, IntPtr.Add(pMessage, dataOffset), DataAsArraySegment.Count);
            }
            else if (Data is IList<byte>)   //Collection with indexer is second best
            {
                var DataAsList = (IList<byte>)Data;
                int length = DataAsList.Count;
                IntPtr Ptr = IntPtr.Add(pMessage, dataOffset);  //Offset to data array
                NativeLong.Write(pMessage, longSize * 4, longSize, length);
                for (int indexer = 0; indexer < length; indexer++)
                {
                    Marshal.WriteByte(Ptr, indexer, DataAsList[indexer]);
                }
            }
            else if (Data is IEnumerable<byte>)    //Enumerator is third
            {
                IntPtr Ptr = IntPtr.Add(pMessage, dataOffset);  //Offset to data array
                int index_count = 0;
                foreach (byte b in Data)
                {
                    Marshal.WriteByte(Ptr, index_count, b);
                    index_count++;
                }
                NativeLong.Write(pMessage, longSize * 4, longSize, index_count);  //Set length
            }
        }
        public void FromMessage(Message Message)
        {
            Length = 1;
            setIndex(0, Message);
        }
        public Message ToMessage()
        {
            return getIndex(0);
        }
        public Message[] ToMessageArray()
        {
            int length = Length;
            Message[] result = new Message[length];
            for (int i = 0; i < length; i++)
                result[i] = getIndex(i);
            return result;
        }
        public void FromMessageArray(Message [] Messages)
        {
            Length = Messages.Length;
            for (int i = 0; i < Messages.Length; i++)
            {
                setIndex(i, Messages[i]);
            }
        }
        public void FromDataBytes(IEnumerable<byte> Data, TxFlag TxFlags = TxFlag.NONE)
        {
            Length = 1;
            NativeLong.Write(MessagePtr, 0, longSize, protocolID);
            NativeLong.Write(MessagePtr, longSize * 2, longSize, (int)TxFlags);
            marshalIEnumerableToHeapData(MessagePtr, Data);
        }
        public void FromDataBytesArray(IEnumerable<byte>[] Data, TxFlag TxFlags = TxFlag.NONE)
        {
            Length = Data.Length;
            for (int i = 0; i < Data.Length; i++)
            {
                IntPtr pMessage = IntPtr.Add(MessagePtr, i * messageSize);
                NativeLong.Write(pMessage, 0, longSize, protocolID);
                NativeLong.Write(pMessage, longSize * 2, longSize, (int)TxFlags);
                marshalIEnumerableToHeapData(pMessage, Data[i]);
            }
        }
        public static explicit operator IntPtr(HeapMessageArray HeapMessageArray)
        {
            return HeapMessageArray.Ptr;
        }
        protected override void DisposeUnmanaged()
        {
            Marshal.FreeHGlobal(LengthPtr);
        }
    }
}
