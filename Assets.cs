using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Media3D;
using System.Xml;
using System.Xml.Linq;

namespace W3xScene
{
    class Asset
    {
        public string Id, Source;
        public override string ToString() { return Id; }
    }
    class Influence
    {
        public int Bone;
        public double Weight;
    }
    class Mesh : Asset
    {
        public Point3D[][] Positions;
        public Vector3D[][] Normals;
        public Influence[][] Weights;
        public int[] Indices;
        public Point[] UV;
        public string Texture;
        public string ShaderName;
        public Dictionary<string,string> TextureSlots;
        public EffectMaterial Effect;
        public FxPreview.DecodedImage Mask;
        public Point[] SecondUV;
        public System.Numerics.Vector4[] VertexColors;
        public int SortLevel;
        public bool Hidden, Alpha;
        public Material Material;
    }
    class Bone
    {
        public string Name;
        public int Parent;
        public BoneTransform Local;
    }
    class Rig : Asset
    {
        public Bone[] Bones;
        public int[] Order;
    }
    class Track
    {
        public int Bone, Start;
        public string Type;
        public double[] Scalars;
        public Quaternion[] Rotations;
        public bool[] Steps;
    }
    class Clip : Asset
    {
        public string RigId;
        public int Frames;
        public double Fps;
        public Track[] Tracks;
    }
    class Part
    {
        public string AssetId,SubObjectId;
        public bool IsBox;
        public int Bone;
    }
    class CollisionBox : Asset { public Vector3D Center, Extent; }
    class Container : Asset
    {
        public string RigId;
        public Part[] Parts;
    }
    class TextureFile : Asset { public string FileName; }

    class AssetFile
    {
        public readonly List<Asset> Assets = new List<Asset>();
        public readonly List<string> Includes = new List<string>();
        public string PathName;

