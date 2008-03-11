using System;
using System.Collections.Generic;
using System.Reflection;
using System.Runtime.InteropServices;

using IronPython.Hosting;
using IronPython.Modules;
using IronPython.Runtime;
using IronPython.Runtime.Calls;
using IronPython.Runtime.Exceptions;
using IronPython.Runtime.Operations;

using Ironclad.Structs;

namespace Ironclad
{
    public interface IAllocator
    {
        IntPtr Alloc(int bytes);
        IntPtr Realloc(IntPtr old, int bytes);
        void Free(IntPtr address);
    }
    
    public class HGlobalAllocator : IAllocator
    {
        public IntPtr 
        Alloc(int bytes)
        {
            return Marshal.AllocHGlobal(bytes);
        }
        public IntPtr
        Realloc(IntPtr old, int bytes)
        {
            return Marshal.ReAllocHGlobal(old, (IntPtr)bytes);
        }
        public void 
        Free(IntPtr address)
        {
            Marshal.FreeHGlobal(address);
        }
    }

    public enum UnmanagedDataMarker
    {
        PyStringObject,
        PyTupleObject,
        None,
    }

    public class BadRefCountException : Exception
    {
        public BadRefCountException(string message): base(message)
        {
        }
    }


    public partial class Python25Mapper : PythonMapper
    {
        private PythonEngine engine;
        private Dictionary<IntPtr, object> ptrmap;
        private Dictionary<object, IntPtr> objmap;
        private List<IntPtr> tempPtrs;
        private List<IntPtr> tempObjects;
        private IAllocator allocator;
        private object _lastException;
        
        public Python25Mapper(PythonEngine eng): this(eng, new HGlobalAllocator())
        {
        }
        
        public Python25Mapper(PythonEngine eng, IAllocator alloc)
        {
            this.engine = eng;
            this.allocator = alloc;
            this.ptrmap = new Dictionary<IntPtr, object>();
            this.objmap = new Dictionary<object, IntPtr>();
            this.tempPtrs = new List<IntPtr>();
            this.tempObjects = new List<IntPtr>();
            this._lastException = null;
        }
        
        public IntPtr 
        Store(object obj)
        {
            if (obj != null && obj.GetType() == typeof(UnmanagedDataMarker))
            {
                throw Ops.TypeError("UnmanagedDataMarkers should not be stored by clients.");
            }
            if (obj == null)
            {
                obj = UnmanagedDataMarker.None;
            }
            if (this.objmap.ContainsKey(obj))
            {
                IntPtr ptr = this.objmap[obj];
                this.IncRef(ptr);
                return ptr;
            }
            return this.StoreDispatch(obj);
        }
        
        
        private IntPtr
        StoreObject(object obj)
        {
            IntPtr ptr = this.allocator.Alloc(Marshal.SizeOf(typeof(PyObject)));
            CPyMarshal.WriteInt(ptr, 1);
            IntPtr typePtr = CPyMarshal.Offset(ptr, Marshal.OffsetOf(typeof(PyObject), "ob_type"));
            CPyMarshal.WritePtr(typePtr, IntPtr.Zero);
            this.StoreUnmanagedData(ptr, obj);
            return ptr;
        }
        
        public void
        StoreUnmanagedData(IntPtr ptr, object obj)
        {
            this.ptrmap[ptr] = obj;
            this.objmap[obj] = ptr;
        }
        
        private static char
        CharFromByte(byte b)
        {
            return (char)b;
        }
        
        private static byte
        ByteFromChar(char c)
        {
            return (byte)c;
        }
        
