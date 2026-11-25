using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Windows.Media.Media3D;
using System.Xml;
using System.Xml.Linq;

namespace W3xScene
{
    // Immutable track arrays are shared until a gesture edits a channel. Undo restores the array.
    class MotionDocument
    {
        public readonly Clip Original, Working;
        public readonly Rig Rig;
        readonly Stack<Track[]> undo=new Stack<Track[]>();
        Track[] saved;
        public bool Dirty { get { return saved!=Working.Tracks; } }
        public bool CanUndo { get { return undo.Count>0; } }
        public MotionDocument(Clip original,Rig rig)
        {
            if(rig==null || !Import.Names.Equals(original.RigId,rig.Id) || rig.Bones.Length==0)
                throw new InvalidDataException("请选择与动画匹配的骨架。");
            Original=original; Rig=rig;
            Working=new Clip {Id=original.Id,RigId=rig.Id,Frames=original.Frames,Fps=original.Fps,Source=original.Source,Tracks=original.Tracks};
            saved=Working.Tracks;
        }
        static Track Active(Track[] tracks,int bone,string type,int frame)
        {
            return tracks.LastOrDefault(t=>t.Bone==bone && t.Type==type && t.Start<=frame);
        }
        static int Index(Track t,int frame) { return Math.Min(frame-t.Start,t.Steps.Length-1); }
        static double Scalar(Track[] tracks,int bone,string type,int frame)
        {
            var t=Active(tracks,bone,type,frame);
            return t==null ? 0 : t.Scalars[Index(t,frame)];
        }
        static Quaternion Turn(Track[] tracks,int bone,int frame)
        {
            var t=Active(tracks,bone,"Orientation",frame);
            return t==null ? Quaternion.Identity : t.Rotations[Index(t,frame)];
        }
        static Quaternion Inverse(Quaternion q) { q.Invert(); return q; }
        static Vector3D Rotate(Quaternion q,Vector3D v)
        {
            var m=Matrix3D.Identity; m.Rotate(q); return m.Transform(v);
        }
        public BoneTransform Local(Track[] tracks,int bone,int frame)
        {
            var rest=Rig.Bones[bone].Local;
            var v=new Vector3D(Scalar(tracks,bone,"XTranslation",frame),Scalar(tracks,bone,"YTranslation",frame),Scalar(tracks,bone,"ZTranslation",frame));
            return new BoneTransform(rest.Position+Rotate(rest.Rotation,v),rest.Rotation*Turn(tracks,bone,frame));
        }
        static double Weight(int f,int start,int center,int end)
        {
            if(f==center) return 1;
            if(f<=start || f>=end) return 0;
            double t=f<center ? (double)(f-start)/(center-start) : (double)(end-f)/(end-center);
            return t*t*(3-2*t);
        }
        // Build from the pose at mouse-down, even during repeated live drag updates.
        public void Preview(Track[] before,int bone,int center,int start,int end,BoneTransform desired)
        {
            if(start<0 || center<start || end<center || end>=Working.Frames) throw new ArgumentOutOfRangeException("center");
            var local=Local(before,bone,center);
            var rest=Rig.Bones[bone].Local.Rotation;
            var delta=Rotate(Inverse(rest),desired.Position-local.Position);
            var rotation=Inverse(rest)*desired.Rotation*Inverse(local.Rotation)*rest;
            rotation.Normalize();
            var changed=new List<Track>();
            string[] types={"XTranslation","YTranslation","ZTranslation","Orientation"};
            double[] offsets={delta.X,delta.Y,delta.Z};
            for(int axis=0;axis<4;axis++)
            {
                bool angular=axis==3;
                if(angular ? rotation.X*rotation.X+rotation.Y*rotation.Y+rotation.Z*rotation.Z<1e-24 : Math.Abs(offsets[axis])<1e-12) continue;
                string type=types[axis];
                var sources=before.Where(t=>t.Bone==bone && t.Type==type).ToArray();
                var tnew=new Track {Bone=bone,Type=type,Start=0,Steps=new bool[Working.Frames]};
                if(angular) tnew.Rotations=new Quaternion[Working.Frames]; else tnew.Scalars=new double[Working.Frames];
                Track prev=null;
                for(int f=0;f<Working.Frames;f++)
                {
                    var src=sources.LastOrDefault(t=>t.Start<=f);
                    int j=src==null ? 0 : Index(src,f);
                    tnew.Steps[f]=src!=null && (src!=prev || (f-src.Start<src.Steps.Length && src.Steps[j]));
                    prev=src;
                    double w=Weight(f,start,center,end);
                    if(angular)
                    {
                        var q=src==null ? Quaternion.Identity : src.Rotations[j];
                        if(w!=0){ q=Quaternion.Slerp(Quaternion.Identity,rotation,w)*q; q.Normalize(); }
                        tnew.Rotations[f]=q;
                    }
                    else tnew.Scalars[f]=(src==null ? 0 : src.Scalars[j])+w*offsets[axis];
                }
                changed.Add(tnew);
            }
            Working.Tracks=changed.Count==0 ? before : before.Where(t=>!changed.Any(c=>c.Type==t.Type && c.Bone==t.Bone)).Concat(changed).ToArray();
        }
        public void Commit(Track[] before)
        {
            if(before!=Working.Tracks) undo.Push(before);
        }
        public void Undo() { if(CanUndo) Working.Tracks=undo.Pop(); }

