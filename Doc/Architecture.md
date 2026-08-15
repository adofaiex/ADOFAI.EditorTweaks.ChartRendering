# ChartRendering 架构

入口 `ADOFAI.EditorTweaks.ChartRendering.Main.Load` 负责独立设置、渲染补丁和本地 Web UI。渲染任务链如下：

```text
Web UI / Api.Rendering
        -> ChartRenderService
        -> ChartRenderSession
        -> PlaybackController + VisualClock
        -> Camera/GameView capture + AudioRenderer
        -> FramePipeline -> FFmpeg
```

RenderInputGuard 和渲染补丁使用本 Mod 自己的 Harmony ID。视频背景同步只在渲染会话中启用，渲染结束后恢复 VideoPlayer 原始设置。