        internal static XmlReader Reader(string path)
        {
            return XmlReader.Create(path, new XmlReaderSettings { DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null, IgnoreComments = true, IgnoreWhitespace = true });
        }
        static IEnumerable<XElement> Children(XElement e, string tag)
        {
            return e.Elements(e.Name.Namespace + tag);
        }
        static IEnumerable<XElement> Values(XElement e, string tag)
        {
            return Children(e, tag).Take(1).SelectMany(a => a.Elements());
        }
        static double Number(string s)
        {
            double n = double.Parse(s, CultureInfo.InvariantCulture);
            if (double.IsNaN(n) || double.IsInfinity(n)) throw new FormatException("坐标或关键帧含无效数值");
            return n;
        }
        static double N(XElement e, string attr, double fallback)
        {
            return e.Attribute(attr) == null ? fallback : Number((string)e.Attribute(attr));
        }
        static Vector3D Vector(XElement e)
        {
            return new Vector3D(N(e,"X",0), N(e,"Y",0), N(e,"Z",0));
        }
        static Quaternion Rotation(XElement e)
        {
            Quaternion q = new Quaternion(N(e,"X",0), N(e,"Y",0), N(e,"Z",0), N(e,"W",1));
            if (q.X*q.X + q.Y*q.Y + q.Z*q.Z + q.W*q.W < 1e-12) return Quaternion.Identity;
            q.Normalize();
            return q;
        }
        static Mesh ReadMesh(XElement e)
        {
            var mesh = new Mesh();
            mesh.Positions = Children(e,"Vertices").Select(s => s.Elements().Select(v => (Point3D)Vector(v)).ToArray()).ToArray();
            if (mesh.Positions.Length == 0 || mesh.Positions[0].Length == 0) throw new InvalidDataException("网格缺少顶点");
            mesh.Normals = Children(e,"Normals").Select(s => s.Elements().Select(Vector).ToArray()).ToArray();
            mesh.Weights = Children(e,"BoneInfluences").Select(s => s.Elements().Select(v => new Influence { Bone=(int?)v.Attribute("Bone") ?? 0, Weight=N(v,"Weight",1) }).ToArray()).ToArray();
            int count = mesh.Positions[0].Length;
            if (mesh.Positions.Any(s=>s.Length != count) || mesh.Weights.Any(s=>s.Length != count) || mesh.Normals.Any(s=>s.Length != count))
                throw new InvalidDataException("顶点流长度不一致");
            mesh.UV = Values(e,"TexCoords").Select(t => new Point(N(t,"X",0), 1-N(t,"Y",0))).ToArray();
            mesh.Indices = Values(e,"Triangles").SelectMany(t => {
                var tri = Children(t,"V").Select(v => (int)v).ToArray();
                if (tri.Length != 3 || tri.Any(i=>i<0 || i>=count)) throw new InvalidDataException("三角形顶点索引无效");
                return tri;
            }).ToArray();
            mesh.Hidden = (bool?)e.Attribute("Hidden") ?? false;
            var constants = Children(e,"FXShader").SelectMany(f=>Values(f,"Constants")).ToList();
            var texture = constants.FirstOrDefault(c=>c.Name.LocalName=="Texture" &&
                new[]{"DiffuseTexture","Texture_0","BaseTexture","DiffuseMap"}.Contains((string)c.Attribute("Name"), StringComparer.OrdinalIgnoreCase));
            mesh.Texture = texture == null ? null : texture.Value.Trim();
            mesh.Alpha = constants.Any(c=>(string)c.Attribute("Name")=="AlphaTestEnable" && (c.Value.Trim()=="1" || c.Value.Trim().Equals("true",StringComparison.OrdinalIgnoreCase)));
            var shader=Children(e,"FXShader").FirstOrDefault();
            mesh.ShaderName=shader==null ? null : (string)shader.Attribute("ShaderName");
            mesh.TextureSlots=constants.Where(c=>c.Name.LocalName=="Texture" && c.Attribute("Name")!=null)
                .GroupBy(c=>(string)c.Attribute("Name")).ToDictionary(g=>g.Key,g=>g.First().Value.Trim());
            mesh.Effect=EffectMaterial.Read(shader,constants);
            if(mesh.Effect!=null)
            {
                mesh.Texture=mesh.Effect.Texture0;
                mesh.SortLevel=(int?)e.Attribute("SortLevel") ?? 0;
                var second=Children(e,"TexCoords").Skip(1).FirstOrDefault();
                if(second!=null)mesh.SecondUV=second.Elements().Select(t=>new Point(N(t,"X",0),1-N(t,"Y",0))).ToArray();
                if(!mesh.Effect.Lightning || mesh.Effect.ClassicLightning)mesh.VertexColors=Values(e,"VertexColors").Select(c=>new System.Numerics.Vector4(
                    (float)N(c,"R",1),(float)N(c,"G",1),(float)N(c,"B",1),(float)N(c,"A",1))).ToArray();
            }
            return mesh;
        }
        static Rig ReadRig(XElement e)
        {
            var bones = Children(e,"Pivot").Select(p=>{
                var rotation = Children(p,"Rotation").FirstOrDefault();
                var translation = Children(p,"Translation").FirstOrDefault();
                return new Bone {
                    Name=(string)p.Attribute("Name") ?? "",
                    Parent=(int?)p.Attribute("Parent") ?? -1,
                    Local=new BoneTransform(translation==null ? new Vector3D() : Vector(translation),
                        rotation==null ? Quaternion.Identity : Rotation(rotation))
                };
            }).ToArray();
            var sorted = new List<int>();
            var done = new bool[bones.Length];
            // Resolve parent-first even for files that store pivots out of order.
            while (sorted.Count < bones.Length)
            {
                int before = sorted.Count;
                for (int i=0; i<bones.Length; i++)
                {
                    int p=bones[i].Parent;
                    if (!done[i] && (p == -1 || (p >= 0 && p < bones.Length && done[p])))
                    { sorted.Add(i); done[i]=true; }
                }
                if (before == sorted.Count) throw new InvalidDataException("骨架的父索引越界或循环");
            }
            return new Rig { Bones=bones, Order=sorted.ToArray() };
        }
        static Clip ReadClip(XElement e)
        {
            var clip = new Clip { RigId=(string)e.Attribute("Hierarchy"), Frames=(int?)e.Attribute("NumFrames") ?? 1, Fps=N(e,"FrameRate",30) };
            if (clip.Frames < 1 || clip.Fps <= 0) throw new InvalidDataException("动画时长或帧率无效");
            clip.Tracks = Values(e,"Channels").Select(c=>{
                var t = new Track { Bone=(int?)c.Attribute("Pivot") ?? 0, Start=(int?)c.Attribute("FirstFrame") ?? 0, Type=(string)c.Attribute("Type") };
                var frames = Children(c,"Frame").ToArray();
                t.Steps = frames.Select(f=>(bool?)f.Attribute("BinaryMove") ?? false).ToArray();
                if (t.Type == "Orientation") t.Rotations = frames.Select(Rotation).ToArray();
                else t.Scalars = frames.Select(v=>Number(v.Value)).ToArray();
                return t;
            }).Where(t=>t.Steps.Length>0).ToArray();
            return clip;
        }
        public static AssetFile Read(string path)
        {
            var file = new AssetFile {PathName=path};
            using (var reader=Reader(path))
            {
                reader.MoveToContent();
                if (reader.LocalName!="AssetDeclaration") throw new InvalidDataException("需要 XML 格式的 W3X AssetDeclaration");
                if (reader.IsEmptyElement) return file;
                reader.ReadStartElement();
                while (reader.MoveToContent()==XmlNodeType.Element)
                {
                    string tag=reader.LocalName;
                    if (!new[]{"W3DMesh","W3DContainer","W3DHierarchy","W3DAnimation","W3DCollisionBox","Texture","Includes"}.Contains(tag)) { reader.Skip(); continue; }
                    var e=(XElement)XNode.ReadFrom(reader);
                    Asset a=null;
                    switch(tag)
                    {
                        case "W3DMesh": a=ReadMesh(e); break;
                        case "W3DHierarchy": a=ReadRig(e); break;
                        case "W3DAnimation": a=ReadClip(e); break;
                        case "W3DCollisionBox": a=new CollisionBox {Center=Vector(Children(e,"Center").First()),Extent=Vector(Children(e,"Extent").First())}; break;
                        case "W3DContainer":
                            a=new Container { RigId=(string)e.Attribute("Hierarchy"), Parts=Children(e,"SubObject")
                                .SelectMany(s=>Values(s,"RenderObject").Where(v=>v.Name.LocalName=="Mesh" || v.Name.LocalName=="CollisionBox")
                                    .Select(v=>new Part {AssetId=v.Value.Trim(),SubObjectId=(string)s.Attribute("SubObjectID"),IsBox=v.Name.LocalName=="CollisionBox",Bone=(int?)s.Attribute("BoneIndex") ?? 0})).ToArray() };
                            break;
                        case "Texture": a=new TextureFile {FileName=(string)e.Attribute("File")}; break;
                        case "Includes": file.Includes.AddRange(Children(e,"Include").Select(i=>(string)i.Attribute("source")).Where(s=>!string.IsNullOrWhiteSpace(s))); break;
                    }
                    if (a!=null) { a.Id=(string)e.Attribute("id") ?? Path.GetFileNameWithoutExtension(path); a.Source=path; file.Assets.Add(a); }
                }
            }
            return file;
        }
    }
}
