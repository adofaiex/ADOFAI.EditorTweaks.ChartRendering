# ADOFAI.EditorTweaks.ChartRendering

独立的谱面视频渲染 Mod，版本 `1.1.0`。它负责摄像机/游戏画面捕获、音频捕获、FFmpeg 编码、渲染进度、取消、选中段落渲染、视频背景渲染同步、诊断日志和公共渲染 API v1。

## 功能

- 摄像机模式和游戏画面模式（离屏渲染，可输出高于显示器的分辨率）。
- 视频编码：H.264、HEVC/H.265、AV1、ProRes 422 HQ，以及实验性的 H.266/VVC。
- 画质模式：CRF 恒定质量、目标码率（VBR），以及按填写码率输出的恒定比特率（CBR，尽力而为）。
- 音频：AAC、FLAC、ALAC；MP4 / MKV / MOV 容器。
- 渲染进度、取消、选中段落渲染、渲染结束后自动回到编辑器。
- 本机 Web 设置页、编码器可用性检测、每次渲染的独立日志。
- 自带 gyan.dev 的 **FFmpeg 9.0.2 full build**，包含 `libsvtav1`（AV1 CPU 编码）和 `libvvenc`（H.266/VVC，实验性）。

> H.266/VVC 只有 CPU 编码，1080p60 大约每秒数帧，且大多数播放器、剪辑软件和网站不支持。设置页把它标记为实验性、不推荐，仅供尝鲜。

UMM 面板只负责录入打开 Web UI 的快捷键，默认 `Ctrl+Shift+E`。渲染设置、进度和 FFmpeg 帮助都在 Web UI 中；页面只保留渲染相关功能。

公共 API 命名空间保持 `ADOFAI.EditorTweaks.Api.Rendering`，但程序集是 `ADOFAI.EditorTweaks.ChartRendering.dll`。不提供旧程序集转发层，调用方必须单独引用并安装本 Mod。

## 构建

```powershell
dotnet build ADOFAI.EditorTweaks.ChartRendering.csproj -c Debug
dotnet build ADOFAI.EditorTweaks.ChartRendering.csproj -c Release
```

首次构建会把 `Local.props.example` 里的游戏路径填到 `Local.props`，然后下载 FFmpeg 并安装网页依赖。产物在 `out/` 和 `Build/`，部署目录为 `Mods/ADOFAI.EditorTweaks.ChartRendering/`。发行包包含 Web UI、FFmpeg 及渲染资源。

## 文档

- `Doc/ChartRendering.md`：渲染流程和编码参数。
- `Doc/Api/ChartRendering.md`：公共 API 和新增字段。
- `Doc/BuildAndRelease.md`：构建与发布。
