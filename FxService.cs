using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Threading.Tasks;
using System.Web.Script.Serialization;
using FxPreview;

namespace W3xScene
{
    sealed class SpriteMaterial : Mesh
    {
        public bool Ground,TwoQuads,Concentric;
    }
    sealed class FxAsset
    {
        public int Key;
        public SpriteMaterial[] Materials;
        public string Note;
    }

    // A Draw window owns the server it starts. FXPreview chooses the actual loopback
    // port and applies its normal workspace, extra XML roots and texture overrides.
    sealed class FxService : IDisposable
    {
        public static string DefaultExe()
        {
            string sibling=Path.GetFullPath(Path.Combine(AppDomain.CurrentDomain.BaseDirectory,@"..\fxpreview\FXPreview.exe"));
            return File.Exists(sibling) ? sibling : @"D:\RA3 MODSDK-X\Mods\corona\tools\fxpreview\FXPreview.exe";
        }
        readonly string exe;
        readonly HttpClient http=new HttpClient(new HttpClientHandler{UseProxy=false});
        readonly Dictionary<string,Task<FxAsset>> assets=new Dictionary<string,Task<FxAsset>>(Import.Names);
        readonly Dictionary<string,Task<DecodedImage>> textures=new Dictionary<string,Task<DecodedImage>>(Import.Names);
        readonly TaskCompletionSource<Uri> address=new TaskCompletionSource<Uri>();
        Process server;
        Task<Uri> startup;
        public FxSimulation Simulation {get;private set;}
        int nextKey;
        bool disposed;

        public FxService(string path){exe=path;}
        async Task<Uri> Start()
        {
            if(!File.Exists(exe))throw new FileNotFoundException("请通过「选择预览器」指定 FXPreview.exe。");
            string dir=Path.GetDirectoryName(exe);
            Simulation=new FxSimulation(Path.Combine(dir,"viewer.html"));
            server=new Process{StartInfo=new ProcessStartInfo(exe,"--no-open"){
                WorkingDirectory=dir,UseShellExecute=false,CreateNoWindow=true,RedirectStandardOutput=true,
                StandardOutputEncoding=System.Text.Encoding.UTF8},EnableRaisingEvents=true};
            server.OutputDataReceived+=(s,e)=>{
                string line=e.Data;
                if(line==null || !line.TrimStart().StartsWith("viewer ",StringComparison.Ordinal))return;
                int i=line.IndexOf("http://",StringComparison.Ordinal);
                Uri url;
                if(i>=0 && Uri.TryCreate(line.Substring(i).Trim(),UriKind.Absolute,out url) && url.Host=="127.0.0.1")
                    address.TrySetResult(url);
            };
            server.Exited+=(s,e)=>address.TrySetException(new IOException("FXPreview 已退出，请检查其资源目录后重新连接。"));
            server.Start();server.BeginOutputReadLine();
            if(await Task.WhenAny(address.Task,Task.Delay(TimeSpan.FromMinutes(3)))!=address.Task)
                throw new TimeoutException("等待 FXPreview 建立资源索引超时。");
            return await address.Task;
        }
        public Task<Uri> Connect(){return startup ?? (startup=Start());}
        internal class CatalogEntry
        {
            public string kind {get;set;}
            public string id {get;set;}
        }
        class IndexPage { public CatalogEntry[] effects {get;set;} }
        Task<CatalogEntry[]> catalog;
        public async Task<CatalogEntry[]> Catalog()
        {
            if(catalog==null || catalog.IsFaulted)catalog=ReadCatalog();
            return await catalog;
        }
        async Task<CatalogEntry[]> ReadCatalog()
        {
            Uri url=await Connect();
            string json=await http.GetStringAsync(new Uri(url,"api/index"));
            return await Task.Run(()=>new JavaScriptSerializer{MaxJsonLength=int.MaxValue}.Deserialize<IndexPage>(json).effects);
        }
        public Task<FxAsset> Load(string kind,string id)
        {
            string key=kind+"/"+id;Task<FxAsset> task;
            if(!assets.TryGetValue(key,out task))assets[key]=task=LoadAsset(kind,id,++nextKey);
            return task;
        }
        class Descriptor
        {
            public MaterialInfo[] materials {get;set;}
            public string[] absent {get;set;}
        }
        class MaterialInfo
        {
            public string texture {get;set;}
            public int columns {get;set;}
            public int frames {get;set;}
            public string shader {get;set;}
            public bool ground {get;set;}
            public int sort {get;set;}
            public string geometry {get;set;}
            public string pass {get;set;}
        }
        async Task<FxAsset> LoadAsset(string kind,string id,int key)
        {
            Uri url=await Connect();
            string json=await http.GetStringAsync(new Uri(url,"api/effect?kind="+kind+"&id="+Uri.EscapeDataString(id)));
            if(disposed)throw new OperationCanceledException();
            var info=new JavaScriptSerializer().Deserialize<Descriptor>(Simulation.Install(key,json));
            var mats=await Task.WhenAll(info.materials.Select(async m=>{
                var fx=new EffectMaterial{Blend=m.shader.StartsWith("ADDITIVE")?SurfaceBlend.Add:m.shader=="MULTIPLY"?SurfaceBlend.Multiply:SurfaceBlend.Alpha,
                    AlphaTest=m.shader.Contains("ALPHA_TEST")};
                if(!string.IsNullOrEmpty(m.texture))
                {
                    string query="api/tex?id="+Uri.EscapeDataString(m.texture)+"&fr="+m.columns+"&tf="+m.frames;
                    Task<DecodedImage> t;
                    if(!textures.TryGetValue(query,out t))textures[query]=t=LoadTexture(new Uri(url,query));
                    fx.First=await t;
                }
                return new SpriteMaterial{Effect=fx,SortLevel=m.sort,Ground=m.ground,TwoQuads=m.geometry=="TWO_QUADS",Concentric=m.geometry=="TWO_CONCENTRIC_QUADS"};
            }));
            string note=info.absent.Length>0 ? "缺少引用 "+string.Join("、",info.absent) :
                mats.Length==0 ? id+" 没有可绘制的粒子系统" : null;
            var passes=info.materials.Select(m=>m.pass).Where(p=>!string.IsNullOrEmpty(p)).Distinct().ToArray();
            if(passes.Length>0)note=(note==null?"":note+"。")+id+" 含"+string.Join("、",passes)+"，可在独立预览器查看";
            return new FxAsset{Key=key,Materials=mats,Note=note};
        }
        async Task<DecodedImage> LoadTexture(Uri url)
        {
            byte[] bytes=await http.GetByteArrayAsync(url);
            return await Task.Run(()=>TextureLib.FromGdi(bytes));
        }
        public void Dispose()
        {
            disposed=true;address.TrySetCanceled();http.Dispose();
            if(server!=null)
            {
                try{if(!server.HasExited)server.Kill();}catch(InvalidOperationException){}
                server.Dispose();
            }
            if(Simulation!=null)Simulation.Dispose();
        }
    }
}
