using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media.Media3D;

namespace W3xScene
{
    class MountPanel
    {
        readonly Window window;
        readonly Action refreshInspector;
        readonly Action<string> status;
        readonly ComboBox targets;
        readonly ListBox bones;
        readonly JointMarkers markers;
        readonly string usualHint;
        List<BoneRow> rows;
        Matrix3D[] lastPose;
        Matrix3D lastPlacement;
        public SceneItem Item {get;private set;}

        SceneItem Target
        {
            get
            {
                var choice=targets.SelectedItem as ComboBoxItem;
                return choice==null ? null : (SceneItem)choice.Tag;
            }
        }
        T UI<T>(string name) where T:class { return (T)window.FindName(name); }

        public MountPanel(Window window,Func<Point3D,Point?> project,Action refreshInspector,Action<string> status,Action<SceneItem> focus)
        {
            this.window=window;
            this.refreshInspector=refreshInspector;
            this.status=status;
            targets=UI<ComboBox>("MountTargets");
            bones=UI<ListBox>("MountBones");
            markers=new JointMarkers(project);
            usualHint=UI<TextBlock>("ViewHint").Text;
            UI<Grid>("JointOverlay").Children.Add(markers);
            targets.SelectionChanged+=(s,e)=>SelectTarget();
            bones.SelectionChanged+=(s,e)=>ShowPoint();
            UI<Button>("FocusMountTarget").Click+=(s,e)=>{ if(Target!=null) focus(Target); };
            UI<Button>("ConfirmMount").Click+=(s,e)=>Confirm();
            UI<Button>("CancelMount").Click+=(s,e)=>Close();
        }

        public void Open(SceneItem item,List<SceneItem> scene)
        {
            if(item==null) return;
            Item=item;
            UI<TextBlock>("MountSource").Text="待附加对象   "+item.Spec.Name;
            foreach(var host in scene.Where(item.CanMountOn))
            {
                var option=new ComboBoxItem {Content=InstanceName(host,scene),Tag=host};
                targets.Items.Add(option);
                if(host==item.MountParent) targets.SelectedItem=option;
            }
            if(targets.Items.Count==0) { Close(); return; }
            if(targets.SelectedIndex<0) targets.SelectedIndex=0;
            UI<CheckBox>("MountRotation").IsChecked=item.FollowBoneRotation;
            UI<ColumnDefinition>("SidebarWidth").Width=new GridLength(400);
            UI<FrameworkElement>("ObjectPanel").Visibility=Visibility.Collapsed;
            UI<FrameworkElement>("MountPanel").Visibility=Visibility.Visible;
            UI<TextBlock>("ViewHint").Text="单击目标骨骼点  ·  左拖旋转  ·  滚轮缩放";
            UI<ScrollViewer>("SidebarScroll").ScrollToTop();
            status("在列表或视图中选择骨骼，确认后将模型原点对齐到它的世界坐标。");
        }
        public static string InstanceName(SceneItem item,List<SceneItem> scene)
        {
            return "#"+(scene.IndexOf(item)+1)+"  "+item.Spec.Name;
        }
        void SelectTarget()
        {
            var host=Target;
            markers.Item=host; markers.Selected=-1;
            lastPose=null;
            rows=host==null ? null : host.Spec.Rig.Bones.Select((b,i)=>
                new BoneRow {Name=string.IsNullOrEmpty(b.Name) ? "#"+i : b.Name}).ToList();
            bones.ItemsSource=rows;
            if(host!=null && Item!=null && host==Item.MountParent)
            {
                bones.SelectedIndex=Item.MountBone;
                bones.ScrollIntoView(bones.SelectedItem);
            }
            UpdatePose(); ShowPoint(); Redraw();
        }
        void ShowPoint()
        {
            int index=bones.SelectedIndex;
            markers.Selected=index;
            bool selected=Target!=null && index>=0;
            UI<Button>("ConfirmMount").IsEnabled=selected;
            var detail=UI<TextBlock>("MountPoint");
            if(selected)
            {
                var m=Target.BoneWorldMatrix(index);
                detail.Text=string.Format(CultureInfo.InvariantCulture,"#{0}   {1}\n世界坐标   ({2:0.######},  {3:0.######},  {4:0.######})",
                    index,rows[index].Name,m.OffsetX,m.OffsetY,m.OffsetZ);
            }
            else detail.Text="请选择列表中的骨骼或视图中的坐标点。";
            Redraw();
        }
        public void UpdatePose()
        {
            if(Item==null) return;
            var host=Target;
            if(host==null || rows==null) return;
            var placement=host.Group.Transform.Value;
            if(lastPose==host.Pose && lastPlacement==placement) return;
            for(int i=0;i<rows.Count;i++) rows[i].Update(host.BoneWorldMatrix(i));
            lastPose=host.Pose; lastPlacement=placement;
            ShowPoint();
        }
        public void Redraw() { if(Item!=null) markers.InvalidateVisual(); }
        public void Pick(Point p)
        {
            if(Target==null) return;
            int i=markers.Nearest(p);
            if(i>=0) { bones.SelectedIndex=i; bones.ScrollIntoView(bones.SelectedItem); }
        }
        void Confirm()
        {
            var host=Target;
            int i=bones.SelectedIndex;
            if(Item==null || !Item.CanMountOn(host) || i<0 || i>=host.Pose.Length) return;
            Item.MountOn(host,i,UI<CheckBox>("MountRotation").IsChecked==true);
            string message=Item.Spec.Name+" 已附加到 "+host.Spec.Name+" / "+rows[i].Name;
            Close(); refreshInspector(); status(message);
        }
        public void Close()
        {
            if(Item==null) return;
            Item=null; markers.Item=null; markers.Selected=-1;
            targets.Items.Clear(); bones.ItemsSource=null; rows=null; lastPose=null;
            markers.InvalidateVisual();
            UI<ColumnDefinition>("SidebarWidth").Width=new GridLength(310);
            UI<FrameworkElement>("MountPanel").Visibility=Visibility.Collapsed;
            UI<FrameworkElement>("ObjectPanel").Visibility=Visibility.Visible;
            UI<TextBlock>("ViewHint").Text=usualHint;
            UI<ScrollViewer>("SidebarScroll").ScrollToTop();
            UI<Button>("OpenMount").Focus();
            status("已返回场景对象。");
        }
    }
}
