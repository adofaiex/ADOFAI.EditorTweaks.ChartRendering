# ChartRendering 构建

```powershell
dotnet build ADOFAI.EditorTweaks.ChartRendering.csproj -c Debug
dotnet build ADOFAI.EditorTweaks.ChartRendering.csproj -c Release
```

构建会运行 `webui/npm run build`，必要时下载 FFmpeg，并把 Web UI、FFmpeg、Mono.Cecil、许可证和渲染资源复制到 `out/`。发行包在 `Build/`，部署到 `Mods/ADOFAI.EditorTweaks.ChartRendering/`。
