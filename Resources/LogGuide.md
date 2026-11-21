# AETC 诊断包：日志分析指南

> **给用户**：把整个诊断包（zip）发给 AI（ChatGPT、Claude、DeepSeek、Kimi 等）或开发者就行，这份文档已经在包里。可以直接对 AI 说：“请按诊断包里的《日志分析指南》分析问题，我遇到的问题是：……”。把你看到的现象（比如“视频没有声音”“渲染到 92% 卡住”“雨键不显示”）写清楚，AI 才能对上号。
>
> **给 AI**：你是 ADOFAI 谱面渲染 Mod（AETC，ADOFAI Editor Tweaks - Chart Rendering）的排障助手。请**只根据诊断包里的日志内容**下结论，每个结论都要引用对应的日志原文（带文件名）。日志里找不到依据的就说“日志里看不出来”，不要猜。最后用普通玩家能看懂的话给出可操作的建议。

## 一、诊断包里有什么

| 文件 | 内容 | 什么时候重点看 |
|---|---|---|
| `01-系统与设置.txt` | Mod/游戏版本、显卡和驱动、编码器检测结果、已装 Mod、AETC 设置 | 每次都先看 |
| `02-渲染日志-*.log` | 最近 3 次渲染的详细过程（`render_时间.log`，1 是最新的） | 渲染失败、画面/声音/雨键问题 |
| `03-游戏日志-Player.log` | Unity 游戏日志，从最早那次渲染开始截取 | 渲染日志中断、报错原因不明 |
| `04-上一次游戏的日志-Player-prev.log` | 上一次启动游戏的日志 | **游戏崩溃或闪退**（重开后崩溃记录在这里） |

隐私处理：用户名路径换成了 `%USERPROFILE%`、`<user>`，网页令牌和设备密钥显示为 `<hidden>`。这些不是问题。

## 二、先确认这几件事

1. **版本**（`01`）：`Mod:`、`Game:`。游戏会静默更新，补丁失效时先看 Player.log 里有没有 Harmony 报错。
2. **这次渲染有没有跑完**：最新渲染日志末尾有没有 `Render complete:`。
   - 有：渲染成功，问题在画面内容、声音或体积，看第四节。
   - 没有、日志突然中断：看日志最后几行，再到 `03` 找同一时间的报错；游戏直接退出的看 `04`。
3. **用的是什么**：搜 `FFmpeg video args:`，这一行有编码格式 `codec=`、实际编码器 `hardwareEncoder=`（`cpu` 表示 CPU 编码）、码率方式 `rate=`、完整的 FFmpeg 参数。

## 三、渲染流程和对应日志（按时间顺序）

1. `Chart render diagnostics started` → 日志开始。
2. `Player.log offset at render start:` → 导出诊断包时据此截取 Player.log。
3. `Diagnostic mode: on/off` → 诊断模式（详细日志）是否打开。
4. `Removed N leftover temp folder(s)` → 清理了之前残留的临时文件，正常。
5. `Capture source locked for this render: camera/game-view backend=offscreen/screen requested=宽x高@帧率fps.`
   - `camera`：摄像机渲染，录不到按键显示 Mod。
   - `game-view` + `offscreen`：离屏渲染，支持 2K/4K；`screen`：最终屏幕捕获，分辨率跟随窗口。
6. `Render audio DSP buffer ...` → 音频缓冲。`set temporarily` 表示临时改成 256，渲染后会 `restored`。之后可能有 `Level song reloaded after audio buffer switch`（正常）。
7. `FFmpeg is not installed; downloading ...` / `FFmpeg is ready.` → 首次使用会自动下载 FFmpeg。
8. `Render estimate: N frames` → 预计总帧数。
9. `FFmpeg video args: ...` → 编码器和参数（见上）。
10. 按键显示 Mod（只在游戏画面模式）：见第五节。
11. `AudioRenderer capture started. sampleRate=` → 开始录音。
12. `Level end detected at frame N` → 谱面播放结束。
13. `Audio capture complete. samples=` → 录音结束。
14. `Muxing captured audio.` / `FFmpeg mux args:` → 合成声音和画面（进度 92%–100%）。
15. `Output size: X for Y s, average video Z Mbps (P% of the C Mbps cap)` → 成片大小、平均码率、用了上限的百分之多少。
16. `Render complete: 路径` → 成功。

进度条：0–90% 捕获画面，90–92% 完成编码，92–100% 合成音频。

## 四、常见问题对照表

