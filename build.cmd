@echo off
setlocal
cd /d "%~dp0"
set "NETFX=%SystemRoot%\Microsoft.NET\Framework64\v4.0.30319"
if not exist "%NETFX%\csc.exe" set "NETFX=%SystemRoot%\Microsoft.NET\Framework\v4.0.30319"
set "VIEWER_OUTPUT=W3XSceneViewer.exe"
if not "%~1"=="" set "VIEWER_OUTPUT=%~1"
set "SCHEMAS=Schemas"
"%NETFX%\csc.exe" /nologo /codepage:65001 /target:winexe /platform:anycpu /optimize+ /out:"%VIEWER_OUTPUT%" /r:System.Numerics.dll /r:System.Numerics.Vectors.dll /resource:EffectComposite.ps,EffectComposite.ps /resource:Window.xaml,Window.xaml /resource:MotionPanel.xaml,MotionPanel.xaml /resource:ConditionPanel.xaml,ConditionPanel.xaml /resource:DrawEntry.xaml,DrawEntry.xaml /resource:AnimationEntry.xaml,AnimationEntry.xaml /resource:"%SCHEMAS%\AssetTypeGameObject.xsd",GameObject.xsd ^
/resource:"%SCHEMAS%\Modules\ScriptedModelDraw.xsd",ScriptedModelDraw.xsd ^
/resource:"%SCHEMAS%\Modules\DynamicsDraw.xsd",DynamicsDraw.xsd ^
/resource:"%SCHEMAS%\Modules\SpotlightDraw.xsd",SpotlightDraw.xsd ^
/resource:"%SCHEMAS%\Modules\TankDraw.xsd",TankDraw.xsd ^
/resource:"%SCHEMAS%\Modules\TruckDraw.xsd",TruckDraw.xsd ^
/resource:"%SCHEMAS%\Modules\PropDraw.xsd",PropDraw.xsd ^
/resource:"%SCHEMAS%\Modules\LaserDraw.xsd",LaserDraw.xsd ^
/resource:"%SCHEMAS%\Modules\StreamDraw.xsd",StreamDraw.xsd ^
/resource:"%SCHEMAS%\Modules\TracerDraw.xsd",TracerDraw.xsd /resource:"%SCHEMAS%\Modules\BaseModules.xsd",BaseModules.xsd /resource:"%SCHEMAS%\Includes\ModelState.xsd",ModelState.xsd /resource:"%SCHEMAS%\Includes\AnimationMode.xsd",AnimationMode.xsd /resource:FxDraw.js,FxDraw.js /r:System.Net.Http.dll /r:System.Web.Extensions.dll /r:System.Xaml.dll /r:System.Xml.Linq.dll /r:System.Drawing.dll /r:System.Windows.Forms.dll /r:"%NETFX%\WPF\PresentationCore.dll" /r:"%NETFX%\WPF\PresentationFramework.dll" /r:"%NETFX%\WPF\WindowsBase.dll" Assets.cs EffectMaterial.cs EffectViewport.cs Import.cs Scene.cs BoneTransform.cs BoneEditor.cs MountPanel.cs EditWorkspace.cs MotionDocument.cs MotionWorkspace.cs MotionGizmo.cs ConditionWorkspace.cs DrawAuthoring.cs DrawEntryDialog.cs AnimationEntryDialog.cs FxSimulation.cs FxService.cs DrawFx.cs DrawPreview.cs DrawXmlFile.cs DrawRules.cs Viewer.cs BlenderPreview.cs TextureLib.cs
exit /b %errorlevel%
