# ChartRendering Web UI

启用 Mod 后会启动只监听 `127.0.0.1` 的本地 HTTP 服务。按 UMM 面板中的快捷键打开页面，默认是 `F8`（也可以用 F9、Ctrl+Shift+U 等，避免和编辑器快捷键冲突）。

打开方式默认优先尝试 Steam 覆盖层，失败时回退系统浏览器；可以在 Web 设置页固定为“Steam 覆盖层”或“系统浏览器”，避免有时打开 Edge、有时打开 Steam 覆盖层。

局域网访问默认关闭。在 Web 设置页开启“允许局域网访问”后，Mod 会在本机另一个端口启动一个 TCP 转发，把局域网连接转发到本地 HTTP 服务（不需要管理员权限）。页面会显示 `http://<局域网IP>:<端口>/?token=...` 的地址，同一局域网的手机/平板可以直接打开。地址包含控制令牌，请勿公开；需要外网访问时建议用 Tailscale / ZeroTier 等组网工具，不要把端口直接暴露到公网。

页面保留：

- 渲染兼容状态和补丁状态。
- 摄像机/游戏画面渲染设置。
- 开始、取消、实时进度、结果路径和诊断信息。
- FFmpeg 参数帮助。

接口包括 `GET /api/events`、`POST /api/settings`、`POST /api/settings/reset`、`POST /api/render/start` 和 `POST /api/render/cancel`。所有请求都需要启动时生成的令牌；HTTP 线程只负责通信，真正修改 Unity 状态的命令会排队到主线程。
