using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media.Media3D;
using Microsoft.Win32;

namespace W3xScene
{
    class MotionWorkspace : EditWorkspace
    {
        readonly ComboBox clips,models,bones;
        readonly Slider timeline;
        readonly TextBox[] fields;
        readonly JointMarkers markers;
        readonly MotionGizmo gizmo;
        readonly List<ModelSpec> library=new List<ModelSpec>();
        readonly Dictionary<Clip,MotionDocument> documents=new Dictionary<Clip,MotionDocument>();
        public MotionDocument Document {get;private set;}
        public SceneItem Actor {get;private set;}
        bool sync,busy,skinRequest;
        Track[] before;
        BoneTransform baseValue;
        double[] exact;
        string[] shown;
        int start,end,center,editBone,axis=-1,shownFrame=-1;
        Point mouseStart;
        Vector tangent;

        public MotionWorkspace(Viewer view,List<SceneItem> scene) : base(view,scene,"MotionPanel.xaml","点选骨骼 · 拖动彩色轴或环编辑 · 左拖空白旋转视角")
        {
            clips=UI<ComboBox>("Clips"); models=UI<ComboBox>("Models"); bones=UI<ComboBox>("Bones"); timeline=UI<Slider>("Frames");
            var transport=UI<DockPanel>("Transport");
            ((StackPanel)Panel).Children.Remove(transport);
            var host=(ContentControl)view.Window.FindName("EditorTimeline");
            host.Content=transport; host.Visibility=Visibility.Visible;
            library.AddRange(Scene.Select(s=>s.Spec));
            markers=new JointMarkers(view.ProjectJoint); gizmo=new MotionGizmo(view.ProjectJoint);
            var overlay=(Grid)view.Window.FindName("JointOverlay"); overlay.Children.Add(markers); overlay.Children.Add(gizmo);
            fields=new[]{"PosX","PosY","PosZ","RotX","RotY","RotZ"}.Select(UI<TextBox>).ToArray();
            foreach(var box in fields)
            {
                box.GotKeyboardFocus+=(s,e)=>{if(!sync && Actor!=null) PauseAtFrame();};
                box.TextChanged+=(s,e)=>Numeric();
                box.LostKeyboardFocus+=(s,e)=>{Finish(); SyncFields();};
                box.KeyDown+=(s,e)=>{if(e.Key==Key.Enter){Finish();SyncFields();box.SelectAll();e.Handled=true;}};
            }
            OnClick("OpenMotion",()=>Browse("打开动画，可同时选择配套骨架","W3X|*.w3x"));
            OnClick("AddSkin",()=>{skinRequest=true; try{Browse("载入匹配的骨架或模型","W3X|*.w3x");}finally{skinRequest=false;}});
            OnClick("Play",Play); OnClick("Undo",Undo); OnClick("Save",Save);
            OnClick("StepBack",()=>Step(-1)); OnClick("StepNext",()=>Step(1));
            clips.SelectionChanged+=(s,e)=>{if(!sync) ModelsFor(null);};
            models.SelectionChanged+=(s,e)=>{if(!sync) BindActor();};
            bones.SelectionChanged+=(s,e)=>{if(!sync){Finish(); SyncFields();}};
            timeline.ValueChanged+=(s,e)=>{if(!sync && Actor!=null){Finish();Actor.Playing=false;Actor.Frame=Math.Round(timeline.Value)-1;Actor.Apply();SyncFields();}};
            UI<RadioButton>("Rotate").Checked+=(s,e)=>{gizmo.Rotating=true;Redraw();};
            UI<RadioButton>("Translate").Checked+=(s,e)=>{gizmo.Rotating=false;Redraw();};
            UI<CheckBox>("UseRange").Click+=(s,e)=>UI<Grid>("RangeInputs").IsEnabled=UI<CheckBox>("UseRange").IsChecked==true;
            View.Status("打开动画 W3X，可同时选择配套的骨架或蒙皮模型。");
        }
        public override async Task LoadFiles(string[] paths)
        {
            if(busy) return;
            bool justSkin=skinRequest;
            var files=paths.Where(p=>string.Equals(Path.GetExtension(p),".w3x",StringComparison.OrdinalIgnoreCase)).ToArray();
            if(files.Length==0) {View.Status("请选择 W3X 文件。");return;}
            busy=true; Panel.IsEnabled=false;
            try
            {
                var batch=await Task.Run(()=>Import.Load(files));
                var old=clips.SelectedItem as Clip;
                var next=justSkin ? old : batch.ExplicitClips.FirstOrDefault() ?? old;
                if(next==null) throw new InvalidDataException("文件中没有 W3DAnimation，请先打开动画。");
                var matching=batch.Models.Where(m=>m.Rig!=null && Import.Names.Equals(m.Rig.Id,next.RigId)).ToList();
                if((justSkin || batch.ExplicitClips.Count==0) && matching.Count==0)
                    throw new InvalidDataException("该骨架或模型与 "+next.RigId+" 不匹配。");
                library.AddRange(batch.Models);
                sync=true;
                if(!justSkin) foreach(var clip in batch.ExplicitClips)
                {
                    var existing=clips.Items.Cast<Clip>().FirstOrDefault(c=>Import.Names.Equals(c.Id,clip.Id) && Import.Names.Equals(c.Source,clip.Source));
                    if(existing==null) clips.Items.Add(clip); else if(next==clip) next=existing;
                }
                clips.SelectedItem=next; sync=false;
                ModelsFor(matching.FirstOrDefault(m=>m.Parts.Count>0));
                View.Status(Actor==null ? "已读取动作，请载入骨架 "+next.RigId+" 或同骨架模型。" :
                    next.Id+"   ·   "+next.Frames+" 帧   ·   "+next.Fps+" 帧/秒"+(batch.Warnings.Count>0 ? "   ·   "+batch.Warnings[0] : ""));
            }
            catch(Exception ex){Report(ex);}
            finally{sync=false;busy=false;Panel.IsEnabled=true;}
        }
        void ModelsFor(ModelSpec prefer)
        {
            var clip=clips.SelectedItem as Clip;
            var matches=library.Where(m=>clip!=null && m.Rig!=null && Import.Names.Equals(m.Rig.Id,clip.RigId))
                .OrderByDescending(m=>m.Parts.Count>0).GroupBy(m=>m.Source+"|"+m.Name,Import.Names).Select(g=>g.First()).ToArray();
            sync=true; models.Items.Clear();
            foreach(var spec in matches) models.Items.Add(new ComboBoxItem{Tag=spec,Content=spec.Name+(spec.RigOnly ? "  (骨架)" : "")});
            models.SelectedIndex=matches.Length==0 ? -1 : Math.Max(0,Array.FindIndex(matches,m=>prefer!=null && Import.Names.Equals(m.Source,prefer.Source) && Import.Names.Equals(m.Name,prefer.Name)));
            sync=false; BindActor();
        }
        void BindActor()
        {
            Finish();
            var previous=Actor;
            var choice=models.SelectedItem as ComboBoxItem; var clip=clips.SelectedItem as Clip;
            Actor=null;Document=null;PreviewItems.Clear();
            sync=true;
            try
            {
                int bone=bones.SelectedIndex; bones.Items.Clear();
                if(choice!=null && clip!=null)
                {
                    var spec=(ModelSpec)choice.Tag;
                    MotionDocument d;
                    if(!documents.TryGetValue(clip,out d)) documents[clip]=d=new MotionDocument(clip,spec.Rig);
                    // Matching IDs can be supplied by different files; interpreting an edit requires the same bind pose.
                    if(d.Rig.Bones.Length!=spec.Rig.Bones.Length || d.Rig.Bones.Where((b,i)=>
                        b.Parent!=spec.Rig.Bones[i].Parent || b.Name!=spec.Rig.Bones[i].Name || b.Local.Matrix!=spec.Rig.Bones[i].Local.Matrix).Any())
                    {View.Status("骨架 ID 相同，但关节层级或静止姿态不同。");}
                    else
                    {
                        Document=d;PreviewItems.AddRange(CopyPreview(spec));Actor=PreviewItems[0];
                        Actor.ShowBones=spec.RigOnly; Actor.SetMotion(d.Working);Actor.Playing=false;
                        if(previous!=null && previous.Motion==d.Working) Actor.Frame=previous.Frame;
                        Actor.Apply();
                        foreach(var b in spec.Rig.Bones) bones.Items.Add(b.Name);
                        bones.SelectedIndex=Math.Max(0,Math.Min(bone,bones.Items.Count-1));
                        timeline.Maximum=clip.Frames; UI<TextBox>("From").Text="1";UI<TextBox>("To").Text=clip.Frames.ToString();
                    }
                }
                UI<StackPanel>("Controls").IsEnabled=Actor!=null;
                UI<DockPanel>("Transport").IsEnabled=Actor!=null;
                if(Actor==null) UI<TextBlock>("FrameLabel").Text="等待匹配骨架";
                markers.Item=gizmo.Item=Actor;
                View.Preview(PreviewItems,true);
            }
            finally{sync=false;}
            SyncFields();
        }
        void Play(){if(Actor==null)return;Finish();Actor.Playing=!Actor.Playing;SyncFields();}
        void Step(int direction)
        {
            if(Actor==null)return;
            Finish();Actor.Playing=false;
            Actor.Frame=Math.Max(0,Math.Min(Document.Working.Frames-1,Math.Floor(Actor.Frame)+direction));
            Actor.Apply();SyncFields();
        }
        void PauseAtFrame()
        {
            Actor.Playing=false;Actor.Frame=Math.Floor(Actor.Frame);Actor.Apply();SyncFields();
        }
        void SyncFields()
        {
            if(sync || Actor==null || bones.SelectedIndex<0){Redraw();return;}
            int f=(int)Actor.Frame;
            var pose=Document.Local(Document.Working.Tracks,bones.SelectedIndex,f);
            var angles=pose.EulerDegrees();
            exact=new[]{pose.Position.X,pose.Position.Y,pose.Position.Z,angles.X,angles.Y,angles.Z};
            shown=exact.Select(n=>n.ToString("0.######",CultureInfo.InvariantCulture)).ToArray();
            sync=true;
            for(int i=0;i<6;i++) fields[i].Text=shown[i];
            timeline.Value=f+1;
            UI<TextBlock>("FrameLabel").Text=(f+1)+" / "+Document.Working.Frames+" 帧   ·   "+Document.Working.Fps+" fps";
            UI<Button>("Play").Content=Actor.Playing ? "暂停" : "播放";
            UI<Button>("Undo").IsEnabled=Document.CanUndo || before!=null && before!=Document.Working.Tracks;
            markers.Selected=gizmo.Bone=bones.SelectedIndex;shownFrame=f;
            sync=false;Redraw();
        }
        bool BeginChange()
        {
            center=(int)Actor.Frame;start=end=center;editBone=bones.SelectedIndex;
            if(UI<CheckBox>("UseRange").IsChecked==true)
            {
                int a,b;
                if(!int.TryParse(UI<TextBox>("From").Text,out a) || !int.TryParse(UI<TextBox>("To").Text,out b) ||
                    a<1 || a>center+1 || b<center+1 || b>Document.Working.Frames)
                {View.Status("影响区间必须包含当前帧，且位于动画帧范围内。");return false;}
                start=a-1;end=b-1;
            }
            before=Document.Working.Tracks;baseValue=Document.Local(before,editBone,center);return true;
        }
        void Numeric()
        {
            if(sync || Actor==null || bones.SelectedIndex<0 || shown==null)return;
            var v=new double[6];
            for(int i=0;i<6;i++)
            {
                if(fields[i].Text==shown[i]) v[i]=exact[i];
                else if(!double.TryParse(fields[i].Text,NumberStyles.Float,CultureInfo.InvariantCulture,out v[i]) || double.IsNaN(v[i]) || Math.Abs(v[i])>float.MaxValue)
                {View.Status("请输入有限数值。");return;}
            }
            Actor.Playing=false;
            if(before==null && !BeginChange())return;
            var q=v[3]==exact[3] && v[4]==exact[4] && v[5]==exact[5] ? baseValue.Rotation : BoneTransform.FromDegrees(new Vector3D(v[3],v[4],v[5]));
            Apply(new BoneTransform(new Vector3D(v[0],v[1],v[2]),q));
        }
        void Apply(BoneTransform pose)
        {
            Document.Preview(before,editBone,center,start,end,pose);
            UI<Button>("Undo").IsEnabled=Document.CanUndo || before!=Document.Working.Tracks;
            Actor.Apply();Redraw();
        }
        void Finish()
        {
            if(before==null)return;
            Document.Commit(before);before=null;
            UI<Button>("Undo").IsEnabled=Document.CanUndo;
        }
        void Undo(){if(Actor==null)return;Finish();Actor.Playing=false;Document.Undo();Actor.Apply();SyncFields();}
        public override bool BeginDrag(Point p)
        {
            if(busy || Actor==null || bones.SelectedIndex<0)return false;
            axis=gizmo.Hit(p,out tangent);
            if(axis<0)return false;
            Finish();PauseAtFrame();mouseStart=p;
            return BeginChange();
        }
        public override void Drag(Point p)
        {
            if(before==null)return;
            double amount=Vector.Multiply(p-mouseStart,tangent)/tangent.LengthSquared;
            var direction=MotionGizmo.Axis(axis);
            var pose=gizmo.Rotating ? new BoneTransform(baseValue.Position,new Quaternion(direction,amount*180/Math.PI)*baseValue.Rotation)
                : new BoneTransform(baseValue.Position+direction*amount,baseValue.Rotation);
            Apply(pose);SyncFields();
        }
        public override void EndDrag(){Finish();axis=-1;SyncFields();}
        public override void Pick(Point p){if(Actor!=null){int i=markers.Nearest(p);if(i>=0)bones.SelectedIndex=i;}}
        public override void Redraw(){markers.InvalidateVisual();gizmo.InvalidateVisual();}
        public override void Tick(double dt)
        {
            base.Tick(dt);
            if(Actor!=null && Actor.Playing)
            {if(shownFrame!=(int)Actor.Frame)SyncFields();else Redraw();}
        }
        public override void HandleKey(KeyEventArgs e)
        {
            if(e.Key==Key.Z && Keyboard.Modifiers==ModifierKeys.Control){Undo();e.Handled=true;}
            if(e.Key==Key.Space && !(Keyboard.FocusedElement is TextBox) && !bones.IsDropDownOpen && !clips.IsDropDownOpen && !models.IsDropDownOpen)
            {Play();e.Handled=true;}
        }
        void Save()
        {
            if(Document==null)return;Finish();
            var dlg=new SaveFileDialog{Title="另存动画（文件名即新资源 ID）",Filter="W3X|*.w3x",DefaultExt=".w3x",
                InitialDirectory=Path.GetDirectoryName(Document.Original.Source),FileName=Document.Original.Id+"_edited.w3x"};
            if(dlg.ShowDialog(View.Window)!=true)return;
            try
            {
                if(PreviewItems.Any(s=>Import.Names.Equals(s.Spec.Source,dlg.FileName)))throw new IOException("请选择新的动画文件名。");
                Document.Save(dlg.FileName);View.Status("已保存动画   "+dlg.FileName);
            }
            catch(Exception ex){Report(ex);}
        }
        public override bool CanClose(){Finish();return !documents.Values.Any(d=>d.Dirty) || Discard("有动画修改尚未另存。放弃未保存的修改并关闭？");}
    }
}
