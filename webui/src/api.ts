import type { SettingsState, WebUiState } from "./types";

const queryToken = (): string => {
  return new URLSearchParams(window.location.search).get("token") ?? "";
};

export const isLocalPreview = (): boolean => {
  return window.location.protocol === "file:" || !queryToken();
};

const apiUrl = (path: string): string => {
  const token = queryToken();
  return `${path}${path.includes("?") ? "&" : "?"}token=${encodeURIComponent(token)}`;
};

export const localDocumentUrl = (path: string): string => apiUrl(path);

export async function postJson<T>(path: string, body: unknown = {}): Promise<T> {
  const response = await fetch(apiUrl(path), {
    method: "POST",
    headers: { "Content-Type": "application/json" },
    body: JSON.stringify(body),
  });
  const payload = (await response.json()) as T & { error?: string };
  if (!response.ok) {
    throw new Error(payload.error ?? `Request failed: ${response.status}`);
  }
  return payload;
}

export function createEventSource(onState: (state: WebUiState) => void): EventSource {
  const events = new EventSource(apiUrl("/api/events"));
  events.addEventListener("state", (event) => {
    try {
      onState(JSON.parse((event as MessageEvent).data) as WebUiState);
    } catch {
      // A malformed event must not break the EventSource reconnect loop.
    }
  });
  return events;
}

export function createMockState(): WebUiState {
  const settings: SettingsState = {
    WebUiOpenHotkey: "F8",
    WebUiOpenMode: "auto",
    WebUiAllowLan: false,
    ChartRenderWorkspaceDirectory: "D:/ADOFAI/Workspace",
    ChartRenderExportDirectory: "D:/ADOFAI/Exports",
    ChartRenderWidth: 1920,
    ChartRenderHeight: 1080,
    ChartRenderFps: 60,
    ChartRenderCrf: 18,
    ChartRenderBitrateMbps: 0,
    ChartRenderConstantBitrate: false,
    ChartRenderPreset: "veryfast",
    ChartRenderEncoderMode: "auto-balanced",
    ChartRenderCaptureFormat: "rgba",
    ChartRenderCaptureSource: "camera",
    ChartRenderPreviewMode: "full",
    ChartRenderAudioFormat: "aac",
    ChartRenderVideoFormat: "mp4",
    ChartRenderCompletionTailSeconds: 5,
    ChartRenderAudioSyncOffsetMs: 0,
    ChartRenderShowHitJudgments: true,
    ChartRenderUseSelectedRange: false,
    ChartRenderCustomMuxArgs: "",
    ChartRenderVideoCodec: "h264",
    ChartRenderHardwareEncoder: "auto",
    ChartRenderHardwarePreset: "auto",
    ChartRenderCpuPreset: "auto",
    ChartRenderDetailedEncoderSettings: false,
    FfmpegCustomPath: "",
  };

  const names = ["渲染输入保护", "谱面渲染"];
  return {
    server: { connected: false, version: "演示状态", lanEnabled: false, lanUrl: "", lanUrls: [] },
    compatibility: { gameVersion: "1.3.2", editorVersion: "1.3.2", modVersion: "1.1.0" },
    patches: names.map((name, index) => ({
      id: `mock-${index}`,
      name,
      description: "与当前游戏版本兼容",
      state: "active",
      patchCount: 1,
      reason: "",
    })),
    patchSummary: { active: 2, total: 2, registrationError: false },
    settings,
    encoders: {
      status: "ready",
      detectedAt: "2026-11-10 20:00",
      gpu: "NVIDIA GeForce RTX 3070",
      results: { h264_nvenc: true, hevc_nvenc: true, av1_nvenc: false, h264_amf: false, hevc_amf: false, av1_amf: false, h264_qsv: false, hevc_qsv: false, av1_qsv: false, libx264: true, libx265: true, libsvtav1: true, "libaom-av1": true, libvvenc: true, prores_aw: true },
    },
    render: {
      active: false,
      taskId: "",
      state: "Idle",
      cancelRequested: false,
      writingOutput: false,
      quitRefused: false,
      captureSource: "Camera",
      outputPath: "",
      message: "",
      progress: {
        value: 0,
        writtenFrames: 0,
        totalFrames: 1,
        duplicateFrames: 0,
        duplicateRatio: 0,
        processingFramesPerSecond: 0,
        estimatedRemaining: "00:00:00",
        stage: "等待渲染",
        detail: "从 Mod 页面启动渲染后，进度会实时显示在这里。",
        encoderName: "",
        memoryBudget: "",
        queueBudget: "",
      },
    },
  };
}
