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
using Microsoft.Win32;

namespace W3xScene
{
    abstract class EditWorkspace
    {
        protected readonly Viewer View;
        protected readonly List<SceneItem> Scene;
        public readonly FrameworkElement Panel;
        protected readonly List<SceneItem> PreviewItems=new List<SceneItem>();
        protected EditWorkspace(Viewer view,List<SceneItem> scene,string resource,string hint)
        {
            View=view; Scene=scene.ToList();
            using(var stream=Assembly.GetExecutingAssembly().GetManifestResourceStream(resource))
                Panel=(FrameworkElement)XamlReader.Load(stream);
            ((TextBlock)view.Window.FindName("ViewHint")).Text=hint;
        }
        protected T UI<T>(string name) where T:class {return (T)Panel.FindName(name);}
        protected void OnClick(string name,Action action) {UI<Button>(name).Click+=(s,e)=>action();}
        protected async void Browse(string title,string filter)
        {
            var dlg=new OpenFileDialog {Title=title,Filter=filter,Multiselect=true};
            if(dlg.ShowDialog(View.Window)==true) await LoadFiles(dlg.FileNames);
        }
        protected List<SceneItem> CopyPreview(ModelSpec spec)
        {
            var root=new SceneItem(spec);
            var result=new List<SceneItem>{root};
            var template=Scene.FirstOrDefault(s=>s.Spec==spec);
            if(template!=null) CopyChildren(template,root,result);
            return result;
        }
        void CopyChildren(SceneItem original,SceneItem copy,List<SceneItem> result)
        {
            foreach(var source in Scene.Where(i=>i.MountParent==original))
            {
                var child=new SceneItem(source.Spec);
                foreach(var edit in source.BoneEdits) child.BoneEdits[edit.Key]=edit.Value;
                child.Motion=source.Motion; child.Playing=source.Playing; child.Frame=source.Frame;
                if(child.Motion!=null) child.SetPlaybackFrames(source.PlaybackFrames);
                child.Frame=source.Frame; child.Apply();
                child.MountOn(copy,source.MountBone,source.FollowBoneRotation);
                result.Add(child); CopyChildren(source,child,result);
            }
        }
        public abstract Task LoadFiles(string[] paths);
        public virtual void Tick(double dt) {foreach(var item in PreviewItems) item.Advance(dt);}
        public virtual void Redraw() {}
        public virtual bool BeginDrag(Point p) {return false;}
        public virtual void Drag(Point p) {}
        public virtual void EndDrag() {}
        public virtual void Pick(Point p) {}
        public virtual void HandleKey(KeyEventArgs e) {}
        public virtual bool CanClose() {return true;}
        protected void Report(Exception ex) {View.Status(ex.GetBaseException().Message);}
        protected bool Discard(string message)
        {
            return MessageBox.Show(View.Window,message,View.Window.Title,MessageBoxButton.YesNo,MessageBoxImage.Question)==MessageBoxResult.Yes;
        }
    }
}
