using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Markup;
using System.Windows.Media;
using System.Windows.Media.Media3D;
using Microsoft.Win32;

namespace W3xScene
{
    class Viewer
    {
        public readonly Window Window;
        readonly Viewport3D viewport;
        readonly Border surface;
        readonly ListBox objects;
        readonly ComboBox motions;
        readonly Slider timeline;
        readonly TextBox frameCountInput;
        readonly TextBox[] coords;
        readonly BoneEditor boneEditor;
        readonly MountPanel mountPanel;
        internal EditWorkspace Workspace;
        bool draggingHandle;
        Matrix3D shownPlacement;
        readonly PerspectiveCamera camera=new PerspectiveCamera();
        readonly EffectViewport effects;
        readonly Model3DGroup world=new Model3DGroup();
        readonly ModelVisual3D ground=new ModelVisual3D();
        readonly List<SceneItem> items=new List<SceneItem>();
        readonly List<Clip> importedClips=new List<Clip>();
        readonly List<Rig> importedRigs=new List<Rig>();
        readonly SemaphoreSlim imports=new SemaphoreSlim(1);
        readonly Stopwatch clock=Stopwatch.StartNew();
        readonly List<string> history=new List<string>();
        SceneItem current {get {return (SceneItem)objects.SelectedItem;}}
        bool syncing, moving, groundDrag;
        double previousTime, yaw=-1.22, pitch=.55, distance=95, gridStep, gridX, gridY;
        Point3D target=new Point3D(), dragStart;
        Vector3D initialOffset;
        MouseButton? button;
        Point previousPoint, downPoint;

        T UI<T>(string name) where T:class { return (T)Window.FindName(name); }
        internal void Status(string message) { UI<TextBlock>("Status").Text=message; }

