# ChartRendering 构建

```powershell
dotnet build ADOFAI.EditorTweaks.ChartRendering.csproj -c Debug
dotnet build ADOFAI.EditorTweaks.ChartRendering.csproj -c Release
```

构建会运行 `webui/npm run build`，必要时下载 FFmpeg，并把 Web UI、FFmpeg、许可证和渲染资源复制到 `out/`。发行包在 `Build/`，部署到 `Mods/ADOFAI.EditorTweaks.ChartRendering/`。

## 本机配置

首次构建前，把 `Local.props.example` 复制为 `Local.props`，填入本机的 `GameExePath`。`Local.props` 已加入 `.gitignore`，不会被提交。

需要的工具：.NET SDK、Node.js（含 npm）。首次构建会自动安装 Web UI 依赖并下载 FFmpeg 9.0.2 full build（约 169 MB 的 `.7z`，解压后 `ffmpeg.exe` 约 228 MB）。构建脚本用 `curl.exe` 下载、Windows 自带的 `tar.exe` 解压；如果 `ThirdParty/FFmpeg/ffmpeg.exe` 还是旧的 essentials 版，会自动替换。发行包会因此明显变大。

如果游戏里同时装着 CheryTools，建议保持 `EnableModDeployment=false`，手动复制 `out/` 测试，避免两个渲染模块同时加载。

## 发布

1. 在功能分支上完成构建和测试，确认 `Info.json`、`src/Properties/AssemblyInfo.cs`、`webui/package.json` 和 `CHANGELOG.md` 的版本一致。
2. 推送功能分支，向 `main` 开 Pull Request，请求 `memsys-lizi` 审核。不要直接推送 `main`。
3. 审核通过后用 **Create a merge commit** 合并，保留每个独立改动；需要回退时对这个合并提交执行 Revert 即可恢复 `main`。
4. `main` 的 push 会触发 `.github/workflows/build.yml` 构建，但不会自动发布。确认构建通过后，到 Actions → Build Mod → Run workflow，选择 `main`、`publish_release=true`，工作流会读取 `Info.json` 的版本号并创建 GitHub Release。
5. 检查 Release 附件：应包含 `ADOFAI.EditorTweaks.ChartRendering-<版本>.zip`、`ThirdParty/FFmpeg/ffmpeg.exe`、根目录 `LICENSE` 和 `ThirdParty/FFmpeg/GPL-3.0.txt`。
6. 发布后发现问题：优先发一个修复版本（例如 `1.1.1`），不要复用已经发布的 tag。需要撤回时先删除或标记 Release，再按修复版本发布。
