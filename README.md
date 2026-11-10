# ADOFAI.EditorTweaks.ChartRendering

独立的谱面视频渲染 Mod，版本 `1.1.0`。基于 AETC 1.0.3，并移植了 CheryTools AET Recorder 的渲染改进（经作者 sui 同意）。它负责摄像机/游戏画面捕获、音频捕获、FFmpeg 编码、渲染进度、取消、选中段落渲染、视频背景渲染同步、诊断日志和公共渲染 API v1。

## 功能

- **画面捕获**：摄像机模式，以及包含游戏界面的游戏画面模式。游戏画面模式使用离屏渲染，可按设置输出高于显示器的分辨率，屏幕上只显示预览。
- **视频编码**：H.264、HEVC/H.265、AV1、ProRes 422 HQ，以及实验性的 H.266/VVC。
- **画质控制**：CRF 恒定质量、目标码率（VBR），以及按填写码率输出的恒定比特率（CBR，尽力而为）。
- **编码器选择**：支持 NVIDIA NVENC、AMD AMF 和 Intel QSV 硬件编码，也可以手动指定厂商。默认按 NVENC → AMF → QSV 的顺序选择，硬件或驱动不支持时自动回退 CPU（x264 / x265 / SVT-AV1 / libaom）。设置页会检测这台电脑实际可用的编码器，并显示渲染将使用哪一个。
- **速度预设**：NVENC p1–p7，x264/x265 ultrafast–veryslow；AMD/Intel 硬件编码和 AV1 CPU 编码器按同等速度换算。
- **音频与容器**：AAC、FLAC、ALAC；MP4 / MKV / MOV。ProRes 自动使用 MOV，MOV + FLAC 自动改用 ALAC（同为无损）。
- **渲染流程**：画面捕获 0–90%，完成编码 90–92%，合成音频 92–100%；合成先写 `.part`，成功后再改名，导出目录里正式名字的文件一定是完整的。
- **稳定性**：渲染中取消、关闭游戏拦截、与网页断线提示、渲染结束后自动回到编辑器。
- **其他**：选中段落渲染、结束后延迟停止、判定文字开关、音频同步偏移、自定义 FFmpeg 路径和合成参数。
- **诊断**：每次渲染单独保存日志到 `Workspace\Logs\render_*.log`（保留最近 10 份），记录编码器、FFmpeg 参数、内存和歌曲状态。
- **FFmpeg**：自带 gyan.dev 的 **9.0.2 full build**，包含 `libsvtav1`（AV1 CPU 编码）和 `libvvenc`（H.266/VVC，实验性）。

> H.266/VVC 只有 CPU 编码，1080p60 大约每秒数帧，且大多数播放器、剪辑软件和网站不支持。设置页把它标记为实验性、不推荐，仅供尝鲜。

> AV1 硬件编码需要 NVIDIA RTX 40 系、AMD RDNA 3 或 Intel Arc 及更新的显卡；更老的显卡会回退 CPU 的 `libsvtav1`。

> 音频缓冲大于 256 时，渲染会临时切换 DSP 缓冲并重新加载歌曲；其他 mod 自己加载的音效（例如 Creplay 的按键音）可能需要重进谱面才能恢复。

UMM 面板只负责录入打开 Web UI 的快捷键，默认 `Ctrl+Shift+E`。渲染设置、进度和 FFmpeg 帮助都在 Web UI 中；页面只保留渲染相关功能。

公共 API 命名空间保持 `ADOFAI.EditorTweaks.Api.Rendering`，但程序集是 `ADOFAI.EditorTweaks.ChartRendering.dll`。不提供旧程序集转发层，调用方必须单独引用并安装本 Mod。Creplay 等调用方不需要改动；API 只做了向后兼容的新增。

## 构建

```powershell
dotnet build ADOFAI.EditorTweaks.ChartRendering.csproj -c Debug
dotnet build ADOFAI.EditorTweaks.ChartRendering.csproj -c Release
```

首次构建会把 `Local.props.example` 里的游戏路径填到 `Local.props`，然后下载 FFmpeg 并安装网页依赖。产物在 `out/` 和 `Build/`，部署目录为 `Mods/ADOFAI.EditorTweaks.ChartRendering/`。发行包包含 Web UI、FFmpeg 及渲染资源。

本项目以 GNU GPL v3.0 许可发布，详见 LICENSE。

## 文档

- `Doc/ChartRendering.md`：渲染流程和编码参数。
- `Doc/Api/ChartRendering.md`：公共 API 和新增字段。
- `Doc/BuildAndRelease.md`：构建与发布。