        public object 
        Retrieve(IntPtr ptr)
        {
            object possibleMarker = this.ptrmap[ptr];
            if (possibleMarker.GetType() == typeof(UnmanagedDataMarker))
            {
                UnmanagedDataMarker marker = (UnmanagedDataMarker)possibleMarker;
                switch (marker)
                {
                    case UnmanagedDataMarker.None:
                        return null;
                    
                    case UnmanagedDataMarker.PyStringObject:
                        IntPtr buffer = CPyMarshal.Offset(ptr, Marshal.OffsetOf(typeof(PyStringObject), "ob_sval"));
                        IntPtr lengthPtr = CPyMarshal.Offset(ptr, Marshal.OffsetOf(typeof(PyStringObject), "ob_size"));
                        int length = CPyMarshal.ReadInt(lengthPtr);
                        
                        byte[] bytes = new byte[length];
                        Marshal.Copy(buffer, bytes, 0, length);
                        char[] chars = Array.ConvertAll<byte, char>(
                            bytes, new Converter<byte, char>(CharFromByte));
                        this.StoreUnmanagedData(ptr, new string(chars));
                        break;
                    
                    case UnmanagedDataMarker.PyTupleObject:
                        IntPtr itemCountPtr = CPyMarshal.Offset(ptr, Marshal.OffsetOf(typeof(PyTupleObject), "ob_size"));
                        int itemCount = CPyMarshal.ReadInt(itemCountPtr);
                        IntPtr itemAddressPtr = CPyMarshal.Offset(ptr, Marshal.OffsetOf(typeof(PyTupleObject), "ob_item"));
                        
                        object[] items = new object[itemCount];
                        for (int i = 0; i < itemCount; i++)
                        {
                            IntPtr itemPtr = CPyMarshal.ReadPtr(itemAddressPtr);
                            items[i] = this.Retrieve(itemPtr);
                            itemAddressPtr = CPyMarshal.Offset(itemAddressPtr, CPyMarshal.PtrSize);
                        }
                        this.StoreUnmanagedData(ptr, Tuple.MakeTuple(items));
                        break;
                    
                    default:
                        throw new Exception("Found impossible data in pointer map");
                }
            }
            return this.ptrmap[ptr];
        }
        
        public int 
        RefCount(IntPtr ptr)
        {
            if (this.ptrmap.ContainsKey(ptr))
            {
                int result = CPyMarshal.ReadInt(ptr);
                return result;
            }
            else
            {
                throw new KeyNotFoundException(String.Format(
                    "RefCount: missing key in pointer map: {0}", ptr));
            }
        }
        
        public void 
        IncRef(IntPtr ptr)
        {
            if (this.ptrmap.ContainsKey(ptr))
            {
                int count = CPyMarshal.ReadInt(ptr);
                CPyMarshal.WriteInt(ptr, count + 1);
            }
            else
            {
                throw new KeyNotFoundException(String.Format(
                    "IncRef: missing key in pointer map: {0}", ptr));
            }
        }
        
        public void 
        DecRef(IntPtr ptr)
        {
            if (this.ptrmap.ContainsKey(ptr))
            {
                int count = this.RefCount(ptr);
                if (count == 0)
                {
                    throw new BadRefCountException("Trying to DecRef an object with ref count 0");
                }
                
                if (count == 1)
                {
                    IntPtr typePtrPtr = CPyMarshal.Offset(ptr, Marshal.OffsetOf(typeof(PyObject), "ob_type"));
                    IntPtr typePtr = CPyMarshal.ReadPtr(typePtrPtr);
                    if (typePtr != IntPtr.Zero)
                    {
                        IntPtr deallocFPPtr = CPyMarshal.Offset(typePtr, Marshal.OffsetOf(typeof(PyTypeObject), "tp_dealloc"));
                        IntPtr deallocFP = CPyMarshal.ReadPtr(deallocFPPtr);
                        if (deallocFP != IntPtr.Zero)
                        {
                            CPython_destructor_Delegate deallocDgt = (CPython_destructor_Delegate)Marshal.GetDelegateForFunctionPointer(
                                deallocFP, typeof(CPython_destructor_Delegate));
                            deallocDgt(ptr);
                            return;
                        }
                    }
                    this.Free(ptr);
                }
                else
                {
                    CPyMarshal.WriteInt(ptr, count - 1);
                }
            }
            else
            {
                throw new KeyNotFoundException(String.Format(
                    "DecRef: missing key in pointer map: {0}", ptr));
            }
        }
        
