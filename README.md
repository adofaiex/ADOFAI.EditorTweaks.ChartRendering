# ADOFAI.EditorTweaks.ChartRendering

独立的谱面视频渲染 Mod，版本 `1.0.0`。它负责摄像机/游戏画面捕获、音频捕获、FFmpeg 编码、渲染进度、取消、选中段落渲染、视频背景渲染同步、诊断日志和公共渲染 API v1。

UMM 面板只负责录入打开 Web UI 的快捷键，默认 `Ctrl+Shift+E`。渲染设置、进度和 FFmpeg 帮助仍在当前 Web UI 中；页面只保留渲染相关功能。

公共 API 命名空间保持 `ADOFAI.EditorTweaks.Api.Rendering`，但程序集是 `ADOFAI.EditorTweaks.ChartRendering.dll`。不提供旧程序集转发层，调用方必须单独引用并安装本 Mod。

构建：

```powershell
dotnet build ADOFAI.EditorTweaks.ChartRendering.csproj -c Debug
dotnet build ADOFAI.EditorTweaks.ChartRendering.csproj -c Release
```

产物在 `out/` 和 `Build/`，部署目录为 `Mods/ADOFAI.EditorTweaks.ChartRendering/`。发行包包含 Web UI、FFmpeg 及渲染资源。
