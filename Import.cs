using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Media.Media3D;
using FxPreview;

namespace W3xScene
{
    class Attachment
    {
        public Mesh Mesh;
        public string SubObjectId;
        public int Bone;
    }
    class ModelSpec
    {
        public string Name, RigId, Source;
        public Rig Rig;
        public readonly List<Attachment> Parts = new List<Attachment>();
        public readonly List<KeyValuePair<int,CollisionBox>> Boxes = new List<KeyValuePair<int,CollisionBox>>();
        public readonly List<Clip> Clips = new List<Clip>();
        public bool RigOnly { get { return Parts.Count==0 && Boxes.Count==0; } }
    }
    class Import
    {
        internal static readonly StringComparer Names = StringComparer.OrdinalIgnoreCase;
        readonly Dictionary<string,AssetFile> files = new Dictionary<string,AssetFile>(Names);
        readonly Dictionary<string,List<string>> links = new Dictionary<string,List<string>>(Names);
        readonly Dictionary<string,List<string[]>> headers = new Dictionary<string,List<string[]>>(Names);
        readonly Dictionary<string,Material> materials = new Dictionary<string,Material>(Names);
        readonly Dictionary<string,DecodedImage> effectTextures = new Dictionary<string,DecodedImage>(Names);
        readonly List<AssetFile> selected = new List<AssetFile>();
        public int ReadCount { get { return selected.Count; } }
        public readonly List<ModelSpec> Models = new List<ModelSpec>();
        public readonly List<Clip> ExplicitClips = new List<Clip>();
        public readonly List<Rig> ExplicitRigs = new List<Rig>();
        public readonly List<string> Warnings = new List<string>();
        public readonly List<string> EmptyDeclarations = new List<string>();

