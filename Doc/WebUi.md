# ChartRendering Web UI

启用 Mod 后会启动只监听 `127.0.0.1` 的本地 HTTP 服务。按 UMM 面板中的快捷键打开页面，默认是 `Ctrl+Shift+E`。

页面保留：

- 渲染兼容状态和补丁状态。
- 摄像机/游戏画面渲染设置。
- 开始、取消、实时进度、结果路径和诊断信息。
- FFmpeg 参数帮助。

接口包括 `GET /api/events`、`POST /api/settings`、`POST /api/settings/reset`、`POST /api/render/start` 和 `POST /api/render/cancel`。所有请求都需要启动时生成的令牌；HTTP 线程只负责通信，真正修改 Unity 状态的命令会排队到主线程。
