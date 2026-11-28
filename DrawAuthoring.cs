using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows.Controls;
using System.Xml.Linq;

namespace W3xScene
{
    partial class ConditionWorkspace
    {
        void InitDrawEditor()
        {
            var models=UI<ComboBox>("ModelStates");
            models.SelectionChanged+=(s,e)=>{if(!sync && Selected(models)!=null)PreviewModel(Selected(models));};
            OnClick("NewModelState",()=>CreateDrawEntry(DrawEntryKind.ModelState,null));
            OnClick("AttachDraw",()=>CreateDrawEntry(DrawEntryKind.ChildDraw,null));
            OnClick("ModelFx",()=>CreateDrawEntry(DrawEntryKind.BoneEffect,current.ModelRule));
            OnClick("AnimationFx",()=>CreateDrawEntry(DrawEntryKind.BoneEffect,current.State));
        }
        void UpdateDrawControls(DrawPreview draw,bool supportsStates)
        {
            var list=UI<ComboBox>("ModelStates");
            // Keep the element identity in the selector even when two rules have the same flags.
            var rules=DrawRules.Children(draw.Module,"ModelConditionState").ToArray();
            if(!rules.SequenceEqual(list.Items.Cast<ComboBoxItem>().Select(i=>(XElement)i.Tag)))
            {
                list.Items.Clear();
                foreach(var r in rules)
                    list.Items.Add(new ComboBoxItem{Tag=r,Content=DrawRules.Label(r).Trim()+"  ·  "+ModelName(r)});
            }
            list.SelectedItem=list.Items.Cast<ComboBoxItem>().FirstOrDefault(i=>i.Tag==draw.ModelRule);
            UI<Button>("NewModelState").IsEnabled=supportsStates;
            UI<Button>("ModelFx").IsEnabled=draw.ModelRule!=null;
            UI<Button>("AnimationFx").IsEnabled=draw.State!=null;
            UI<Button>("AttachDraw").IsEnabled=supportsStates && draw.Actor!=null && draw.Actor.Pose.Length>0;
        }
        void PreviewModel(XElement rule)
        {
            current.PinnedModelRule=rule;
            var conditions=new HashSet<string>(DrawRules.Words(DrawRules.A(rule,"ConditionsYes")));
            var state=DrawRules.FirstAnimationState(current.Module,conditions);
            current.State=state;current.Action=state==null ? null : DrawRules.Children(state,"Animation").FirstOrDefault();
            current.Manual=true;bool changed=BindModel(current);current.Start(current.Action,clips);
            if(changed)RebuildScene(PreviewItems.Count==0);
            Inspector();
        }
        internal static bool ExportsBone(DrawPreview d,string bone)
        {return DrawRules.Words(DrawRules.A(d.Module,"ExtraPublicBone")).Contains(bone,Import.Names);}
        void CreateDrawEntry(DrawEntryKind kind,XElement target)
        {
            if(current==null || (target==null && kind==DrawEntryKind.BoneEffect))return;
            if(current.Draft!=current.Applied){View.Status("请先应用当前 Draw 的 XML 修改。");return;}
            try
            {
                var group=draws.Where(d=>d.Group==current.Group).ToArray();
                var dialog=new DrawEntryDialog(View.Window,kind,current,target,library,active,group,fx);
                if(!dialog.Show())return;
                if(dialog.Imported!=null)RegisterAssets(dialog.Imported);
                ApplyDrawEntry(dialog.Entry,target);
            }
            catch(Exception ex){Report(ex);}
        }
        void ApplyDrawEntry(XElement entry,XElement target)
        {
            if(entry.Name.LocalName=="ScriptedModelDraw")
            {
                var parent=current;string bone=DrawRules.A(entry,"AttachToBoneInAnotherModule");
                if(!ExportsBone(parent,bone))
                {
                    var names=DrawRules.Words(DrawRules.A(parent.Module,"ExtraPublicBone"));
                    parent.Module.SetAttributeValue("ExtraPublicBone",string.Join(" ",names.Concat(new[]{bone})));SetText();
                }
                var child=AddModule(entry,parent.Group.Name.LocalName=="Draws"?parent.SourceFile:null,parent);
                MatchDraw(child,false);RebuildScene(false);
                modules.SelectedIndex=draws.IndexOf(child);UI<ComboBox>("Scope").SelectedIndex=3;
                View.Status("已将 "+child.ModelName+" 挂到 "+ModuleLabel(parent)+" 的 "+bone+"。");return;
            }
            if(entry.Name.LocalName=="ModelConditionState")
            {
                DrawRules.InsertEntry(current.Module,entry);SetText();PreviewModel(entry);
                UI<ComboBox>("Scope").SelectedIndex=4;return;
            }
            DrawRules.InsertEntry(target,entry);SetText();
            current.Start(current.Action,clips);Inspector();
            UI<ComboBox>("Scope").SelectedIndex=target.Name.LocalName=="ModelConditionState"?4:0;
            View.Status("已添加 "+DrawRules.A(entry,"FXParticleSystemTemplate",DrawRules.A(entry,"Effect"))+"　·　"+DrawRules.A(entry,"BoneName",DrawRules.A(entry,"Bone")));
        }
        XElement GroupXml(DrawPreview[] members)
        {
            var group=members[0].Group;
            if(group.Name.LocalName!="Draws")return new XElement(members[0].Module.Name.Namespace+"Draws",members.Select(d=>new XElement(d.Module)));
            var copy=new XElement(group);var originals=group.Elements().ToArray();var slots=copy.Elements().ToArray();
            foreach(var d in members)
            {
                int i=Array.IndexOf(originals,d.ImportedNode);
                if(i>=0)slots[i].ReplaceWith(new XElement(d.Module));
                else
                {
                    var last=copy.Elements().LastOrDefault();
                    var space=last==null ? null : last.PreviousNode as XText;
                    if(last==null)copy.Add(new XElement(d.Module));
                    else last.AddAfterSelf(space!=null && string.IsNullOrWhiteSpace(space.Value)?space.Value:null,new XElement(d.Module));
                }
            }
            return copy;
        }
    }
}
