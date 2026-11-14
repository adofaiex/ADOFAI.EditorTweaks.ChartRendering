# ADOFAI Editor Tweaks - Chart Rendering

《冰与火之舞》（A Dance of Fire and Ice）的谱面视频渲染 Mod：在编辑器里一键把谱面渲染成视频，画面和音乐逐帧对齐，不受电脑卡顿影响。

原作者 lizi（memsys-lizi），现由离殇i 维护。部分功能移植自 sui 的 CheryTools AET Recorder（经作者同意）。

## 安装

1. 安装 [Unity Mod Manager](https://www.nexusmods.com/site/mods/21)（0.27.0 或更新），并为游戏启用。
2. 下载 [Releases](https://github.com/adofaiex/ADOFAI.EditorTweaks.ChartRendering/releases) 里的 `ADOFAI.EditorTweaks.ChartRendering-版本号.zip`。
3. 解压到游戏目录的 `Mods\` 下，得到 `Mods\ADOFAI.EditorTweaks.ChartRendering\`。升级时直接覆盖。

安装包已经自带 FFmpeg，不需要另外下载。

## 使用

1. 启动游戏，打开编辑器并加载谱面。
2. 按 **F8** 打开设置页（默认；快捷键可在 UMM 面板里改，也可以用 F9、Ctrl+Shift+U 等避免和编辑器快捷键冲突）。
3. 在“谱面渲染”页设置分辨率、帧率、编码格式，点“开始渲染”。
4. 渲染时游戏会自动播放谱面，进度显示在设置页里，随时可以取消。完成后视频保存在导出目录，游戏自动回到编辑器。

> 渲染期间请不要关闭游戏。合成阶段（进度 92% 以后）关闭会导致视频不完整；误点关闭时 Mod 会拦截一次。

### 画面捕获

- **摄像机渲染（推荐）**：只录谱面画面，最稳定。
- **游戏画面渲染**：包含游戏内界面和额外摄像机效果，可以输出高于显示器的分辨率，屏幕上只显示预览。

### 编码格式怎么选

| 格式 | 适合 | 说明 |
|---|---|---|
| H.264 | 上传视频网站、发给别人 | 兼容性最好，默认选项 |
| HEVC / H.265 | 想要更小的文件 | 同画质体积更小，老设备可能放不了 |
| AV1 | 体积最小 | RTX 40 / RDNA 3 / Intel Arc 及以上用显卡编码，更老的显卡用 CPU（较慢） |
| ProRes 422 HQ | 导入剪辑软件再加工 | 文件很大，固定输出 MOV |
| H.266 / VVC | 尝鲜 | **实验性，不推荐**：只能 CPU 编码，非常慢，大多数播放器打不开 |

支持 NVIDIA、AMD、Intel 的显卡/核显硬件编码；没有可用的硬件编码器时自动改用 CPU。总览页可以看到这台电脑能用哪些编码器，渲染设置里会显示实际会用哪一个。

### 画质

- **码率填 0（默认）**：按 CRF 控制画质，数字越小越清晰、文件越大，18 基本看不出损失。
- **填写码率**：按目标码率编码（可变码率）。
- **恒定比特率（CBR）**：在高级设置里打开，码率尽量保持恒定，适合对码率有要求的平台。

## 常见问题

**渲染很慢？**
CPU 编码、慢速预设、H.266 都会明显拖慢渲染。优先使用默认的“自动均衡”档位，让显卡编码。

**视频没有声音或音乐？**
请先更新到最新版本。如果仍然出现，请附上日志反馈（见下）。

**升级后视频体积和旧版不一样？**
1.1.0 起码率为 0 时改为按画质（CRF）编码，体积会随画面复杂度变化。需要固定码率时填写码率或打开 CBR。

**音频缓冲改大后，渲染完其他 Mod 的音效没了？**
音频缓冲大于 256 时，渲染会临时切换音频设置，其他 Mod 自己加载的音效（例如 Creplay 的按键音）可能需要重新进入谱面才能恢复。

**AMD / Intel 显卡编码报错？**
在“详细编码设置”里把编码器改为 CPU 可以绕过，并请反馈显卡型号和驱动版本。

## 反馈问题

请在 [Issues](https://github.com/adofaiex/ADOFAI.EditorTweaks.ChartRendering/issues) 附上：

- 显卡型号、驱动版本、游戏版本，以及选择的编码格式和容器；
- 渲染日志：`Mods\ADOFAI.EditorTweaks.ChartRendering\Workspace\Logs\` 里对应时间的 `render_*.log`；
- 游戏日志：`%USERPROFILE%\AppData\LocalLow\7th Beat Games\A Dance of Fire and Ice\Player.log`；
- 复现步骤。

## 给其他 Mod 作者

本 Mod 提供公共渲染 API（命名空间 `ADOFAI.EditorTweaks.Api.Rendering`，程序集 `ADOFAI.EditorTweaks.ChartRendering.dll`），Creplay 用它渲染回放。API 只做向后兼容的新增，详见 [Doc/Api/ChartRendering.md](Doc/Api/ChartRendering.md)。

## 开发

```powershell
copy Local.props.example Local.props   # 填入本机游戏路径 GameExePath
dotnet build ADOFAI.EditorTweaks.ChartRendering.csproj -c Debug
```

首次构建会安装网页依赖并下载 FFmpeg。构建与发布见 [Doc/BuildAndRelease.md](Doc/BuildAndRelease.md)，技术文档索引见 [Doc/README.md](Doc/README.md)，更新内容见 [CHANGELOG.md](CHANGELOG.md)。

## 许可

GNU GPL v3.0，见 [LICENSE](LICENSE)。自带的 FFmpeg 为 GPL 版本，许可和源码获取方式见 `ThirdParty/FFmpeg/`。
