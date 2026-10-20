#region License
/*Copyright(c) 2021, Brian Humlicek
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
    /// A {unsigned long NumOfItems; T* pItems} list followed by its items. Items are structs made only of
    /// 'unsigned long' fields, so they are written field by field to suit the driver's long size.
    /// </summary>
    internal abstract class HeapStructArray<T> : Common.UnmanagedDisposable
    {
        private readonly int longSize;
        private readonly int elementSize;
        private readonly IntPtr firstElementPtr;
        public IntPtr Ptr { get; }

        protected abstract int FieldCount { get; }
        protected abstract int[] ToFields(T Element);
        protected abstract T FromFields(int[] Fields);

        private HeapStructArray(int Length, int LongSize)
        {
            longSize = LongSize;
            elementSize = FieldCount * LongSize;
            //Create a blob big enough for all elements, NumOfItems and pItems
            int header = NativeLong.ListPointerOffset(LongSize) + IntPtr.Size;
            Ptr = Marshal.AllocHGlobal(Length * elementSize + header);
            firstElementPtr = IntPtr.Add(Ptr, header);
            Marshal.WriteInt64(Ptr, 0);
            this.Length = Length;
            //Write pItems.  To save complexity, the array immediately follows SConfigArray.
            Marshal.WriteIntPtr(Ptr, NativeLong.ListPointerOffset(LongSize), firstElementPtr);
        }
        protected HeapStructArray(T Element, int LongSize) : this(1, LongSize)
        {
            setIndex(0, Element);
        }
        protected HeapStructArray(T[] Elements, int LongSize) : this(Elements.Length, LongSize)
        {
            //Write the array to the blob
            for (int i = 0; i < Elements.Length; i++)
                setIndex(i, Elements[i]);
        }
        public int Length
        {
            get { return NativeLong.Read(Ptr, 0, longSize); }
            private set { NativeLong.Write(Ptr, 0, longSize, value); }
        }
        public T this[int Index]
        {
            get
            {
                if (Index >= Length || Index < 0) throw new IndexOutOfRangeException("Index out of bounds HeapStructArrayPtr!");
                return getIndex(Index);
            }
            set
            {
                if (Index >= Length || Index < 0) throw new IndexOutOfRangeException("Index out of bounds HeapStructArrayPtr!");
                setIndex(Index, value);
            }
        }
        private T getIndex(int Index)
        {
            IntPtr pElement = IntPtr.Add(firstElementPtr, Index * elementSize);
            int[] fields = new int[FieldCount];
            for (int i = 0; i < fields.Length; i++)
                fields[i] = NativeLong.Read(pElement, i * longSize, longSize);
            return FromFields(fields);
        }
        private void setIndex(int Index, T Element)
        {
            IntPtr pElement = IntPtr.Add(firstElementPtr, Index * elementSize);
            int[] fields = ToFields(Element);
            for (int i = 0; i < fields.Length; i++)
                NativeLong.Write(pElement, i * longSize, longSize, fields[i]);
        }
        public T[] ToArray()
        {
            T[] result = new T[Length];
            for (int i = 0; i < result.Length; i++)
                result[i] = getIndex(i);
            return result;
        }
        public static explicit operator IntPtr(HeapStructArray<T> HeapStructArray)
        {
            return HeapStructArray.Ptr;
        }
        protected override void DisposeUnmanaged()
        {
            Marshal.FreeHGlobal(Ptr);
        }
    }
}