        public Viewer()
        {
            using(var stream=Assembly.GetExecutingAssembly().GetManifestResourceStream("Window.xaml"))
                Window=(Window)XamlReader.Load(stream);
            viewport=UI<Viewport3D>("Viewport"); surface=UI<Border>("Surface");
            effects=new EffectViewport(UI<Grid>("SceneLayer"),camera,ground);
            objects=UI<ListBox>("Objects"); motions=UI<ComboBox>("Motions"); timeline=UI<Slider>("Timeline");
            frameCountInput=UI<TextBox>("FrameCountInput");
            coords=new[]{UI<TextBox>("PosX"),UI<TextBox>("PosY"),UI<TextBox>("PosZ")};
            boneEditor=new BoneEditor(Window,ProjectJoint,Status);
            mountPanel=new MountPanel(Window,ProjectJoint,Refresh,Status,item=>Fit(new[]{item}));
            UI<Button>("EditBones").Click+=(s,e)=>boneEditor.Open(current);
            UI<Button>("MotionEditorButton").Click+=(s,e)=>OpenWorkspace(true);
            UI<Button>("ConditionEditorButton").Click+=(s,e)=>OpenWorkspace(false);
            UI<Button>("OpenMount").Click+=(s,e)=>mountPanel.Open(current,items);
            UI<Button>("DetachMount").Click+=(s,e)=>{
                if(current==null) return;
                current.Detach(); Refresh(); Status("已解除附加，模型停留在当前世界位置和朝向。");
            };
            camera.FieldOfView=44;
            viewport.Camera=camera;
            world.Children.Add(new AmbientLight(Color.FromRgb(150,150,150)));
            world.Children.Add(new DirectionalLight(Color.FromRgb(166,167,161),new Vector3D(-.45,.62,-.94)));
            world.Children.Add(new DirectionalLight(Color.FromRgb(66,79,93),new Vector3D(.75,-.4,-.3)));
            viewport.Children.Add(ground);
            viewport.Children.Add(new ModelVisual3D{Content=world});
            UI<Button>("OpenButton").Click+=(s,e)=>Open();
            UI<Button>("FitButton").Click+=(s,e)=>Fit(items);
            UI<Button>("FocusButton").Click+=(s,e)=>Fit(new[]{current});
            UI<Button>("RemoveButton").Click+=(s,e)=>Remove();
            UI<Button>("PlayButton").Click+=(s,e)=>Play();
            UI<Button>("ApplyFrameCount").Click+=(s,e)=>ApplyFrameCount();
            frameCountInput.KeyDown+=(s,e)=>{
                if(e.Key==Key.Enter){ApplyFrameCount(); e.Handled=true;}
            };
            UI<Button>("ResetFrameCount").Click+=(s,e)=>{
                if(current==null || current.Motion==null) return;
                current.SetPlaybackFrames(current.Motion.Frames);
                ShowFrameCount(); UpdatePlayback();
                Status("已恢复原动画的 "+current.Motion.Frames+" 帧。");
            };
            UI<Button>("LogButton").Click+=(s,e)=>Log();
            UI<CheckBox>("GridToggle").Click+=(s,e)=>{gridStep=0; Camera();};
            UI<CheckBox>("BonesToggle").Click+=(s,e)=>{if(current!=null){current.ShowBones=UI<CheckBox>("BonesToggle").IsChecked==true; current.Apply();}};
            UI<CheckBox>("BoxesToggle").Click+=(s,e)=>{if(current!=null){current.ShowBoxes=UI<CheckBox>("BoxesToggle").IsChecked==true; current.Apply();}};
            objects.SelectionChanged+=(s,e)=>Refresh();
            motions.SelectionChanged+=(s,e)=>{
                if(syncing || current==null) return;
                current.SetMotion(motions.SelectedItem as Clip); ShowFrameCount(); UpdatePlayback();
            };
            timeline.ValueChanged+=(s,e)=>{
                if(syncing || current==null || current.Motion==null) return;
                current.Playing=false; current.Frame=Math.Round(timeline.Value); current.Apply(); UpdatePlayback();
            };
            for(int i=0;i<coords.Length;i++)
            {
                int axis=i;
                coords[i].LostKeyboardFocus+=(s,e)=>Commit(axis);
                coords[i].KeyDown+=(s,e)=>{ if(e.Key==Key.Enter){Commit(axis); surface.Focus(); e.Handled=true;} };
            }
            Window.DragOver+=(s,e)=>{e.Effects=e.Data.GetDataPresent(DataFormats.FileDrop) ? DragDropEffects.Copy : DragDropEffects.None; e.Handled=true;};
            Window.Drop+=async(s,e)=>{
                var paths=e.Data.GetData(DataFormats.FileDrop) as string[];
                if(paths!=null) { if(Workspace!=null) await Workspace.LoadFiles(paths); else await AddFiles(paths); }
            };
            surface.MouseDown+=Down; surface.MouseMove+=Move;
            surface.MouseUp+=(s,e)=>{
                if(button!=e.ChangedButton) return;
                var point=e.GetPosition(viewport);
                if(draggingHandle) { Workspace.EndDrag(); draggingHandle=false; }
                else
                if(button==MouseButton.Left && (point-downPoint).Length<3)
                {
                    if(Workspace!=null) Workspace.Pick(point);
                    else if(boneEditor.Item!=null) boneEditor.Pick(point);
                    else if(mountPanel.Item!=null) mountPanel.Pick(point);
                }
                button=null; surface.ReleaseMouseCapture();
            };
            surface.LostMouseCapture+=(s,e)=>{ if(draggingHandle) Workspace.EndDrag(); draggingHandle=false; button=null; };
            surface.MouseWheel+=(s,e)=>{distance=Math.Max(.01,Math.Min(1e9,distance*Math.Pow(.85,e.Delta/120.0))); Camera();};
            Window.PreviewKeyDown+=(s,e)=>{
                if(Workspace!=null) { Workspace.HandleKey(e); return; }
                if(mountPanel.Item!=null)
                {
                    if(e.Key==Key.Escape && !UI<ComboBox>("MountTargets").IsDropDownOpen){mountPanel.Close(); e.Handled=true;}
                    return;
                }
                if(boneEditor.Item!=null)
                {
                    if(e.Key==Key.Escape){boneEditor.Close(); e.Handled=true;}
                    return;
                }
                if(e.Key==Key.O && Keyboard.Modifiers==ModifierKeys.Control){Open(); e.Handled=true; return;}
                if(Keyboard.FocusedElement is TextBox || motions.IsDropDownOpen) return;
                if(e.Key==Key.F) Fit(current==null ? items.ToArray() : new[]{current});
                else if(e.Key==Key.Delete) Remove();
                else if(e.Key==Key.Space && !(Keyboard.FocusedElement is Button)){Play(); e.Handled=true;}
            };
            CompositionTarget.Rendering+=Tick;
            Window.Closed+=(s,e)=>CompositionTarget.Rendering-=Tick;
            Camera(); Refresh();
        }
        internal Viewer OpenWorkspace(bool motion)
        {
            var editor=new Viewer();
            editor.Window.Title=motion ? "W3X 动画编辑器" : "条件动画编辑器";
            editor.UI<TextBlock>("WindowHeading").Text=editor.Window.Title;
            foreach(var name in new[]{"MotionEditorButton","ConditionEditorButton","ObjectPanel","OpenButton","EmptyHint","LogButton"})
                editor.UI<FrameworkElement>(name).Visibility=Visibility.Collapsed;
            editor.UI<ColumnDefinition>("SidebarWidth").Width=new GridLength(motion ? 368 : 405);
            editor.Workspace=motion ? (EditWorkspace)new MotionWorkspace(editor,items) : new ConditionWorkspace(editor,items);
            var host=editor.UI<ContentControl>("EditorPanel"); host.Content=editor.Workspace.Panel; host.Visibility=Visibility.Visible;
            editor.Window.Closing+=(s,e)=>{ if(!editor.Workspace.CanClose()) e.Cancel=true; };
            editor.Window.Show();
            return editor;
        }
        internal void Preview(IEnumerable<SceneItem> preview,bool fit)
        {
            effects.InvalidateScene();
            foreach(var old in items) world.Children.Remove(old.Group);
            items.Clear();
            foreach(var item in preview){items.Add(item); world.Children.Add(item.Group);}
            if(fit) Fit(items);
        }
        async void Open()
        {
            var dlg=new OpenFileDialog{Filter="W3X 模型、骨架和动画 (*.w3x)|*.w3x",Multiselect=true,Title="添加到场景"};
            if(dlg.ShowDialog(Window)==true) await AddFiles(dlg.FileNames);
        }
        public async Task AddFiles(string[] paths)
        {
            await imports.WaitAsync();
            try
            {
                paths=paths.Where(s=>string.Equals(Path.GetExtension(s),".w3x",StringComparison.OrdinalIgnoreCase)).ToArray();
                if(paths.Length==0){Status("请选择 .w3x 文件"); return;}
                Status("正在读取 "+paths.Length+" 个文件及关联资源…");
                var batch=await Task.Run(()=>Import.Load(paths));
                foreach(var clip in batch.ExplicitClips)
                {
                    importedClips.RemoveAll(c=>Import.Names.Equals(c.Source,clip.Source) && Import.Names.Equals(c.Id,clip.Id));
                    importedClips.Add(clip);
                }
                foreach(var rig in batch.ExplicitRigs)
                {
                    importedRigs.RemoveAll(r=>Import.Names.Equals(r.Source,rig.Source) && Import.Names.Equals(r.Id,rig.Id));
                    importedRigs.Add(rig);
                }
                SceneItem last=null;
                foreach(var spec in batch.Models)
                {
                    if(spec.RigOnly && items.Any(i=>Import.Names.Equals(i.Spec.RigId,spec.RigId))) continue;
                    var item=new SceneItem(spec);
                    if(items.Count>0)
                    {
                        var bounds=Bounds(items); var local=item.RestBounds;
                        if(!bounds.IsEmpty && !local.IsEmpty)
                            item.SetPosition(bounds.X+bounds.SizeX-local.X+Math.Max(2,Math.Max(local.SizeX,local.SizeY)*.14),0,0);
                    }
                    items.Add(item); world.Children.Add(item.Group); objects.Items.Add(item); last=item;
                }
                foreach(var item in items)
                {
                    if(item.Spec.Rig==null)
                    {
                        item.Spec.Rig=importedRigs.LastOrDefault(r=>Import.Names.Equals(r.Id,item.Spec.RigId));
                        if(item.Spec.Rig!=null) item.Apply();
                    }
                    foreach(var clip in importedClips.Where(c=>Import.Names.Equals(c.RigId,item.Spec.RigId)))
                    {
                        var known=item.Clips.FirstOrDefault(c=>Import.Names.Equals(c.Id,clip.Id));
                        if(known==null) item.Clips.Add(clip);
                    }
                }
                var chosen=last ?? current;
                if(batch.ExplicitClips.Count>0)
                {
                    var clip=batch.ExplicitClips[0];
                    if(chosen==null || !Import.Names.Equals(chosen.Spec.RigId,clip.RigId))
                        chosen=items.FirstOrDefault(i=>Import.Names.Equals(i.Spec.RigId,clip.RigId)) ?? chosen;
                    if(chosen!=null && chosen.Spec.Rig!=null && Import.Names.Equals(chosen.Spec.RigId,clip.RigId))
                        chosen.SetMotion(chosen.Clips.First(c=>Import.Names.Equals(c.Id,clip.Id)));
                }
                objects.SelectedItem=chosen;
                if(last!=null)effects.InvalidateScene();
                if(chosen!=null) objects.ScrollIntoView(chosen);
                Refresh();
                if(last!=null) Fit(items);
                var warnings=batch.Warnings.Distinct().ToArray();
                var summary=string.Format("已读取 {0} / {1} 个文件  ·  场景中有 {2} 个对象",batch.ReadCount,paths.Length,items.Count);
                if(warnings.Length>0) summary+="  ·  "+warnings.Length+" 条提示，见导入详情";
                Status(summary);
                history.Add(summary+Environment.NewLine+string.Join(Environment.NewLine,paths.Concat(warnings)));
            }
            catch(Exception e) { Status("导入失败  ·  "+e.Message); history.Add(e.ToString()); }
            finally {imports.Release();}
        }
        void Log()
        {
            var box=new TextBox{Text=history.Count==0 ? "尚无导入记录" : string.Join("\r\n\r\n",history),IsReadOnly=true,
                VerticalScrollBarVisibility=ScrollBarVisibility.Auto,HorizontalScrollBarVisibility=ScrollBarVisibility.Auto,
                Padding=new Thickness(12),FontSize=13,BorderThickness=new Thickness(0)};
            var dlg=new Window{Owner=Window,Title="导入详情",Content=box,Width=720,Height=410,WindowStartupLocation=WindowStartupLocation.CenterOwner};
            box.ScrollToEnd(); dlg.ShowDialog();
        }
        void Remove()
        {
            var item=current; if(item==null) return;
            item.UnlinkFromScene();
            effects.InvalidateScene();
            int i=items.IndexOf(item); world.Children.Remove(item.Group); items.Remove(item); objects.Items.Remove(item);
            objects.SelectedIndex=Math.Min(i,items.Count-1); Refresh(); Status("已从场景移除 "+item.Spec.Name);
        }
        void Commit(int axis)
        {
            if(current==null || current.MountParent!=null || syncing) return;
            double n;
            if(!double.TryParse(coords[axis].Text,NumberStyles.Float,CultureInfo.InvariantCulture,out n) || double.IsInfinity(n) || double.IsNaN(n))
            { Status("坐标需要输入有效数字"); PositionText(); return; }
            var p=current.Offset;
            current.SetPosition(axis==0 ? n : p.OffsetX,axis==1 ? n : p.OffsetY,axis==2 ? n : p.OffsetZ);
        }
        void PositionText()
        {
            var item=current;
            double[] values=item==null ? new double[3] : new[]{item.Offset.OffsetX,item.Offset.OffsetY,item.Offset.OffsetZ};
            for(int i=0;i<3;i++) coords[i].Text=values[i].ToString("0.######",CultureInfo.InvariantCulture);
        }
        void Refresh()
        {
            syncing=true;
            var item=current; UI<FrameworkElement>("Inspector").IsEnabled=item!=null;
            if(mountPanel.Item!=null) mountPanel.Close();
            if(boneEditor.Item!=null && boneEditor.Item!=item) boneEditor.Close();
            bool mounted=item!=null && item.MountParent!=null;
            var attach=UI<Button>("OpenMount");
            attach.IsEnabled=item!=null && items.Any(item.CanMountOn);
            attach.Content=mounted ? "更改附加" : "附加";
            attach.ToolTip=attach.IsEnabled ? "将当前模型的原点吸附到另一个对象的骨骼" : "请先添加另一个带骨架的目标模型";
            UI<Button>("DetachMount").Visibility=mounted ? Visibility.Visible : Visibility.Collapsed;
            var info=UI<TextBlock>("MountInfo");
            info.Visibility=mounted ? Visibility.Visible : Visibility.Collapsed;
            if(mounted)
            {
                info.Text=MountPanel.InstanceName(item.MountParent,items)+" / #"+item.MountBone+" "+item.MountParent.Spec.Rig.Bones[item.MountBone].Name;
                shownPlacement=item.Group.Transform.Value;
            }
            foreach(var field in coords) field.IsReadOnly=mounted;
            UI<Button>("EditBones").IsEnabled=item!=null && item.Pose.Length>0;
            UI<TextBlock>("Count").Text=items.Count.ToString();
            UI<FrameworkElement>("EmptyHint").Visibility=items.Count==0 ? Visibility.Visible : Visibility.Collapsed;
            motions.Items.Clear(); motions.Items.Add("静止姿态");
            if(item!=null) foreach(var clip in item.Clips) motions.Items.Add(clip);
            motions.SelectedItem=item!=null && item.Motion!=null ? (object)item.Motion : motions.Items[0];
            motions.IsEnabled=item!=null && item.Spec.Rig!=null && item.Clips.Count>0;
            ShowFrameCount();
            var check=UI<CheckBox>("BonesToggle");
            check.IsChecked=item!=null && (item.ShowBones || item.Spec.RigOnly);
            check.IsEnabled=item!=null && item.Spec.Rig!=null && !item.Spec.RigOnly;
            var boxes=UI<CheckBox>("BoxesToggle");
            boxes.IsChecked=item!=null && item.ShowBoxes;
            boxes.IsEnabled=item!=null && item.Spec.Boxes.Count>0;
            UI<TextBlock>("Stats").Text=item==null ? "请选择场景对象" : string.Format("{0} 网格  ·  {1:N0} 三角面  ·  {2} 骨骼",item.Spec.Parts.Count,
                item.Spec.Parts.Sum(p=>(long)p.Mesh.Indices.Length/3),item.Pose.Length);
            PositionText(); syncing=false; UpdatePlayback();
        }
        void Play()
        {
            var item=current;
            if(item==null || item.Spec.Rig==null || item.Clips.Count==0) return;
            if(item.Motion==null) { motions.SelectedItem=item.Clips[0]; return; }
            item.Playing=!item.Playing; UpdatePlayback();
        }
        void ShowFrameCount()
        {
            var item=current;
            bool hasClip=item!=null && item.Motion!=null;
            UI<FrameworkElement>("LengthControls").IsEnabled=hasClip;
            frameCountInput.Text=hasClip ? item.PlaybackFrames.ToString(CultureInfo.InvariantCulture) : "";
            UI<Button>("ResetFrameCount").ToolTip=hasClip ? "原动画共 "+item.Motion.Frames+" 帧" : null;
        }
        void ApplyFrameCount()
        {
            if(current==null || current.Motion==null) return;
            int count;
            if(!int.TryParse(frameCountInput.Text.Trim(),NumberStyles.None,CultureInfo.InvariantCulture,out count) || count<1)
            {
                Status("播放帧数需要输入大于零的整数。");
                frameCountInput.Focus(); frameCountInput.SelectAll(); return;
            }
            current.SetPlaybackFrames(count);
            ShowFrameCount(); UpdatePlayback();
            Status("每轮播放帧数已设为 "+count+" 帧。");
        }
        void UpdatePlayback()
        {
            syncing=true;
            var item=current; var clip=item==null ? null : item.Motion;
            var play=UI<Button>("PlayButton");
            play.IsEnabled=item!=null && item.Spec.Rig!=null && item.Clips.Count>0;
            play.Content=item!=null && item.Playing ? "暂停" : "播放";
            int frames=clip==null ? 0 : item.PlaybackFrames;
            timeline.IsEnabled=clip!=null; timeline.Maximum=clip==null ? 1 : frames-1;
            timeline.Value=clip==null ? 0 : Math.Floor(item.Frame);
            UI<TextBlock>("FrameInfo").Text=clip==null ? "—" : string.Format("{0} / {1} 帧  ·  {2:0.##} 帧/秒",(int)item.Frame+1,frames,clip.Fps);
            syncing=false;
        }
        void Tick(object sender,EventArgs e)
        {
            var now=clock.Elapsed.TotalSeconds; var dt=now-previousTime;
            if(dt<1.0/60) return;
            previousTime=now;
            if(Workspace!=null){Workspace.Tick(dt);effects.Draw(items,now);return;}
            foreach(var item in items) item.Advance(dt);
            effects.Draw(items,now);
            mountPanel.UpdatePose();
            if(current!=null && current.MountParent!=null && current.Group.Transform.Value!=shownPlacement)
            {
                shownPlacement=current.Group.Transform.Value;
                PositionText(); boneEditor.Redraw();
            }
            if(current!=null && current.Playing && !current.EditingBones) UpdatePlayback();
        }
        static Rect3D Bounds(IEnumerable<SceneItem> collection)
        {
            var bounds=Rect3D.Empty;
            foreach(var item in collection.Where(i=>i!=null))
            {
                var b=item.Group.Bounds;
                if(!b.IsEmpty) bounds.Union(b);
            }
            return bounds;
        }
        void Fit(IEnumerable<SceneItem> selection)
        {
            var b=Bounds(selection); if(b.IsEmpty) return;
            target=new Point3D(b.X+b.SizeX*.5,b.Y+b.SizeY*.5,b.Z+b.SizeZ*.5);
            double aspect=Math.Max(1,viewport.ActualWidth)/Math.Max(1,viewport.ActualHeight);
            double tanX=Math.Tan(camera.FieldOfView*Math.PI/360), tanY=tanX/aspect;
            Vector3D right,up; Basis(out right,out up);
            distance=.2;
            foreach(var item in selection.Where(i=>i!=null))
            {
                var box=item.Group.Bounds; if(box.IsEmpty) continue;
                for(int i=0;i<8;i++)
                {
                    var corner=new Point3D(box.X+((i&1)==0 ? 0 : box.SizeX),
                        box.Y+((i&2)==0 ? 0 : box.SizeY),box.Z+((i&4)==0 ? 0 : box.SizeZ));
                    var delta=corner-target;
                    double extent=Math.Max(Math.Abs(Vector3D.DotProduct(delta,right))/tanX,
                        Math.Abs(Vector3D.DotProduct(delta,up))/tanY);
                    distance=Math.Max(distance,extent*1.14-Vector3D.DotProduct(delta,camera.LookDirection));
                }
            }
            Camera();
        }
        void Camera()
        {
            var eye=new Vector3D(Math.Cos(pitch)*Math.Cos(yaw),Math.Cos(pitch)*Math.Sin(yaw),Math.Sin(pitch));
            camera.Position=target+eye*distance; camera.LookDirection=-eye; camera.UpDirection=new Vector3D(0,0,1);
            camera.NearPlaneDistance=Math.Max(.0001,distance*.0008); camera.FarPlaneDistance=Math.Max(10000,distance*100);
            boneEditor.Redraw();
            if(Workspace!=null) Workspace.Redraw();
            mountPanel.Redraw();
            if(UI<CheckBox>("GridToggle").IsChecked!=true){ground.Content=null;return;}
            double step=Math.Pow(10,Math.Floor(Math.Log10(distance/6))); if(distance/step>33) step*=5; else if(distance/step>15) step*=2;
            double gx=Math.Floor(target.X/step), gy=Math.Floor(target.Y/step);
            if(step!=gridStep || gx!=gridX || gy!=gridY)
            {gridStep=step;gridX=gx;gridY=gy;ground.Content=Shapes.Grid(step,target);}
        }
        void Basis(out Vector3D right,out Vector3D up)
        {
            right=Vector3D.CrossProduct(camera.LookDirection,camera.UpDirection); right.Normalize();
            up=Vector3D.CrossProduct(right,camera.LookDirection); up.Normalize();
        }
        internal Point? ProjectJoint(Point3D worldPoint)
        {
            var delta=worldPoint-camera.Position;
            double depth=Vector3D.DotProduct(delta,camera.LookDirection);
            if(depth<=camera.NearPlaneDistance || depth>=camera.FarPlaneDistance) return null;
            Vector3D right,up; Basis(out right,out up);
            double scale=viewport.ActualWidth/(2*depth*Math.Tan(camera.FieldOfView*Math.PI/360));
            return new Point(viewport.ActualWidth*.5+scale*Vector3D.DotProduct(delta,right),
                viewport.ActualHeight*.5-scale*Vector3D.DotProduct(delta,up));
        }
        bool GroundPoint(Point px,double z,out Point3D p)
        {
            Vector3D right,up; Basis(out right,out up);
            double width=Math.Max(1,viewport.ActualWidth), unit=2*Math.Tan(camera.FieldOfView*Math.PI/360)/width;
            var ray=camera.LookDirection+right*((px.X-width*.5)*unit)+up*((viewport.ActualHeight*.5-px.Y)*unit);
            p=camera.Position;
            if(Math.Abs(ray.Z)<1e-6) return false;
            double d=(z-p.Z)/ray.Z;
            if(d<=0) return false;
            p+=ray*d; return true;
        }
        SceneItem Pick(Point point)
        {
            SceneItem picked=null;
            VisualTreeHelper.HitTest(viewport,null,result=>{
                var hit=result as RayMeshGeometry3DHitTestResult;
                if(hit==null) return HitTestResultBehavior.Continue;
                picked=items.FirstOrDefault(i=>i.Group.Children.Contains(hit.ModelHit));
                return picked==null ? HitTestResultBehavior.Continue : HitTestResultBehavior.Stop;
            },new PointHitTestParameters(point));
            return picked;
        }
        void Down(object sender,MouseButtonEventArgs e)
        {
            if(button!=null) return;
            surface.Focus(); button=e.ChangedButton; downPoint=previousPoint=e.GetPosition(viewport);
            moving=false;
            if(button==MouseButton.Left && Workspace!=null)
                draggingHandle=Workspace.BeginDrag(downPoint);
            else if(button==MouseButton.Left && boneEditor.Item==null && mountPanel.Item==null)
            {
                var picked=Pick(downPoint);
                if(picked!=null) objects.SelectedItem=picked;
                if(current!=null && Keyboard.Modifiers.HasFlag(ModifierKeys.Shift))
                {
                    if(current.MountParent!=null)
                    {
                        Status("先解除附加，即可独立移动这个模型。");
                        button=null; e.Handled=true; return;
                    }
                    initialOffset=new Vector3D(current.Offset.OffsetX,current.Offset.OffsetY,current.Offset.OffsetZ);
                    moving=true;
                    groundDrag=GroundPoint(downPoint,initialOffset.Z,out dragStart);
                }
            }
            surface.CaptureMouse(); e.Handled=true;
        }
        void Move(object sender,MouseEventArgs e)
        {
            if(button==null) return;
            Point p=e.GetPosition(viewport);
            var delta=p-previousPoint; previousPoint=p;
            if(draggingHandle) Workspace.Drag(p);
            else if(moving && current!=null)
            {
                Point3D here;
                if(groundDrag && GroundPoint(p,initialOffset.Z,out here))
                {var n=initialOffset+(here-dragStart); current.SetPosition(n.X,n.Y,n.Z); PositionText();}
                else if(!groundDrag)
                {
                    var right=new Vector3D(-Math.Sin(yaw),Math.Cos(yaw),0);
                    var forward=new Vector3D(-Math.Cos(yaw),-Math.Sin(yaw),0);
                    var unit=2*distance*Math.Tan(camera.FieldOfView*Math.PI/360)/Math.Max(1,viewport.ActualWidth);
                    var n=initialOffset+(right*(p.X-downPoint.X)-forward*(p.Y-downPoint.Y))*unit;
                    current.SetPosition(n.X,n.Y,n.Z); PositionText();
                }
            }
            else if(button==MouseButton.Left)
            { yaw-=delta.X*.009; pitch=Math.Max(-1.53,Math.Min(1.53,pitch+delta.Y*.008)); Camera(); }
            else
            {
                Vector3D right,up; Basis(out right,out up);
                double unit=2*distance*Math.Tan(camera.FieldOfView*Math.PI/360)/Math.Max(1,viewport.ActualWidth);
                target+=(-right*delta.X+up*delta.Y)*unit; Camera();
            }
        }
    }
    static class Program
    {
        [STAThread] public static void Main(string[] args)
        {
            var app=new Application(); var viewer=new Viewer();
            if(args.Length>0) viewer.Window.ContentRendered+=async delegate {await viewer.AddFiles(args);};
            app.Run(viewer.Window);
        }
    }
}
