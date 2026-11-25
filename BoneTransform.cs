using System;
using System.IO;
using System.Linq;
using System.Windows.Media.Media3D;
using System.Xml;
using System.Xml.Linq;

namespace W3xScene
{
    struct BoneTransform
    {
        public readonly Vector3D Position;
        public readonly Quaternion Rotation;

        public BoneTransform(Vector3D position, Quaternion rotation)
        {
            Position=position;
            Rotation=rotation;
        }

        public Matrix3D Matrix
        {
            get
            {
                var m=Matrix3D.Identity;
                m.Rotate(Rotation);
                m.Translate(Position);
                return m;
            }
        }

        // WPF uses row vectors. Fixed parent axes X, then Y, then Z.
        public Vector3D EulerDegrees()
        {
            var m=Matrix;
            double cosY=Math.Sqrt(m.M11*m.M11+m.M12*m.M12);
            double y=Math.Atan2(-m.M13,cosY);
            double x=cosY>1e-10 ? Math.Atan2(m.M23,m.M33) : 0;
            double z=cosY>1e-10 ? Math.Atan2(m.M12,m.M11) : Math.Atan2(-m.M21,m.M22);
            return new Vector3D(x,y,z)*(180/Math.PI);
        }
        public static Quaternion FromDegrees(Vector3D degrees)
        {
            var x=new Quaternion(new Vector3D(1,0,0),degrees.X%360);
            var y=new Quaternion(new Vector3D(0,1,0),degrees.Y%360);
            var z=new Quaternion(new Vector3D(0,0,1),degrees.Z%360);
            var q=z*y*x;
            q.Normalize();
            return q;
        }
    }

    static class RigExport
    {
        static void SetVector(XElement element, double x, double y, double z)
        {
            element.SetAttributeValue("X",(float)x);
            element.SetAttributeValue("Y",(float)y);
            element.SetAttributeValue("Z",(float)z);
        }

        public static void SaveCopy(SceneItem item,string destination)
        {
            var rig=item.Spec.Rig;
            destination=Path.GetFullPath(destination);
            if(Import.Names.Equals(destination,Path.GetFullPath(rig.Source)) ||
               Import.Names.Equals(destination,Path.GetFullPath(item.Spec.Source)))
                throw new IOException("请为副本选择新的文件名。");
            XDocument doc;
            using(var r=XmlReader.Create(rig.Source,new XmlReaderSettings {XmlResolver=null,DtdProcessing=DtdProcessing.Prohibit}))
                doc=XDocument.Load(r,LoadOptions.PreserveWhitespace);
            var ns=doc.Root.Name.Namespace;
            var hierarchy=doc.Root.Elements(ns+"W3DHierarchy").Single(h=>
                Import.Names.Equals((string)h.Attribute("id") ?? Path.GetFileNameWithoutExtension(rig.Source),rig.Id));
            var pivots=hierarchy.Elements(ns+"Pivot").ToArray();
            if(pivots.Length!=rig.Bones.Length || pivots.Where((p,i)=>
                ((string)p.Attribute("Name") ?? "")!=rig.Bones[i].Name || ((int?)p.Attribute("Parent") ?? -1)!=rig.Bones[i].Parent).Any())
                throw new InvalidDataException("源骨架的层级已变更，请重新导入后再编辑。");
            foreach(var edit in item.BoneEdits)
            {
                var original=rig.Bones[edit.Key].Local;
                var pivot=pivots[edit.Key];
                if(edit.Value.Position!=original.Position)
                {
                    var t=pivot.Element(ns+"Translation");
                    if(t==null) { t=new XElement(ns+"Translation"); pivot.AddFirst(t); }
                    var v=edit.Value.Position;
                    SetVector(t,v.X,v.Y,v.Z);
                }
                if(edit.Value.Rotation!=original.Rotation)
                {
                    var r=pivot.Element(ns+"Rotation");
                    if(r==null)
                    {
                        r=new XElement(ns+"Rotation");
                        var t=pivot.Element(ns+"Translation");
                        if(t==null) pivot.AddFirst(r); else t.AddAfterSelf(r);
                    }
                    var q=edit.Value.Rotation;
                    SetVector(r,q.X,q.Y,q.Z);
                    r.SetAttributeValue("W",(float)q.W);
                }
            }
            string temp=Path.Combine(Path.GetDirectoryName(destination),Guid.NewGuid().ToString("N")+".tmp");
            try
            {
                using(var output=new FileStream(temp,FileMode.CreateNew,FileAccess.Write,FileShare.None))
                    doc.Save(output,SaveOptions.DisableFormatting);
                if(File.Exists(destination)) File.Replace(temp,destination,null);
                else File.Move(temp,destination);
            }
            finally { if(File.Exists(temp)) File.Delete(temp); }
        }
    }
}
