using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Effects;
using System.Windows.Media.Imaging;
using System.Windows.Media.Media3D;

namespace W3xScene
{
    struct ParticleSprite
    {
        public SpriteMaterial Material;
        public Point3D Center;
        public float Size,Angle;
        public Vector4 Color,UV;
    }
    sealed class EffectComposite : ShaderEffect
    {
        static readonly DependencyProperty Input=RegisterPixelShaderSamplerProperty("Input",typeof(EffectComposite),0);
        static readonly DependencyProperty Light=RegisterPixelShaderSamplerProperty("Light",typeof(EffectComposite),1,SamplingMode.Bilinear);
        static readonly DependencyProperty Transmission=RegisterPixelShaderSamplerProperty("Transmission",typeof(EffectComposite),2,SamplingMode.Bilinear);
        public EffectComposite(BitmapSource light,BitmapSource transmission)
        {
            var shader=new PixelShader();
            using(var stream=Assembly.GetExecutingAssembly().GetManifestResourceStream("EffectComposite.ps"))shader.SetStreamSource(stream);
            shader.Freeze();PixelShader=shader;
            SetValue(Light,new ImageBrush(light));SetValue(Transmission,new ImageBrush(transmission));
            UpdateShaderValue(Input);UpdateShaderValue(Light);UpdateShaderValue(Transmission);
        }
    }

