# ChartRendering API v1

公共命名空间保持：

```csharp
using ADOFAI.EditorTweaks.Api.Rendering;
```

调用方直接引用 `ADOFAI.EditorTweaks.ChartRendering.dll`，并单独安装 `ADOFAI.EditorTweaks.ChartRendering` Mod。旧的 `ADOFAI.EditorTweaks.dll` 不提供转发层。

主要入口：

```csharp
int version = ChartRenderApi.ApiVersion;
ChartRenderAvailability availability = ChartRenderApi.GetAvailability();
ChartRenderRequest request = ChartRenderApi.CreateRequestFromCurrentSettings();
ChartRenderStartResult start = ChartRenderApi.Start(request);
ChartRenderTask task = start.Task;
```

API v1 保留请求、任务、进度、结果、渲染枚举和错误码的现有形状。调用必须在 Unity 主线程执行；任务支持进度事件、完成事件和取消。
