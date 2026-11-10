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

- `ChartRenderVideoCodec` 枚举（`H264`、`Hevc`、`Av1`、`ProRes`、`Vvc`）和 `ChartRenderRequest.VideoCodec` 属性，默认 `H264`。不设置该属性的旧调用方行为不变。`CreateRequestFromCurrentSettings()` 会带上用户在设置页选择的编码格式。
- `Vvc`（H.266/VVC）是实验性格式：只有 CPU 编码（`libvvenc`），速度很慢，播放器和剪辑软件支持有限。它需要自带完整版 FFmpeg 或带 `libvvenc` 的自定义 FFmpeg；没有可用编码器时渲染会失败。`Vvc` 可以使用 MP4、MKV、MOV，像素格式固定为 10-bit。
- `ProRes` 始终输出 `.mov`，忽略 `VideoFormat`。
- `EncoderMode = Custom` 时固定使用 H.264，`VideoCodec` 不生效。
- `BitrateMbps` 为自动（0）时，画质只由 `Quality`（CRF）控制，不再限制码率。
- `ChartRenderRequest.ConstantBitrate`（默认 `false`）在 API v1 之后新增。为 `true` 时按 `BitrateMbps` 输出；`BitrateMbps = 0` 使用推荐码率。这是尽力而为的 CBR：NVENC 使用 `-rc cbr`，x264/x265 使用受 VBV 限制的近似 CBR，SVT-AV1 使用低延迟 CBR，libaom 使用固定码率；H.266/VVC 自动退回 VBR，ProRes 忽略该选项（码率由 profile 决定）。
- `ChartRenderHardwareEncoder` 枚举（`Auto`、`Nvidia`、`Intel`、`Amd`）和 `ChartRenderRequest.HardwareEncoder` 属性，默认 `Auto`。`Auto` 依次尝试 NVENC → AMD AMF → Intel QSV；指定的硬件编码器不可用时回退 CPU。H.264/HEVC/AV1 支持硬件编码，H.266/VVC 和 ProRes 不使用该字段。
- `ChartRenderHardwarePreset`（`p1`–`p7`）和 `ChartRenderCpuPreset`（x264/x265 的 `ultrafast`–`veryslow`，AV1 CPU 编码器按同等速度换算）枚举，以及 `ChartRenderRequest.HardwarePreset`、`CpuPreset` 属性。默认 `FollowEncoderMode`，即沿用 `EncoderMode` 选择的预设，旧调用方行为不变。`p1`–`p7` 是共享的速度档位：NVIDIA 直接使用 NVENC 预设，AMD AMF 和 Intel QSV 会换算成各自的 `speed`/`balanced`/`quality` 或 `veryfast`–`veryslow` 预设。`EncoderMode = Custom` 和 ProRes 不使用这两项。较慢的 CPU 预设会拖慢整个渲染，因为每一帧都要等编码器处理完。
