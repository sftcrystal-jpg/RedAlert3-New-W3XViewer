using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Numerics;
using System.Xml.Linq;
using FxPreview;

namespace W3xScene
{
    enum SurfaceBlend { Opaque, Alpha, Add, Multiply }

    sealed class EffectMaterial
    {
        public bool Lightning, Multi, WorldUV, UniqueWorld, Cull, DepthWrite, AlphaTest;
        public int TextureCount, SecondaryMode, PrimaryMapper, SecondaryMapper;
        public float Opacity, Edge, HDR, NoiseScale, NoiseAmplitude, NoiseSpeed, NoiseCos, NoiseSin, WorldScale;
        public Vector4 Color, Ambient, Emission, Specular, PrimaryUV, SecondaryUV, Animation;
        public float Shininess;
        public string Texture0,Texture1;
        public DecodedImage First,Second;
        public SurfaceBlend Blend;
        public bool UsesFirst {get{return Lightning || TextureCount>0;}}
        public bool UsesSecond {get{return Lightning ? NoiseAmplitude!=0 : TextureCount>1;}}

        public static EffectMaterial Read(XElement shader,List<XElement> constants)
        {
            if(shader==null)return null;
            string name=Path.GetFileNameWithoutExtension((string)shader.Attribute("ShaderName") ?? "").ToLowerInvariant();
            if(name!="fxlightning" && name!="defaultw3d" && name!="defaultw3dnofognocloud")return null;
            var args=new Parameters(constants);
            var m=new EffectMaterial{Lightning=name=="fxlightning"};
            m.Texture0=args.Text("Texture_0");m.Texture1=args.Text("Texture_1");
            m.TextureCount=(int)args.Number("NumTextures",string.IsNullOrEmpty(m.Texture0)?0:1);
            m.Multi=args.Flag("MultiTextureEnable",false);
            int blend=(int)args.Number("BlendMode",0);
            m.Blend=m.Lightning ? (args.Flag("MultiplyBlendEnable",false)?SurfaceBlend.Multiply:SurfaceBlend.Add) :
                blend==2 ? SurfaceBlend.Add : blend==1 ? SurfaceBlend.Alpha : SurfaceBlend.Opaque;
            m.Cull=args.Flag("CullingEnable",!m.Lightning);
            m.DepthWrite=args.Flag("DepthWriteEnable",m.Blend==SurfaceBlend.Opaque);
            m.AlphaTest=args.Flag("AlphaTestEnable",false);
            m.Color=args.Vector("ColorDiffuse",Vector4.One);
            m.Ambient=args.Vector("ColorAmbient",m.Color);
            m.Emission=args.Vector("ColorEmissive",Vector4.Zero);
            m.Specular=args.Vector("ColorSpecular",Vector4.Zero);m.Shininess=Math.Max(1,args.Number("Shininess",1));
            m.HDR=args.Number("EmissiveHDRMultipler",1);
            m.Opacity=args.Number("Opacity",1);m.Edge=args.Number("EdgeFadeOut",0);
            m.PrimaryMapper=(int)args.Number("TexCoordMapper_0",0);m.SecondaryMapper=(int)args.Number("TexCoordMapper_1",0);
            m.PrimaryUV=m.Lightning ? args.Vector("DiffuseCoordOffset",new Vector4(0,0,1,1)) :
                args.Vector("TexCoordTransform_0",new Vector4(1,1,0,0));
            m.SecondaryUV=args.Vector("TexCoordTransform_1",new Vector4(1,1,0,0));
            m.Animation=args.Vector("TextureAnimation_FPS_NumPerRow_LastFrame_FrameOffset_0",new Vector4(0,1,1,0));
            m.SecondaryMode=(int)args.Number("SecondaryTextureBlendMode",0);
            m.WorldUV=args.Flag("UseWorldCords",false);
            m.UniqueWorld=args.Flag("UniqueWorldCoordEnable",false);m.WorldScale=args.Number("UniqueWorldCoordScalar",1);
            m.NoiseScale=args.Number("DisplaceScalar",1);m.NoiseAmplitude=args.Number("DisplaceAmp",0);
            m.NoiseSpeed=args.Number("DisplaceSpeed",0);
            double angle=args.Number("DisplaceDivergenceAngle",0)*Math.PI/360.0;
            m.NoiseCos=(float)Math.Cos(angle);m.NoiseSin=(float)Math.Sin(angle);
            return m;
        }

        sealed class Parameters
        {
            readonly Dictionary<string,XElement> values=new Dictionary<string,XElement>(StringComparer.OrdinalIgnoreCase);
            public Parameters(IEnumerable<XElement> constants)
            {foreach(var c in constants)values[(string)c.Attribute("Name") ?? ""]=c;}
            public string Text(string key){XElement e;return values.TryGetValue(key,out e)?e.Value.Trim():"";}
            static float Parse(string text,float fallback)
            {
                float n;
                return float.TryParse(text,NumberStyles.Float,CultureInfo.InvariantCulture,out n) && !float.IsNaN(n) && !float.IsInfinity(n)?n:fallback;
            }
            public float Number(string key,float fallback){return Parse(Text(key),fallback);}
            public bool Flag(string key,bool fallback)
            {string s=Text(key);return s.Length==0?fallback:s=="1" || s.Equals("true",StringComparison.OrdinalIgnoreCase);}
            public Vector4 Vector(string key,Vector4 fallback)
            {
                XElement e;if(!values.TryGetValue(key,out e))return fallback;
                var v=e.Elements().Select(x=>x.Value).ToArray();
                return new Vector4(v.Length>0?Parse(v[0],fallback.X):fallback.X,v.Length>1?Parse(v[1],fallback.Y):fallback.Y,
                    v.Length>2?Parse(v[2],fallback.Z):fallback.Z,v.Length>3?Parse(v[3],fallback.W):fallback.W);
            }
        }

