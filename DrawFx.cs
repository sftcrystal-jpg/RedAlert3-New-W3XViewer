using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using System.Threading.Tasks;
using System.Windows.Media.Media3D;
using System.Xml.Linq;

namespace W3xScene
{
    sealed class DrawFx
    {
        internal sealed class Binding
        {
            public string Kind,Id,Bone,Problem;
            public int Index,Frame,Step,Stop;
            public bool Timed,Follow,FireWhenSkipped;
            public Task<FxAsset> Resource;
            public readonly List<Emitter> Live=new List<Emitter>();
            public override string ToString(){return Id+"  ·  "+(Bone.Length==0 ? "模型原点" : Bone)+(Timed ? "  ·  帧 "+Frame : "");}
        }
        internal sealed class Emitter
        {
            public int Handle;
            public Matrix3D Initial;
        }
        public readonly List<Binding> Bindings=new List<Binding>();
        readonly FxService service;
        readonly DrawPreview draw;
        XElement rule,state;
        SceneItem actor;
        int version=-1,previous;
        bool water;
        double remainder;
        static int nextHandle;

        public DrawFx(FxService service,DrawPreview draw){this.service=service;this.draw=draw;}
        static bool Flag(XElement e,string key)
        {string s=DrawRules.A(e,key);return s=="1" || s.Equals("true",StringComparison.OrdinalIgnoreCase);}
        static int Integer(XElement e,string key,int fallback)
        {int n;return int.TryParse(DrawRules.A(e,key),out n)?n:fallback;}

