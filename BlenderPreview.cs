using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Web.Script.Serialization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Media.Media3D;
using System.Windows.Threading;

namespace W3xScene
{
    partial class Viewer
    {
        // One camera and one preview scene per saved Blender document.
        bool blenderPreview;

        internal static void RunBlenderPreview(string folder,bool inline)
        {
            folder=Path.GetFullPath(folder);
            string key;
            using(var hash=SHA256.Create())
                key=BitConverter.ToString(hash.ComputeHash(Encoding.UTF8.GetBytes(folder.ToUpperInvariant()))).Replace("-","");
            bool first;
            using(var owner=new Mutex(true,@"Local\W3XBlender-"+key,out first))
            {
                if(!first)
                {
                    if(!inline) File.WriteAllText(Path.Combine(folder,"show-window"),"");
                    return;
                }
                var app=new Application();
                var viewer=new Viewer();
                viewer.StartBlenderPreview(folder,inline);
                app.Run(viewer.Window);
                owner.ReleaseMutex();
            }
        }

        void StartBlenderPreview(string folder,bool inline)
        {
            blenderPreview=true;
            Window.Title="Blender W3X 预览 · "+Path.GetFileName(folder);
            UI<TextBlock>("WindowHeading").Text="Blender W3X 预览";
            UI<Button>("OpenButton").Visibility=Visibility.Collapsed;
            Window.AllowDrop=false;
            // WPF needs a measured scene to take the first capture. Show it offscreen
            // for layout, then hide the host while Blender displays the images.
            if(inline)
            {
                Window.WindowStartupLocation=WindowStartupLocation.Manual;
                Window.Left=SystemParameters.VirtualScreenLeft-Window.Width-80;
                Window.ShowInTaskbar=false;
                Window.ShowActivated=false;
            }
            var timer=new DispatcherTimer {Interval=TimeSpan.FromMilliseconds(300)};
            string last=null;
            bool closed=false;
            var idle=System.Diagnostics.Stopwatch.StartNew();
            timer.Tick+=async delegate {
                timer.Stop();
                try
                {
                    string show=Path.Combine(folder,"show-window");
                    if(File.Exists(show))
                    {
                        File.Delete(show);
                        if(!Window.IsVisible)
                        {
                            var screen=SystemParameters.WorkArea;
                            Window.Left=screen.Left+Math.Max(0,(screen.Width-Window.Width)/2);
                            Window.Top=screen.Top+Math.Max(0,(screen.Height-Window.Height)/2);
                            Window.ShowInTaskbar=true;
                            Window.Show();
                        }
                        Window.Activate();
                    }
                    string pointer=Path.Combine(folder,"current.txt");
                    if(!File.Exists(pointer)) return;
                    string version;
                    using(var stream=new FileStream(pointer,FileMode.Open,FileAccess.Read,FileShare.ReadWrite|FileShare.Delete))
                    using(var reader=new StreamReader(stream)) version=reader.ReadToEnd().Trim();
                    Guid id;
                    if(version==last || !Guid.TryParseExact(version,"N",out id))
                    {
                        if(!Window.IsVisible && idle.Elapsed.TotalMinutes>2)Window.Close();
                        return;
                    }
                    last=version;
                    string run=Path.Combine(folder,version);
                    var exportError=Path.Combine(run,"export-error.txt");
                    if(File.Exists(exportError))Status("Blender 导出失败 · "+File.ReadAllText(exportError));
                    else await RefreshBlender(run);
                    idle.Restart();
                }
                catch(IOException e){Status("读取预览任务失败 · "+e.Message);}
                catch(UnauthorizedAccessException e){Status("预览目录访问失败 · "+e.Message);}
                finally {if(!closed)timer.Start();}
            };
            bool started=false;
            Window.ContentRendered+=(s,e)=>{
                if(started)return;
                started=true;
                if(inline)Window.Hide();
                timer.Start();
            };
            Window.Closed+=(s,e)=>{closed=true;timer.Stop();};
        }

