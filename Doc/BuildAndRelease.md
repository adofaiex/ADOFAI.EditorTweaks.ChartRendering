# ChartRendering 构建

```powershell
dotnet build ADOFAI.EditorTweaks.ChartRendering.csproj -c Debug
dotnet build ADOFAI.EditorTweaks.ChartRendering.csproj -c Release
```

构建会运行 `webui/npm run build`，必要时下载 FFmpeg，并把 Web UI、FFmpeg、许可证和渲染资源复制到 `out/`。发行包在 `Build/`，部署到 `Mods/ADOFAI.EditorTweaks.ChartRendering/`。

## 本机配置

首次构建前，把 `Local.props.example` 复制为 `Local.props`，填入本机的 `GameExePath`。`Local.props` 已加入 `.gitignore`，不会被提交。

需要的工具：.NET SDK、Node.js（含 npm）。首次构建会自动安装 Web UI 依赖并下载 FFmpeg。

如果游戏里同时装着 CheryTools，建议保持 `EnableModDeployment=false`，手动复制 `out/` 测试，避免两个渲染模块同时加载。