        void Add(Binding b)
        {
            if(string.IsNullOrWhiteSpace(b.Id))return;
            b.Index=-1;
            if(actor==null)b.Problem="缺少模型 "+draw.ModelName;
            else if(b.Bone.Length>0 && !Import.Names.Equals(b.Bone,"NONE"))
            {
                if(actor.Spec.Rig!=null)
                    b.Index=Array.FindIndex(actor.Spec.Rig.Bones,bone=>Import.Names.Equals(bone.Name,b.Bone));
                if(b.Index<0)b.Problem="缺少骨骼 "+b.Bone;
            }
            if(b.Problem==null)b.Resource=service.Load(b.Kind,b.Id);
            Bindings.Add(b);
        }
        void Read(XElement entry)
        {
            if(entry==null)return;
            foreach(var p in DrawRules.Children(entry,"ParticleSysBone"))
            {
                if((water && Flag(p,"OnlyIfOnLand")) || (!water && Flag(p,"OnlyIfOnWater")))continue;
                var action=DrawRules.A(p,"FXAction");
                if(action=="KILL" || action=="HOLD" || DrawRules.A(p,"FXTrigger","NONE")!="NONE")continue;
                Add(new Binding{Kind="particle",Id=DrawRules.A(p,"FXParticleSystemTemplate"),Bone=DrawRules.A(p,"BoneName").Trim(),Follow=Flag(p,"FollowBone")});
            }
            foreach(var e in DrawRules.Children(entry,"FXEvent"))
            {
                int start=Integer(e,"Frame",0);
                Add(new Binding{Kind="fxlist",Id=DrawRules.A(e,"Effect"),Bone=DrawRules.A(e,"Bone").Trim(),
                    Timed=true,Frame=start,Step=Integer(e,"FrameStep",0),Stop=Integer(e,"FrameStop",start),FireWhenSkipped=Flag(e,"FireWhenSkipped")});
            }
            if(entry.Attribute("EnteringStateFX")!=null)
                Add(new Binding{Kind="fxlist",Id=DrawRules.A(entry,"EnteringStateFX"),Bone=""});
        }
        public void Clear()
        {
            if(actor!=null)actor.Particles.Clear();
            foreach(var b in Bindings)foreach(var em in b.Live)if(em.Handle!=0)service.Simulation.Stop(em.Handle);
            Bindings.Clear();version=-1;remainder=0;
        }
        public bool Sync(bool overWater)
        {
            var playingState=draw.InTransition && draw.PlayingAction!=null ? draw.PlayingAction.Parent : draw.State;
            if(version==draw.PlaybackVersion && actor==draw.Actor && rule==draw.ModelRule && state==playingState && water==overWater)return false;
            Clear();actor=draw.Actor;rule=draw.ModelRule;state=playingState;water=overWater;version=draw.PlaybackVersion;
            Read(rule);Read(state);
            previous=actor==null ? 0 : actor.SourceFrame;
            foreach(var b in Bindings)if(!b.Timed || EventBetween(b,previous,previous))Fire(b);
            return true;
        }
        static bool EventBetween(Binding b,int lo,int hi)
        {
            if(b.Frame>=lo && b.Frame<=hi)return true;
            if(b.Step<1 || b.Stop<b.Frame || hi<b.Frame)return false;
            long n=Math.Max(0L,((long)lo-b.Frame+b.Step-1)/b.Step);
            return b.Frame+n*b.Step<=Math.Min(hi,b.Stop);
        }
        Matrix3D Pose(Binding b)
        {return b.Index<0 ? actor.Group.Transform.Value : actor.BoneWorldMatrix(b.Index);}
        void Fire(Binding b)
        {
            if(b.Problem!=null)return;
            if(b.Live.Count>=16)
            {
                var oldest=b.Live[0];if(oldest.Handle!=0)service.Simulation.Stop(oldest.Handle);b.Live.RemoveAt(0);
            }
            b.Live.Add(new Emitter{Initial=Pose(b)});
        }
        public void Tick(double dt)
        {
            if(actor==null || Bindings.Count==0)return;
            int frame=actor.SourceFrame;
            string mode=DrawRules.A(draw.PlayingAction,"AnimationMode","LOOP");
            bool backwards=mode.EndsWith("BACKWARDS",StringComparison.Ordinal);
            int last=actor.Motion==null ? 0 : actor.Motion.Frames-1;
            if(previous!=frame)
            {
                foreach(var b in Bindings.Where(b=>b.Timed))
                {
                    bool wrap=mode.StartsWith("LOOP") && mode!="LOOP_PINGPONG" && (backwards ? frame>previous : frame<previous);
                    bool hit=!b.FireWhenSkipped ? EventBetween(b,frame,frame) : wrap ? EventBetween(b,Math.Max(previous,frame)+(backwards?0:1),last) ||
                        EventBetween(b,0,Math.Min(previous,frame)-(backwards?1:0))
                        : frame>previous ? EventBetween(b,previous+1,frame) : EventBetween(b,frame,previous-1);
                    if(hit)Fire(b);
                }
                previous=frame;
            }
            if(!draw.PausedByUser)remainder+=Math.Min(dt,.2)*30;
            int steps=(int)remainder;remainder-=steps;
            if(steps==0 && !Bindings.Any(b=>b.Problem==null && b.Resource.IsCompleted && b.Live.Any(e=>e.Handle==0)))return;
            actor.Particles.Clear();
            foreach(var b in Bindings)
            {
                if(b.Problem!=null || !b.Resource.IsCompleted)continue;
                try
                {
                    var asset=b.Resource.GetAwaiter().GetResult();
                    for(int i=b.Live.Count-1;i>=0;i--)
                    {
                        var em=b.Live[i];bool fresh=em.Handle==0;
                        if(fresh){em.Handle=++nextHandle;service.Simulation.Start(em.Handle,asset.Key,water);}
                        var values=service.Simulation.Sample(em.Handle,fresh?Math.Max(1,steps):steps,b.Follow?Pose(b):em.Initial);
                        if(values.Length==1){service.Simulation.Stop(em.Handle);b.Live.RemoveAt(i);continue;}
                        Append(values,asset);
                    }
                }
                catch(Exception ex){b.Problem=ex.GetBaseException().Message;}
            }
        }
        void Append(float[] values,FxAsset asset)
        {
            for(int i=0;i<values.Length;i+=14)
            {
                var sprite=new ParticleSprite{Material=asset.Materials[(int)values[i]],Center=new Point3D(values[i+1],values[i+2],values[i+3]),
                    Size=values[i+4],Angle=values[i+5],Color=new Vector4(values[i+6],values[i+7],values[i+8],values[i+9]),
                    UV=new Vector4(values[i+10],values[i+11],values[i+12],values[i+13])};
                actor.Particles.Add(sprite);
                if(sprite.Material.TwoQuads || sprite.Material.Concentric)
                {
                    sprite.Angle+=(float)(Math.PI*.5);if(sprite.Material.Concentric)sprite.Size*=.55f;actor.Particles.Add(sprite);
                }
            }
        }
        public string Summary()
        {
            if(Bindings.Count==0)return "当前状态没有挂载特效。";
            var messages=Bindings.Where(b=>b.Problem!=null).Select(b=>b.Id+" · "+b.Problem)
                .Concat(Bindings.Where(b=>b.Resource!=null && b.Resource.Status==TaskStatus.RanToCompletion && b.Resource.Result.Note!=null)
                    .Select(b=>b.Resource.Result.Note)).Distinct().ToArray();
            string state=Bindings.Any(b=>b.Resource!=null && !b.Resource.IsCompleted)?"正在读取特效…":"挂载特效 "+Bindings.Count+" 处";
            return messages.Length==0?state:state+"\n"+string.Join("\n",messages);
        }
    }
}