        public virtual void 
        Free(IntPtr ptr)
        {
            this.objmap.Remove(this.ptrmap[ptr]);
            this.ptrmap.Remove(ptr);
            this.allocator.Free(ptr);
        }

        public void RememberTempPtr(IntPtr ptr)
        {
            this.tempPtrs.Add(ptr);
        }

        public void RememberTempObject(IntPtr ptr)
        {
            this.tempObjects.Add(ptr);
        }

        public void FreeTemps()
        {
            foreach (IntPtr ptr in this.tempPtrs)
            {
                this.allocator.Free(ptr);
            }
            foreach (IntPtr ptr in this.tempObjects)
            {
                this.DecRef(ptr);
            }
            this.tempObjects.Clear();
            this.tempPtrs.Clear();
        }
        
        public object LastException
        {
            get
            {
                return this._lastException;
            }
            set
            {
                this._lastException = value;
            }
        }
        
        public override void
        PyErr_SetString(IntPtr excTypePtr, string message)
        {
            if (excTypePtr == IntPtr.Zero)
            {
                this._lastException = new Exception(message);
            }
            else
            {
                object excType = this.Retrieve(excTypePtr);
                this._lastException = Ops.Call(excType, new object[1]{ message });
            }
        }
        
        private PythonModule GetPythonModule(EngineModule eModule)
        {
            PropertyInfo info = (PropertyInfo)(eModule.GetType().GetMember(
                "Module", BindingFlags.NonPublic | BindingFlags.Instance)[0]);
            return (PythonModule)info.GetValue(eModule, null);
        }
        
        
        public override int
        PyCallable_Check(IntPtr objPtr)
        {
            if (Builtin.Callable(this.Retrieve(objPtr)))
            {
                return 1;
            }
            return 0;
        }
        
        public override IntPtr
        PyObject_Call(IntPtr objPtr, IntPtr argsPtr, IntPtr kwargsPtr)
        {
            // ignore kwargsPtr for now
            ICallerContext context = this.GetPythonModule(
                this.engine.DefaultModule);
            object obj = this.Retrieve(objPtr);
            Tuple args = (Tuple)this.Retrieve(argsPtr);
            object[] argsArray = new object[args.Count];
            args.CopyTo(argsArray, 0);
            
            object result = Ops.CallWithContext(
                context, obj, argsArray);
            return this.Store(result);
        }
        
        public override IntPtr
        PyObject_GetAttrString(IntPtr objPtr, string name)
        {
            object obj = this.Retrieve(objPtr);
            object attr = null;
            ICallerContext context = this.GetPythonModule(
                this.engine.DefaultModule);
            if (Ops.TryGetAttr(context, obj, SymbolTable.StringToId(name), out attr))
            {
                return this.Store(attr);
            }
            this.LastException = new PythonNameErrorException(name);
            return IntPtr.Zero;
        }
        
        
        public override void
        Fill__Py_NoneStruct(IntPtr address)
        {
            PyObject none = new PyObject();
            none.ob_refcnt = 1;
            none.ob_type = IntPtr.Zero;
            Marshal.StructureToPtr(none, address, false);
            this.StoreUnmanagedData(address, UnmanagedDataMarker.None);
        }
        
        
        public override IntPtr
        PyInt_FromLong(int value)
        {
            return this.Store(value);
        }
        
        
        public override IntPtr
        PyInt_FromSsize_t(int value)
        {
            return this.Store(value);
        }
        
        
        public override IntPtr
        PyFloat_FromDouble(double value)
        {
            return this.Store(value);
        }
        
    }

}