        static IEnumerable<string> Folders(string source,string name)
        {
            string dir=Path.GetDirectoryName(source);
            yield return dir;
            if (name.Length<2) yield break;
            string shard=name.Substring(0,2);
            // Mod ART before the base SDK ART. Also works when the viewer is moved.
            for (var p=new DirectoryInfo(dir);p!=null;p=p.Parent)
            {
                var art=Path.Combine(p.FullName,"Art",shard);
                if (Directory.Exists(art) && !Names.Equals(art,dir)) yield return art;
            }
        }
        internal static string Locate(string source,string reference)
        {
            string name=reference.Trim().Replace('/',Path.DirectorySeparatorChar);
            if (name.StartsWith("ART:",StringComparison.OrdinalIgnoreCase)) name=name.Substring(4);
            else if (name.Contains(":") && !Path.IsPathRooted(name)) return null;
            var leaf=Path.GetFileName(name);
            foreach (var dir in Folders(source,leaf))
                foreach (var candidate in new[]{Path.Combine(dir,name),Path.Combine(dir,leaf)})
                    if (File.Exists(candidate)) return Path.GetFullPath(candidate);
            return null;
        }
        AssetFile Read(string path)
        {
            AssetFile f;
            if (files.TryGetValue(path,out f)) return f;
            try { f=AssetFile.Read(path); }
            catch(Exception e) { Warnings.Add(Path.GetFileName(path)+"  ·  "+e.Message); }
            files[path]=f;
            return f;
        }
        List<string> Dependencies(AssetFile f)
        {
            List<string> paths;
            if (links.TryGetValue(f.PathName,out paths)) return paths;
            var visited=new HashSet<string>(Names){f.PathName};
            var queue=new Queue<AssetFile>();
            queue.Enqueue(f);
            paths=new List<string>();
            while(queue.Count>0)
            {
                var next=queue.Dequeue();
                foreach(var s in next.Includes)
                {
                    if (!s.EndsWith(".w3x",StringComparison.OrdinalIgnoreCase) && !s.EndsWith(".xml",StringComparison.OrdinalIgnoreCase)) continue;
                    var path=Locate(next.PathName,s);
                    if (path==null || !visited.Add(path)) continue;
                    paths.Add(path);
                    var child=Read(path);
                    if(child!=null) queue.Enqueue(child);
                }
            }
            links[f.PathName]=paths;
            return paths;
        }
        static T Find<T>(AssetFile file,string id) where T:Asset
        {
            return file==null ? null : file.Assets.OfType<T>().FirstOrDefault(a=>Names.Equals(id,a.Id));
        }
        T Resolve<T>(AssetFile file,string id) where T:Asset
        {
            if(string.IsNullOrEmpty(id)) return null;
            var asset=Find<T>(file,id);
            if(asset!=null) return asset;
            foreach(var path in Dependencies(file))
                if ((asset=Find<T>(Read(path),id))!=null) return asset;
            foreach(var s in selected.OrderByDescending(s=>Names.Equals(Path.GetDirectoryName(s.PathName),Path.GetDirectoryName(file.PathName))))
                if ((asset=Find<T>(s,id))!=null) return asset;
            var names=new List<string>{id};
            if(typeof(T)==typeof(Rig)) { names.Add(id+"_HRC"); names.Add(id+"_SKL"); names.Add(id+"_SKN"); }
            int dot=id.IndexOf('.');
            if(dot>0) names.Add(id.Substring(0,dot));
            foreach(var name in names)
            {
                var path=Locate(file.PathName,name+".w3x");
                if(path!=null && (asset=Find<T>(Read(path),id))!=null) return asset;
            }
            // Some hierarchies live inside a differently named SKN.
            if(typeof(T)==typeof(Rig))
                foreach(var h in Headers(file.PathName).Where(h=>h[1]=="W3DHierarchy" && Names.Equals(h[2],id)))
                    if((asset=Find<T>(Read(h[0]),id))!=null) return asset;
            return null;
        }
        List<string[]> Headers(string source)
        {
            string dir=Path.GetDirectoryName(source);
            List<string[]> list;
            if(headers.TryGetValue(dir,out list)) return list;
            list=new List<string[]>();
            foreach(var path in Directory.EnumerateFiles(dir,"*.w3x").OrderBy(s=>s,Names))
            {
                try
                {
                    using(var r=AssetFile.Reader(path))
                    {
                        while(r.Read())
                        {
                            if(r.NodeType!=System.Xml.XmlNodeType.Element || r.Depth!=1 || !r.LocalName.StartsWith("W3D",StringComparison.Ordinal)) continue;
                            list.Add(new[]{path,r.LocalName,r.GetAttribute("id"),r.GetAttribute("Hierarchy")});
                            break;
                        }
                    }
                }
                catch(System.Xml.XmlException) { }
                catch(IOException) { }
                catch(UnauthorizedAccessException) { }
            }
            headers[dir]=list;
            return list;
        }
        void ClipsFor(ModelSpec model,AssetFile f)
        {
            if(string.IsNullOrEmpty(model.RigId)) return;
            var sources=new List<AssetFile>{f};
            sources.AddRange(selected);
            sources.AddRange(Dependencies(f).Select(Read));
            sources.AddRange(Headers(f.PathName).Where(h=>h[1]=="W3DAnimation" && Names.Equals(h[3],model.RigId)).Select(h=>Read(h[0])));
            model.Clips.AddRange(sources.Where(s=>s!=null).SelectMany(s=>s.Assets.OfType<Clip>())
                .Where(c=>Names.Equals(c.RigId,model.RigId)).GroupBy(c=>c.Id,Names).Select(g=>g.First()).OrderBy(c=>c.Id,Names));
        }
        string TexturePath(Mesh mesh,string id)
        {
            if(string.IsNullOrWhiteSpace(id)) return null;
            foreach(var ext in TextureLib.ImageExts)
            {
                string image=Locate(mesh.Source,id+ext);
                if(image!=null) return image;
            }
            var file=Read(mesh.Source);
            var declarations=new List<string>{mesh.Source};
            declarations.AddRange(Dependencies(file));
            var dir=Path.GetDirectoryName(mesh.Source);
            var parent=Directory.GetParent(dir);
            foreach(var p in new[]{Locate(mesh.Source,id+".xml"),Path.Combine(dir,"Texture.xml"),parent==null ? null : Path.Combine(parent.FullName,"Texture.xml")})
                if(p!=null && File.Exists(p)) declarations.Add(p);
            foreach(var path in declarations.Distinct(Names))
            {
                var tex=Find<TextureFile>(Read(path),id);
                if(tex!=null && !string.IsNullOrEmpty(tex.FileName))
                {
                    var image=Locate(path,tex.FileName);
                    if(image!=null) return image;
                }
            }
            return Locate(mesh.Source,id);
        }
        DecodedImage DecodedTexture(string path)
        {
            DecodedImage image;
            if(!effectTextures.TryGetValue(path,out image))
            {
                try{image=TextureLib.Load(path);}
                catch(Exception e){Warnings.Add(Path.GetFileName(path)+"  ·  "+e.Message);}
                effectTextures[path]=image;
            }
            return image;
        }
        DecodedImage EffectTexture(Mesh mesh,string id,bool useProxy)
        {
            if(string.IsNullOrWhiteSpace(id))return null;
            string path=TexturePath(mesh,id);
            if(path!=null)return DecodedTexture(path);
            Warnings.Add(mesh.Id+"  ·  缺少贴图 "+id+(useProxy?"，使用替代特效贴图":""));
            if(!useProxy)return null;
            string key="proxy:"+id;
            DecodedImage proxy;
            if(!effectTextures.TryGetValue(key,out proxy))effectTextures[key]=proxy=TextureLib.Proxy(id,128);
            return proxy;
        }
        void MaterialFor(Mesh mesh)
        {
            if(mesh.Material!=null) return;
            if(mesh.Effect!=null)
            {
                var effect=mesh.Effect;
                if(effect.UsesFirst)effect.First=EffectTexture(mesh,effect.Texture0,effect.Blend!=SurfaceBlend.Opaque);
                if(effect.UsesSecond)effect.Second=EffectTexture(mesh,effect.Texture1,false);
                // Geometry stays in the WPF scene for bounds and picking. The effect pass supplies its pixels.
                var empty=new MaterialGroup();empty.Freeze();mesh.Material=empty;
                return;
            }
            var path=TexturePath(mesh,mesh.Texture);
            if(mesh.Alpha && path!=null)mesh.Mask=DecodedTexture(path);
            var key=(path ?? "")+"|"+mesh.Alpha;
            Material material;
            if(!materials.TryGetValue(key,out material))
            {
                Brush brush=new SolidColorBrush(Color.FromRgb(158,179,189));
                if(path!=null)
                {
                    try
                    {
                        var img=mesh.Mask ?? TextureLib.Load(path);
                        byte[] pixels=mesh.Mask==null?img.Rgba:(byte[])img.Rgba.Clone();
                        if(!mesh.Alpha)
                            for(int i=3;i<pixels.Length;i+=4) pixels[i]=255;
                        for(int i=0;i<pixels.Length;i+=4)
                        {
                            byte red=pixels[i]; pixels[i]=pixels[i+2]; pixels[i+2]=red;
                        }
                        var bitmap=BitmapSource.Create(img.Width,img.Height,96,96,PixelFormats.Bgra32,null,pixels,img.Width*4);
                        bitmap.Freeze();
                        brush=new ImageBrush(bitmap){TileMode=TileMode.Tile, ViewportUnits=BrushMappingMode.Absolute, Viewport=new Rect(0,0,1,1), Stretch=Stretch.Fill};
                    }
                    catch(Exception e) { Warnings.Add(Path.GetFileName(path)+"  ·  "+e.Message); }
                }
                material=new DiffuseMaterial(brush); material.Freeze();
                materials[key]=material;
            }
            if(path==null && !string.IsNullOrEmpty(mesh.Texture)) Warnings.Add("缺少漫反射贴图  "+mesh.Texture);
            mesh.Material=material;
        }
        ModelSpec Spec(string name,string rigId,AssetFile f)
        {
            var spec=new ModelSpec { Name=name, Source=f.PathName, RigId=rigId, Rig=Resolve<Rig>(f,rigId) };
            if(rigId!=null && spec.Rig==null) Warnings.Add(name+"  ·  缺少骨架 "+rigId);
            ClipsFor(spec,f);
            return spec;
        }
        public static Import Load(string[] paths)
        {
            var batch=new Import();
            batch.Build(paths);
            return batch;
        }
        void Build(string[] paths)
        {
            foreach(var path in paths.Select(Path.GetFullPath).Distinct(Names))
            {
                var f=Read(path);
                if(f==null) continue;
                selected.Add(f);
                ExplicitRigs.AddRange(f.Assets.OfType<Rig>());
                ExplicitClips.AddRange(f.Assets.OfType<Clip>());
                if(!f.Assets.Any())
                {
                    Warnings.Add(Path.GetFileName(path)+"  ·  没有可预览的模型、骨架或动画");
                    if(f.Includes.Count==0)EmptyDeclarations.Add(path);
                }
            }
            var used=new HashSet<Mesh>();
            var usedBoxes=new HashSet<CollisionBox>();
            foreach(var f in selected)
                foreach(var c in f.Assets.OfType<Container>())
                {
                    var model=Spec(c.Id,c.RigId,f);
                    foreach(var part in c.Parts)
                    {
                        if(part.IsBox)
                        {
                            var box=Resolve<CollisionBox>(f,part.AssetId);
                            if(box==null) Warnings.Add(c.Id+"  ·  缺少碰撞盒 "+part.AssetId);
                            else { model.Boxes.Add(new KeyValuePair<int,CollisionBox>(part.Bone,box)); usedBoxes.Add(box); }
                            continue;
                        }
                        var mesh=Resolve<Mesh>(f,part.AssetId);
                        if(mesh==null) { Warnings.Add(c.Id+"  ·  缺少网格 "+part.AssetId); continue; }
                        MaterialFor(mesh);
                        used.Add(mesh);
                        model.Parts.Add(new Attachment{Mesh=mesh,Bone=part.Bone,SubObjectId=part.SubObjectId});
                    }
                    if(!model.RigOnly || model.Rig!=null) Models.Add(model);
                }
            foreach(var f in selected)
            {
                var loose=f.Assets.OfType<Mesh>().Where(m=>!used.Contains(m)).ToList();
                var boxes=f.Assets.OfType<CollisionBox>().Where(b=>!usedBoxes.Contains(b)).ToList();
                if(loose.Count==0 && boxes.Count==0) continue;
                var rig=f.Assets.OfType<Rig>().FirstOrDefault();
                var model=Spec(Path.GetFileNameWithoutExtension(f.PathName),rig==null ? null : rig.Id,f);
                foreach(var mesh in loose) { MaterialFor(mesh); model.Parts.Add(new Attachment{Mesh=mesh}); }
                foreach(var box in boxes) model.Boxes.Add(new KeyValuePair<int,CollisionBox>(0,box));
                Models.Add(model);
            }
            // Rig-only and motion-only inputs are visualized as skeletons when no skin is loaded.
            foreach(var f in selected)
            {
                var ids=f.Assets.OfType<Rig>().Select(r=>r.Id).Concat(f.Assets.OfType<Clip>().Select(c=>c.RigId)).Distinct(Names);
                foreach(var id in ids)
                {
                    if(Models.Any(m=>Names.Equals(m.RigId,id) && (m.Rig==null || Resolve<Rig>(f,id)==m.Rig))) continue;
                    var spec=Spec(id,id,f);
                    if(spec.Rig!=null) Models.Add(spec);
                }
            }
        }
    }
}
