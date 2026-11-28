using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Xml.Linq;

namespace W3xScene
{
    // Each ScriptedModelDraw owns its XML selection, pose and clock, even when another row is selected.
    class DrawPreview
    {
        public XElement Module, ModelRule, PinnedModelRule, State, Action;
        public readonly XElement ImportedNode;
        public readonly XElement Group;
        public readonly string Original;
        public readonly DrawXmlFile SourceFile;
        public int[] SourceElementPath;
        public string Saved;
        public string Applied, Draft, ModelName, MountNote;
        public bool Visible=true, Manual;
        public DrawFx Effects;
        public List<SceneItem> Items=new List<SceneItem>();
        public SceneItem Actor {get{return Items.Count==0 ? null : Items[0];}}
        public XElement PlayingAction {get;private set;}
        public bool InTransition {get;private set;}
        public int PlaybackVersion {get;private set;}
        public bool PausedByUser {get;private set;}
        string mode;
        XElement visibilityModule;
        double elapsed,seconds;

        public DrawPreview(XElement module,DrawXmlFile sourceFile,DrawPreview parent=null)
        {
            Module=module;Group=parent!=null ? parent.Group : module.Parent ?? module;SourceFile=sourceFile;
            ImportedNode=parent==null ? module : null;
            Draft=Applied=module.ToString();
            Original=Saved=sourceFile==null || parent!=null ? "" : Applied;
            SourceElementPath=parent==null ? DrawXmlFile.ElementPath(module) : null;
        }
        public Clip FindClip(XElement action,IEnumerable<Clip> clips)
        {
            if(Actor==null || action==null)return null;
            string name=DrawRules.A(action,"AnimationName");
            return clips.FirstOrDefault(c=>Import.Names.Equals(c.Id,name) && Import.Names.Equals(c.RigId,Actor.Spec.RigId));
        }
        static double Duration(string text)
        {
            int suffix=text.EndsWith("ms",StringComparison.Ordinal) ? 2 : text.EndsWith("s",StringComparison.Ordinal) ? 1 : 0;
            double value;
            if(suffix==0 || !double.TryParse(text.Substring(0,text.Length-suffix),NumberStyles.Float,CultureInfo.InvariantCulture,out value) ||
                double.IsNaN(value) || double.IsInfinity(value) || value<=0)return 0;
            return suffix==2 ? value/1000 : value;
        }
        static double Seconds(XElement action,Clip clip)
        {
            double duration=Duration(DrawRules.A(action,"AnimationAbsoluteTime"));
            return duration>0 ? duration : clip.Frames/clip.Fps;
        }
        public static int FrameCount(XElement action,Clip clip)
        {
            return (int)Math.Max(1,Math.Min(int.MaxValue,Math.Round(Seconds(action,clip)*clip.Fps)));
        }
        public void Start(XElement action,IEnumerable<Clip> clips,bool transition=false)
        {
            PlaybackVersion++;PausedByUser=false;
            InTransition=transition;PlayingAction=action;elapsed=0;
            mode=DrawRules.A(action,"AnimationMode","LOOP");
            if(Actor==null)return;
            if(Module!=visibilityModule)
            {
                foreach(var part in Actor.RenderParts)part.DrawVisible=null;
                visibilityModule=Module;
            }
            DrawRules.ApplyVisibility(State,Actor);
            if(transition && action!=null && action.Parent!=State)DrawRules.ApplyVisibility(action.Parent,Actor);
            var clip=FindClip(action,clips);Actor.SetMotion(clip);
            if(clip==null)return;
            seconds=Seconds(action,clip);
            int count=FrameCount(action,clip);
            Actor.SetPlaybackFrames(count);
            Actor.Playing=mode!="MANUAL" && mode!="MATCH_UNPACKING" && mode!="PLAY_TO_FRAME";
            if(mode.EndsWith("BACKWARDS"))Actor.Frame=count-1;
            Actor.Apply();
        }
        public void Toggle()
        {
            if(Actor==null || Actor.Motion==null)return;
            if(!Actor.Playing && mode.StartsWith("ONCE") && elapsed>=seconds) elapsed=0;
            Actor.Playing=!Actor.Playing;
            PausedByUser=!Actor.Playing;
        }
        public void Seek(double frame)
        {
            if(Actor==null || Actor.Motion==null)return;
            PlaybackVersion++;PausedByUser=true;
            Actor.Playing=false;Actor.Frame=Math.Round(frame);Actor.Apply();
            int count=Actor.PlaybackFrames;
            elapsed=(mode.EndsWith("BACKWARDS") ? count-1-Actor.Frame : Actor.Frame)/count*seconds;
        }
        public void Tick(double dt,IEnumerable<Clip> clips)
        {
            for(int i=1;i<Items.Count;i++)Items[i].Advance(dt);
            if(Actor==null || !Actor.Playing || Actor.Motion==null)return;
            elapsed+=dt;double phase=elapsed/seconds;
            if((mode.StartsWith("ONCE") || InTransition) && phase>=1)
            {
                if(InTransition){Start(Action,clips);return;}
                phase=1;Actor.Playing=false;
            }
            else if(mode=="LOOP_PINGPONG"){phase%=2;if(phase>1)phase=2-phase;}
            else phase%=1;
            int count=Actor.PlaybackFrames;
            double frame=Math.Min(count-1,phase*count);
            Actor.Frame=mode.EndsWith("BACKWARDS") ? count-1-frame : frame;
            Actor.Apply();
        }
    }
}
