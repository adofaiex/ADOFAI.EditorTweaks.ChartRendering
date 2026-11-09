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

## v1 内的新增（向后兼容）

- `ChartRenderVideoCodec` 枚举（`H264`、`Hevc`、`Av1`、`ProRes`）和 `ChartRenderRequest.VideoCodec` 属性，默认 `H264`。不设置该属性的旧调用方行为不变。`CreateRequestFromCurrentSettings()` 会带上用户在设置页选择的编码格式。
- `ProRes` 始终输出 `.mov`，忽略 `VideoFormat`。
- `EncoderMode = Custom` 时固定使用 H.264，`VideoCodec` 不生效。
- `BitrateMbps` 为自动（0）时，画质只由 `Quality`（CRF）控制，不再限制码率。
- `ChartRenderHardwarePreset`（NVENC `p1`–`p7`）和 `ChartRenderCpuPreset`（x264/x265 的 `ultrafast`–`veryslow`，AV1 CPU 编码器按同等速度换算）枚举，以及 `ChartRenderRequest.HardwarePreset`、`CpuPreset` 属性。默认 `FollowEncoderMode`，即沿用 `EncoderMode` 选择的预设，旧调用方行为不变。`EncoderMode = Custom` 和 ProRes 不使用这两项。较慢的 CPU 预设会拖慢整个渲染，因为每一帧都要等编码器处理完。
