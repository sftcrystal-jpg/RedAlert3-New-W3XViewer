using System;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Media3D;

namespace W3xScene
{
    class MotionGizmo : FrameworkElement
    {
        readonly Func<Point3D,Point?> project;
        readonly Brush[] colors={Brushes.Salmon,Brushes.LightGreen,Brushes.DeepSkyBlue};
        public SceneItem Item;
        public int Bone=-1;
        public bool Rotating;
        public static Vector3D Axis(int i) {return i==0 ? new Vector3D(1,0,0) : i==1 ? new Vector3D(0,1,0) : new Vector3D(0,0,1);}
        public MotionGizmo(Func<Point3D,Point?> project){this.project=project; IsHitTestVisible=false;}

        // Store the projected screen tangent per model unit (or radian) at the picked segment.
        public int Hit(Point point,out Vector tangent)
        {
            tangent=new Vector();
            return Geometry(null,point,ref tangent);
        }
        protected override void OnRender(DrawingContext dc)
        {
            var unused=new Vector(); Geometry(dc,new Point(),ref unused);
        }
        int Geometry(DrawingContext dc,Point mouse,ref Vector tangent)
        {
            if(Item==null || Bone<0) return -1;
            var frame=Item.BoneWorldMatrix(Bone);
            var origin=new Point3D(frame.OffsetX,frame.OffsetY,frame.OffsetZ);
            var center=project(origin);
            if(!center.HasValue) return -1;
            int parent=Item.Spec.Rig.Bones[Bone].Parent;
            var basis=parent<0 ? Item.Group.Transform.Value : Item.BoneWorldMatrix(parent);
            var axes=new Vector3D[3]; double max=0;
            for(int i=0;i<3;i++)
            {
                axes[i]=basis.Transform(Axis(i));
                var end=project(origin+axes[i]);
                if(end.HasValue) max=Math.Max(max,(end.Value-center.Value).Length);
            }
            if(max<1e-8) return -1;
            double size=(Rotating ? 56 : 73)/max;
            double nearest=7; int hit=-1;
            for(int i=0;i<3;i++)
            {
                var pen=new Pen(colors[i],Rotating ? 2.4 : 3.5);
                int steps=Rotating ? 64 : 1;
                Point? prev=null;
                for(int k=0;k<=steps;k++)
                {
                    double a=2*Math.PI*k/steps;
                    var dir=Rotating ? axes[(i+1)%3]*Math.Cos(a)+axes[(i+2)%3]*Math.Sin(a) : axes[i]*k;
                    var p=project(origin+size*dir);
                    if(prev.HasValue && p.HasValue)
                    {
                        var d=p.Value-prev.Value;
                        if(dc!=null)
                        {
                            dc.DrawLine(pen,prev.Value,p.Value);
                            if(!Rotating && d.Length>12)
                            {
                                var unit=d/d.Length;
                                var wing=new Vector(-unit.Y,unit.X)*4;
                                var back=p.Value-unit*9;
                                dc.DrawLine(pen,p.Value,back+wing);
                                dc.DrawLine(pen,p.Value,back-wing);
                            }
                        }
                        else if(d.LengthSquared>0.05)
                        {
                            double f=Vector.Multiply(mouse-prev.Value,d)/d.LengthSquared;
                            double dist=(prev.Value+d*Math.Max(0,Math.Min(1,f))-mouse).Length;
                            if(dist<nearest && (Rotating || (mouse-center.Value).Length>8))
                            {nearest=dist;hit=i;tangent=d/(Rotating ? 2*Math.PI/steps : size);}
                        }
                    }
                    prev=p;
                }
            }
            return hit;
        }
    }
}
