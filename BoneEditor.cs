using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Media3D;
using Microsoft.Win32;

namespace W3xScene
{
    class BoneRow : INotifyPropertyChanged
    {
        public string Name {get;set;}
        public string Coordinates {get;private set;}
        public event PropertyChangedEventHandler PropertyChanged;
        public void Update(Matrix3D pose)
        {
            var value=string.Format(CultureInfo.InvariantCulture,"{0,7:0.##} {1,7:0.##} {2,7:0.##}",pose.OffsetX,pose.OffsetY,pose.OffsetZ);
            if(Coordinates==value) return;
            Coordinates=value;
            if(PropertyChanged!=null) PropertyChanged(this,new PropertyChangedEventArgs("Coordinates"));
        }
    }

    // Draw joints in screen space so attachment points inside the hull are selectable.
    class JointMarkers : FrameworkElement
    {
        public SceneItem Item;
        public int Selected=-1;
        readonly Func<Point3D,Point?> project;
        readonly Pen outline=new Pen(Brushes.Black,1.4);
        public JointMarkers(Func<Point3D,Point?> project) { this.project=project; IsHitTestVisible=false; }
        Point? ScreenPoint(int index)
        {
            var p=Item.BoneWorldMatrix(index);
            return project(new Point3D(p.OffsetX,p.OffsetY,p.OffsetZ));
        }
        protected override void OnRender(DrawingContext dc)
        {
            if(Item==null) return;
            for(int i=0;i<Item.Pose.Length;i++)
            {
                var p=ScreenPoint(i);
                if(p.HasValue) dc.DrawEllipse(Brushes.Goldenrod,outline,p.Value,3.4,3.4);
            }
            if(Selected<0) return;
            var selected=ScreenPoint(Selected);
            if(!selected.HasValue) return;
            dc.DrawEllipse(Brushes.Turquoise,outline,selected.Value,6.8,6.8);
            dc.DrawEllipse(Brushes.Black,null,selected.Value,1.8,1.8);
        }
        public int Nearest(Point point)
        {
            int best=-1;
            double distance=9*9;
            for(int i=0;i<Item.Pose.Length;i++)
            {
                var p=ScreenPoint(i); if(!p.HasValue) continue;
                double d=(point-p.Value).LengthSquared;
                if(d<distance) { best=i; distance=d; }
            }
            return best;
        }
    }

    class BoneEditor
    {
        readonly Window window;
        readonly Action<string> status;
        readonly ListBox list;
        readonly JointMarkers markers;
        readonly TextBox[] fields;
        readonly string usualHint;
        readonly double[] displayed=new double[6];
        readonly string[] formatted=new string[6];
        List<BoneRow> rows;
        BoneTransform initial;
        public SceneItem Item {get;private set;}
        bool filling;
        bool valid;
        T UI<T>(string name) where T:class {return (T)window.FindName(name);}

