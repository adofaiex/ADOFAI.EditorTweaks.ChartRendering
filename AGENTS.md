# 开发说明（给维护者和 AI 工具）

这份文件写给维护这个仓库的人，以及 Claude、DeepSeek、Codex 等 AI 编程工具。开始改代码前请完整读一遍。

## 项目是什么

ADOFAI.EditorTweaks.ChartRendering（简称 AETC）是《冰与火之舞》（A Dance of Fire and Ice）的 UMM（Unity Mod Manager）mod，用来把谱面渲染成视频。

- 原作者 lizi（memsys-lizi），现由离殇i（GitHub `lishangi688`）维护。
- 一部分功能移植自 sui 的 CheryTools（CT）中的 AET Recorder 模块，经 sui 同意。两者都是 GPL-3.0。
- Creplay（作者 potato）通过公共 API 调用本 mod 渲染回放视频。**不能破坏这个 API。**

## 不能改的东西

- mod Id、程序集名 `ADOFAI.EditorTweaks.ChartRendering.dll`、API 命名空间 `ADOFAI.EditorTweaks.Api.Rendering`。Creplay 按这些名字查找本 mod。
- 公共 API（`src/Api/Rendering/`）**只能新增，不能修改或删除**已有的类型、属性、枚举值。新增的请求字段必须有“保持旧行为”的默认值。
- 视频滤镜里的 `scale=out_color_matrix=bt709`（BT.709 色彩，社区贡献的修复）。

## 目录结构

```
src/
  Main.cs                     UMM 入口：Load / OnToggle
  Settings.cs                 用户设置（UMM 保存为 Settings.xml）
  Localization.cs             读取 Resources/localization.json（中/英）
  Api/Rendering/              公共 API：ChartRenderApi、请求/任务/进度/结果契约
  Features/ChartRendering/    渲染本体
    ChartRenderService.cs       任务调度；渲染请求 <-> 设置的双向转换
    ChartRenderSession.cs       一次渲染的完整流程（协程）
    ChartFrameCapture.cs        摄像机模式捕获 + 捕获方式工厂
    ChartOffscreenGameViewCapture.cs  游戏画面模式（离屏渲染）
    ChartGameViewFrameCapture.cs      游戏画面模式的退回方案（截屏）
    ChartRenderFramePipeline.cs GPU 回读 -> 编码队列
    FfmpegEncoder.cs            FFmpeg 视频编码、合成音频
    ChartEncoderCapabilities.cs 编码器检测：后台探测、保存结果、按显卡/FFmpeg 变化自动重测
    ChartUnityAudioCapture.cs   AudioRenderer 离线录音
    ChartRenderAudioBuffer.cs   渲染时切换 DSP 缓冲（会丢运行时音频，见下）
    ChartRenderUnscaledTime.cs  修正动画速度
    ChartRenderQuitGuard.cs     渲染中拦截关闭游戏
    ChartRenderDiagnostics.cs   每次渲染的日志 Workspace\Logs\render_*.log
  Features/WebUi/             本机 HTTP 服务（127.0.0.1 + 随机令牌）和设置页状态
  Features/RenderInputGuard/  渲染时屏蔽玩家输入
  Patching/PatchManager.cs    Harmony 补丁分组；一组失效不影响其他组
webui/                        设置页前端（React + Vite），构建后嵌入 mod
Resources/                    本地化、手册、FFmpeg 参数参考
scripts/                      下载 FFmpeg、打包
Doc/                          技术文档（Api/ 为公共 API 说明）
```

## 构建与测试

1. 把 `Local.props.example` 复制为 `Local.props`，填 `GameExePath`。**用工作区里的游戏副本开发，不要指向 Steam 安装目录。** `EnableModDeployment=true` 时构建会自动复制到该游戏的 `Mods` 目录。
2. `dotnet build ADOFAI.EditorTweaks.ChartRendering.csproj -c Debug`。首次构建会安装网页依赖并下载 FFmpeg。
3. 12 个 `MSB3245` 警告（找不到 Purchasing 等 Unity 模块）是正常的。新增的 C# 警告不正常。
4. **只看编译通过不够**，按改动类型选择验证方式：
   - **FFmpeg 参数**：用 `ThirdParty/FFmpeg/ffmpeg.exe` 在游戏外实测，参数要和代码生成的完全一致。用 `-f lavfi -i testsrc2=...` 生成测试画面。
   - **设置页**：`npm --prefix webui run dev`，打开不带 `token` 的地址即进入演示模式，不需要游戏。
   - **渲染行为**：需要维护者在游戏里实测，然后看 `Mods/ADOFAI.EditorTweaks.ChartRendering/Workspace/Logs/` 里最新的 `render_*.log`，以及游戏日志 `%USERPROFILE%\AppData\LocalLow\7th Beat Games\A Dance of Fire and Ice\Player.log`。

## 踩过的坑（改相关代码前必读）

### Unity 线程

- Unity API（包括 `Time.*`）只能在主线程调用。
- 网页请求跑在 .NET ThreadPool 上。需要主线程做的事，通过 `WebUiHost.ExecuteOnMain` 排队执行，有 5 秒超时。
- **渲染时主线程可能被编码器拖住**：编码器比渲染慢时，每帧都在等编码队列有空位。需要立刻生效的操作不能依赖主线程，比如取消渲染就是直接在 HTTP 线程设置取消标志。
- **不要用 `Process.BeginOutputReadLine` / `BeginErrorReadLine`**。在 Unity 的 Mono 下，它们会在进程运行期间一直占住 ThreadPool 线程，导致网页请求排队。曾经因此导致取消按钮无响应。请用独立的后台 `Thread` 同步读取。