        public void Save(string path)
        {
            path=Path.GetFullPath(path);
            if(Import.Names.Equals(path,Original.Source) || Import.Names.Equals(path,Rig.Source))
                throw new IOException("请为动画副本选择新的文件名。");
            string id=Path.GetFileNameWithoutExtension(path);
            if(Import.Names.Equals(id,Original.Id)) throw new IOException("新动画的文件名需要与源动画 ID 不同。");
            XDocument source;
            using(var r=XmlReader.Create(Original.Source,new XmlReaderSettings{XmlResolver=null}))
                source=XDocument.Load(r,LoadOptions.PreserveWhitespace);
            var ns=source.Root.Name.Namespace;
            var clip=new XElement(source.Root.Elements(ns+"W3DAnimation").Single(e=>Import.Names.Equals((string)e.Attribute("id") ?? Path.GetFileNameWithoutExtension(Original.Source),Original.Id)));
            clip.SetAttributeValue("id",id);
            var channels=clip.Element(ns+"Channels");
            if(channels==null) { channels=new XElement(ns+"Channels"); clip.Add(channels); }
            foreach(var t in Working.Tracks.Except(Original.Tracks))
            {
                channels.Elements().Where(c=>((int?)c.Attribute("Pivot") ?? 0)==t.Bone && (string)c.Attribute("Type")==t.Type).Remove();
                var channel=new XElement(ns+(t.Rotations==null ? "ChannelScalar" : "ChannelQuaternion"),
                    new XAttribute("Pivot",t.Bone),new XAttribute("Type",t.Type),new XAttribute("FirstFrame",t.Start));
                for(int i=0;i<t.Steps.Length;i++)
                {
                    var frame=new XElement(ns+"Frame");
                    if(t.Steps[i]) frame.SetAttributeValue("BinaryMove",true);
                    if(t.Rotations==null) frame.SetValue((float)t.Scalars[i]);
                    else
                    {
                        var q=t.Rotations[i];
                        frame.SetAttributeValue("X",(float)q.X); frame.SetAttributeValue("Y",(float)q.Y);
                        frame.SetAttributeValue("Z",(float)q.Z); frame.SetAttributeValue("W",(float)q.W);
                    }
                    channel.Add(frame);
                }
                channels.Add(channel);
            }
            var relative=new Uri(path).MakeRelativeUri(new Uri(Path.GetFullPath(Rig.Source)));
            string rigPath=relative.IsAbsoluteUri ? Rig.Source : Uri.UnescapeDataString(relative.ToString());
            var root=new XElement(ns+"AssetDeclaration",new XElement(ns+"Includes",new XElement(ns+"Include",
                new XAttribute("type","all"),new XAttribute("source",rigPath))),clip);
            string temp=Path.Combine(Path.GetDirectoryName(path),Guid.NewGuid().ToString("N")+".tmp");
            try
            {
                new XDocument(root).Save(temp);
                if(File.Exists(path)) File.Replace(temp,path,null); else File.Move(temp,path);
                saved=Working.Tracks;
            }
            finally { if(File.Exists(temp)) File.Delete(temp); }
        }
    }
}
