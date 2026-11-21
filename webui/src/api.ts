import type { SettingsState, WebUiState } from "./types";

// The access token arrives in the link the mod opens (or the QR code). It is kept in memory and
// removed from the address bar once the mod has remembered this device with a cookie, so a bookmark
// is a plain http://<pc>:<port>/ that keeps working after the game restarts.
const initialParams = new URLSearchParams(window.location.search);
const accessToken = initialParams.get("token") ?? "";

export type SessionMode = "full" | "view" | "unauthorized" | "demo";

const apiUrl = (path: string): string => {
  if (!accessToken) return path;
  return `${path}${path.includes("?") ? "&" : "?"}token=${encodeURIComponent(accessToken)}`;
};

export const localDocumentUrl = (path: string): string => apiUrl(path);

export const diagnosticsDownloadUrl = (file: string): string => apiUrl(`/api/diagnostics/download?file=${encodeURIComponent(file)}`);

// Asks the mod who we are. A file:// page, or the Vite dev server (which answers with HTML), is
// the demo preview.
export async function detectSession(): Promise<SessionMode> {
  if (window.location.protocol === "file:") return "demo";
  try {
    const response = await fetch(apiUrl("/api/session"), { credentials: "same-origin" });
    const type = response.headers.get("content-type") ?? "";
    if (!type.includes("application/json")) return "demo";
    if (response.status === 401) return "unauthorized";
    const body = (await response.json()) as { mode?: string };
    const mode: SessionMode = body.mode === "view" ? "view" : "full";
    if (initialParams.has("token") || initialParams.has("view")) {
      window.history.replaceState(null, "", window.location.pathname);
    }
    return mode;
  } catch {
    return "demo";
  }
}

export async function postJson<T>(path: string, body: unknown = {}): Promise<T> {
  const response = await fetch(apiUrl(path), {
    method: "POST",
    credentials: "same-origin",
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
    ChartRenderQualityTier: "standard",
    ChartRenderDiagnosticMode: false,
    WebUiLanPort: 18200,
    ChartRenderBitrateMbps: 0,
    ChartRenderConstantBitrate: false,
    ChartRenderPreset: "veryfast",
    ChartRenderEncoderMode: "auto-balanced",
    ChartRenderCaptureFormat: "rgba",
    ChartRenderCaptureSource: "camera",
    ChartRenderGameViewBackend: "offscreen",
    ChartRenderPreviewMode: "full",
    ChartRenderAudioFormat: "aac",
    ChartRenderVideoFormat: "mp4",
    ChartRenderCompletionTailSeconds: 5,
    ChartRenderAudioSyncOffsetMs: 0,
    ChartRenderShowHitJudgments: true,
    ChartRenderCompatKeyViewers: true,
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
    server: { connected: false, version: "演示状态", lanEnabled: true, lanUrl: "http://192.168.1.23:18200/?token=demo", lanUrls: ["http://192.168.1.23:18200/?token=demo"], lanPort: 18200, lanViewUrls: ["http://192.168.1.23:18200/?view=demo"] },
    compatibility: { gameVersion: "3.4.0", gameRelease: 150, editorVersion: "3.4.0", modVersion: "1.1.2" },
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
    ffmpeg: {
      status: "ready",
      version: "AETC N-129000",
      variant: "ADOFAITools 定制版",
      progress: 1,
      downloadedBytes: 13279311,
      totalBytes: 13279311,
      installedBytes: 46367232,
      countdown: -1,
      source: "ADOFAITools",
      message: "",
      path: "Mods/ADOFAI.EditorTweaks.ChartRendering/ThirdParty/FFmpeg/ffmpeg.exe",
      sha256: "90e8f8d2dbaa47fffc2b7118bc1345fec7b36a7046a22269f4a3928083b24558",
    },
    update: {
      status: "current",
      latest: "1.1.1",
      current: "1.1.1",
      url: "https://github.com/adofaiex/ADOFAI.EditorTweaks.ChartRendering/releases",
      notes: "",
      auroraUrl: "https://www.adofaitools.top/tools/adofai-mod-manager",
    },
    render: {
      active: false,
      taskId: "",
      state: "Idle",
      cancelRequested: false,
      writingOutput: false,
      quitRefused: false,
      captureSource: "Camera",
      tempVideoBytes: 0,
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
    quality: {
      quality: { nvenc: [28, 25, 21, 16], nvenc_av1: [40, 34, 28, 20], amf: [28, 25, 21, 16], qsv: [28, 25, 21, 16], x264: [26, 23, 20, 16], x265: [28, 25, 22, 18], svtav1: [40, 34, 28, 20], aom: [40, 34, 28, 20], vvenc: [34, 30, 27, 23] },
      caps: { small: 12, standard: 24, high: 48, lossless: 0 },
      platformMbps: 12,
      learned: {},
    },
    chart: { loaded: true, seconds: 192, selectionSeconds: 45 },
    disk: { export: { drive: "D:", freeBytes: 120 * 1024 ** 3 }, workspace: { drive: "D:", freeBytes: 120 * 1024 ** 3 } },
    workspace: { path: "D:/ADOFAI/Workspace", tempFolders: 2, tempBytes: 3.4 * 1024 ** 3, logFiles: 10, logBytes: 2.1 * 1024 ** 2 },
    lastResult: { path: "D:/ADOFAI Renders/level_20261120_195037.mp4", bytes: 380 * 1024 ** 2, averageMbps: 15.6, capRatio: 0.65 },
  };
}