        public BoneEditor(Window window,Func<Point3D,Point?> project,Action<string> status)
        {
            this.window=window;
            this.status=status;
            usualHint=UI<TextBlock>("ViewHint").Text;
            list=UI<ListBox>("BoneList");
            markers=new JointMarkers(project);
            UI<Grid>("JointOverlay").Children.Add(markers);
            fields=new[]{"BoneX","BoneY","BoneZ","BoneRX","BoneRY","BoneRZ"}.Select(UI<TextBox>).ToArray();
            foreach(var box in fields) box.TextChanged+=(s,e)=>Change();
            list.SelectionChanged+=(s,e)=>Fill();
            UI<Button>("DoneBones").Click+=(s,e)=>Close();
            UI<Button>("ResetBone").Click+=(s,e)=>{
                Item.BoneEdits.Remove(list.SelectedIndex); Item.Apply(); UpdateRows(); Fill();
            };
            UI<Button>("SaveRig").Click+=(s,e)=>Save();
        }
        public void Open(SceneItem item)
        {
            if(item==null || item.Spec.Rig==null || item.Pose.Length==0) return;
            Item=item; item.EditingBones=true; item.Apply();
            markers.Item=item;
            rows=item.Spec.Rig.Bones.Select((b,i)=>new BoneRow {Name=string.IsNullOrEmpty(b.Name) ? "#"+i : b.Name}).ToList();
            UpdateRows();
            list.ItemsSource=rows; list.SelectedIndex=0;
            UI<TextBlock>("RigCaption").Text=item.Spec.Name+"   ·   "+rows.Count+" 骨骼";
            UI<TextBlock>("ViewHint").Text="单击坐标点选骨骼  ·  左拖旋转  ·  滚轮缩放";
            UI<ColumnDefinition>("SidebarWidth").Width=new GridLength(400);
            UI<FrameworkElement>("ObjectPanel").Visibility=Visibility.Collapsed;
            UI<FrameworkElement>("BonePanel").Visibility=Visibility.Visible;
            UI<ScrollViewer>("SidebarScroll").ScrollToTop();
            status("编辑静止姿态。位置和旋转相对于父骨骼，修改即时预览。");
        }
        public void Close()
        {
            if(Item==null) return;
            Item.EditingBones=false; Item.Apply(); Item=null;
            markers.Item=null; markers.Selected=-1; markers.InvalidateVisual();
            list.ItemsSource=null; rows=null;
            UI<ColumnDefinition>("SidebarWidth").Width=new GridLength(310);
            UI<FrameworkElement>("BonePanel").Visibility=Visibility.Collapsed;
            UI<FrameworkElement>("ObjectPanel").Visibility=Visibility.Visible;
            UI<ScrollViewer>("SidebarScroll").ScrollToTop();
            UI<TextBlock>("ViewHint").Text=usualHint;
            UI<Button>("EditBones").Focus();
            status("已返回对象，骨骼修改保留在当前场景。");
        }
        public void Redraw() { if(Item!=null) markers.InvalidateVisual(); }
        public void Pick(Point point)
        {
            int i=markers.Nearest(point);
            if(i>=0) { list.SelectedIndex=i; list.ScrollIntoView(list.SelectedItem); }
        }
        void UpdateRows()
        {
            for(int i=0;i<rows.Count;i++) rows[i].Update(Item.Pose[i]);
            ShowCoordinates();
            Redraw();
        }
        void ShowCoordinates()
        {
            int i=list.SelectedIndex;
            if(i<0) return;
            var p=Item.Pose[i];
            UI<TextBox>("JointCoordinates").Text=string.Format(CultureInfo.InvariantCulture,
                "模型坐标   ({0:0.######},  {1:0.######},  {2:0.######})",p.OffsetX,p.OffsetY,p.OffsetZ);
        }
        void Fill()
        {
            int i=list.SelectedIndex;
            if(Item==null || i<0) return;
            var bone=Item.Spec.Rig.Bones[i];
            markers.Selected=i;
            UI<TextBlock>("JointName").Text="#"+i+"   "+rows[i].Name;
            UI<TextBlock>("JointParent").Text=bone.Parent<0 ? "根骨骼 · 相对于模型原点" : "父骨骼   #"+bone.Parent+"   "+rows[bone.Parent].Name;
            ShowCoordinates();
            initial=Item.LocalBone(i);
            var pos=initial.Position;
            var rot=initial.EulerDegrees();
            var values=new[]{pos.X,pos.Y,pos.Z,rot.X,rot.Y,rot.Z};
            filling=true;
            for(int k=0;k<fields.Length;k++)
            {
                displayed[k]=values[k];
                formatted[k]=values[k].ToString("0.######",CultureInfo.InvariantCulture);
                fields[k].Text=formatted[k];
            }
            filling=false;
            SetValidity(true);
            Redraw();
        }
        void SetValidity(bool ok)
        {
            valid=ok;
            UI<TextBlock>("BoneError").Visibility=ok ? Visibility.Collapsed : Visibility.Visible;
            UI<Button>("SaveRig").IsEnabled=ok && Item.BoneEdits.Count>0;
            UI<Button>("ResetBone").IsEnabled=!ok || Item.BoneEdits.ContainsKey(list.SelectedIndex);
        }
        void Change()
        {
            if(filling || Item==null || list.SelectedIndex<0) return;
            var values=new double[6];
            for(int k=0;k<fields.Length;k++)
            {
                if(fields[k].Text==formatted[k]) values[k]=displayed[k];
                else if(!double.TryParse(fields[k].Text,NumberStyles.Float,CultureInfo.InvariantCulture,out values[k]) ||
                    double.IsNaN(values[k]) || Math.Abs(values[k])>float.MaxValue)
                { SetValidity(false); return; }
            }
            var position=new Vector3D(values[0],values[1],values[2]);
            bool sameRotation=values[3]==displayed[3] && values[4]==displayed[4] && values[5]==displayed[5];
            var rotation=sameRotation ? initial.Rotation : BoneTransform.FromDegrees(new Vector3D(values[3],values[4],values[5]));
            var index=list.SelectedIndex;
            var source=Item.Spec.Rig.Bones[index].Local;
            if(position==source.Position && rotation==source.Rotation) Item.BoneEdits.Remove(index);
            else Item.BoneEdits[index]=new BoneTransform(position,rotation);
            Item.Apply(); UpdateRows(); SetValidity(true);
        }
        void Save()
        {
            if(!valid || Item==null) return;
            string source=Item.Spec.Rig.Source;
            var dlg=new SaveFileDialog {Title="另存包含骨架的 W3X 文件",Filter="W3X 文件 (*.w3x)|*.w3x",DefaultExt=".w3x",
                FileName=Path.GetFileNameWithoutExtension(source)+"_edited.w3x",InitialDirectory=Path.GetDirectoryName(source)};
            if(dlg.ShowDialog(window)!=true) return;
            try { RigExport.SaveCopy(Item,dlg.FileName); status("已保存骨架副本  ·  "+dlg.FileName); }
            catch(Exception e) {MessageBox.Show(window,e.Message,"保存失败",MessageBoxButton.OK,MessageBoxImage.Warning);}
        }
    }
}
