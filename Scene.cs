using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows.Media;
using System.Windows.Media.Media3D;

namespace W3xScene
{
    static class Shapes
    {
        public static Material Paint(byte r,byte g,byte b)
        {
            var m=new DiffuseMaterial(new SolidColorBrush(Color.FromRgb(r,g,b))); m.Freeze(); return m;
        }
        public static GeometryModel3D Surface(MeshGeometry3D shape,Material mat)
        {
            return new GeometryModel3D(shape,mat){BackMaterial=mat};
        }
        public static void Face(MeshGeometry3D shape,Point3D a,Point3D b,Point3D c,Point3D d)
        {
            int i=shape.Positions.Count;
            shape.Positions.Add(a); shape.Positions.Add(b); shape.Positions.Add(c); shape.Positions.Add(d);
            foreach(int n in new[]{0,1,2,0,2,3}) shape.TriangleIndices.Add(i+n);
        }
        public static Model3D Grid(double spacing,Point3D focus)
        {
            var grid=new MeshGeometry3D();
            double x=Math.Floor(focus.X/spacing)*spacing, y=Math.Floor(focus.Y/spacing)*spacing;
            double r=spacing*22,t=spacing*.009;
            for(int i=-22;i<=22;i++)
            {
                double a=x+i*spacing, b=y+i*spacing;
                Face(grid,new Point3D(a-t,y-r,-t),new Point3D(a+t,y-r,-t),new Point3D(a+t,y+r,-t),new Point3D(a-t,y+r,-t));
                Face(grid,new Point3D(x-r,b-t,-t),new Point3D(x+r,b-t,-t),new Point3D(x+r,b+t,-t),new Point3D(x-r,b+t,-t));
            }
            var model=Surface(grid,Paint(61,79,89)); model.Freeze(); return model;
        }
        public static GeometryModel3D BoxOutline(CollisionBox box)
        {
            var shape=new MeshGeometry3D();
            double width=Math.Max(.012,box.Extent.Length*.007);
            var corners=new Point3D[8];
            for(int i=0;i<8;i++)
                corners[i]=(Point3D)(box.Center+new Vector3D((i&1)==0 ? -box.Extent.X : box.Extent.X,
                    (i&2)==0 ? -box.Extent.Y : box.Extent.Y,(i&4)==0 ? -box.Extent.Z : box.Extent.Z));
            foreach(int bit in new[]{1,2,4})
            {
                Vector3D u=bit==1 ? new Vector3D(0,width,0) : new Vector3D(width,0,0);
                Vector3D v=bit==4 ? new Vector3D(0,width,0) : new Vector3D(0,0,width);
                var ring=new[]{u+v,u-v,-u-v,-u+v};
                for(int i=0;i<8;i++) if((i&bit)==0)
                    for(int j=0;j<4;j++)
                    {
                        Point3D a=corners[i], b=corners[i|bit];
                        var p=ring[j]; var q=ring[(j+1)%4];
                        Face(shape,a+p,a+q,b+q,b+p);
                    }
            }
            shape.Freeze(); return Surface(shape,Paint(110,224,195));
        }
    }

