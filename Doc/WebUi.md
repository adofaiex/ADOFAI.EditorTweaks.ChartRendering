# ChartRendering Web UI

启用 Mod 后会启动只监听 `127.0.0.1` 的本地 HTTP 服务。按 UMM 面板中的快捷键打开页面，默认是 `F8`（也可以用 F9、Ctrl+Shift+U 等，避免和编辑器快捷键冲突）。

打开方式默认优先尝试 Steam 覆盖层，失败时回退系统浏览器；可以在 Web 设置页固定为“Steam 覆盖层”或“系统浏览器”，避免有时打开 Edge、有时打开 Steam 覆盖层。

局域网访问默认关闭。在 Web 设置页开启“允许局域网访问”后，Mod 会在固定端口（默认 18200，可改；被占用时自动用附近的端口）启动一个 TCP 转发，把局域网连接转发到本地 HTTP 服务（不需要管理员权限）。需要外网访问时建议用 Tailscale / ZeroTier 等组网工具，不要把端口直接暴露到公网。

## 访问控制（`WebUiAccess`）

- **访问令牌**：每次启动随机生成，放在 Mod 打开的链接和二维码里，拥有完整控制权。日志里不写令牌。
- **记住此设备**：带令牌打开页面时，服务端下发 `aetc_device` cookie（HttpOnly、SameSite=Strict、一年），值是保存在设置里的 `WebUiDeviceKey`。之后这台设备不带令牌也能访问，游戏重启后仍然有效；页面会把令牌从地址栏去掉，书签就是 `http://<电脑IP>:<端口>/`。
- **只读链接**：`/?view=<WebUiViewKey>`，下发 `aetc_view` cookie。服务端拒绝它的所有写操作（403），并通过 `WebUiStateBuilder.ForViewer` 发给它不含局域网地址、令牌、密钥和本地路径的状态。
- **忘记所有设备**：`POST /api/access/forget` 重新生成两个密钥，所有已记住的设备和只读链接失效。
- 用 cookie 认证的写请求必须是 `application/json`，其他网站的表单无法提交（跨站 JSON 需要预检，服务端不响应 CORS）。
- 没有任何凭据时页面显示“需要从游戏里打开”的说明，而不是演示数据。

## 页面

导航：总览、渲染、输出、画质与编码、画面与音频、网页与连接、工具与诊断。每页顶部的状态条显示设置摘要、进度和开始/取消；渲染页有完整的进度卡片，所以状态条在那里只显示摘要。手机上导航改为横向滚动。

- **画质档位**：表格来自 `ChartRenderQualityTiers.BuildState`，大小估算在前端 `estimate.ts`（同一套公式，演示模式也能用）。“通常约”= 上限 × 学到的比例（没有记录时 60%），“最多约”= 上限 × 1.05，都加上音频。
- **文件夹选择**：浏览器拿不到所选文件夹的完整路径（`showDirectoryPicker` 只给句柄），原生对话框又会被全屏游戏挡住、在平板上看不到，所以由 Mod 列出文件夹（`POST /api/folders/list`），只列名字。`POST /api/folders/open` 只接受固定目标（导出目录、工作区、日志、诊断包、上次成片）。
- **诊断包**：`POST /api/diagnostics/export` 在主线程收集系统信息，在 HTTP 线程写 zip（`Workspace\Diagnostics`，保留 5 个）；`GET /api/diagnostics/download?file=` 下载到当前设备。
- **分享码**：`AETC1:` + base64(JSON)，只含渲染设置（`SHARE_KEYS`），导入走普通的 `POST /api/settings`。

## 接口

`GET /api/events`（SSE）、`GET /api/session`、`GET /api/diagnostics/download`；`POST /api/settings`、`/api/settings/reset`、`/api/render/start`、`/api/render/cancel`、`/api/encoders/detect`、`/api/ffmpeg/install`、`/api/ffmpeg/cancel-auto`、`/api/access/forget`、`/api/workspace/clean`、`/api/folders/list`、`/api/folders/open`、`/api/diagnostics/export`。HTTP 线程只负责通信，真正修改 Unity 状态的命令会排队到主线程（取消渲染例外，直接在 HTTP 线程设置标志）。