| 现象 | 日志里找什么 | 原因和建议 |
|---|---|---|
| 视频没有声音 | `AudioRenderer produced no usable samples`、`Frame rate ... is too high for audio capture` | 帧率太高（48 kHz 下超过约 187 帧/秒时每帧不足一个音频缓冲）。降到 180 以下 |
| 视频没有音乐、只有音效 | `Render audio DSP buffer`、`Level song reloaded`、Player.log 里的音频报错 | 旧版本问题已修复；先确认 Mod 是最新版 |
| 合成失败，提示空间不足 | `FFmpeg audio mux failed with exit code -28`、`No space left on device` | 硬盘满了。成片加临时文件大约需要两倍成片大小的空间。清理硬盘，或降一档画质 |
| 合成失败，其他退出码 | `FFmpeg audio mux failed with exit code`，后面跟着 FFmpeg 的报错原文 | 看报错原文；常见原因：自定义合成参数写错、容器和音频格式不兼容（MOV 不能装 FLAC，会自动改成 ALAC） |
| 视频编码失败 | `FFmpeg video process is not running`、`FFmpeg writer failed` + stderr | 显卡编码器启动失败或驱动问题。到“画质与编码”改用 CPU 编码，并在总览页“重新检测” |
| AV1 很慢 | `hardwareEncoder=cpu` 且 `codec=av1` | 显卡不支持 AV1 编码（需要 RTX 40 / RDNA 3 / Intel Arc），用的是 CPU，属于正常 |
| 视频体积很大 | `rate=`：`tier lossless uncapped`、`crf N`（自定义且码率为 0）、`cbr` | 近似无损和自定义 CRF 没有上限。改用“标准”档 |
| 体积超过预期 | `Output size: ... (P% of the C Mbps cap)` | P 接近 100% 说明画面很复杂（粒子多），已经被上限兜住；想更小就降一档 |
| 渲染卡在开始 | `FFmpeg is not installed; downloading` 之后没有 `FFmpeg is ready.` | FFmpeg 下载失败（网络问题）。到工具页手动下载，或下载完整版安装包 |
| 取消后又能渲染吗 | `Cancel requested during`、`Deleted partial output after cancel`、`Background worker did not stop within` | 最后一条表示 FFmpeg 没及时退出，临时文件下次启动会自动清理 |
| 动画速度在视频里过快 | Player.log 里补丁组 `RenderVisualTime` 失败 | 游戏更新导致动画速度修正失效，需要更新 Mod |
| 渲染画面里有多余的东西或缺东西 | `Offscreen capture took canvas 名字 (mode=...)` | 离屏渲染接管了哪些界面。缺东西时试试“最终屏幕捕获”对照 |
| GPU 回读警告 | `Pending GPU readbacks were slow or failed` | 偶尔出现无害；频繁出现说明显卡负载过高 |

## 五、按键显示 Mod（JRP / JKV / Quartz）

只在**游戏画面渲染**且打开“兼容其他按键显示 Mod”时生效。摄像机渲染录不到按键显示。

**JipperResourcePack（JRP）**

| 日志 | 含义 |
|---|---|
| `Jipper KeyViewer compatibility adapter ready` | 时钟适配成功，雨键/KPS 跟随视频时间 |
| `Jipper KeyViewer compatibility skipped: ...` | 没适配上，后面写了原因（没装、版本不兼容） |
| `JRP_PREWARM pools=8 normal=... ghost=...` | 离屏渲染前预先创建了雨条，正常 |
| `JRP_PREWARM skipped: JRP KeyViewer is not loaded at render start.` | 开始渲染时 JRP 还没加载完（JALib 异步加载）。后面应该出现 `JRP rain pools appeared during an offscreen render` 和 `JRP_LATE_PREWARM` |
| `JRP_POOL_TOPUP` | 渲染中雨条不够，临时补充，正常 |
| `rainPoolAllocations=N`（在 `Jipper KeyViewer render clock ended` 那行） | **必须是 0**。大于 0 表示有 N 条雨条是在离屏画面下新建的，它们会一直不可见。重启游戏可恢复 |
| `Jipper KeyViewer render clock ended ... handoff=continuous` | 渲染结束，正常交接回真实时间 |

**JipperKeyViewer（JKV）**：`JKV compatibility adapter ready. version=... methods=N`（N 是被调整的方法数）；结束行 `stopwatchReads` 应该接近总帧数。`timeReads=0` 也正常（默认布局只用另一种时间）。

**Quartz**：`Quartz compatibility found KvClock in ...` 后应有 `Quartz render clock enabled (clock only, input unchanged, focus kept).`；`skipped` 表示没装或版本不兼容；`another replay driver is using Quartz` 表示别的回放渲染器正在接管，AETC 不会抢。

已知限制：反复开关按键显示 Mod 或来回切换捕获方式后，偶尔会有雨键缺失，重启游戏可恢复。AETC 自带的自动补打不会让按键显示 Mod 显示按键，需要真实输入或 Creplay 回放。

## 六、画质档位和体积（`rate=`）

| `rate=` | 含义 |
|---|---|
| `tier small/standard/high cap N M` | 按画质编码，码率上限 N Mbps（推荐的默认方式）|
| `tier lossless uncapped` | 近似无损，不限码率，文件可能很大 |
| `crf N` | 自定义画质，没有码率上限 |
| `vbr N M` | 自定义码率 |
| `cbr N M` | 恒定码率 |
| `prores` | ProRes，码率由格式决定，体积很大 |

上限按 YouTube 推荐码率计算（1080p60 = 12 Mbps，标准档是 2 倍即 24 Mbps；HEVC×0.7、AV1×0.6、H.266×0.5）。

## 七、诊断模式

工具页可以打开“诊断模式”。打开后：
- 渲染日志每 600 帧多记一行 `SAMPLE` 状态，每 3000 帧记一次内存；
- 渲染完**不删除临时文件**（`Diagnostic mode: temp files kept in ...`），方便检查原始视频和音频。用完记得关掉，并在工具页“清理临时文件”。

## 八、给开发者

- 代码仓库：https://github.com/adofaiex/ADOFAI.EditorTweaks.ChartRendering
- 渲染流程说明：仓库 `Doc/ChartRendering.md`
- 日志文件原始位置：`Mods\ADOFAI.EditorTweaks.ChartRendering\Workspace\Logs\render_*.log`，游戏日志 `%USERPROFILE%\AppData\LocalLow\7th Beat Games\A Dance of Fire and Ice\Player.log`