    class RenderPart
    {
        public readonly GeometryModel3D Model;
        public Mesh Source {get{return mesh;}}
        public readonly string SubObjectId;
        public bool? DrawVisible;
        public bool Deforms {get{return skinned;}}
        readonly Mesh mesh;
        readonly MeshGeometry3D geometry;
        readonly MatrixTransform3D transform=new MatrixTransform3D();
        readonly int rigidBone, parentBone;
        readonly bool skinned;
        public RenderPart(Attachment attachment)
        {
            mesh=attachment.Mesh;
            SubObjectId=attachment.SubObjectId ?? mesh.Id.Substring(mesh.Id.LastIndexOf('.')+1);
            parentBone=rigidBone=attachment.Bone;
            skinned=mesh.Weights.Length>0;
            if(mesh.Weights.Length==1 && mesh.Weights[0].All(w=>w.Bone==mesh.Weights[0][0].Bone && w.Weight>0))
            { rigidBone=mesh.Weights[0][0].Bone; skinned=false; }
            geometry=new MeshGeometry3D { Positions=new Point3DCollection(mesh.Positions[0]), TriangleIndices=new Int32Collection(mesh.Indices) };
            if(mesh.Normals.Length>0) geometry.Normals=new Vector3DCollection(mesh.Normals[0]);
            if(mesh.UV.Length==mesh.Positions[0].Length) geometry.TextureCoordinates=new PointCollection(mesh.UV);
            if(!skinned) geometry.Freeze();
            Model=Shapes.Surface(geometry,mesh.Material);
            Model.Transform=transform;
        }
        public void Update(Matrix3D[] pose,bool[] visible)
        {
            int show=skinned ? parentBone : rigidBone;
            bool hidden=!(DrawVisible ?? !mesh.Hidden) || (show>=0 && show<visible.Length && !visible[show]);
            Model.Geometry=hidden ? null : geometry;
            if(hidden) return;
            if(!skinned)
            {
                transform.Matrix=rigidBone>=0 && rigidBone<pose.Length ? pose[rigidBone] : Matrix3D.Identity;
                return;
            }
            if(pose.Length==0) return;
            var points=new Point3DCollection(geometry.Positions.Count);
            var normals=new Vector3DCollection(geometry.Positions.Count);
            for(int i=0;i<mesh.Positions[0].Length;i++)
            {
                Vector3D position=new Vector3D(), normal=new Vector3D();
                double total=0;
                for(int s=0;s<mesh.Weights.Length;s++)
                {
                    Influence w=mesh.Weights[s][i];
                    if(w.Weight<=0 || w.Bone<0 || w.Bone>=pose.Length) continue;
                    // Each influence stream stores a position in that bone's local space.
                    var v=mesh.Positions[Math.Min(s,mesh.Positions.Length-1)][i];
                    position+=(Vector3D)pose[w.Bone].Transform(v)*w.Weight;
                    if(mesh.Normals.Length>0)
                        normal+=pose[w.Bone].Transform(mesh.Normals[Math.Min(s,mesh.Normals.Length-1)][i])*w.Weight;
                    total+=w.Weight;
                }
                points.Add(total>0 ? (Point3D)(position/total) : mesh.Positions[0][i]);
                if(mesh.Normals.Length>0)
                {
                    if(normal.LengthSquared>1e-14) normal.Normalize();
                    normals.Add(normal);
                }
            }
            points.Freeze(); geometry.Positions=points;
            if(normals.Count>0) { normals.Freeze(); geometry.Normals=normals; }
        }
    }