    // Keep WPF's normal meshes and picking. Shade the effect meshes against scene depth,
    // then combine their light and background transmission with the original viewport.
    sealed class EffectViewport
    {
        readonly Grid layer;
        readonly PerspectiveCamera camera;
        readonly ModelVisual3D ground;
        readonly ConditionalWeakTable<MeshGeometry3D,Mesh> guideMeshes=new ConditionalWeakTable<MeshGeometry3D,Mesh>();
        int width,height;
        double focal,lastTime=-1;
        float near,far;
        Task rendering;
        bool discard;
        Point3D eye;
        Vector3D forward,right,up;
        float[] depth;
        int[] lightBuffer,transmissionBuffer;
        WriteableBitmap lightImage,transmissionImage;
        struct DrawCall
        {
            public Mesh Mesh;
            public ParticleSprite Sprite;
            public Point3DCollection SkinnedPositions;
            public Vector3DCollection SkinnedNormals;
            public Matrix3D World;
            public double Distance;
        }
        struct Vertex
        {
            public float X,Y,Z,Fade,Light,Specular;
            public Vector4 UV,Noise,Tint;
            public static Vertex Lerp(Vertex a,Vertex b,float t)
            {
                return new Vertex{X=a.X+(b.X-a.X)*t,Y=a.Y+(b.Y-a.Y)*t,Z=a.Z+(b.Z-a.Z)*t,
                    UV=Vector4.Lerp(a.UV,b.UV,t),Noise=Vector4.Lerp(a.Noise,b.Noise,t),Tint=Vector4.Lerp(a.Tint,b.Tint,t),
                    Fade=a.Fade+(b.Fade-a.Fade)*t,Light=a.Light+(b.Light-a.Light)*t,Specular=a.Specular+(b.Specular-a.Specular)*t};
            }
        }
        Vertex[] vertices=new Vertex[0],screen=new Vertex[0];
        readonly List<DrawCall> transparent=new List<DrawCall>();
        readonly List<DrawCall> calls=new List<DrawCall>(),opaqueEffects=new List<DrawCall>();
        struct Face {public Vertex A,B,C;public Mesh Mesh;}
        readonly List<Face> faces=new List<Face>();
        const int BandHeight=32;
        List<int>[] bands;
        readonly Vertex[] cutA=new Vertex[8],cutB=new Vertex[8];
        public EffectViewport(Grid layer,PerspectiveCamera camera,ModelVisual3D ground)
        {this.layer=layer;this.camera=camera;this.ground=ground;}
        void AddGuide(GeometryModel3D model,Matrix3D placement)
        {
            if(model==null)return;
            var geometry=model.Geometry as MeshGeometry3D;
            if(geometry==null || geometry.Positions.Count==0)return;
            var mesh=guideMeshes.GetValue(geometry,g=>new Mesh{Positions=new[]{g.Positions.ToArray()},Indices=g.TriangleIndices.ToArray()});
            calls.Add(new DrawCall{Mesh=mesh,World=model.Transform.Value*placement});
        }
        void Resize(int w,int h)
        {
            width=w;height=h;int count=w*h;
            depth=new float[count];lightBuffer=new int[count];transmissionBuffer=new int[count];
            lightImage=new WriteableBitmap(w,h,96,96,PixelFormats.Bgr32,null);
            transmissionImage=new WriteableBitmap(w,h,96,96,PixelFormats.Bgr32,null);
            bands=Enumerable.Range(0,(h+BandHeight-1)/BandHeight).Select(i=>new List<int>()).ToArray();
            layer.Effect=null;
        }
        public void InvalidateScene(){discard=true;lastTime=-1;layer.Effect=null;}
        // Publish a complete effect pass for this camera before rendering an offscreen PNG.
        public void DrawSnapshot(List<SceneItem> items,double now)
        {
            if(rendering!=null)rendering.GetAwaiter().GetResult();
            InvalidateScene();
            Draw(items,now);
            if(rendering==null)return;
            rendering.GetAwaiter().GetResult();
            Draw(items,now);
        }
        public void Draw(List<SceneItem> items,double now)
        {
            bool active=items.Any(i=>i.HasEffects || i.Particles.Count>0);
            if(!active && layer.Effect!=null)InvalidateScene();
            if(rendering!=null)
            {
                if(!rendering.IsCompleted)return;
                rendering.GetAwaiter().GetResult();rendering=null;
                if(active && !discard)
                {
                    var rect=new Int32Rect(0,0,width,height);
                    lightImage.WritePixels(rect,lightBuffer,width*4,0);
                    transmissionImage.WritePixels(rect,transmissionBuffer,width*4,0);
                    if(layer.Effect==null)layer.Effect=new EffectComposite(lightImage,transmissionImage);
                }
            }
            if(!active)
            {
                calls.Clear();transparent.Clear();opaqueEffects.Clear();faces.Clear();
                if(width!=0)
                {
                    width=height=0;lightImage=transmissionImage=null;
                    depth=null;lightBuffer=transmissionBuffer=null;bands=null;
                    faces.TrimExcess();vertices=screen=new Vertex[0];
                }
                return;
            }
            if(now-lastTime<1.0/30 || layer.ActualWidth<1 || layer.ActualHeight<1)return;
            discard=false;
            lastTime=now;
            double scale=Math.Min(1,960/Math.Max(layer.ActualWidth,layer.ActualHeight));
            int w=Math.Max(1,(int)Math.Round(layer.ActualWidth*scale)),h=Math.Max(1,(int)Math.Round(layer.ActualHeight*scale));
            if(w!=width || h!=height)Resize(w,h);
            eye=camera.Position;forward=camera.LookDirection;forward.Normalize();
            right=Vector3D.CrossProduct(forward,camera.UpDirection);right.Normalize();up=Vector3D.CrossProduct(right,forward);
            focal=width*.5/Math.Tan(camera.FieldOfView*Math.PI/360);
            near=(float)camera.NearPlaneDistance;far=(float)camera.FarPlaneDistance;
            calls.Clear();transparent.Clear();opaqueEffects.Clear();
            AddGuide(ground.Content as GeometryModel3D,Matrix3D.Identity);
            foreach(var item in items)
            {
                var placement=item.Group.Transform.Value;
                foreach(var sprite in item.Particles)
                    transparent.Add(new DrawCall{Mesh=sprite.Material,Sprite=sprite,Distance=Vector3D.DotProduct(sprite.Center-eye,forward)});
                foreach(var guide in item.VisibleGuides)AddGuide(guide,placement);
                foreach(var p in item.RenderParts)
                {
                    var geometry=p.Model.Geometry as MeshGeometry3D;
                    if(geometry==null)continue;
                    // Animated skin streams are frozen by RenderPart before publication. Workers only
                    // see these snapshots, never live WPF geometry or editor state.
                    var call=new DrawCall{Mesh=p.Source,World=p.Model.Transform.Value*placement};
                    if(p.Deforms && geometry.Positions.IsFrozen)
                    {
                        call.SkinnedPositions=geometry.Positions;
                        if(geometry.Normals.IsFrozen)call.SkinnedNormals=geometry.Normals;
                    }
                    var fx=p.Source.Effect;
                    if(fx==null){calls.Add(call);continue;}
                    if(fx.Blend==SurfaceBlend.Opaque && fx.DepthWrite){opaqueEffects.Add(call);continue;}
                    var bounds=geometry.Bounds;
                    var center=new Point3D(bounds.X+bounds.SizeX*.5,bounds.Y+bounds.SizeY*.5,bounds.Z+bounds.SizeZ*.5);
                    call.Distance=Vector3D.DotProduct(call.World.Transform(center)-eye,forward);
                    transparent.Add(call);
                }
            }
            calls.AddRange(opaqueEffects);
            if(opaqueEffects.Count==0 && transparent.Count==0){layer.Effect=null;return;}
            // Stable sort preserves XML order for coincident surfaces at the same sort level.
            calls.AddRange(transparent.OrderBy(c=>c.Mesh.SortLevel).ThenByDescending(c=>c.Distance));
            rendering=Task.Run(()=>RenderFrame((float)now));
        }
        void RenderFrame(float time)
        {
            Array.Clear(depth,0,depth.Length);Array.Clear(lightBuffer,0,lightBuffer.Length);
            for(int i=0;i<transmissionBuffer.Length;i++)transmissionBuffer[i]=0xFFFFFF;
            faces.Clear();foreach(var band in bands)band.Clear();
            foreach(var call in calls)
                if(call.Sprite.Material!=null)DrawSprite(call.Sprite);
                else DrawMesh(call,time);
            Parallel.For(0,bands.Length,new ParallelOptions{MaxDegreeOfParallelism=4},i=>{
                int y0=i*BandHeight,y1=Math.Min(height,y0+BandHeight);
                foreach(int f in bands[i])
                {var face=faces[f];Triangle(face.A,face.B,face.C,face.Mesh,y0,y1);}
            });
        }
        void QueueFace(Vertex a,Vertex b,Vertex c,Mesh mesh)
        {
            float min=Math.Max(0,Math.Min(a.Y,Math.Min(b.Y,c.Y)));
            float max=Math.Min(height-1,Math.Max(a.Y,Math.Max(b.Y,c.Y)));
            if(min>max || Math.Max(a.X,Math.Max(b.X,c.X))<0 || Math.Min(a.X,Math.Min(b.X,c.X))>=width)return;
            int index=faces.Count;
            faces.Add(new Face{A=a,B=b,C=c,Mesh=mesh});
            for(int i=(int)min/BandHeight;i<=(int)max/BandHeight;i++)bands[i].Add(index);
        }
        Vertex Project(Vertex v)
        {
            float z=1/v.Z;
            v.X=(float)(width*.5+v.X*z*focal);v.Y=(float)(height*.5-v.Y*z*focal);v.Z=z;
            v.UV*=z;v.Noise*=z;v.Tint*=z;v.Fade*=z;v.Light*=z;v.Specular*=z;
            return v;
        }
        static int Clip(Vertex[] input,int count,Vertex[] output,float plane,bool near)
        {
            int n=0;var a=input[count-1];
            for(int i=0;i<count;i++)
            {
                var b=input[i];bool insA=near?a.Z>=plane:a.Z<=plane,insB=near?b.Z>=plane:b.Z<=plane;
                if(insA!=insB)output[n++]=Vertex.Lerp(a,b,(plane-a.Z)/(b.Z-a.Z));
                if(insB)output[n++]=b;a=b;
            }
            return n;
        }
        void DrawSprite(ParticleSprite s)
        {
            var axisX=s.Material.Ground ? new Vector3D(1,0,0) : right;
            var axisY=s.Material.Ground ? new Vector3D(0,1,0) : up;
            double cos=Math.Cos(s.Angle)*s.Size*.5,sin=Math.Sin(s.Angle)*s.Size*.5;
            var x=axisX*cos+axisY*sin;var y=axisY*cos-axisX*sin;
            for(int i=0;i<4;i++)
            {
                bool r=i==1 || i==2, t=i>=2;
                var d=(s.Center-eye)+(r?x:-x)+(t?y:-y);
                cutA[i]=new Vertex{X=(float)Vector3D.DotProduct(d,right),Y=(float)Vector3D.DotProduct(d,up),
                    Z=(float)Vector3D.DotProduct(d,forward),Tint=s.Color,UV=new Vector4(r?s.UV.Z:s.UV.X,t?s.UV.W:s.UV.Y,0,0)};
            }
            int count=Clip(cutA,4,cutB,near,true);if(count<3)return;
            count=Clip(cutB,count,cutA,far,false);if(count<3)return;
            var first=Project(cutA[0]);var prev=Project(cutA[1]);
            for(int i=2;i<count;i++)
            {
                var next=Project(cutA[i]);QueueFace(first,prev,next,s.Material);prev=next;
            }
        }
        void DrawMesh(DrawCall call,float time)
        {
            var mesh=call.Mesh;var fx=mesh.Effect;
            int count=mesh.Positions[0].Length;
            if(vertices.Length<count){vertices=new Vertex[count];screen=new Vertex[count];}
            bool getUV=fx!=null || mesh.Mask!=null;
            for(int i=0;i<count;i++)
            {
                var world=call.World.Transform(call.SkinnedPositions!=null?call.SkinnedPositions[i]:mesh.Positions[0][i]);
                var delta=world-eye;
                var v=new Vertex{X=(float)Vector3D.DotProduct(delta,right),Y=(float)Vector3D.DotProduct(delta,up),Z=(float)Vector3D.DotProduct(delta,forward)};
                if(getUV)
                {
                    var tex=mesh.UV.Length==count?mesh.UV[i]:new Point();
                    if(fx==null)v.UV=new Vector4((float)tex.X,(float)(1-tex.Y),0,0);
                    else
                    {
                        var uv1=mesh.SecondUV!=null && mesh.SecondUV.Length==count?mesh.SecondUV[i]:tex;
                        fx.Coordinates((float)tex.X,(float)(1-tex.Y),(float)uv1.X,(float)(1-uv1.Y),(float)world.X,(float)world.Y,time,out v.UV,out v.Noise);
                        var normal=call.SkinnedNormals!=null && call.SkinnedNormals.Count==count?call.SkinnedNormals[i]:
                            mesh.Normals.Length>0?mesh.Normals[0][i]:new Vector3D(0,0,1);
                        var n=call.World.Transform(normal);
                        if(n.LengthSquared>1e-12)n.Normalize();
                        var view=-delta/Math.Max(1e-10,delta.Length);
                        float facing=(float)Math.Abs(Vector3D.DotProduct(n,view));
                        float edge=fx.Edge<=0?1:EffectMaterial.Clamp(facing/fx.Edge);
                        v.Fade=edge*edge*(3-2*edge);
                        if(fx.Lightning)v.Fade=(float)Math.Pow(Math.Max(0,2*fx.HDR*v.Fade),1/2.2);
                        if(mesh.VertexColors!=null && mesh.VertexColors.Length==count)v.Tint=mesh.VertexColors[i];
                        if(fx.Specular!=Vector4.Zero)
                        {
                            var half=view+new Vector3D(.41,-.51,.75);if(half.LengthSquared>1e-12)half.Normalize();
                            v.Specular=(float)Math.Pow(Math.Max(0,Vector3D.DotProduct(n,half)),fx.Shininess);
                        }
                        v.Light=(float)(.22+.65*Math.Max(0,.41*n.X-.51*n.Y+.75*n.Z));
                    }
                }
                vertices[i]=v;if(v.Z>=near && v.Z<=far)screen[i]=Project(v);
            }
            for(int i=0;i<mesh.Indices.Length;i+=3)
            {
                int ia=mesh.Indices[i],ib=mesh.Indices[i+1],ic=mesh.Indices[i+2];
                var a=vertices[ia];var b=vertices[ib];var c=vertices[ic];
                if(a.Z>=near && b.Z>=near && c.Z>=near && a.Z<=far && b.Z<=far && c.Z<=far)
                    QueueFace(screen[ia],screen[ib],screen[ic],mesh);
                else
                {
                    cutA[0]=a;cutA[1]=b;cutA[2]=c;
                    int n=Clip(cutA,3,cutB,near,true);if(n<3)continue;
                    n=Clip(cutB,n,cutA,far,false);if(n<3)continue;
                    var first=Project(cutA[0]);
                    for(int j=1;j+1<n;j++)QueueFace(first,Project(cutA[j]),Project(cutA[j+1]),mesh);
                }
            }
        }
        static bool TopLeft(Vertex a,Vertex b){return b.Y<a.Y || (b.Y==a.Y && b.X>a.X);}
        void Triangle(Vertex a,Vertex b,Vertex c,Mesh mesh,int bandStart,int bandEnd)
        {
            float area=(b.X-a.X)*(c.Y-a.Y)-(b.Y-a.Y)*(c.X-a.X);
            if(Math.Abs(area)<1e-6f)return;
            var fx=mesh.Effect;
            bool sprite=mesh is SpriteMaterial;
            if(fx!=null && fx.Cull && area>0)return;
            if(area<0){var swap=b;b=c;c=swap;area=-area;}
            int x0=(int)Math.Max(0,Math.Min(width,Math.Floor(Math.Min(a.X,Math.Min(b.X,c.X)))));
            int y0=(int)Math.Max(bandStart,Math.Min(height,Math.Floor(Math.Min(a.Y,Math.Min(b.Y,c.Y)))));
            int x1=(int)Math.Max(0,Math.Min(width,Math.Ceiling(Math.Max(a.X,Math.Max(b.X,c.X)))));
            int y1=(int)Math.Max(0,Math.Min(bandEnd,Math.Ceiling(Math.Max(a.Y,Math.Max(b.Y,c.Y)))));
            float dx0=(b.Y-c.Y)/area,dx1=(c.Y-a.Y)/area,dy0=(c.X-b.X)/area,dy1=(a.X-c.X)/area;
            float row0=dx0*(x0+.5f-b.X)+dy0*(y0+.5f-b.Y);
            float row1=dx1*(x0+.5f-c.X)+dy1*(y0+.5f-c.Y);
            float limit0=TopLeft(b,c)?0:1e-7f,limit1=TopLeft(c,a)?0:1e-7f,limit2=TopLeft(a,b)?0:1e-7f;
            for(int y=y0;y<y1;y++,row0+=dy0,row1+=dy1)
            {
                float p=row0,q=row1;
                for(int x=x0;x<x1;x++,p+=dx0,q+=dx1)
                {
                    float r=1-p-q;
                    if(p<limit0 || q<limit1 || r<limit2)continue;
                    int index=y*width+x;float z=p*a.Z+q*b.Z+r*c.Z;
                    if(z<depth[index])continue;
                    if(fx==null && mesh.Mask==null){depth[index]=z;continue;}
                    float inv=1/z;var uv=(a.UV*p+b.UV*q+c.UV*r)*inv;
                    if(fx==null)
                    {
                        if(EffectMaterial.Sample(mesh.Mask,uv.X,uv.Y).W>=.5f)depth[index]=z;
                        continue;
                    }
                    if(sprite)
                    {
                        var color=EffectMaterial.Sample(fx.First,uv.X,uv.Y)*((a.Tint*p+b.Tint*q+c.Tint*r)*inv);
                        if(fx.AlphaTest && color.W<.4f)continue;
                        if(fx.Blend==SurfaceBlend.Multiply)color=Vector4.One-new Vector4(color.X,color.Y,color.Z,0);
                        if(fx.AlphaTest && fx.Blend==SurfaceBlend.Alpha)color.W=1;
                        Composite(index,color,fx.Blend);continue;
                    }
                    var noise=fx.UsesSecond?(a.Noise*p+b.Noise*q+c.Noise*r)*inv:Vector4.Zero;
                    var pixel=fx.Shade(uv,noise,(a.Fade*p+b.Fade*q+c.Fade*r)*inv,(a.Light*p+b.Light*q+c.Light*r)*inv,
                        (a.Specular*p+b.Specular*q+c.Specular*r)*inv);
                    if(mesh.VertexColors!=null && mesh.VertexColors.Length==mesh.Positions[0].Length)pixel*=(a.Tint*p+b.Tint*q+c.Tint*r)*inv;
                    if(fx.AlphaTest && pixel.W<96f/255)continue;
                    if(fx.Blend==SurfaceBlend.Opaque)pixel.W=1;
                    if(fx.DepthWrite)depth[index]=z;
                    Composite(index,pixel,fx.Blend);
                }
            }
        }
        static int Pack(float r,float g,float b)
        {
            return ((int)(EffectMaterial.Clamp(r)*255+.5f)<<16)|((int)(EffectMaterial.Clamp(g)*255+.5f)<<8)|(int)(EffectMaterial.Clamp(b)*255+.5f);
        }
        static Vector3 RGB(int p){return new Vector3((p>>16)&255,(p>>8)&255,p&255)*(1f/255);}
        void Composite(int index,Vector4 pixel,SurfaceBlend blend)
        {
            var source=new Vector3(pixel.X,pixel.Y,pixel.Z)*pixel.W;
            if(blend==SurfaceBlend.Add)
            {
                var sum=RGB(lightBuffer[index])+source;lightBuffer[index]=Pack(sum.X,sum.Y,sum.Z);return;
            }
            Vector3 factor=blend==SurfaceBlend.Multiply?Vector3.Max(Vector3.Zero,Vector3.One-source):new Vector3(1-pixel.W);
            var light=RGB(lightBuffer[index])*factor;
            if(blend!=SurfaceBlend.Multiply)light+=source;
            var transmission=RGB(transmissionBuffer[index])*factor;
            lightBuffer[index]=Pack(light.X,light.Y,light.Z);transmissionBuffer[index]=Pack(transmission.X,transmission.Y,transmission.Z);
        }
    }
}
