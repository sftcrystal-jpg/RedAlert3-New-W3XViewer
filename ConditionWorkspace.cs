using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Xml.Linq;
using Forms=System.Windows.Forms;

namespace W3xScene
{
    class ConditionWorkspace : EditWorkspace
    {
        readonly List<ModelSpec> library=new List<ModelSpec>();
        readonly List<Clip> clips=new List<Clip>();
        readonly HashSet<string> emptyAssets=new HashSet<string>(Import.Names);
        readonly List<DrawPreview> draws=new List<DrawPreview>();
        readonly ListBox modules,states;
        readonly ComboBox animations;
        readonly TextBox xml,flags;
        HashSet<string> active=new HashSet<string>();
        DrawPreview current;
        bool sync,busy;

        public ConditionWorkspace(Viewer view,List<SceneItem> scene) : base(view,scene,"ConditionPanel.xaml","左拖旋转 · 右拖平移 · 滚轮缩放")
        {
            library.AddRange(Scene.Select(s=>s.Spec));clips.AddRange(Scene.SelectMany(s=>s.Clips));
            modules=UI<ListBox>("Modules");states=UI<ListBox>("States");animations=UI<ComboBox>("Animations");
            xml=UI<TextBox>("Xml");flags=UI<TextBox>("Flags");
            UI<ComboBox>("FlagsCatalog").ItemsSource=DrawRules.Flags.OrderBy(x=>x).ToArray();
            UI<ComboBox>("Mode").ItemsSource=DrawRules.Modes.OrderBy(x=>x).ToArray();
            OnClick("OpenConfig",()=>Browse("打开 GameObject.xml 或 ScriptedModelDraw","RA3 XML|*.xml"));
            OnClick("Assets",()=>Browse("加载用于条件预览的模型与动画","W3X|*.w3x"));
            modules.SelectionChanged+=(s,e)=>{if(!sync && modules.SelectedIndex>=0) Inspect(draws[modules.SelectedIndex]);};
            states.SelectionChanged+=(s,e)=>{
                if(sync || Selected(states)==null)return;
                var state=Selected(states);PreviewAction(state,DrawRules.Children(state,"Animation").FirstOrDefault());
            };
            animations.SelectionChanged+=(s,e)=>{
                var action=Selected(animations);
                if(!sync && action!=null)PreviewAction(action.Parent,action);
            };
            OnClick("PreviewAction",()=>{if(current!=null && current.Action!=null)PreviewAction(current.State,current.Action);});
            OnClick("NewState",()=>CreateEntry(true));
            OnClick("NewAction",()=>CreateEntry(false));
            OnClick("StateUp",()=>MoveState(true));
            OnClick("StateDown",()=>MoveState(false));
            xml.TextChanged+=(s,e)=>{if(current!=null)current.Draft=xml.Text;};
            OnClick("ApplyXml",ApplyXml); OnClick("Match",Match);
            flags.KeyDown+=(s,e)=>{if(e.Key==Key.Enter){Match();e.Handled=true;}};
            OnClick("AppendFlag",()=>{
                var flag=UI<ComboBox>("FlagsCatalog").Text.Trim();
                if(!DrawRules.Flags.Contains(flag)){View.Status("请选择有效的模型条件。");return;}
                flags.Text=string.Join(" ",DrawRules.Words(flags.Text).Concat(new[]{flag}).Distinct());Match();
            });
            OnClick("Play",()=>{if(current!=null){current.Toggle();Transport();}});
            UI<Slider>("Frames").ValueChanged+=(s,e)=>{if(!sync && current!=null)current.Seek(UI<Slider>("Frames").Value);};
            OnClick("ApplyDuration",ApplyDuration);OnClick("ApplyAction",ApplyAction);OnClick("Generate",Generate);
            View.Status("打开单位 XML，或粘贴绘制模块后点击「应用 XML 并预览」。");
        }
        static XElement Selected(System.Windows.Controls.Primitives.Selector box)
        { var item=box.SelectedItem as ListBoxItem;return item==null ? null : item.Tag as XElement; }
        public override async Task LoadFiles(string[] paths)
        {
            if(busy)return;
            busy=true;Panel.IsEnabled=false;
            try
            {
                var loaded=new List<DrawXmlFile>();
                var arts=new HashSet<string>(paths.Where(p=>Path.GetExtension(p).Equals(".w3x",StringComparison.OrdinalIgnoreCase)),Import.Names);
                foreach(var path in paths.Where(p=>Path.GetExtension(p).Equals(".xml",StringComparison.OrdinalIgnoreCase)).Select(Path.GetFullPath).Distinct(Import.Names))
                {
                    var file=new DrawXmlFile(path);
                    var found=file.Modules;
                    if(found.Length==0)throw new InvalidDataException("该 XML 中没有可读取的 Draw。");
                    loaded.Add(file);
                    var dir=Path.GetDirectoryName(Path.GetFullPath(path));
                    var names=found.SelectMany(d=>d.Descendants()).Where(e=>e.Name.LocalName=="Model" || e.Name.LocalName=="Animation")
                        .Select(e=>DrawRules.A(e,e.Name.LocalName=="Model" ? "Name" : "AnimationName"))
                        .Concat(found.Where(d=>d.Name.LocalName=="PropDraw").Select(d=>DrawRules.A(d,"Model"))).Where(n=>n.Length>0).Distinct(Import.Names);
                    foreach(string name in names)
                    {
                        string neighbor=Path.Combine(dir,"Art",name+".w3x");
                        string art=File.Exists(neighbor) ? neighbor : Import.Locate(path,name+".w3x");
                        if(art!=null)arts.Add(art);
                    }
                }
                if(loaded.Count>0 && !CanClose())return;
                var imported=await Task.Run(()=>Import.Load(arts.ToArray()));
                RegisterAssets(imported);
                if(loaded.Count>0)
                {
                    current=null;sync=true;draws.Clear();modules.Items.Clear();
                    foreach(var file in loaded)foreach(var module in file.Modules)AddModule(module,file);
                    modules.SelectedIndex=0;sync=false;
                    var input=DrawRules.Words(flags.Text);
                    if(input.All(DrawRules.Flags.Contains))active=new HashSet<string>(input);
                    else {flags.Clear();active.Clear();}
                    foreach(var draw in draws)MatchDraw(draw,false);
                    RebuildScene(true);Inspect(draws[0]);
                }
                else if(current!=null)
                {
                    bool changed=false;
                    foreach(var draw in draws)
                    {
                        bool replaced=BindModel(draw);
                        if(replaced || draw.Actor!=null && draw.Actor.Motion!=draw.FindClip(draw.PlayingAction,clips))
                            draw.Start(draw.Action,clips);
                        changed|=replaced;
                    }
                    if(changed)RebuildScene(PreviewItems.Count==0);
                    Inspector();
                }
                else View.Status("资源已加入预览库，可以粘贴绘制模块 XML。");
            }
            catch(Exception ex){Report(ex);}
            finally{busy=false;sync=false;Panel.IsEnabled=true;}
        }
        void RegisterAssets(Import imported)
        {
            emptyAssets.UnionWith(imported.EmptyDeclarations.Select(Path.GetFileNameWithoutExtension));
            var modelPaths=new HashSet<string>(imported.Models.Select(s=>s.Source),Import.Names);
            library.RemoveAll(s=>modelPaths.Contains(s.Source));
            library.InsertRange(0,imported.Models);
            var foundClips=imported.ExplicitClips.Concat(imported.Models.SelectMany(s=>s.Clips)).Distinct().ToList();
            var clipPaths=new HashSet<string>(foundClips.Select(c=>c.Source),Import.Names);
            clips.RemoveAll(c=>clipPaths.Contains(c.Source));clips.InsertRange(0,foundClips);
        }
        void AddModule(XElement module,DrawXmlFile sourceFile)
        {
            var draw=new DrawPreview(module,sourceFile);draws.Add(draw);
            var row=new DockPanel();
            var visible=new CheckBox{IsChecked=true,Focusable=false,ToolTip="显示此 Draw",Margin=new Thickness(0,0,8,0)};
            string label=ModuleLabel(draw);
            System.Windows.Automation.AutomationProperties.SetName(visible,"显示 "+label);
            row.Children.Add(visible);
            row.Children.Add(new TextBlock{Text=label,ToolTip=ModuleInfo(draw),TextTrimming=TextTrimming.CharacterEllipsis});
            modules.Items.Add(row);
            RoutedEventHandler toggle=(s,e)=>{draw.Visible=visible.IsChecked==true;Compose(false);if(current==draw)Status();};
            visible.Checked+=toggle;visible.Unchecked+=toggle;
        }
        string ModuleLabel(DrawPreview draw){return "#"+(draws.IndexOf(draw)+1)+"   "+DrawRules.A(draw.Module,"id",draw.Module.Name.LocalName);}
        static string ModuleInfo(DrawPreview draw)
        {
            var owner=draw.Group.AncestorsAndSelf().FirstOrDefault(e=>e.Name.LocalName=="GameObject");
            return draw.Module.Name.LocalName+(owner==null ? "" : "   ·   "+DrawRules.A(owner,"id"));
        }
        void Inspect(DrawPreview draw)
        {
            current=draw;xml.Text=draw.Draft;Inspector();
        }
        void SetText(){current.Applied=current.Module.ToString();current.Draft=current.Applied;xml.Text=current.Applied;}
        void MoveState(bool up)
        {
            var state=Selected(states);
            if(current==null || state==null)return;
            if(current.Draft!=current.Applied){View.Status("当前 XML 有改动，请先点「应用 XML 并预览」，再调整顺序。");return;}
            var neighbor=up ? state.ElementsBeforeSelf(state.Name).LastOrDefault() : state.ElementsAfterSelf(state.Name).FirstOrDefault();
            if(neighbor==null)return;
            // Swap the two element slots, keeping their actions, intervening nodes and whitespace intact.
            var slot=new XComment("");
            state.ReplaceWith(slot);
            neighbor.ReplaceWith(state);
            slot.ReplaceWith(neighbor);
            listedModule=null;SetText();
            UI<ComboBox>("Scope").SelectedIndex=2;
            if(!current.Manual && DrawRules.FirstAnimationState(current.Module,active)!=current.State)
            {
                if(MatchDraw(current,false))RebuildScene(PreviewItems.Count==0);
            }
            Inspector();
        }
        void CreateEntry(bool createState)
        {
            if(current==null || !DrawRules.SupportsAnimationStates(current.Module) || (!createState && current.State==null))return;
            if(current.Draft!=current.Applied){View.Status("当前 XML 有改动，请先点「应用 XML 并预览」，再新增条目。");return;}
            try
            {
                var rigs=new HashSet<string>(DrawRules.Children(current.Module,"ModelConditionState").Select(ModelFor)
                    .Where(m=>m!=null && m.RigId!=null).Select(m=>m.RigId),Import.Names);
                if(current.Actor!=null && current.Actor.Spec.RigId!=null)rigs.Add(current.Actor.Spec.RigId);
                var names=clips.Where(c=>rigs.Count==0 || rigs.Contains(c.RigId)).Select(c=>c.Id).Concat(
                    DrawRules.Children(current.Module,"AnimationState").SelectMany(s=>DrawRules.Children(s,"Animation"))
                    .Select(a=>DrawRules.A(a,"AnimationName"))).Where(n=>!string.IsNullOrWhiteSpace(n));
                var dialog=new AnimationEntryDialog(View.Window,current.Module,createState ? null : current.State,names,active);
                if(!dialog.Show())return;
                foreach(var batch in dialog.Imports)RegisterAssets(batch);
                var state=createState ? dialog.State : current.State;
                DrawRules.InsertAnimationEntry(createState ? current.Module : state,createState ? state : dialog.Action);
                listedModule=null;SetText();PreviewAction(state,dialog.Action);
                UI<ComboBox>("Scope").SelectedIndex=createState ? 0 : 1;
                View.Status((createState ? "已新增动画状态   "+DrawRules.Label(state) : "已添加动作   "+DrawRules.A(dialog.Action,"AnimationName"))+
                    (current.Visible ? "" : "   ·   此 Draw 已隐藏"));
            }
            catch(Exception ex){Report(ex);}
        }
        void ApplyXml()
        {
            try
            {
                var m=XElement.Parse(xml.Text,LoadOptions.PreserveWhitespace);DrawRules.Validate(m);
                bool first=current==null;
                if(first)
                {
                    sync=true;AddModule(m,null);modules.SelectedIndex=0;sync=false;current=draws[0];
                }
                var prior=current.State;var priorAction=current.Action;bool manual=current.Manual;
                int stateIndex=DrawRules.Children(current.Module,"AnimationState").ToList().IndexOf(prior);
                int ordinal=priorAction==null ? -1 : DrawRules.Children(prior,"Animation").ToList().IndexOf(priorAction);
                current.Module=m;SetText();
                var row=(DockPanel)modules.SelectedItem;var name=(TextBlock)row.Children[1];
                name.Text=ModuleLabel(current);name.ToolTip=ModuleInfo(current);
                System.Windows.Automation.AutomationProperties.SetName((CheckBox)row.Children[0],"显示 "+name.Text);
                var options=DrawRules.Children(m,"AnimationState").ToList();
                Func<XElement,bool> same=s=>new[]{"StateName","Name","ConditionsYes","ParseCondStateType"}.All(k=>DrawRules.A(s,k)==DrawRules.A(prior,k));
                var state=prior==null ? null : stateIndex>=0 && stateIndex<options.Count && same(options[stateIndex]) ? options[stateIndex] : options.FirstOrDefault(same);
                if(manual && state!=null)
                {
                    current.State=state;var actions=DrawRules.Children(state,"Animation").ToList();
                    current.Action=ordinal>=0 && ordinal<actions.Count ? actions[ordinal] : actions.FirstOrDefault();
                    BindModel(current);current.Start(current.Action,clips);
                }
                else MatchDraw(current,false);
                RebuildScene(first || PreviewItems.Count==0);Inspector();
            }
            catch(Exception ex){Report(ex);}
        }
        void Match()
        {
            if(current==null){View.Status("请先应用绘制模块 XML。");return;}
            var input=new HashSet<string>(DrawRules.Words(flags.Text));var bad=input.FirstOrDefault(x=>!DrawRules.Flags.Contains(x));
            if(bad!=null){View.Status("未知模型条件 "+bad);return;}
            active=input;
            bool changed=false;
            foreach(var draw in draws)changed|=MatchDraw(draw,true);
            if(changed)RebuildScene(PreviewItems.Count==0);
            Inspector();
        }
        bool MatchDraw(DrawPreview draw,bool transition)
        {
            string previous=DrawRules.A(draw.State,"StateName");
            draw.Manual=false;draw.State=DrawRules.FirstAnimationState(draw.Module,active);
            draw.Action=draw.State==null ? null : DrawRules.Children(draw.State,"Animation").FirstOrDefault();
            bool changed=BindModel(draw);
            var trans=transition && draw.State!=null ? DrawRules.Transition(draw.State,previous) : null;
            var via=trans==null ? null : DrawRules.Children(draw.Module,"AnimationState").Where(s=>DrawRules.A(s,"ParseCondStateType")=="PARSE_TRANSITION" && DrawRules.A(s,"StateName")==trans)
                         .SelectMany(s=>DrawRules.Children(s,"Animation")).FirstOrDefault();
            if(via!=null && draw.FindClip(via,clips)!=null)draw.Start(via,clips,true);
            else draw.Start(draw.Action,clips);
            return changed;
        }
        ModelSpec ModelFor(XElement rule)
        {
            string name=ModelName(rule);
            return name.Length==0 ? null : library.FirstOrDefault(m=>Import.Names.Equals(m.Name,name));
        }
        static string ModelName(XElement rule)
        {return rule==null ? "" : DrawRules.A(DrawRules.Children(rule,"Model").FirstOrDefault(),"Name");}
        bool BindModel(DrawPreview draw)
        {
            var rule=DrawRules.ModelMatches(draw.Module,active).FirstOrDefault();
            var spec=ModelFor(rule);
            string prop=DrawRules.A(draw.Module,"Model");
            if(draw.Module.Name.LocalName=="PropDraw")spec=library.FirstOrDefault(m=>Import.Names.Equals(m.Name,prop));
            if(draw.Manual && draw.Action!=null)
            {
                var rigs=new HashSet<string>(clips.Where(c=>Import.Names.Equals(c.Id,DrawRules.A(draw.Action,"AnimationName"))).Select(c=>c.RigId),Import.Names);
                if(rigs.Count>0 && (spec==null || !rigs.Contains(spec.RigId)))
                {
                    var stateFlags=new HashSet<string>(DrawRules.Words(DrawRules.A(draw.State,"ConditionsYes")));
                    var candidates=DrawRules.ModelMatches(draw.Module,stateFlags).Concat(DrawRules.Children(draw.Module,"ModelConditionState"));
                    var compatible=candidates.FirstOrDefault(r=>{var m=ModelFor(r);return m!=null && rigs.Contains(m.RigId);});
                    if(compatible!=null){rule=compatible;spec=ModelFor(rule);}
                    else spec=library.FirstOrDefault(m=>m.Rig!=null && rigs.Contains(m.RigId)) ?? spec;
                }
            }
            draw.ModelRule=rule;draw.ModelName=spec==null ? (prop.Length>0 ? prop : ModelName(rule)) : spec.Name;
            if((draw.Actor==null ? null : draw.Actor.Spec)==spec)return false;
            if(draw.Actor!=null)draw.Actor.UnlinkFromScene();
            draw.Items=spec==null ? new List<SceneItem>() : CopyPreview(spec);
            if(draw.Actor!=null && spec.RigOnly){draw.Actor.ShowBones=true;draw.Actor.Apply();}
            return true;
        }
        void RebuildScene(bool fit)
        {
            // A hidden parent still supplies its animated bone transform to visible sibling Draws.
            foreach(var draw in draws)if(draw.Actor!=null)draw.Actor.ResetPlacement();
            foreach(var draw in draws)
            {
                draw.MountNote=null;
                string bone=DrawRules.A(draw.Module,"AttachToBoneInAnotherModule");
                if(draw.Actor==null || bone.Length==0)continue;
                foreach(var other in draws)
                {
                    var parent=other.Actor;
                    if(other.Group!=draw.Group || parent==null || parent.Spec.Rig==null || !draw.Actor.CanMountOn(parent))continue;
                    int index=Array.FindIndex(parent.Spec.Rig.Bones,b=>Import.Names.Equals(b.Name,bone));
                    if(index<0)continue;
                    draw.Actor.MountOn(parent,index,true);break;
                }
                if(draw.Actor.MountParent==null)draw.MountNote="缺少附加骨骼 "+bone;
            }
            Compose(fit);
        }
        void Compose(bool fit)
        {
            PreviewItems.Clear();
            foreach(var draw in draws)if(draw.Visible)PreviewItems.AddRange(draw.Items);
            View.Preview(PreviewItems,fit);
        }
        void PreviewAction(XElement state,XElement action)
        {
            current.State=state;current.Action=action;current.Manual=true;
            bool changed=BindModel(current);
            current.Start(action,clips);
            if(changed)RebuildScene(PreviewItems.Count==0);
            Inspector();
        }
        XElement listedModule;
        void Inspector()
        {
            var draw=current;if(draw==null)return;
            sync=true;
            try
            {
                if(listedModule!=draw.Module)
                {
                    states.Items.Clear();animations.Items.Clear();listedModule=draw.Module;
                    foreach(var state in DrawRules.Children(draw.Module,"AnimationState"))
                    {
                        var label=DrawRules.Label(state);
                        states.Items.Add(new ListBoxItem{Tag=state,Content=(states.Items.Count+1)+".  "+label,ToolTip=label});
                        foreach(var action in DrawRules.Children(state,"Animation"))
                            animations.Items.Add(new ComboBoxItem{Tag=action,Content=DrawRules.A(action,"AnimationName"),ToolTip=label});
                    }
                }
                states.SelectedItem=states.Items.Cast<ListBoxItem>().FirstOrDefault(i=>i.Tag==draw.State);
                if(states.SelectedItem!=null)states.ScrollIntoView(states.SelectedItem);
                UI<Button>("StateUp").IsEnabled=states.SelectedIndex>0;
                UI<Button>("StateDown").IsEnabled=states.SelectedIndex>=0 && states.SelectedIndex+1<states.Items.Count;
                var winner=DrawRules.FirstAnimationState(draw.Module,active);
                UI<TextBlock>("MatchedState").Text=states.Items.Count==0 ? "" : winner==null ? "当前条件未命中动画状态" : "当前条件命中　"+states.Items.Cast<ListBoxItem>().First(i=>i.Tag==winner).Content;
                animations.SelectedItem=animations.Items.Cast<ComboBoxItem>().FirstOrDefault(i=>i.Tag==draw.Action);
                if(animations.SelectedItem!=null)((ComboBoxItem)animations.SelectedItem).Content=DrawRules.A(draw.Action,"AnimationName");
                UI<Button>("PreviewAction").IsEnabled=draw.Action!=null;
                bool supportsStates=DrawRules.SupportsAnimationStates(draw.Module);
                var newState=UI<Button>("NewState");var newAction=UI<Button>("NewAction");
                newState.IsEnabled=supportsStates;newAction.IsEnabled=supportsStates && draw.State!=null;
                newState.ToolTip=supportsStates ? "设置触发条件和首个动作" : draw.Module.Name.LocalName+" 不支持动画状态";
                newAction.ToolTip=newAction.IsEnabled ? "添加到 "+DrawRules.Label(draw.State) : "先选择或新增一个动画状态";
                UI<TextBlock>("StatesLabel").Text="动画状态（"+states.Items.Count+"）· 从上往下匹配";
                UI<TextBlock>("AnimationsLabel").Text="此 Draw 的全部动作（"+animations.Items.Count+"）";
                var model=draw.Actor==null ? null : draw.Actor.Spec;
                UI<ComboBox>("ClipName").ItemsSource=clips.Where(c=>model==null || Import.Names.Equals(c.RigId,model.RigId)).Select(c=>c.Id).Distinct(Import.Names).OrderBy(id=>id,Import.Names).ToArray();
                UI<ComboBox>("ClipName").Text=DrawRules.A(draw.Action,"AnimationName");
                UI<ComboBox>("Mode").SelectedItem=DrawRules.A(draw.Action,"AnimationMode","LOOP");
                UI<Button>("ApplyAction").IsEnabled=draw.Action!=null;
                UI<TextBlock>("ModelMatch").Text=ModuleInfo(draw)+(draw.ModelRule==null ? "" : "\n模型规则   "+DrawRules.Label(draw.ModelRule))+"\n预览模型   "+
                    (string.IsNullOrEmpty(draw.ModelName) ? "空" : draw.ModelName+(model==null ? "（请补充 W3X）" : ""));
                var notes=new List<string>();
                if(model==null && emptyAssets.Contains(draw.ModelName))notes.Add(draw.ModelName+" 的源文件没有模型数据，请用「补充 W3X」载入完整模型");
                if(draw.Module.Name.LocalName=="LaserDraw" || draw.Module.Name.LocalName=="StreamDraw" || draw.Module.Name.LocalName=="TracerModelDraw")
                    notes.Add("此 Draw 由游戏引擎绘制特效，没有 W3X 骨骼动作。可查看和输出模块 XML");
                if(draw.MountNote!=null)notes.Add(draw.MountNote);
                if(draw.Action!=null && draw.FindClip(draw.Action,clips)==null)notes.Add("缺少兼容的模型或动作 "+DrawRules.A(draw.Action,"AnimationName"));
                if(draw.State!=null && draw.State.Elements().Any(e=>e.Name.LocalName!="Animation"))notes.Add("事件及粒子保留在 XML，脚本仅预览常见状态过渡");
                if(draw.Action!=null && (draw.Action.Attribute("UseWeaponTiming")!=null || draw.Action.Attribute("AnimationSpeedFactorMin")!=null ||
                    draw.Action.Attribute("AnimationSpeedFactorMax")!=null || draw.Action.Attribute("AnimationBlendTime")!=null || draw.State.Attribute("Flags")!=null))
                    notes.Add("随机、武器计时及混合效果以游戏为准");
                UI<TextBlock>("Coverage").Text=string.Join("。",notes);
                UpdateDuration();
            }
            finally{sync=false;}
            Transport();Status();
        }
        void UpdateDuration()
        {
            var clip=current.FindClip(current.Action,clips);
            // A transition plays its own clock; the duration field always describes the selected action.
            UI<TextBox>("Duration").Text=clip==null ? "" : DrawPreview.FrameCount(current.Action,clip).ToString(CultureInfo.InvariantCulture);
            UI<Button>("ApplyDuration").IsEnabled=clip!=null;
        }
        void Status()
        {
            string selection=current.State==null ? "没有匹配的动画状态，可从全部动作中点选" : (current.Manual ? "手动预览   " : "条件匹配   ")+DrawRules.Label(current.State);
            View.Status(ModuleLabel(current)+(current.Visible ? "   ·   " : "（已隐藏）   ·   ")+selection);
        }
        void Transport()
        {
            var actor=current.Actor;bool ready=actor!=null && actor.Motion!=null;
            var slider=UI<Slider>("Frames");sync=true;
            slider.Maximum=ready ? actor.PlaybackFrames-1 : 1;slider.Value=ready ? actor.Frame : 0;slider.IsEnabled=ready;
            var play=UI<Button>("Play");play.IsEnabled=ready;play.Content=ready && actor.Playing ? "暂停" : "播放";
            sync=false;
        }
        public override void Tick(double dt)
        {
            foreach(var draw in draws)draw.Tick(dt,clips);
            if(current!=null)Transport();
        }
        void ApplyAction()
        {
            if(current==null || current.Action==null)return;
            if(current.Draft!=current.Applied){View.Status("请先应用 XML 文本。");return;}
            string name=UI<ComboBox>("ClipName").Text.Trim(),mode=UI<ComboBox>("Mode").SelectedItem as string;
            if(name.Length==0 || !DrawRules.Modes.Contains(mode)){View.Status("请选择动画资源和播放方式。");return;}
            current.Action.SetAttributeValue("AnimationName",name);current.Action.SetAttributeValue("AnimationMode",mode);
            SetText();PreviewAction(current.State,current.Action);
        }
        void ApplyDuration()
        {
            if(current==null)return;
            if(current.Draft!=current.Applied){View.Status("请先应用 XML 文本。");return;}
            var clip=current.FindClip(current.Action,clips);int n;
            if(clip==null || !int.TryParse(UI<TextBox>("Duration").Text,out n) || n<1){View.Status("请选择已解析的动作并输入正整数帧数。");return;}
            current.Action.SetAttributeValue("AnimationAbsoluteTime",(n/clip.Fps).ToString("0.###############",CultureInfo.InvariantCulture)+"s");
            SetText();current.Start(current.Action,clips);Inspector();
        }
        static async Task<bool> TryCopyXmlAsync(Action write,Func<bool> stillRequested)
        {
            const int clipboardBusy=unchecked((int)0x800401D0);
            for(int attempt=0;attempt<=10 && stillRequested();attempt++)
            {
                try { write();return true; }
                catch(ExternalException ex) { if(ex.ErrorCode!=clipboardBusy)throw; }
                if(attempt<10)await Task.Delay(160);
            }
            return false;
        }
        void Generate()
        {
            try
            {
                if(current==null || current.Draft!=current.Applied)throw new InvalidDataException("请先应用 XML 并预览。");
                DrawRules.Validate(current.Module);
                int scope=UI<ComboBox>("Scope").SelectedIndex;
                var node=scope==2 ? current.Module : scope==1 ? current.Action : current.State;
                if(node==null){scope=2;node=current.Module;UI<ComboBox>("Scope").SelectedIndex=scope;}
                var output=node.ToString();var layout=new DockPanel{Margin=new Thickness(12)};
                var footer=new Grid{Margin=new Thickness(0,8,0,0)};
                footer.RowDefinitions.Add(new RowDefinition{Height=GridLength.Auto});
                footer.RowDefinitions.Add(new RowDefinition{Height=GridLength.Auto});
                footer.ColumnDefinitions.Add(new ColumnDefinition());
                footer.ColumnDefinitions.Add(new ColumnDefinition{Width=GridLength.Auto});
                footer.ColumnDefinitions.Add(new ColumnDefinition{Width=GridLength.Auto});
                var button=new Button{Content="复制 XML"};
                var feedback=new TextBlock{VerticalAlignment=VerticalAlignment.Center,TextWrapping=TextWrapping.Wrap,Margin=new Thickness(0,0,8,0)};
                Grid.SetRow(button,1);Grid.SetColumn(button,1);footer.Children.Add(button);
                Grid.SetRow(feedback,1);footer.Children.Add(feedback);
                var draw=current;var source=draw.SourceFile;
                var save=new Button{Content="写回原文件",Margin=new Thickness(8,0,0,0),IsEnabled=source!=null && draw.Saved!=draw.Applied};
                Grid.SetRow(save,1);Grid.SetColumn(save,2);footer.Children.Add(save);
                var destination=new TextBlock{TextWrapping=TextWrapping.Wrap,Margin=new Thickness(0,0,0,6)};
                destination.Text=source==null ? "此 Draw 来自粘贴。通过「打开单位 XML」载入文件后即可写回。" :
                    "写回当前完整 Draw　"+ModuleLabel(draw)+"　·　"+ModuleInfo(draw)+"\n"+source.FilePath;
                if(source!=null && !save.IsEnabled)feedback.Text="当前 Draw 没有待写回的改动。";
                Grid.SetColumnSpan(destination,3);footer.Children.Add(destination);
                DockPanel.SetDock(footer,Dock.Bottom);layout.Children.Add(footer);
                var tabs=new TabControl();
                var dialog=new Window{Title="动画配置代码",Owner=View.Window,Width=820,Height=530,Content=layout,WindowStartupLocation=WindowStartupLocation.CenterOwner};
                int copyRequest=0;
                dialog.Closed+=(s,e)=>copyRequest++;
                Func<string,Task> copy=async value=>{
                    int request=++copyRequest;
                    feedback.Text="正在复制…";
                    try
                    {
                        var data=new Forms.DataObject(Forms.DataFormats.UnicodeText,value);
                        // A single OLE attempt per dispatch keeps the window responsive while the clipboard is locked.
                        bool copied=await TryCopyXmlAsync(()=>Forms.Clipboard.SetDataObject(data,true,0,0),()=>request==copyRequest);
                        if(request==copyRequest)feedback.Text=copied ? "已复制" : "剪贴板仍被占用，请稍后再复制。";
                    }
                    catch(Exception err){if(request==copyRequest)feedback.Text="复制失败："+err.Message;}
                };
                var parts=new List<string[]>{new[]{scope==2 ? "生成结果 · Draws 内" : scope==1 ? "生成结果 · AnimationState 内" : "生成结果 · "+draw.Module.Name.LocalName+" 内",output},new[]{"导入时的模块",draw.Original}};
                if(source!=null && scope!=2)parts.Add(new[]{"写回内容 · 整个 Draw",draw.Applied});
                foreach(var part in parts)
                {
                    var box=new TextBox{Text=part[1],IsReadOnly=true,FontFamily=new System.Windows.Media.FontFamily("Consolas"),VerticalScrollBarVisibility=ScrollBarVisibility.Auto,HorizontalScrollBarVisibility=ScrollBarVisibility.Auto};
                    CommandManager.AddPreviewExecutedHandler(box,async(s,e)=>{
                        if(e.Command!=ApplicationCommands.Copy || box.SelectionLength==0)return;
                        e.Handled=true;await copy(box.SelectedText);
                    });
                    tabs.Items.Add(new TabItem{Header=part[0],Content=box});
                }
                layout.Children.Add(tabs);button.Click+=async(s,e)=>await copy(output);
                save.Click+=(s,e)=>{
                    copyRequest++;
                    try
                    {
                        source.Save(draw.SourceElementPath,draw.Module);
                        draw.Saved=draw.Applied;save.IsEnabled=false;
                        feedback.Text="已写回。上一次保存前的文件已备份为 "+Path.GetFileName(source.BackupPath);
                        View.Status("已写回 "+ModuleLabel(draw)+"　·　"+source.FilePath);
                    }
                    catch(Exception err){feedback.Text="写回失败："+err.GetBaseException().Message;}
                };
                dialog.ShowDialog();
            }
            catch(Exception ex){Report(ex);}
        }
        public override bool CanClose()
        {
            bool dirty=current==null && xml.Text.Length>0 || draws.Any(d=>d.Draft!=d.Applied || d.Applied!=d.Saved);
            return !dirty || Discard("关闭或打开其他单位后会丢弃未保存的配置草稿，继续？");
        }
    }
}