        public static float Clamp(float x){return Math.Max(0,Math.Min(1,x));}
        static float Frac(float x){return x-(float)Math.Floor(x);}
        static Vector4 Pixel(DecodedImage t,int x,int y)
        {
            int i=(y*t.Width+x)*4;var b=t.Rgba;
            return new Vector4(b[i],b[i+1],b[i+2],b[i+3])*(1f/255);
        }
        // W3X uses bottom-up UVs. Decoder rows start at the top, independent of DDS/TGA origin.
        public static Vector4 Sample(DecodedImage t,float u,float v)
        {
            if(t==null)return Vector4.One;
            float x=Frac(u)*t.Width-.5f,y=Frac(1-v)*t.Height-.5f;
            int ix=(int)Math.Floor(x),iy=(int)Math.Floor(y);float fx=x-ix,fy=y-iy;
            if(ix<0)ix=t.Width-1;if(iy<0)iy=t.Height-1;
            int jx=ix+1==t.Width?0:ix+1,jy=iy+1==t.Height?0:iy+1;
            return Vector4.Lerp(Vector4.Lerp(Pixel(t,ix,iy),Pixel(t,jx,iy),fx),Vector4.Lerp(Pixel(t,ix,jy),Pixel(t,jx,jy),fx),fy);
        }
        public static Vector2 Mapped(float u,float v,int mapper,Vector4 transform,Vector4 sheet,float time)
        {
            if(mapper==2)
            {
                int cols=Math.Max(1,(int)sheet.Y),count=Math.Max(1,(int)sheet.Z);
                float f=(float)Math.Floor(time*sheet.X+sheet.W)%count;if(f<0)f+=count;
                return new Vector2((u+(int)f%cols)/cols,(v+(int)f/cols)/cols);
            }
            if(mapper==1)return new Vector2(u*transform.X+Frac(time*transform.Z),v*transform.Y+Frac(time*transform.W));
            return new Vector2(u,v);
        }
        public void Coordinates(float u,float v,float u1,float v1,float wx,float wy,float time,out Vector4 uv,out Vector4 noise)
        {
            noise=Vector4.Zero;
            if(Lightning)
            {
                float du=Frac(time*.25f*PrimaryUV.X),dv=Frac(time*.25f*PrimaryUV.Y);
                uv=new Vector4(u*PrimaryUV.Z+du,v*PrimaryUV.W+dv,u*PrimaryUV.Z-du,v*PrimaryUV.W-dv);
                float x=u*NoiseScale,y=v*NoiseScale+(UniqueWorld?(wx+wy)*WorldScale*.1f:0);
                float t=time*.0025f*NoiseSpeed;
                noise=new Vector4(x*NoiseCos+y*NoiseSin+t,y*NoiseCos-x*NoiseSin,x*NoiseCos-y*NoiseSin+t,y*NoiseCos+x*NoiseSin);
                return;
            }
            var a=Mapped(u,v,PrimaryMapper,PrimaryUV,Animation,time);
            var b=Mapped(WorldUV?wx:u1,WorldUV?wy:v1,SecondaryMapper,SecondaryUV,Animation,time);
            uv=new Vector4(a.X,a.Y,b.X,b.Y);
        }
        public Vector4 Shade(Vector4 uv,Vector4 noise,float fade,float light,float specular)
        {
            if(Lightning)
            {
                if(Second!=null && NoiseAmplitude!=0)
                {
                    var n=Sample(Second,noise.X,noise.Y)+Sample(Second,noise.Z,noise.W)-Vector4.One;
                    uv+=new Vector4(n.X,n.Y,n.X,n.Y)*NoiseAmplitude;
                }
                var c=Sample(First,uv.X,uv.Y);
                if(Multi)c*=Sample(First,uv.Z,uv.W);
                // Map the linear shader intensity into this viewer's LDR/sRGB display.
                c*=Color*fade;c.W=Clamp(Opacity);
                return c;
            }
            var color=UsesFirst?Sample(First,uv.X,uv.Y):Vector4.One;
            float alpha=color.W;
            if(Second!=null)
            {
                var detail=Sample(Second,uv.Z,uv.W);
                if(SecondaryMode<=1)color*=detail*(SecondaryMode==1?2:1);
                else if(SecondaryMode==2)color=Vector4.Lerp(color,detail,detail.W);
                else specular*=detail.W;
            }
            var illumination=Ambient*.32f+Color*light+Emission*HDR;
            color=color*illumination+Specular*specular;
            color.W=Clamp((Blend==SurfaceBlend.Opaque && !AlphaTest?1:alpha)*Opacity*fade);
            return color;
        }
    }
}
