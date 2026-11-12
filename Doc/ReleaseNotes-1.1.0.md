# ADOFAI Editor Tweaks - Chart Rendering 1.1.0

这是 Chart Rendering 作为独立 Mod 发布的第一个大版本。下面列出了从 1.0.3 到 1.1.0 的全部更新。

本版本由离殇i 维护。部分渲染改进移植自 sui 的 CheryTools AET Recorder，已获作者同意。感谢 lizi 审核代码，也感谢参加内测的朋友们。

## 下载与安装

1. 下载下方附件 `ADOFAI.EditorTweaks.ChartRendering-1.1.0.zip`（约 85 MB，已自带 FFmpeg）。
2. 解压到游戏目录的 `Mods\`；已安装旧版的直接覆盖。建议先备份原来的 `Mods\ADOFAI.EditorTweaks.ChartRendering\` 文件夹。
3. 启动游戏，在编辑器里按 **Ctrl+Shift+E** 打开设置页。

需要 Unity Mod Manager 0.27.0 或更新版本。Creplay 等调用本 Mod 的 Mod 不需要更新（音频缓冲大于 256 时的音效问题见“已知问题”）。

## 升级前请注意

- **码率为 0（默认）时，改为按画质（CRF）编码。** 1.0.3 使用的是带上限的可变码率，所以同一张谱面的文件体积会和旧版不同。需要固定码率时，请填写视频码率，或打开新增的“恒定比特率”。
- 安装包增大到约 85 MB，因为自带的 FFmpeg 换成了完整版，才能支持更快的 AV1 编码和 H.266。

## 新增

### 编码格式
- 除 H.264 外，新增 **HEVC/H.265**、**AV1**、**ProRes 422 HQ** 三种格式。
- 新增 **H.266/VVC**（实验性，不推荐）：只能用 CPU 编码，非常慢，大多数播放器和网站都不支持，仅供尝鲜。

### 硬件编码
- 除 NVIDIA 外，新增 **AMD** 和 **Intel** 的独显、核显硬件编码。
- 默认自动选择（NVIDIA → AMD → Intel），也可以在“详细编码设置”里指定。指定的显卡不可用时，自动改用 CPU 编码。
- AV1 硬件编码需要 NVIDIA RTX 40 系、AMD RDNA 3、Intel Arc 或更新的显卡。更老的显卡会用 CPU 的 SVT-AV1 编码，速度比以前快很多。

### 编码器检测
- 总览页显示这台电脑能用哪些编码器。
- 渲染设置里显示这次渲染实际会使用哪个编码器，不能用的选项会变灰。
- 更换显卡或 FFmpeg 后自动重新检测；更新显卡驱动后请手动点“重新检测”。

### 画质与速度
- **恒定比特率（CBR）**：在高级设置中打开后，按填写的码率输出。码率填 0 时使用推荐码率。
- **编码预设**：“详细编码设置”里可以选择编码器（显卡 / CPU / 自定义），并选对应的速度预设：显卡 p1–p7，CPU ultrafast–veryslow。

### 画面
- **游戏画面模式支持高分辨率**：可以输出高于显示器的分辨率（例如在 1080p 屏幕上输出 4K），屏幕上只显示预览。

### 稳定性与诊断
- **关闭保护**：渲染中第一次关闭游戏会被拦截并提示，10 秒内再次关闭才会真正退出。
- **断线提示**：游戏关闭或崩溃时，设置页会显示提示，说明渲染可能已中断。
- **渲染日志**：每次渲染单独保存到 `Workspace\Logs\render_*.log`，保留最近 10 份，反馈问题时附上即可。
- **自定义 FFmpeg 路径**：可以使用自己的 `ffmpeg.exe`；路径无效时自动改用自带的 FFmpeg。

## 改进

- 进度条分成三段：画面捕获 0–90%，完成编码 90–92%，合成音频 92–100%。合成阶段会提示“请勿关闭游戏”。
- 视频先写到 `.part` 临时文件，完成后再改名。导出目录里正式名字的文件一定是完整的。
- 选择 ProRes 时自动改用 MOV，MOV 下的 FLAC 自动改为 ALAC（同为无损）。不兼容的选项会变灰，鼠标悬停可以看到原因。
- ProRes 编码速度提升约 3 倍，文件体积更合理。
- 渲染结束后自动回到编辑器，不需要再按 Esc。
- 取消渲染更可靠，取消后会删除没做完的视频。
- 减少了不必要的整帧画面复制，4K 渲染更快。
- 临时文件夹每次渲染都使用新的名字，取消后可以立刻开始下一次渲染；开始新的渲染时会清理残留超过 1 小时的旧临时文件夹。
- HEVC 视频使用 `hvc1` 标签，苹果设备可以直接播放。

## 修复

- 同一张谱面第二次渲染开始，视频里没有音乐。
- 视频里星球自转、彩虹星球、Miss 闪光、打击停顿、镜头后坐、文字装饰抖动的速度比游戏里快。
- 渲染过程中点“取消”没有反应，CPU 慢速预设下尤其明显。
- 合成音频阶段进度条停在 100% 不动；此时关闭游戏会留下损坏的视频。
- 选择 HEVC、AV1、ProRes 后，输出的仍然是 H.264。
- ProRes 渲染失败。
- MOV 格式搭配 FLAC 音频时合成失败。
- 音频缓冲大于 256 时，官方谱或 Bundle 谱渲染失败。
- 音频缓冲切换失败后，音频设置没有恢复。
- 自定义 FFmpeg 路径包含非法字符时，渲染直接报错。
- “Finalizing video” 阶段的文字没有翻译。

## 已知问题

- **AMD / Intel 硬件编码**还没有在所有显卡上验证。遇到问题时，可以在“详细编码设置”中改用 CPU，并请反馈显卡型号和驱动版本。
- **CBR** 只在 NVIDIA 和 AMD 显卡编码时是真正的恒定码率；CPU 编码只能做到近似恒定；H.266 不支持 CBR，会改用可变码率；ProRes 的码率由格式本身决定，CBR 对它无效。
- **H.266** 速度很慢（1080p60 大约每秒几帧），只建议短片段尝鲜。
- **音频缓冲大于 256** 时，渲染会临时切换音频设置。渲染后其他 Mod 自己加载的音效（例如 Creplay 的按键音）可能需要重新进入谱面才能恢复。
- 电脑上同时有多块显卡时，自动选择按 NVIDIA → AMD → Intel 的顺序，不会根据游戏实际使用的显卡切换，需要时请手动指定。

## 给 Mod 作者

公共 API（`ADOFAI.EditorTweaks.Api.Rendering`）只做了向后兼容的新增，旧调用方不需要改代码：

- `ChartRenderRequest.VideoCodec` 和 `ChartRenderVideoCodec`（`H264`、`Hevc`、`Av1`、`ProRes`、`Vvc`）
- `ChartRenderRequest.HardwareEncoder` 和 `ChartRenderHardwareEncoder`（`Auto`、`Nvidia`、`Intel`、`Amd`）
- `ChartRenderRequest.HardwarePreset` / `CpuPreset` 和对应的枚举
- `ChartRenderRequest.ConstantBitrate`
- 游戏画面模式现在会使用请求里的 `Width` / `Height`；宽高为 0 时仍按窗口尺寸截屏

请注意“码率为 0 时改为 CRF”的成片差异。详见 `Doc/Api/ChartRendering.md`。

## 反馈问题

请在 Issues 中附上：显卡型号和驱动版本、游戏版本、选择的编码格式和容器、对应的 `render_*.log`、`Player.log`（`%USERPROFILE%\AppData\LocalLow\7th Beat Games\A Dance of Fire and Ice\Player.log`），以及复现步骤。

## 许可与第三方源码

本 Mod 以 GNU GPL v3.0 发布。自带的 `ffmpeg.exe` 是 gyan.dev 的 9.0.2 GPLv3 完整版，静态链接了以下库，对应源码地址如下：

- FFmpeg 9.0.2：https://github.com/FFmpeg/FFmpeg/archive/946fcce07b.zip
- x264：https://code.videolan.org/videolan/x264
- x265：https://bitbucket.org/multicoreware/x265_git
- libaom：https://aomedia.googlesource.com/aom
- SVT-AV1：https://gitlab.com/AOMediaCodec/SVT-AV1
- VVenC：https://github.com/fraunhoferhhi/vvenc
- rav1e：https://github.com/xiph/rav1e
- 构建来源：https://github.com/GyanD/codexffmpeg

更多许可信息见安装包内的 `LICENSE` 和 `ThirdParty/FFmpeg/`。