        async Task RefreshBlender(string run)
        {
            var warnings=new List<string>();
            string id=Path.GetFileName(run);
            var report=new Dictionary<string,object>{{"run",id},{"warnings",warnings}};
            try
            {
                string warningFile=Path.Combine(run,"export-warnings.txt");
                if(File.Exists(warningFile)) warnings.AddRange(File.ReadAllLines(warningFile));
                string model=Directory.GetFiles(run,"*.w3x").Single();
                report["model"]=Path.GetFileName(model);
                var batch=await Task.Run(()=>Import.Load(new[]{model}));
                warnings.AddRange(batch.Warnings.Distinct());
                if(batch.Models.Count!=1 || batch.Models[0].Parts.Count==0)
                    throw new InvalidDataException("需要包含可见网格的单个层级模型。");
                var replacement=new SceneItem(batch.Models[0]);
                string frameFile=Path.Combine(run,"animation-frame.txt");
                if(File.Exists(frameFile))
                {
                    var clip=replacement.Clips.Single();
                    string[] requested=File.ReadAllLines(frameFile);
                    int frame=int.Parse(requested[0],System.Globalization.CultureInfo.InvariantCulture);
                    int? blenderFrame=requested.Length<2 ? (int?)null : int.Parse(requested[1],System.Globalization.CultureInfo.InvariantCulture);
                    replacement.SetMotion(clip);
                    replacement.Frame=Math.Max(0,Math.Min(frame,clip.Frames-1));
                    replacement.Playing=false;
                    replacement.Apply();
                    report["animation"]=new {id=clip.Id,frame=(int)replacement.Frame,blenderFrame,
                        poseOnly=requested.Length>2 && requested[2]=="pose",frames=clip.Frames,fps=clip.Fps};
                }
                report["bones"]=replacement.Spec.Rig==null ? new string[0] : replacement.Spec.Rig.Bones.Select(b=>b.Name).ToArray();
                report["teamColorPreview"]=false;
                report["materials"]=replacement.Spec.Parts.Select(p=>new {mesh=p.Mesh.Id,shader=p.Mesh.ShaderName,
                    textures=p.Mesh.TextureSlots,preview=p.Mesh.Effect==null ? "diffuse" : "effect",
                    bones=PreviewBones(p,replacement.Spec.Rig)}).ToArray();
                var old=items.FirstOrDefault();
                if(old!=null)replacement.SetPosition(old.Offset.OffsetX,old.Offset.OffsetY,old.Offset.OffsetZ);
                objects.Items.Clear();
                Preview(new[]{replacement},old==null);
                objects.Items.Add(replacement);
                objects.SelectedItem=replacement;
                Refresh();

                report["camera"]=new {yaw,pitch,distance,target=new[]{target.X,target.Y,target.Z}};
                var near=CaptureBlenderFrame();
                var oldTarget=target;
                double oldYaw=yaw,oldPitch=pitch,oldDistance=distance;
                BitmapSource tactical;
                try
                {
                    var b=Bounds(items);
                    target=new Point3D(b.X+b.SizeX*.5,b.Y+b.SizeY*.5,b.Z+b.SizeZ*.5);
                    yaw=-Math.PI/3; pitch=50*Math.PI/180;
                    distance=viewport.ActualWidth/(2*Math.Tan(camera.FieldOfView*Math.PI/360)*9);
                    Camera();
                    tactical=CaptureBlenderFrame();
                }
                finally
                {
                    yaw=oldYaw;pitch=oldPitch;distance=oldDistance;target=oldTarget;
                    Camera();effects.InvalidateScene();
                }
                await Task.Run(delegate {
                    SavePreviewPng(near,Path.Combine(run,"current.png"));
                    SavePreviewPng(tactical,Path.Combine(run,"tactical.png"));
                });
                report["status"]=warnings.Count==0 ? "ok" : "warning";
                report["images"]=new[]{"current.png","tactical.png"};
                report["meshes"]=replacement.Spec.Parts.Count;
                report["triangles"]=replacement.Spec.Parts.Sum(p=>(long)p.Mesh.Indices.Length/3);
                Status("预览已刷新 · "+warnings.Count+" 条提示 · 两张截图已保存");
            }
            catch(Exception e)
            {
                report["status"]="error";report["error"]=e.Message;
                Status("预览失败 · "+e.Message);
            }
            history.Add(Path.GetFileName(run)+" · "+UI<TextBlock>("Status").Text+Environment.NewLine+string.Join(Environment.NewLine,warnings));
            var final=Path.Combine(run,"result.json");
            var temp=final+"."+Guid.NewGuid().ToString("N")+".tmp";
            File.WriteAllText(temp,new JavaScriptSerializer().Serialize(report),new UTF8Encoding(false));
            if(File.Exists(final))File.Replace(temp,final,null);else File.Move(temp,final);
        }

        static string[] PreviewBones(Attachment part,Rig rig)
        {
            if(rig==null)return new string[0];
            var weights=part.Mesh.Weights;
            var indices=weights==null || weights.Length==0 ? new[]{part.Bone} :
                weights.SelectMany(stream=>stream).Where(w=>w.Weight>0).Select(w=>w.Bone).Distinct().ToArray();
            return indices.Where(i=>i>=0 && i<rig.Bones.Length).Select(i=>rig.Bones[i].Name).ToArray();
        }

        BitmapSource CaptureBlenderFrame()
        {
            var layer=UI<Grid>("SceneLayer");
            layer.UpdateLayout();
            effects.DrawSnapshot(items,clock.Elapsed.TotalSeconds);
            var bitmap=new RenderTargetBitmap(Math.Max(1,(int)Math.Ceiling(layer.ActualWidth)),
                Math.Max(1,(int)Math.Ceiling(layer.ActualHeight)),96,96,PixelFormats.Pbgra32);
            bitmap.Render(layer);
            bitmap.Freeze();
            return bitmap;
        }

        static void SavePreviewPng(BitmapSource bitmap,string path)
        {
            var encoder=new PngBitmapEncoder();
            encoder.Frames.Add(BitmapFrame.Create(bitmap));
            using(var output=File.Create(path))encoder.Save(output);
        }
    }
}