### 音频

- `AudioSettings.Reset` 会丢掉所有运行时加载的 AudioClip 数据（自制谱面的歌曲、自定义音效、其他 mod 的音效如 Creplay 的按键音）。游戏内置资源不受影响。只在必须时调用，调用后要让游戏重新加载歌曲。曾经因为每次渲染结束都调用它，导致第二次起的渲染都没有音乐。
- 音乐由游戏自己的 `PlayScheduled` 播放，在离线 AudioRenderer 下工作正常，音画同步精确到采样。不要改成每帧手动播放。

### 渲染请求与设置

- 渲染时用的设置是 `ChartRenderService` 根据 `ChartRenderRequest` **重新创建**的，不是直接读用户设置。新增设置项时，`CreateRequestFromCurrentSettings`（设置 → 请求）和创建渲染设置（请求 → 设置）**两个方向都要写**，否则会被丢掉。CT 的编码格式选择就是因为漏了这一步而一直不生效。

### FFmpeg

- 自带的是 gyan.dev 的 **9.0.2 full build**（约 228 MB），含 `libsvtav1`、`libvvenc`、`libaom-av1` 等；`EnsureFfmpeg.ps1` 用 `curl.exe` 下载、`tar.exe` 解压 `.7z`，发现旧的 essentials 会自动替换。不要改回 essentials，否则 AV1 只能落到很慢的 libaom，H.266 也会消失。显卡编码器（NVENC/AMF/QSV）是否能用取决于用户的硬件和驱动。使用任何编码器前都要通过 `ChartEncoderCapabilities.IsAvailable` 探测，并准备退路。新增编码器时，把它加进 `ChartEncoderCapabilities.KnownEncoders`，同时更新设置页的 `encoderLabels`、`encoderOrder` 和 `predictEncoder`。
- AV1：`av1_nvenc` 需要 RTX 40 系或更新；更老的 NVIDIA 显卡会走 `libsvtav1`（full build 自带），再退到 `libaom-av1`。
- H.266/VVC：只有 `libvvenc`（CPU），只接受 10-bit 输入，1080p60 大约每秒数帧，播放器支持很差。设置页必须保持“实验性、不推荐”的提示。它没有 CBR 模式，`ConstantBitrate` 要退回 VBR。
- 恒定比特率：NVENC 是真正的 `-rc cbr`；x264/x265 是受 VBV 限制的近似 CBR（MP4 不支持 HRD filler）；SVT-AV1 需要 `rc=2:pred-struct=1` 低延迟模式；libaom 用 `minrate=maxrate`；ProRes 不适用。不要把它说成逐段完全恒定。
- 参数取值范围以实测为准。例如这版的 libaom `cpu-used` 只接受 0–8，文档写的 0–10 不适用；libvvenc 只接受 `yuv420p10le` 输入。
- 容器限制：MP4 不能装 ProRes，MOV 不能装 FLAC。VVC 的 MP4/MKV/MOV 都能用，MP4 自动写 `vvc1` 标签。
- 合成先写 `.part`，成功后再改名。

### 游戏版本

- 游戏会**静默更新**，版本号不变但程序集变了。补丁失效时先确认构建用的是当前版本的 `Assembly-CSharp.dll`。
- 需要了解游戏内部逻辑时，可以用 `ilspycmd` 反编译游戏程序集，输出放在临时目录，**不要提交进仓库**。
- 新的 Harmony 补丁如果可能因游戏更新失效，单独放一个补丁组，避免拖垮整个渲染功能。

### Windows 环境

- 在 Git Bash 里用 `sed` 修改含 Windows 路径的文本很容易出错：反斜杠会被当成转义，例如 `\L` 被当成“转小写”，`\r` 被当成回车。修改这类文件请用编辑工具，或者在 Python 里用 `chr(92)` 表示反斜杠。

## 处理需求的原则

**先判断可行性，而不是用户说什么就做什么。** 维护者不写代码，依赖 AI 实现，所以 AI 必须负责把关：

1. 收到需求，先查相关代码，搞清楚现状和根本原因，再回答。
2. 说清楚：能不能做、工作量多大、有什么副作用或风险（性能、兼容性、Creplay、游戏更新）、有没有更好的做法。
3. 需求有问题就直接指出，给出推荐方案，不要为了顺从而实现一个有害的功能。例如“每帧手动驱动 BGM”看似能修好没音乐的问题，实际会损害音画同步。
4. 现象要找到根本原因再修。例如“按空格播放后渲染没有音乐”，真正原因是渲染结束时重置了音频系统，和按空格无关。
5. 实现完成后说清楚哪些已经验证、哪些还没验证，需要维护者在游戏里测什么。不要把没验证的东西说成“已完成”。

## 提交规范

- 在工作分支上开发，每个独立改动一个提交，提交说明用英文写清楚“为什么”。
- 移植自 CT 的代码要在提交说明和文件注释里注明来源（`Ported from CheryTools AET Recorder (sui)`）。
- 推送和合并前要征得维护者同意。

## 相关文档

- `Doc/ChartRendering.md`：渲染流程的详细技术说明。
- `Doc/Api/ChartRendering.md`：公共 API 和新增字段。
- `Doc/BuildAndRelease.md`：构建与发布。
