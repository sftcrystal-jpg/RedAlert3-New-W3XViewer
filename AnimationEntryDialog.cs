using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Markup;
using System.Xml;
using System.Xml.Linq;
using Microsoft.Win32;

namespace W3xScene
{
    class AnimationEntryDialog
    {
        readonly Window window;
        readonly XElement module, targetState;
        readonly ComboBox kind, asset, mode, catalog;
        readonly TextBox conditions, stateName;
        readonly HashSet<string> names;
        readonly List<Import> imports=new List<Import>();
        bool loading;
        public XElement State {get;private set;}
        public XElement Action {get;private set;}
        public IEnumerable<Import> Imports {get{return imports;}}
        string Kind {get{return (string)((ComboBoxItem)kind.SelectedItem).Tag;}}

        public AnimationEntryDialog(Window owner,XElement module,XElement targetState,IEnumerable<string> suggestions,IEnumerable<string> flags)
        {
            this.module=module;this.targetState=targetState;
            names=new HashSet<string>(suggestions,Import.Names);
            using(var stream=Assembly.GetExecutingAssembly().GetManifestResourceStream("AnimationEntry.xaml"))
                window=(Window)XamlReader.Load(stream);
            window.Owner=owner;window.Resources.MergedDictionaries.Add(owner.Resources);
            bool creating=targetState==null;
            window.Title=creating ? "新增动画状态" : "给当前状态添加动作";
            UI<TextBlock>("Target").Text="目标 Draw   "+DrawRules.A(module,"id",module.Name.LocalName)+(creating ? "" : "\n目标状态   "+DrawRules.Label(targetState));
            UI<StackPanel>("StateFields").Visibility=creating ? Visibility.Visible : Visibility.Collapsed;
            UI<TextBlock>("AssetLabel").Text=creating ? "首个动作（可稍后添加）" : "动画资源（选择或输入资源 ID）";
            kind=UI<ComboBox>("Kind");asset=UI<ComboBox>("Asset");mode=UI<ComboBox>("PlaybackMode");catalog=UI<ComboBox>("Catalog");
            conditions=UI<TextBox>("Conditions");stateName=UI<TextBox>("StateName");
            catalog.ItemsSource=DrawRules.Flags.OrderBy(x=>x).ToArray();
            conditions.Text=string.Join(" ",flags.OrderBy(x=>x));
            asset.ItemsSource=names.OrderBy(n=>n,Import.Names).ToArray();
            foreach(string value in DrawRules.Modes.OrderBy(m=>m))
            {
                var item=new ComboBoxItem{Content=ModeLabel(value),Tag=value};
                mode.Items.Add(item);
            }
            kind.SelectionChanged+=(s,e)=>UpdateKind();
            kind.SelectedIndex=creating && !DrawRules.Children(module,"AnimationState").Any() && conditions.Text.Length==0 ? 1 : 0;
            if(!creating)SetMode(DrawRules.A(targetState,"ParseCondStateType")=="PARSE_TRANSITION" ? "ONCE" : "LOOP");
            UI<Button>("Append").Click+=(s,e)=>AppendFlag();
            catalog.PreviewKeyDown+=(s,e)=>{if(e.Key==Key.Enter && !catalog.IsDropDownOpen){AppendFlag();e.Handled=true;}};
            UI<Button>("BrowseClip").Click+=async(s,e)=>await BrowseClip();
            UI<Button>("Create").Click+=(s,e)=>Confirm();
            window.ContentRendered+=(s,e)=>{if(creating && Kind=="PARSE_NORMAL")conditions.Focus();else asset.Focus();};
        }
        T UI<T>(string name) where T:class {return (T)window.FindName(name);}
        public bool Show(){return window.ShowDialog()==true;}
        static string ModeLabel(string value)
        {
            switch(value)
            {
                case "LOOP":return "循环（LOOP）";
                case "ONCE":return "播放一次（ONCE）";
                case "LOOP_BACKWARDS":return "倒放循环（LOOP_BACKWARDS）";
                case "ONCE_BACKWARDS":return "倒放一次（ONCE_BACKWARDS）";
                case "LOOP_PINGPONG":return "往返循环（LOOP_PINGPONG）";
                case "MANUAL":return "手动控制（MANUAL）";
                case "PLAY_TO_FRAME":return "播放至指定帧（PLAY_TO_FRAME）";
                case "MATCH_UNPACKING":return "跟随展开进度（MATCH_UNPACKING）";
                default:return value;
            }
        }
        void SetMode(string value){mode.SelectedItem=mode.Items.Cast<ComboBoxItem>().First(i=>(string)i.Tag==value);}
        void UpdateKind()
        {
            bool transition=Kind=="PARSE_TRANSITION";
            UI<StackPanel>("ConditionFields").Visibility=Kind=="PARSE_NORMAL" ? Visibility.Visible : Visibility.Collapsed;
            UI<TextBlock>("StateNameLabel").Text=transition ? "过渡状态名称（供脚本引用）" : "状态名称（选填）";
            SetMode(transition ? "ONCE" : "LOOP");
            Notice(Kind=="PARSE_DEFAULT" && DrawRules.Children(module,"AnimationState").Any(s=>DrawRules.A(s,"ParseCondStateType")==Kind)
                ? "此 Draw 已有默认状态。要给原默认状态再加一个动作，可选中它后点「添加动作」。" : "");
        }
        void Notice(string text)
        {
            var message=UI<TextBlock>("Feedback");message.Text=text;
            message.Visibility=text.Length==0 ? Visibility.Collapsed : Visibility.Visible;
        }
        bool AppendFlag()
        {
            var flag=catalog.Text.Trim().ToUpperInvariant();
            if(!DrawRules.Flags.Contains(flag)){Notice("请选择有效的模型条件。");catalog.Focus();return false;}
            conditions.Text=string.Join(" ",DrawRules.Words(conditions.Text.ToUpperInvariant()).Concat(new[]{flag}).Distinct());
            catalog.SelectedIndex=-1;catalog.Text="";Notice("");return true;
        }
        async Task BrowseClip()
        {
            var picker=new OpenFileDialog{Title="选择动画 W3X",Filter="W3X 动画|*.w3x"};
            if(picker.ShowDialog(window)!=true)return;
            loading=true;UI<Button>("Create").IsEnabled=false;UI<Button>("BrowseClip").IsEnabled=false;
            Notice("正在读取动画…");
            try
            {
                var batch=await Task.Run(()=>Import.Load(new[]{picker.FileName}));
                if(!window.IsVisible)return;
                if(batch.ExplicitClips.Count==0)throw new InvalidDataException("文件中没有可读取的 W3DAnimation。");
                imports.Add(batch);
                names.UnionWith(batch.ExplicitClips.Select(c=>c.Id));
                asset.ItemsSource=names.OrderBy(n=>n,Import.Names).ToArray();
                asset.SelectedItem=batch.ExplicitClips[0].Id;asset.Text=batch.ExplicitClips[0].Id;
                Notice(batch.ExplicitClips.Count>1 ? "文件含多个动作，可在动画资源列表中选择。" : "");
            }
            catch(Exception ex){if(window.IsVisible)Notice(ex.GetBaseException().Message);}
            finally{loading=false;UI<Button>("Create").IsEnabled=true;UI<Button>("BrowseClip").IsEnabled=true;}
        }
        void Confirm()
        {
            if(loading)return;
            var id=asset.Text.Trim();string name=stateName.Text.Trim();
            bool creating=targetState==null;
            if(!creating && id.Length==0){Notice("请选择 W3X 动作，或输入动画资源 ID。");asset.Focus();return;}
            try
            {
                var ns=module.Name.Namespace;
                XElement state=null;
                if(creating)
                {
                    state=new XElement(ns+"AnimationState",new XAttribute("ParseCondStateType",Kind));
                    if(Kind=="PARSE_NORMAL")
                    {
                        if(catalog.Text.Trim().Length>0 && !AppendFlag())return;
                        var words=DrawRules.Words(conditions.Text.ToUpperInvariant());
                        if(words.Length==0){Notice("请添加触发条件，或选择「默认状态」。");conditions.Focus();return;}
                        string bad=words.FirstOrDefault(f=>!DrawRules.Flags.Contains(f));
                        if(bad!=null){Notice("无法识别模型条件 "+bad);conditions.Focus();return;}
                        state.SetAttributeValue("ConditionsYes",string.Join(" ",words));
                    }
                    if(Kind=="PARSE_TRANSITION" && name.Length==0){Notice("请输入供脚本引用的过渡状态名称。");stateName.Focus();return;}
                    if(name.Length>0)state.SetAttributeValue("StateName",XmlConvert.VerifyXmlChars(name));
                }
                var action=id.Length==0 ? null : new XElement(ns+"Animation",new XAttribute("AnimationName",XmlConvert.VerifyXmlChars(id)),
                    new XAttribute("AnimationMode",(string)((ComboBoxItem)mode.SelectedItem).Tag));
                if(state!=null && action!=null)state.Add(action);
                State=state;Action=action;window.DialogResult=true;
            }
            catch(XmlException ex){Notice(ex.Message);}
        }
    }
}
