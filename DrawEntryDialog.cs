using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Markup;
using System.Xml;
using System.Xml.Linq;
using Microsoft.Win32;

namespace W3xScene
{
    enum DrawEntryKind { ModelState, BoneEffect, ChildDraw }

    sealed class DrawEntryDialog
    {
        readonly Window window;
        readonly DrawEntryKind kind;
        readonly DrawPreview draw;
        readonly List<ModelSpec> models;
        readonly DrawPreview[] siblings;
        readonly string[] bones;
        FxService.CatalogEntry[] catalog;
        public Import Imported {get;private set;}
        public XElement Entry {get;private set;}
        T UI<T>(string id) where T:class {return (T)window.FindName(id);}
        void ShowField(string id,bool visible){UI<FrameworkElement>(id).Visibility=visible?Visibility.Visible:Visibility.Collapsed;}
        public bool Show(){return window.ShowDialog()==true;}

        public DrawEntryDialog(Window owner,DrawEntryKind kind,DrawPreview draw,XElement target,IEnumerable<ModelSpec> library,
            IEnumerable<string> active,DrawPreview[] siblings,FxService fx)
        {
            this.kind=kind;this.draw=draw;this.siblings=siblings;models=library.ToList();
            bones=draw.Actor==null || draw.Actor.Spec.Rig==null ? new string[0] : draw.Actor.Spec.Rig.Bones.Select(b=>b.Name).Distinct(Import.Names).ToArray();
            using(var stream=Assembly.GetExecutingAssembly().GetManifestResourceStream("DrawEntry.xaml"))window=(Window)XamlReader.Load(stream);
            window.Owner=owner;window.Resources.MergedDictionaries.Add(owner.Resources);
            bool state=kind==DrawEntryKind.ModelState, child=kind==DrawEntryKind.ChildDraw;
            window.Title=state?"新增模型条件状态":child?"挂接 W3X 为新 Draw":"添加骨骼特效";
            UI<TextBlock>("Target").Text=(child?"父 Draw　":"Draw　")+DrawRules.A(draw.Module,"id",draw.Module.Name.LocalName)+
                (target==null?"":"\n"+target.Name.LocalName+"　"+DrawRules.Label(target));
            ShowField("ModelFields",state||child);ShowField("ModuleFields",child);ShowField("ConditionFields",state);
            ShowField("BoneFields",!state);ShowField("FxFields",kind==DrawEntryKind.BoneEffect);
            var bone=UI<ComboBox>("Bone");bone.ItemsSource=bones;
            if(bones.Length==1)bone.SelectedIndex=0;
            if(state || child)
            {
                FillModels();UI<Button>("BrowseModel").Click+=async(s,e)=>await BrowseModel();
                if(state)
                {
                    UI<ComboBox>("Model").Text=draw.ModelName ?? "";
                    UI<TextBox>("Conditions").Text=string.Join(" ",active.OrderBy(x=>x));
                    UI<ComboBox>("Flags").ItemsSource=DrawRules.Flags.OrderBy(x=>x).ToArray();
                    UI<Button>("AppendFlag").Click+=(s,e)=>AppendFlag();
                    var k=UI<ComboBox>("StateKind");
                    k.SelectionChanged+=(s,e)=>ShowField("ConditionsPanel",k.SelectedIndex==0);
                    k.SelectedIndex=!DrawRules.Children(draw.Module,"ModelConditionState").Any() && !active.Any()?1:0;
                }
                else
                {
                    int n=1;while(HasId("ModuleTag_AttachedDraw"+n))n++;
                    UI<TextBox>("ModuleId").Text="ModuleTag_AttachedDraw"+n;
                    UI<TextBlock>("BoneLabel").Text="父模型骨骼";
                }
            }
            else
            {
                UI<ComboBox>("FxKind").SelectionChanged+=(s,e)=>UpdateEffectKind();
                UI<TextBox>("Frame").Text=(draw.Actor==null?0:draw.Actor.SourceFrame).ToString();
                UpdateEffectKind();
                window.Loaded+=async(s,e)=>{
                    var hint=UI<TextBlock>("CatalogStatus");hint.Text="正在读取 fxpreview 目录，也可直接输入资源 ID。";
                    try{catalog=await fx.Catalog();if(window.IsVisible){UpdateEffectKind();hint.Text="";}}
                    catch(Exception ex){if(window.IsVisible)hint.Text="可直接输入资源 ID。目录读取失败："+ex.GetBaseException().Message;}
                };
            }
            UI<Button>("Create").Click+=(s,e)=>{
                try{Entry=Build();window.DialogResult=true;}
                catch(Exception ex){UI<TextBlock>("Error").Text=ex.GetBaseException().Message;}
            };
        }
        void UpdateEffectKind()
        {
            bool particle=UI<ComboBox>("FxKind").SelectedIndex==0;
            ShowField("Follow",particle);ShowField("FrameFields",!particle);
            if(catalog==null)return;
            var input=UI<ComboBox>("FxId");string text=input.Text;
            input.ItemsSource=catalog.Where(e=>e.kind==(particle?"particle":"fxlist")).Select(e=>e.id).OrderBy(s=>s,Import.Names).ToArray();
            input.Text=text;
        }
        bool HasId(string id){return siblings.Any(d=>Import.Names.Equals(DrawRules.A(d.Module,"id"),id));}
        void FillModels(){UI<ComboBox>("Model").ItemsSource=models.Select(m=>m.Name).Distinct(Import.Names).OrderBy(n=>n,Import.Names).ToArray();}
        async Task BrowseModel()
        {
            var picker=new OpenFileDialog{Title="选择模型 W3X",Filter="W3X 模型|*.w3x"};
            if(picker.ShowDialog(window)==true)await LoadModel(picker.FileName);
        }
        async Task LoadModel(string file)
        {
            var create=UI<Button>("Create");var browse=UI<Button>("BrowseModel");
            browse.IsEnabled=create.IsEnabled=false;UI<TextBlock>("Error").Text="正在读取模型…";
            try
            {
                var batch=await Task.Run(()=>Import.Load(new[]{file}));
                if(!window.IsVisible)return;
                var found=batch.Models.Where(m=>!m.RigOnly || batch.ExplicitRigs.Any(r=>Import.Names.Equals(r.Id,m.RigId))).ToArray();
                if(found.Length==0)throw new InvalidDataException("该文件没有可挂接的模型或骨架。");
                if(Imported!=null)models.RemoveAll(m=>Imported.Models.Contains(m));
                Imported=batch;models.InsertRange(0,found);FillModels();UI<ComboBox>("Model").Text=found[0].Name;
                UI<TextBlock>("Error").Text=found.Length>1?"文件中有多个模型，请选择所需资源。":"";
            }
            catch(Exception ex){if(window.IsVisible)UI<TextBlock>("Error").Text=ex.GetBaseException().Message;}
            finally{browse.IsEnabled=create.IsEnabled=true;}
        }
        void AppendFlag()
        {
            var flags=UI<ComboBox>("Flags");string value=flags.Text.Trim().ToUpperInvariant();
            if(!DrawRules.Flags.Contains(value)){UI<TextBlock>("Error").Text="请选择 SDK 模型条件。";return;}
            var input=UI<TextBox>("Conditions");input.Text=string.Join(" ",DrawRules.Words(input.Text.ToUpperInvariant()).Concat(new[]{value}).Distinct());
            flags.SelectedIndex=-1;flags.Text="";UI<TextBlock>("Error").Text="";
        }
        XElement Build()
        {
            var ns=draw.Module.Name.Namespace;
            string bone=UI<ComboBox>("Bone").Text.Trim();
            if(kind!=DrawEntryKind.ModelState)
            {
                var matched=bones.FirstOrDefault(b=>Import.Names.Equals(b,bone));
                if(bone.Length==0 || (matched==null && bones.Length>0))throw new InvalidDataException("请选择目标模型中的骨骼。");
                bone=XmlConvert.VerifyXmlChars(matched??bone);
            }
            if(kind==DrawEntryKind.BoneEffect)
            {
                string id=XmlConvert.VerifyXmlChars(UI<ComboBox>("FxId").Text.Trim());
                if(id.Length==0)throw new InvalidDataException("请选择或输入特效资源 ID。");
                if(UI<ComboBox>("FxKind").SelectedIndex==0)
                    return new XElement(ns+"ParticleSysBone",new XAttribute("BoneName",bone),new XAttribute("FXParticleSystemTemplate",id),
                        new XAttribute("FollowBone",UI<CheckBox>("Follow").IsChecked==true));
                int frame;if(!int.TryParse(UI<TextBox>("Frame").Text,out frame) || frame<0)throw new InvalidDataException("触发帧必须是非负整数。");
                return new XElement(ns+"FXEvent",new XAttribute("Effect",id),new XAttribute("Bone",bone),new XAttribute("Frame",frame),new XAttribute("FireWhenSkipped",true));
            }
            string modelName=XmlConvert.VerifyXmlChars(UI<ComboBox>("Model").Text.Trim());
            if(modelName.Length==0)throw new InvalidDataException("请选择 W3X 或输入模型资源 ID。");
            var selected=models.FirstOrDefault(m=>Import.Names.Equals(m.Name,modelName));
            var state=new XElement(ns+"ModelConditionState",new XAttribute("ParseCondStateType","PARSE_DEFAULT"),
                new XElement(ns+"Model",new XAttribute("Name",selected==null?modelName:selected.Name)));
            if(kind==DrawEntryKind.ChildDraw)
            {
                if(selected==null)throw new InvalidDataException("请先选择要挂接的 W3X 模型。");
                string id=XmlConvert.VerifyXmlChars(UI<TextBox>("ModuleId").Text.Trim());
                if(id.Length==0)throw new InvalidDataException("请输入新 Draw 的模块 ID。");
                if(HasId(id))throw new InvalidDataException("同组已有该 Draw ID，请更换名称。");
                if(siblings.Any(d=>d!=draw && ConditionWorkspace.ExportsBone(d,bone)))
                    throw new InvalidDataException("同组其他 Draw 已公开同名骨骼。请选择能唯一定位父 Draw 的挂点。");
                return new XElement(ns+"ScriptedModelDraw",new XAttribute("id",id),new XAttribute("AttachToBoneInAnotherModule",bone),state);
            }
            if(UI<ComboBox>("StateKind").SelectedIndex==0)
            {
                var words=DrawRules.Words((UI<TextBox>("Conditions").Text+" "+UI<ComboBox>("Flags").Text).ToUpperInvariant());
                if(words.Length==0)throw new InvalidDataException("请填写触发条件，或选择默认状态。");
                var bad=words.FirstOrDefault(f=>!DrawRules.Flags.Contains(f));if(bad!=null)throw new InvalidDataException("未知模型条件 "+bad);
                state.SetAttributeValue("ParseCondStateType","PARSE_NORMAL");state.SetAttributeValue("ConditionsYes",string.Join(" ",words));
            }
            else if(DrawRules.Children(draw.Module,"ModelConditionState").Any(r=>DrawRules.A(r,"ParseCondStateType")=="PARSE_DEFAULT"))
                throw new InvalidDataException("此 Draw 已有默认模型状态，可从模型条件状态列表选中它。");
            return state;
        }
    }
}
