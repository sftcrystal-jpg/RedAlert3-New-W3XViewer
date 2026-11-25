# RedAlert3-New-W3XViewer
船新版本的W3X预览器，支持加载多个W3X模型，允许使用者将单个W3X模型绑定到另一个W3X模型的骨骼点，预览拼装效果；可以查看并编辑动画，支持将动画模块编辑到XML文件中，实时预览动画播放机制

## 运行与编译

在 Windows 上打开仓库内的 `W3XSceneViewer.exe`，点击 **添加 W3X** 或将模型文件拖入窗口。推荐使用 .NET Framework 4.8 或更高版本。模型、动画和贴图从本地加载。

如需自行编译，请下载或克隆完整仓库，在仓库根目录运行 `build.cmd`。构建所需的着色器字节码、XML 架构文件和贴图解码器均已随源码提供。

[查看中文使用说明](使用说明.md)

## 许可证

查看器采用 GNU GPL 第 3 版。随程序分发的 13 个 EA XML 架构文件另有 EA 附加条款。[GPLv3 全文](LICENSE) · [EA 许可及附加条款](LICENSES/EA-CnC-Modding-Support.md)