    class SceneItem
    {
        public ModelSpec Spec;
        public readonly Model3DGroup Group=new Model3DGroup();
        public readonly TranslateTransform3D Offset=new TranslateTransform3D();
        readonly MatrixTransform3D orientation=new MatrixTransform3D();
        readonly List<SceneItem> mountedItems=new List<SceneItem>();
        public SceneItem MountParent {get;private set;}
        public int MountBone {get;private set;}
        public bool FollowBoneRotation {get;private set;}
        readonly GeometryModel3D skeleton=Shapes.Surface(new MeshGeometry3D(),Shapes.Paint(255,189,84));
        readonly List<RenderPart> parts=new List<RenderPart>();
        public IEnumerable<RenderPart> RenderParts {get{return parts;}}
        public readonly bool HasEffects;
        public readonly List<ParticleSprite> Particles=new List<ParticleSprite>();
        readonly List<GeometryModel3D> boxes=new List<GeometryModel3D>();
        public IEnumerable<GeometryModel3D> VisibleGuides
        {
            get
            {
                if(skeleton.Geometry!=null)yield return skeleton;
                if(ShowBoxes)foreach(var box in boxes)yield return box;
            }
        }
        public readonly List<Clip> Clips=new List<Clip>();
        public Clip Motion;
        readonly Dictionary<Clip,int> playbackLengths=new Dictionary<Clip,int>();
        public int PlaybackFrames
        {
            get
            {
                if(Motion==null) return 0;
                int length;
                return playbackLengths.TryGetValue(Motion,out length) ? length : Motion.Frames;
            }
        }
        public bool Playing, ShowBones, ShowBoxes;
        public bool EditingBones;
        public readonly Dictionary<int,BoneTransform> BoneEdits=new Dictionary<int,BoneTransform>();
        public BoneTransform LocalBone(int index)
        {
            BoneTransform edit;
            return BoneEdits.TryGetValue(index,out edit) ? edit : Spec.Rig.Bones[index].Local;
        }
        public double Frame;
        public Matrix3D[] Pose;
        public Rect3D RestBounds;
        public SceneItem(ModelSpec spec)
        {
            Spec=spec;
            var placement=new Transform3DGroup();
            placement.Children.Add(orientation);
            placement.Children.Add(Offset);
            Group.Transform=placement;
            MountBone=-1;
            foreach(var a in spec.Parts) { var p=new RenderPart(a); parts.Add(p); Group.Children.Add(p.Model); }
            HasEffects=spec.Parts.Any(a=>a.Mesh.Effect!=null);
            foreach(var box in spec.Boxes) boxes.Add(Shapes.BoxOutline(box.Value));
            ShowBoxes=spec.Parts.Count==0 && boxes.Count>0;
            Clips.AddRange(spec.Clips);
            Group.Children.Add(skeleton);
            Apply();
            RestBounds=Group.Bounds;
        }
        public override string ToString() { return Spec.Name; }
        public void SetPosition(double x,double y,double z)
        {
            if(MountParent!=null) return;
            Offset.OffsetX=x; Offset.OffsetY=y; Offset.OffsetZ=z;
            UpdateMountedItems();
        }
        public Matrix3D BoneWorldMatrix(int bone)
        {
            return Pose[bone]*Group.Transform.Value;
        }
        public bool CanMountOn(SceneItem target)
        {
            if(target==null || target.Pose.Length==0) return false;
            for(var ancestor=target;ancestor!=null;ancestor=ancestor.MountParent)
                if(ancestor==this) return false;
            return true;
        }
        public void MountOn(SceneItem parent,int bone,bool followRotation)
        {
            if(!CanMountOn(parent) || bone<0 || bone>=parent.Pose.Length)
                throw new InvalidOperationException("请选择其他对象上的有效骨骼，附加关系不能形成循环。");
            Detach();
            MountParent=parent; MountBone=bone; FollowBoneRotation=followRotation;
            parent.mountedItems.Add(this);
            FollowMount();
        }
        public void Detach()
        {
            if(MountParent==null) return;
            MountParent.mountedItems.Remove(this);
            MountParent=null; MountBone=-1; FollowBoneRotation=false;
            // The world transform already contains the last evaluated placement.
        }
        public void ResetPlacement()
        {
            Detach();orientation.Matrix=Matrix3D.Identity;SetPosition(0,0,0);
        }
        public void UnlinkFromScene()
        {
            Detach();
            foreach(var child in mountedItems.ToArray()) child.Detach();
        }
        void UpdateMountedItems()
        {
            foreach(var child in mountedItems) child.FollowMount();
        }
        void FollowMount()
        {
            var frame=MountParent.BoneWorldMatrix(MountBone);
            Offset.OffsetX=frame.OffsetX; Offset.OffsetY=frame.OffsetY; Offset.OffsetZ=frame.OffsetZ;
            if(FollowBoneRotation)
            {
                frame.OffsetX=frame.OffsetY=frame.OffsetZ=0;
                orientation.Matrix=frame;
            }
            UpdateMountedItems();
        }
        public void SetMotion(Clip clip)
        {
            Motion=clip; Frame=0; Playing=clip!=null; Apply();
        }
        // Time is zero-based. Align both endpoint poses and keep the final frame's dwell before wrapping.
        static double MapFrame(double frame,int fromCount,int toCount)
        {
            if(fromCount==toCount) return frame;
            if(fromCount==1 || toCount==1) return frame/fromCount*toCount;
            if(frame>=fromCount-1) return (toCount-1)+(frame-(fromCount-1));
            return frame*(toCount-1)/(fromCount-1);
        }
        public int SourceFrame {get{return Motion==null ? 0 : (int)Math.Floor(MapFrame(Frame,PlaybackFrames,Motion.Frames));}}
        public void SetPlaybackFrames(int count)
        {
            if(count<1) throw new ArgumentOutOfRangeException("count");
            if(Motion==null || count==PlaybackFrames) return;
            double sourceFrame=MapFrame(Frame,PlaybackFrames,Motion.Frames);
            Frame=MapFrame(sourceFrame,Motion.Frames,count);
            if(count==Motion.Frames) playbackLengths.Remove(Motion);
            else playbackLengths[Motion]=count;
            Apply();
        }
        public void Advance(double seconds)
        {
            if(EditingBones || !Playing || Motion==null) return;
            Frame=(Frame+seconds*Motion.Fps)%PlaybackFrames;
            Apply();
        }
        public void Apply()
        {
            var rig=Spec.Rig;
            int n=rig==null ? 0 : rig.Bones.Length;
            Pose=new Matrix3D[n];
            var visible=Enumerable.Repeat(true,n).ToArray();
            var shift=new Vector3D[n];
            var turn=Enumerable.Repeat(Quaternion.Identity,n).ToArray();
            if(Motion!=null && !EditingBones)
            {
                double sourceFrame=MapFrame(Frame,PlaybackFrames,Motion.Frames);
                foreach(var t in Motion.Tracks)
                {
                    int bone=t.Bone;
                    if(bone<0 || bone>=n) continue;
                    double local=sourceFrame-t.Start;
                    if(local<0) continue;
                    int last=t.Steps.Length-1;
                    int i=Math.Min((int)local,last), j=Math.Min(i+1,last);
                    double blend=t.Steps[j] ? 0 : Math.Min(local-i,1);
                    if(t.Rotations!=null) turn[bone]=Quaternion.Slerp(t.Rotations[i],t.Rotations[j],blend);
                    else
                    {
                        double value=t.Scalars[i]+(t.Scalars[j]-t.Scalars[i])*blend;
                        switch(t.Type)
                        {
                            case "XTranslation": shift[bone].X=value; break;
                            case "YTranslation": shift[bone].Y=value; break;
                            case "ZTranslation": shift[bone].Z=value; break;
                            case "Visibility": visible[bone]=t.Scalars[i]>.5; break;
                        }
                    }
                }
            }
            if(rig!=null)
            {
                foreach(int i in rig.Order)
                {
                    var local=Matrix3D.Identity;
                    local.Rotate(turn[i]); local.Translate(shift[i]); local.Append(LocalBone(i).Matrix);
                    int parent=rig.Bones[i].Parent;
                    if(parent>=0) { local.Append(Pose[parent]); visible[i]&=visible[parent]; }
                    Pose[i]=local;
                }
            }
            foreach(var part in parts) part.Update(Pose,visible);
            for(int i=0;i<boxes.Count;i++)
            {
                var box=boxes[i]; int bone=Spec.Boxes[i].Key;
                if(!ShowBoxes) { Group.Children.Remove(box); continue; }
                if(!Group.Children.Contains(box)) Group.Children.Add(box);
                box.Transform=bone>=0 && bone<n ? new MatrixTransform3D(Pose[bone]) : Transform3D.Identity;
            }
            skeleton.Geometry=rig!=null && (EditingBones || ShowBones || Spec.RigOnly) ? Skeleton(rig) : null;
            UpdateMountedItems();
        }
        MeshGeometry3D Skeleton(Rig rig)
        {
            var geom=new MeshGeometry3D();
            var pts=Pose.Select(m=>new Point3D(m.OffsetX,m.OffsetY,m.OffsetZ)).ToArray();
            Rect3D bounds=Rect3D.Empty;
            foreach(var p in pts) bounds.Union(p);
            double size=Math.Max(.05, Math.Max(bounds.SizeX,Math.Max(bounds.SizeY,bounds.SizeZ))*.009);
            for(int i=0;i<pts.Length;i++)
            {
                var b=pts[i];
                var a=rig.Bones[i].Parent<0 ? b : pts[rig.Bones[i].Parent];
                var axis=b-a;
                if(axis.Length<size) { a=b-new Vector3D(0,0,size); b+=new Vector3D(0,0,size); axis=b-a; }
                axis.Normalize();
                var u=Vector3D.CrossProduct(axis,Math.Abs(axis.Z)<.9 ? new Vector3D(0,0,1) : new Vector3D(1,0,0));
                u.Normalize(); u*=size;
                var v=Vector3D.CrossProduct(axis,u);
                var center=a+.20*(b-a);
                var ring=new[]{center+u,center+v,center-u,center-v};
                for(int j=0;j<4;j++) Shapes.Face(geom,a,ring[j],b,ring[(j+1)%4]);
            }
            geom.Freeze(); return geom;
        }
    }
}
