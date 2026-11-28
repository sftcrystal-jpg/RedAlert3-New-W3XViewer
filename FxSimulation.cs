using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Windows.Media.Media3D;

namespace W3xScene
{
    // Windows JSRT runs FXPreview's simulator without opening a browser or requiring Node.
    sealed class FxSimulation : IDisposable
    {
        const string Library="chakra.dll";
        [DllImport(Library)] static extern int JsCreateRuntime(int attributes,IntPtr callback,out IntPtr runtime);
        [DllImport(Library)] static extern int JsCreateContext(IntPtr runtime,out IntPtr context);
        [DllImport(Library)] static extern int JsSetCurrentContext(IntPtr context);
        [DllImport(Library)] static extern int JsDisposeRuntime(IntPtr runtime);
        [DllImport(Library)] static extern int JsAddRef(IntPtr value,out uint count);
        [DllImport(Library)] static extern int JsRelease(IntPtr value,out uint count);
        [DllImport(Library,CharSet=CharSet.Unicode)] static extern int JsRunScript(string script,UIntPtr cookie,string source,out IntPtr result);
        [DllImport(Library)] static extern int JsGetGlobalObject(out IntPtr value);
        [DllImport(Library,CharSet=CharSet.Unicode)] static extern int JsGetPropertyIdFromName(string name,out IntPtr id);
        [DllImport(Library)] static extern int JsGetProperty(IntPtr obj,IntPtr id,out IntPtr value);
        [DllImport(Library,CharSet=CharSet.Unicode)] static extern int JsPointerToString(string text,UIntPtr length,out IntPtr value);
        [DllImport(Library)] static extern int JsDoubleToNumber(double number,out IntPtr value);
        [DllImport(Library)] static extern int JsCallFunction(IntPtr function,IntPtr[] args,ushort count,out IntPtr result);
        [DllImport(Library)] static extern int JsGetTypedArrayStorage(IntPtr array,out IntPtr data,out uint bytes,out int type,out int elementSize);
        [DllImport(Library)] static extern int JsStringToPointer(IntPtr value,out IntPtr chars,out UIntPtr length);
        [DllImport(Library)] static extern int JsGetAndClearException(out IntPtr error);
        [DllImport(Library)] static extern int JsConvertValueToString(IntPtr value,out IntPtr text);

        IntPtr runtime,context,global;
        readonly Dictionary<string,IntPtr> functions=new Dictionary<string,IntPtr>();
        public FxSimulation(string htmlPath)
        {
            string html=File.ReadAllText(htmlPath);
            int start=html.IndexOf("<script>",StringComparison.Ordinal);
            int end=start<0 ? -1 : html.IndexOf("const VSH =",start,StringComparison.Ordinal);
            if(end<0 || html.IndexOf("class World",start,end-start,StringComparison.Ordinal)<0)
                throw new InvalidDataException("此版本的 fxpreview 缺少兼容的 World 模拟器。");
            string adapter;
            using(var reader=new StreamReader(Assembly.GetExecutingAssembly().GetManifestResourceStream("FxDraw.js")))
                adapter=reader.ReadToEnd();
            Check(JsCreateRuntime(0,IntPtr.Zero,out runtime));
            try
            {
                Check(JsCreateContext(runtime,out context));
                uint roots;Check(JsAddRef(context,out roots));Enter();
                IntPtr ignored;
                Check(JsRunScript(html.Substring(start+8,end-start-8)+adapter,UIntPtr.Zero,"FXPreview/World",out ignored));
                Check(JsGetGlobalObject(out global));
                foreach(var name in new[]{"installEffect","startEmitter","stopEmitter","sampleEmitter"})
                {
                    IntPtr id,fn;Check(JsGetPropertyIdFromName(name,out id));Check(JsGetProperty(global,id,out fn));
                    functions.Add(name,fn);
                }
            }
            catch{Dispose();throw;}
        }
        void Enter(){Check(JsSetCurrentContext(context));}
        static string Read(IntPtr value)
        {
            IntPtr ptr;UIntPtr n;Check(JsStringToPointer(value,out ptr,out n));
            return Marshal.PtrToStringUni(ptr,checked((int)n.ToUInt64()));
        }
        static void Check(int code)
        {
            if(code==0)return;
            IntPtr error,msg;
            if(JsGetAndClearException(out error)==0 && JsConvertValueToString(error,out msg)==0)
                throw new InvalidDataException(Read(msg));
            throw new InvalidOperationException("FXPreview JavaScript 错误 0x"+code.ToString("X"));
        }
        static IntPtr Number(double n){IntPtr v;Check(JsDoubleToNumber(n,out v));return v;}
        IntPtr Call(string name,params IntPtr[] args)
        {
            args[0]=global;
            IntPtr result;Check(JsCallFunction(functions[name],args,(ushort)args.Length,out result));return result;
        }
        public string Install(int key,string json)
        {
            Enter();IntPtr value;Check(JsPointerToString(json,(UIntPtr)(uint)json.Length,out value));
            return Read(Call("installEffect",global,Number(key),value));
        }
        public void Start(int id,int key,bool water)
        {Enter();Call("startEmitter",global,Number(id),Number(key),Number(water?1:0));}
        public void Stop(int id){Enter();Call("stopEmitter",global,Number(id));}
        public float[] Sample(int id,int steps,Matrix3D m)
        {
            Enter();
            var result=Call("sampleEmitter",global,Number(id),Number(steps),Number(m.M11),Number(m.M12),Number(m.M13),
                Number(m.M21),Number(m.M22),Number(m.M23),Number(m.M31),Number(m.M32),Number(m.M33),
                Number(m.OffsetX),Number(m.OffsetY),Number(m.OffsetZ));
            uint bytes;int type,size;IntPtr data;Check(JsGetTypedArrayStorage(result,out data,out bytes,out type,out size));
            var values=new float[bytes/4];if(values.Length>0)Marshal.Copy(data,values,0,values.Length);
            return values;
        }
        public void Dispose()
        {
            if(runtime==IntPtr.Zero)return;
            JsSetCurrentContext(IntPtr.Zero);
            uint roots;if(context!=IntPtr.Zero)JsRelease(context,out roots);
            JsDisposeRuntime(runtime);runtime=IntPtr.Zero;
        }
    }
}
