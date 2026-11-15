# ChartRendering 设置

UMM 面板只录入 Web UI 快捷键，默认 `F8`（也可以用 F9、Ctrl+Shift+U 等）。渲染设置在 Web UI 中保存：

- `WebUiOpenMode`：打开设置页的方式（自动优先 Steam 覆盖层 / Steam 覆盖层 / 系统浏览器）。
- `WebUiAllowLan`：是否允许局域网访问（默认关闭；开启后显示带令牌的局域网地址）。

- 工作区、导出目录、宽高、帧率、CRF、码率和恒定比特率（CBR）。
- 摄像机/游戏画面来源、游戏画面捕获后端（离屏渲染 / 最终屏幕捕获）、回读格式、预览模式。
- 编码档位、视频编码格式（含实验性的 H.266/VVC）、硬件编码器（自动 / NVIDIA NVENC / Intel QSV / AMD AMF）、音频格式、视频格式和音画偏移。
- 结束尾巴、判定文字、选中段落和自定义 FFmpeg 合成参数。
- `ChartRenderCompatKeyViewers`：实验性的第三方按键显示兼容开关（默认开启；目前只在游戏画面模式适配 JipperResourcePack 的雨键/KPS 时钟）。

`Resources/localization.json` 只包含渲染状态和渲染器使用的文本。
