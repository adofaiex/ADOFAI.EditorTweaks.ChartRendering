using System;
using System.Collections;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Net;
using System.Net.NetworkInformation;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using ADOFAI.EditorTweaks.Api.Rendering;
using ADOFAI.EditorTweaks.ChartRendering.Features.ChartRendering;
using ADOFAI.SteamIntegration;
using GDMiniJSON;
using Steamworks;
using UnityEngine;
using ApiChartRenderResult = ADOFAI.EditorTweaks.Api.Rendering.ChartRenderResult;

namespace ADOFAI.EditorTweaks.ChartRendering.Features.WebUi
{
    internal sealed class WebUiHost : MonoBehaviour
    {
        private const int FirstPort = 18080;
        private const int LastPort = 18180;
        private const int CommandTimeoutMilliseconds = 5000;
        private const float StateBroadcastIntervalSeconds = 0.1f;
        private const int SteamOverlayProbeAttempts = 6;
        private const float SteamOverlayProbeIntervalSeconds = 0.2f;

        private static WebUiHost? instance;

        private readonly ConcurrentQueue<MainThreadCommand> commands = new ConcurrentQueue<MainThreadCommand>();
        private readonly WebUiEventHub eventHub = new WebUiEventHub();
        private readonly object accessGate = new object();
        private readonly object stateGate = new object();
        private int accessGeneration;
        private HttpListener? listener;
        private Thread? listenerThread;
        private string webRoot = string.Empty;
        private string accessToken = string.Empty;
        private string pageUrl = string.Empty;
        private int localPort;
        private WebUiLanProxy? lanProxy;
        private string lanUrl = string.Empty;
        private readonly List<string> lanUrls = new List<string>();
        private bool lanStartAttempted;
        private readonly List<string> lanAddresses = new List<string>();
        private string latestStateJson = "{}";
        private string latestViewerStateJson = "{}";
        private int lanPort;
        private int lanPreferredPort;
        private ChartRenderTask? renderTask;
        private int lastEncoderChangeCount = -1;
        private int lastFfmpegChangeCount = -1;
        private int lastUpdateChangeCount = -1;
        private Coroutine? openSettingsPageRoutine;
        private float nextStateBroadcastTime;
        private bool stateDirty = true;
        private int shutdownRequested;

        public static bool IsRunning => instance != null && instance.listener != null;

        public static bool LanEnabled => instance != null && instance.lanProxy != null;

        public static string LanUrl => instance?.lanUrl ?? string.Empty;

        public static int LanPort => instance?.lanPort ?? 0;

        // Read-only links (no token): http://<address>:<port>/?view=<view key>.
        public static List<string> LanViewUrls
        {
            get
            {
                List<string> urls = new List<string>();
                WebUiHost? host = instance;
                if (host == null || host.lanProxy == null || Main.Settings == null)
                {
                    return urls;
                }

                foreach (string address in host.lanAddresses)
                {
                    urls.Add("http://" + address + ":" + host.lanPort + "/?view=" + Main.Settings.WebUiViewKey);
                }

                return urls;
            }
        }

        // Main thread (the UMM settings panel). Same flow as the open-page hotkey.
        public static void OpenFromModMenu()
        {
            if (instance == null || instance.openSettingsPageRoutine != null)
            {
                return;
            }

            instance.openSettingsPageRoutine = instance.StartCoroutine(instance.OpenSettingsPageRoutine());
        }

        public static IReadOnlyList<string> LanUrls =>
            instance != null ? (IReadOnlyList<string>)instance.lanUrls : Array.Empty<string>();

        public static void Ensure()
        {
            if (instance != null)
            {
                return;
            }

            GameObject host = new GameObject("ADOFAI.EditorTweaks.ChartRendering.WebUiHost");
            DontDestroyOnLoad(host);
            instance = host.AddComponent<WebUiHost>();
        }

        public static void Destroy()
        {
            if (instance == null)
            {
                return;
            }

            instance.Shutdown();
            UnityEngine.Object.Destroy(instance.gameObject);
            instance = null;
        }

        private void Awake()
        {
            StartHost();
        }

        private void Update()
        {
            ProcessCommands();
            ObserveRenderTask();
            UpdateLanProxy();

            if (WebUiHotkey.IsPressed(Main.Settings.WebUiOpenHotkey))
            {
                if (openSettingsPageRoutine == null)
                {
                    openSettingsPageRoutine = StartCoroutine(OpenSettingsPageRoutine());
                }
            }

            if (ChartRenderService.IsActive && Input.GetKeyDown(KeyCode.Escape))
            {
                if (ChartRenderService.IsWritingOutput)
                {
                    Main.Log("[WebUI] Ignored Escape while the output file is being written. Use the web page cancel button if needed.");
                }
                else
                {
                    ChartRenderApi.CurrentTask?.Cancel();
                    Main.Log("[WebUI] Render canceled by Escape.");
                }
            }

            // Encoder detection runs on a background thread; its results are saved and shown from here.
            ChartEncoderCapabilities.FlushIfDirty();
            int encoderChanges = ChartEncoderCapabilities.ChangeCount;
            if (encoderChanges != lastEncoderChangeCount)
            {
                lastEncoderChangeCount = encoderChanges;
                stateDirty = true;
            }

            // The FFmpeg downloader runs on its own thread; push its progress to the page.
            ChartFfmpegInstaller.TickCountdown();
            int ffmpegChanges = ChartFfmpegInstaller.ChangeCount;
            if (ffmpegChanges != lastFfmpegChangeCount)
            {
                lastFfmpegChangeCount = ffmpegChanges;
                stateDirty = true;
            }

            if (ChartFfmpegInstaller.TakePendingDetection())
            {
                ChartEncoderCapabilities.StartDetection();
                stateDirty = true;
            }

            ChartUpdateChecker.StartIfDue();
            ChartUpdateChecker.ApplyToMainThread();
            int updateChanges = ChartUpdateChecker.ChangeCount;
            if (updateChanges != lastUpdateChangeCount)
            {
                lastUpdateChangeCount = updateChanges;
                stateDirty = true;
            }

            bool renderStateNeedsBroadcast = renderTask != null && !renderTask.IsTerminal;
            if ((stateDirty || renderStateNeedsBroadcast) && Time.unscaledTime >= nextStateBroadcastTime)
            {
                PublishState();
            }
        }

        private void OnDestroy()
        {
            Shutdown();
        }

        private void StartHost()
        {
            if (Main.Mod == null)
            {
                return;
            }

            webRoot = Path.GetFullPath(Path.Combine(Main.Mod.Path, "Resources", "WebUI"));
            string indexPath = Path.Combine(webRoot, "index.html");
            if (!File.Exists(indexPath))
            {
                Main.Log("[WebUI] Resources/WebUI/index.html was not found; HTTP service was not started.");
                return;
            }

            accessToken = WebUiAccess.NewKey();
            if (Main.Settings != null && WebUiAccess.EnsureKeys(Main.Settings))
            {
                Main.Settings.Save(Main.Mod);
            }

            if (!TryStartListener())
            {
                Main.Log("[WebUI] Could not bind a loopback HTTP port. The Web settings page is unavailable.");
                return;
            }

            // Both variants exist before the first broadcast: a read-only client that connects right
            // away must not receive an empty state.
            Dictionary<string, object> initialState = WebUiStateBuilder.Build(null);
            latestStateJson = Json.Serialize(initialState);
            latestViewerStateJson = Json.Serialize(WebUiStateBuilder.ForViewer(initialState));
            listenerThread = new Thread(ListenLoop)
            {
                IsBackground = true,
                Name = "ADOFAI.EditorTweaks.ChartRendering.WebUiHttp"
            };
            listenerThread.Start();
            UpdateLanProxy();
            Main.Log("[WebUI] Listening on 127.0.0.1:" + localPort + " (access token hidden).");
            stateDirty = false;
        }

        private bool TryStartListener()
        {
            for (int port = FirstPort; port <= LastPort; port++)
            {
                HttpListener candidate = new HttpListener();
                try
                {
                    candidate.Prefixes.Add("http://127.0.0.1:" + port + "/");
                    candidate.Start();
                    listener = candidate;
                    localPort = port;
                    pageUrl = "http://127.0.0.1:" + port + "/?token=" + accessToken;
                    return true;
                }
                catch (Exception)
                {
                    candidate.Close();
                }
            }

            return false;
        }

        private void UpdateLanProxy()
        {
            bool wanted = Main.Settings != null && Main.Settings.WebUiAllowLan && listener != null;
            if (!wanted)
            {
                StopLanProxy();
                return;
            }

            if (lanProxy != null)
            {
                // A changed LAN port takes effect right away: restart the proxy on the new port.
                if (WebUiAccess.NormalizePort(Main.Settings!.WebUiLanPort) != lanPreferredPort)
                {
                    StopLanProxy();
                }
                else
                {
                    return;
                }
            }

            if (lanProxy != null)
            {
                return;
            }

            if (lanStartAttempted)
            {
                return;
            }

            lanStartAttempted = true;
            int preferred = WebUiAccess.NormalizePort(Main.Settings!.WebUiLanPort);
            WebUiLanProxy? proxy = WebUiLanProxy.TryStart(localPort, preferred, out int startedPort, out string error);
            if (proxy == null)
            {
                Main.Log("[WebUI] LAN access could not start: " + error);
                return;
            }

            lanProxy = proxy;
            lanPort = startedPort;
            lanPreferredPort = preferred;
            lanAddresses.Clear();
            lanAddresses.AddRange(GetLanAddresses());
            RefreshPairingLinks();
            Main.Log("[WebUI] LAN access enabled on port " + lanPort
                + (lanPort != preferred ? " (port " + preferred + " was in use)" : string.Empty) + ".");
            foreach (string address in lanAddresses)
            {
                // Never log the access token: Player.log is often shared when reporting problems.
                Main.Log("[WebUI] LAN address: http://" + address + ":" + lanPort + "/");
            }

            if (lanUrls.Count == 0)
            {
                Main.Log("[WebUI] No LAN IPv4 address was found; use this machine's LAN IP with port "
                    + lanPort + ".");
            }

            stateDirty = true;
        }

        private void RefreshPairingLinks()
        {
            pageUrl = "http://127.0.0.1:" + localPort + "/?token=" + accessToken;
            lanUrls.Clear();
            foreach (string address in lanAddresses)
            {
                lanUrls.Add("http://" + address + ":" + lanPort + "/?token=" + accessToken);
            }

            lanUrl = lanUrls.Count > 0 ? lanUrls[0] : string.Empty;
        }

        private void StopLanProxy()
        {
            lanStartAttempted = false;
            if (lanProxy == null)
            {
                return;
            }

            lanProxy.Stop();
            lanProxy = null;
            lanPort = 0;
            lanUrl = string.Empty;
            lanUrls.Clear();
            lanAddresses.Clear();
            Main.Log("[WebUI] LAN access disabled.");
            stateDirty = true;
        }

        private static List<string> GetLanAddresses()
        {
            List<string> candidates = new List<string>();
            try
            {
                foreach (NetworkInterface networkInterface in NetworkInterface.GetAllNetworkInterfaces())
                {
                    if (networkInterface.OperationalStatus != OperationalStatus.Up
                        || networkInterface.NetworkInterfaceType == NetworkInterfaceType.Loopback
                        || networkInterface.NetworkInterfaceType == NetworkInterfaceType.Tunnel
                        || networkInterface.NetworkInterfaceType == NetworkInterfaceType.Ppp)
                    {
                        continue;
                    }

                    foreach (UnicastIPAddressInformation address in networkInterface.GetIPProperties().UnicastAddresses)
                    {
                        if (address.Address.AddressFamily != System.Net.Sockets.AddressFamily.InterNetwork)
                        {
                            continue;
                        }

                        string text = address.Address.ToString();
                        if (text.StartsWith("169.254.", StringComparison.Ordinal))
                        {
                            continue;
                        }

                        if (!candidates.Contains(text))
                        {
                            candidates.Add(text);
                        }
                    }
                }
            }
            catch
            {
            }

            // Prefer a normal home/office LAN address over VPN and virtual adapters.
            candidates.Sort((a, b) => LanAddressRank(a).CompareTo(LanAddressRank(b)));
            return candidates;
        }

        private static int LanAddressRank(string address)
        {
            if (address.StartsWith("192.168.", StringComparison.Ordinal))
            {
                return 0;
            }

            if (address.StartsWith("10.", StringComparison.Ordinal))
            {
                return 1;
            }

            if (address.StartsWith("172.", StringComparison.Ordinal))
            {
                string[] parts = address.Split('.');
                if (parts.Length >= 2 && int.TryParse(parts[1], out int second) && second >= 16 && second <= 31)
                {
                    return 2;
                }
            }

            return 3;
        }

        private void ListenLoop()
        {
            while (Volatile.Read(ref shutdownRequested) == 0)
            {
                HttpListenerContext? context = null;
                try
                {
                    context = listener?.GetContext();
                }
                catch (HttpListenerException)
                {
                    break;
                }
                catch (ObjectDisposedException)
                {
                    break;
                }

                if (context == null)
                {
                    continue;
                }

                ThreadPool.QueueUserWorkItem(_ => HandleRequest(context));
            }
        }

        private void HandleRequest(HttpListenerContext context)
        {
            try
            {
                if (context.Request.HttpMethod.Equals("OPTIONS", StringComparison.OrdinalIgnoreCase))
                {
                    context.Response.StatusCode = 204;
                    context.Response.Close();
                    return;
                }

                string path = context.Request.Url?.AbsolutePath ?? "/";
                WebUiAccessLevel level;
                bool viaToken;
                bool viaView;
                int generation;
                lock (accessGate)
                {
                    generation = accessGeneration;
                    level = WebUiAccess.Classify(context.Request, accessToken, Main.Settings, out viaToken, out viaView);
                }
                if (path.Equals("/api/events", StringComparison.OrdinalIgnoreCase))
                {
                    if (level == WebUiAccessLevel.None)
                    {
                        WriteJson(context, 401, Error("Invalid WebUI token."));
                        return;
                    }

                    HandleEvents(context, level == WebUiAccessLevel.Viewer, generation);
                    return;
                }

                if (path.StartsWith("/api/", StringComparison.OrdinalIgnoreCase))
                {
                    if (level == WebUiAccessLevel.None)
                    {
                        WriteJson(context, 401, Error("Invalid WebUI token."));
                        return;
                    }

                    if (path.Equals("/api/session", StringComparison.OrdinalIgnoreCase))
                    {
                        WriteJson(context, 200, new Dictionary<string, object> { ["mode"] = level == WebUiAccessLevel.Full ? "full" : "view" });
                        return;
                    }

                    if (level != WebUiAccessLevel.Full)
                    {
                        WriteJson(context, 403, Error("只读链接只能查看进度，不能修改设置或控制渲染。"));
                        return;
                    }

                    // Cookie-authenticated writes must be JSON: a plain HTML form from another site
                    // cannot send that content type without a CORS preflight, which this server refuses.
                    if (!viaToken && context.Request.HttpMethod.Equals("POST", StringComparison.OrdinalIgnoreCase)
                        && (context.Request.ContentType ?? string.Empty).IndexOf("application/json", StringComparison.OrdinalIgnoreCase) < 0)
                    {
                        WriteJson(context, 415, Error("Expected application/json."));
                        return;
                    }

                    HandleApi(context, path);
                    return;
                }

                if (path.Equals("/docs/manual", StringComparison.OrdinalIgnoreCase))
                {
                    if (level == WebUiAccessLevel.None)
                    {
                        WriteJson(context, 401, Error("Invalid WebUI token."));
                        return;
                    }

                    ServeDocumentFile(context, Path.Combine(Main.Mod!.Path, "Resources", "README.html"));
                    return;
                }

                if (path.Equals("/docs/ffmpeg", StringComparison.OrdinalIgnoreCase))
                {
                    if (level == WebUiAccessLevel.None)
                    {
                        WriteJson(context, 401, Error("Invalid WebUI token."));
                        return;
                    }

                    ServeDocumentFile(context, Path.Combine(Main.Mod!.Path, "Resources", "FFmpegReference.html"));
                    return;
                }

                // Opening the page with ?token= (full) or ?view= (read-only) remembers this device.
                if ((viaToken || viaView) && Main.Settings != null && (path == "/" || path.EndsWith("index.html", StringComparison.OrdinalIgnoreCase)))
                {
                    lock (accessGate)
                    {
                        if (generation == accessGeneration)
                        {
                            WebUiAccess.Remember(context.Response, Main.Settings, level);
                        }
                    }
                }

                ServeStaticFile(context, path);
            }
            catch (Exception exception)
            {
                Main.Log("[WebUI] Request failed: " + exception.Message);
                try
                {
                    WriteJson(context, 500, Error("WebUI request failed."));
                }
                catch
                {
                    context.Response.Close();
                }
            }
        }

        private void HandleApi(HttpListenerContext context, string path)
        {
            if (context.Request.HttpMethod.Equals("GET", StringComparison.OrdinalIgnoreCase)
                && path.Equals("/api/diagnostics/download", StringComparison.OrdinalIgnoreCase))
            {
                ServeDiagnosticsDownload(context);
                return;
            }

            if (!context.Request.HttpMethod.Equals("POST", StringComparison.OrdinalIgnoreCase))
            {
                WriteJson(context, 405, Error("Only GET and POST are supported."));
                return;
            }

            Dictionary<string, object> body = ReadJsonBody(context);
            switch (path.ToLowerInvariant())
            {
                case "/api/settings":
                    ExecuteOnMain(() =>
                    {
                        if (ChartRenderService.IsActive)
                        {
                            return new CommandResult(409, Error("渲染进行中，设置暂时锁定，请等待渲染结束。"));
                        }

                        ApplySettings(body.TryGetValue("changes", out object changes) ? changes as Dictionary<string, object> : null);
                        SaveSettings();
                        return StateResult();
                    }, context);
                    return;
                case "/api/settings/reset":
                    ExecuteOnMain(() =>
                    {
                        if (ChartRenderService.IsActive)
                        {
                            return new CommandResult(409, Error("渲染进行中，设置暂时锁定，请等待渲染结束。"));
                        }

                        Main.Settings.ResetAllDefaults(Main.Mod!);
                        SaveSettings();
                        return StateResult();
                    }, context);
                    return;
                case "/api/render/start":
                    ExecuteOnMain(StartRender, context);
                    return;
                case "/api/render/cancel":
                    CommandResult cancellation;
                    lock (accessGate)
                    {
                        cancellation = WebUiAccess.Classify(context.Request, accessToken, Main.Settings, out _, out _) == WebUiAccessLevel.Full
                            ? CancelRenderFromRequestThread()
                            : new CommandResult(401, Error("Invalid WebUI token."));
                    }

                    WriteJson(context, cancellation.StatusCode, cancellation.Body);
                    return;
                case "/api/encoders/detect":
                    ExecuteOnMain(() =>
                    {
                        // Probing starts FFmpeg with every encoder; not while a render is using the GPU.
                        if (ChartRenderService.IsActive)
                        {
                            return new CommandResult(409, Error("渲染进行中，请在渲染结束后再重新检测。"));
                        }

                        ChartEncoderCapabilities.StartDetection();
                        return StateResult();
                    }, context);
                    return;
                case "/api/ffmpeg/install":
                    ExecuteOnMain(() =>
                    {
                        if (ChartRenderService.IsActive)
                        {
                            return new CommandResult(409, Error("渲染进行中，FFmpeg 会在开始渲染前自动准备。"));
                        }

                        bool preferFull = body.TryGetValue("variant", out object variant)
                            && variant is string name
                            && string.Equals(name, "full", StringComparison.OrdinalIgnoreCase);
                        ChartFfmpegInstaller.Start("requested from the settings page", preferFull);
                        return StateResult();
                    }, context);
                    return;
                case "/api/access/forget":
                    ExecuteOnMain(() =>
                    {
                        lock (accessGate)
                        {
                            accessToken = WebUiAccess.ForgetAllDevices(Main.Settings);
                            Interlocked.Increment(ref accessGeneration);
                            eventHub.CloseAll();
                            RefreshPairingLinks();
                            PublishState();
                        }

                        Main.Settings.Save(Main.Mod!);
                        Main.Log("[WebUI] All remembered devices and read-only links were reset.");
                        return new CommandResult(200, new Dictionary<string, object>());
                    }, context);
                    return;
                case "/api/workspace/clean":
                    ExecuteOnMain(() =>
                    {
                        if (ChartRenderService.IsActive)
                        {
                            return new CommandResult(409, Error("渲染进行中，请在渲染结束后再清理。"));
                        }

                        int removed = ChartRenderWorkspace.DeleteTempDirectories(
                            ChartRenderPaths.GetWorkspaceDirectory(Main.Settings), null, out long freed,
                            includeDiagnostics: true);
                        Main.Log("[WebUI] Cleaned " + removed + " temp folder(s), " + ChartRenderWorkspace.FormatBytes(freed) + ".");
                        WebUiStateBuilder.InvalidateCaches();
                        Dictionary<string, object> response = StateResponse();
                        PublishState();
                        response = StateResponse();
                        response["freedBytes"] = freed;
                        return new CommandResult(200, response);
                    }, context);
                    return;
                case "/api/folders/list":
                    WriteJson(context, 200, WebUiFolders.List(body.TryGetValue("path", out object folder) ? folder as string : null));
                    return;
                case "/api/folders/open":
                    WriteJson(context, 200, WebUiFolders.Open(body.TryGetValue("target", out object target) ? target as string : null, Main.Settings));
                    return;
                case "/api/diagnostics/export":
                    ExportDiagnostics(context);
                    return;
                case "/api/ffmpeg/cancel-auto":
                    ExecuteOnMain(() =>
                    {
                        ChartFfmpegInstaller.CancelCountdown();
                        return StateResult();
                    }, context);
                    return;
                default:
                    WriteJson(context, 404, Error("Unknown WebUI API route."));
                    return;
            }
        }

        private void HandleEvents(HttpListenerContext context, bool viewer, int generation)
        {
            WebUiEventHub.Client? client = null;
            string initialState = string.Empty;
            lock (accessGate)
            {
                if (generation == accessGeneration)
                {
                    client = eventHub.AddClient(viewer);
                    initialState = viewer ? GetLatestViewerStateJson() : GetLatestStateJson();
                    if (!viewer)
                    {
                        // Opening the full settings page starts the countdown if FFmpeg is missing.
                        ChartFfmpegInstaller.RequestCountdown();
                    }
                }
            }

            if (client == null)
            {
                WriteJson(context, 401, Error("Invalid WebUI token."));
                return;
            }

            context.Response.StatusCode = 200;
            context.Response.ContentType = "text/event-stream";
            context.Response.ContentEncoding = new UTF8Encoding(false);
            context.Response.ProtocolVersion = HttpVersion.Version11;
            context.Response.SendChunked = true;
            context.Response.KeepAlive = true;
            context.Response.Headers["Cache-Control"] = "no-cache, no-transform";
            context.Response.Headers["Pragma"] = "no-cache";
            context.Response.Headers["X-Accel-Buffering"] = "no";

            try
            {
                Main.Log("[WebUI] SSE client connected.");
                using (StreamWriter writer = new StreamWriter(context.Response.OutputStream, new UTF8Encoding(false), 1024, true))
                {
                    writer.AutoFlush = true;
                    if (generation != Volatile.Read(ref accessGeneration))
                    {
                        return;
                    }

                    WriteSse(writer, "state", initialState);
                    while (Volatile.Read(ref shutdownRequested) == 0 && generation == Volatile.Read(ref accessGeneration))
                    {
                        bool hasMessage = client.TryTake(out string message, TimeSpan.FromSeconds(15));
                        if (generation != Volatile.Read(ref accessGeneration))
                        {
                            break;
                        }

                        writer.Write(hasMessage ? message : ": ping\r\n\r\n");
                    }
                }
            }
            catch (IOException)
            {
            }
            catch (HttpListenerException)
            {
            }
            catch (ObjectDisposedException)
            {
            }
            finally
            {
                eventHub.RemoveClient(client);
                Main.Log("[WebUI] SSE client disconnected.");
                try
                {
                    context.Response.Close();
                }
                catch
                {
                }
            }
        }

        private void ServeStaticFile(HttpListenerContext context, string requestPath)
        {
            string relativePath = Uri.UnescapeDataString(requestPath.TrimStart('/'));
            if (string.IsNullOrWhiteSpace(relativePath))
            {
                relativePath = "index.html";
            }

            if (relativePath.IndexOf("..", StringComparison.Ordinal) >= 0)
            {
                WriteJson(context, 403, Error("Path traversal is not allowed."));
                return;
            }

            string root = Path.GetFullPath(webRoot).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
            string filePath = Path.GetFullPath(Path.Combine(webRoot, relativePath.Replace('/', Path.DirectorySeparatorChar)));
            if (!filePath.StartsWith(root, StringComparison.OrdinalIgnoreCase) || !File.Exists(filePath))
            {
                WriteJson(context, 404, Error("Static file not found."));
                return;
            }

            byte[] data = File.ReadAllBytes(filePath);
            context.Response.StatusCode = 200;
            context.Response.ContentType = GetContentType(filePath);
            context.Response.ContentLength64 = data.Length;
            context.Response.OutputStream.Write(data, 0, data.Length);
            context.Response.Close();
        }

        private static void ServeDocumentFile(HttpListenerContext context, string filePath)
        {
            if (!context.Request.HttpMethod.Equals("GET", StringComparison.OrdinalIgnoreCase))
            {
                WriteJson(context, 405, Error("Only GET is supported."));
                return;
            }

            if (!File.Exists(filePath))
            {
                WriteJson(context, 404, Error("Documentation file not found."));
                return;
            }

            byte[] data = File.ReadAllBytes(filePath);
            context.Response.StatusCode = 200;
            context.Response.ContentType = "text/html; charset=utf-8";
            context.Response.ContentLength64 = data.Length;
            context.Response.Headers["Cache-Control"] = "no-store";
            context.Response.OutputStream.Write(data, 0, data.Length);
            context.Response.Close();
        }

        private void ExecuteOnMain(Func<CommandResult> action, HttpListenerContext context)
        {
            MainThreadCommand command = new MainThreadCommand(() =>
                WebUiAccess.Classify(context.Request, accessToken, Main.Settings, out _, out _) == WebUiAccessLevel.Full
                    ? action()
                    : new CommandResult(401, Error("Invalid WebUI token.")));
            commands.Enqueue(command);
            if (!command.Completion.Task.Wait(CommandTimeoutMilliseconds))
            {
                WriteJson(context, 503, Error("Unity main thread did not respond in time."));
                return;
            }

            CommandResult result = command.Completion.Task.Result;
            WriteJson(context, result.StatusCode, result.Body);
        }

        private void ProcessCommands()
        {
            for (int i = 0; i < 32 && commands.TryDequeue(out MainThreadCommand? command); i++)
            {
                try
                {
                    command.Completion.TrySetResult(command.Action());
                }
                catch (Exception exception)
                {
                    Main.Log("[WebUI] Main-thread command failed: " + exception.Message);
                    command.Completion.TrySetResult(new CommandResult(500, Error("WebUI operation failed.")));
                }
            }
        }

        private CommandResult StartRender()
        {
            ChartRenderAvailability availability = ChartRenderApi.GetAvailability();
            if (!availability.Available)
            {
                return new CommandResult(409, Error(availability.Message));
            }

            ChartRenderRequest request = ChartRenderApi.CreateRequestFromCurrentSettings();
            request.ShowBuiltInProgressUi = false;
            ChartRenderStartResult result = ChartRenderApi.Start(request);
            if (!result.Success)
            {
                return new CommandResult(409, Error(result.Message));
            }

            ObserveRenderTask();
            PublishState();
            return StateResult();
        }

        // Cancel is answered on the HTTP thread instead of going through ExecuteOnMain. When the encoder
        // is slower than rendering (for example a slow CPU preset at 3 frames per second), the main
        // thread spends most of each frame waiting for room in the encoder queue, so a command queued
        // for it could sit there and the cancel button looked dead. ChartRenderTask.Cancel only sets
        // an interlocked flag, which those waits check every 50 ms. The reply carries the last state
        // the main thread published; the main thread publishes the canceling state on its next frame.
        private CommandResult CancelRenderFromRequestThread()
        {
            ChartRenderTask? task = ChartRenderApi.CurrentTask;
            if (task == null)
            {
                // A repeated cancel after the task has reached its terminal state is harmless.
                return new CommandResult(renderTask != null ? 200 : 409,
                    renderTask != null ? StateResponse() : Error("没有正在运行的渲染任务。"));
            }

            if (!task.Cancel() && !task.IsCancellationRequested)
            {
                return new CommandResult(409, Error("渲染任务无法取消。"));
            }

            Main.Log("[WebUI] Render cancel requested from the settings page.");
            commands.Enqueue(new MainThreadCommand(() =>
            {
                PublishState();
                return new CommandResult(200, new Dictionary<string, object>());
            }));
            return new CommandResult(200, StateResponse());
        }

        private void ObserveRenderTask()
        {
            ChartRenderTask? current = ChartRenderService.CurrentTask;
            if (current == null && renderTask != null && renderTask.IsTerminal)
            {
                return;
            }

            if (ReferenceEquals(current, renderTask))
            {
                return;
            }

            if (renderTask != null)
            {
                renderTask.StateChanged -= OnRenderStateChanged;
                renderTask.ProgressChanged -= OnRenderProgressChanged;
                renderTask.Completed -= OnRenderCompleted;
            }

            if (current != null)
            {
                renderTask = current;
                renderTask.StateChanged += OnRenderStateChanged;
                renderTask.ProgressChanged += OnRenderProgressChanged;
                renderTask.Completed += OnRenderCompleted;
            }

            stateDirty = true;
        }

        private void OnRenderStateChanged(ChartRenderTask task)
        {
            stateDirty = true;
        }

        private void OnRenderProgressChanged(ChartRenderTask task)
        {
            stateDirty = true;
        }

        private void OnRenderCompleted(ChartRenderTask task, ApiChartRenderResult result)
        {
            stateDirty = true;
            PublishState();
        }

        private void PublishState()
        {
            Dictionary<string, object> state = WebUiStateBuilder.Build(renderTask);
            string json = Json.Serialize(state);
            string viewerJson = Json.Serialize(WebUiStateBuilder.ForViewer(state));
            lock (stateGate)
            {
                latestStateJson = json;
                latestViewerStateJson = viewerJson;
            }

            eventHub.Publish("state", json, viewerJson);
            stateDirty = false;
            nextStateBroadcastTime = Time.unscaledTime + StateBroadcastIntervalSeconds;
        }

        private void SaveSettings()
        {
            Main.Settings.Normalize();
            Main.Settings.Save(Main.Mod!);
            // A changed custom FFmpeg path changes which encoders are available.
            ChartEncoderCapabilities.EnsureCurrent();
            stateDirty = true;
            PublishState();
        }

        private static void ApplySettings(Dictionary<string, object>? changes)
        {
            if (changes == null)
            {
                return;
            }

            Settings settings = Main.Settings;
            foreach (KeyValuePair<string, object> change in changes)
            {
                switch (change.Key)
                {
                    case "WebUiOpenHotkey":
                        settings.WebUiOpenHotkey = GetString(change.Value, settings.WebUiOpenHotkey);
                        break;
                    case "WebUiOpenMode":
                        settings.WebUiOpenMode = WebUiOpenTargets.Normalize(GetString(change.Value, settings.WebUiOpenMode));
                        break;
                    case "WebUiAllowLan":
                        settings.WebUiAllowLan = GetBool(change.Value, settings.WebUiAllowLan);
                        break;
                    case "ChartRenderWorkspaceDirectory": settings.ChartRenderWorkspaceDirectory = GetString(change.Value, settings.ChartRenderWorkspaceDirectory); break;
                    case "ChartRenderExportDirectory": settings.ChartRenderExportDirectory = GetString(change.Value, settings.ChartRenderExportDirectory); break;
                    case "ChartRenderWidth": settings.ChartRenderWidth = GetInt(change.Value, settings.ChartRenderWidth); break;
                    case "ChartRenderHeight": settings.ChartRenderHeight = GetInt(change.Value, settings.ChartRenderHeight); break;
                    case "ChartRenderFps": settings.ChartRenderFps = GetInt(change.Value, settings.ChartRenderFps); break;
                    case "ChartRenderCrf": settings.ChartRenderCrf = GetInt(change.Value, settings.ChartRenderCrf); break;
                    case "ChartRenderQualityTier": settings.ChartRenderQualityTier = GetString(change.Value, settings.ChartRenderQualityTier); break;
                    case "ChartRenderDiagnosticMode": settings.ChartRenderDiagnosticMode = GetBool(change.Value, settings.ChartRenderDiagnosticMode); break;
                    case "WebUiLanPort": settings.WebUiLanPort = GetInt(change.Value, settings.WebUiLanPort); break;
                    case "ChartRenderBitrateMbps": settings.ChartRenderBitrateMbps = GetInt(change.Value, settings.ChartRenderBitrateMbps); break;
                    case "ChartRenderConstantBitrate": settings.ChartRenderConstantBitrate = GetBool(change.Value, settings.ChartRenderConstantBitrate); break;
                    case "ChartRenderPreset": settings.ChartRenderPreset = GetString(change.Value, settings.ChartRenderPreset); break;
                    case "ChartRenderEncoderMode": settings.ChartRenderEncoderMode = GetString(change.Value, settings.ChartRenderEncoderMode); break;
                    case "ChartRenderHardwareEncoder": settings.ChartRenderHardwareEncoder = GetString(change.Value, settings.ChartRenderHardwareEncoder); break;
                    case "ChartRenderHardwarePreset": settings.ChartRenderHardwarePreset = GetString(change.Value, settings.ChartRenderHardwarePreset); break;
                    case "ChartRenderCpuPreset": settings.ChartRenderCpuPreset = GetString(change.Value, settings.ChartRenderCpuPreset); break;
                    case "ChartRenderDetailedEncoderSettings": settings.ChartRenderDetailedEncoderSettings = GetBool(change.Value, settings.ChartRenderDetailedEncoderSettings); break;
                    case "ChartRenderCaptureFormat": settings.ChartRenderCaptureFormat = GetString(change.Value, settings.ChartRenderCaptureFormat); break;
                    case "ChartRenderCaptureSource": settings.ChartRenderCaptureSource = GetString(change.Value, settings.ChartRenderCaptureSource); break;
                    case "ChartRenderGameViewBackend": settings.ChartRenderGameViewBackend = GetString(change.Value, settings.ChartRenderGameViewBackend); break;
                    case "ChartRenderPreviewMode": settings.ChartRenderPreviewMode = GetString(change.Value, settings.ChartRenderPreviewMode); break;
                    case "ChartRenderAudioFormat": settings.ChartRenderAudioFormat = GetString(change.Value, settings.ChartRenderAudioFormat); break;
                    case "ChartRenderVideoFormat": settings.ChartRenderVideoFormat = GetString(change.Value, settings.ChartRenderVideoFormat); break;
                    case "ChartRenderVideoCodec": settings.ChartRenderVideoCodec = GetString(change.Value, settings.ChartRenderVideoCodec); break;
                    case "ChartRenderCompletionTailSeconds": settings.ChartRenderCompletionTailSeconds = GetFloat(change.Value, settings.ChartRenderCompletionTailSeconds); break;
                    case "ChartRenderAudioSyncOffsetMs": settings.ChartRenderAudioSyncOffsetMs = GetFloat(change.Value, settings.ChartRenderAudioSyncOffsetMs); break;
                    case "ChartRenderShowHitJudgments": settings.ChartRenderShowHitJudgments = GetBool(change.Value, settings.ChartRenderShowHitJudgments); break;
                    case "ChartRenderCompatKeyViewers": settings.ChartRenderCompatKeyViewers = GetBool(change.Value, settings.ChartRenderCompatKeyViewers); break;
                    case "ChartRenderUseSelectedRange": settings.ChartRenderUseSelectedRange = GetBool(change.Value, settings.ChartRenderUseSelectedRange); break;
                    case "ChartRenderCustomMuxArgs": settings.ChartRenderCustomMuxArgs = GetString(change.Value, settings.ChartRenderCustomMuxArgs); break;
                    case "FfmpegCustomPath": settings.FfmpegCustomPath = GetString(change.Value, settings.FfmpegCustomPath); break;
                }
            }

            settings.Normalize();
        }

        private IEnumerator OpenSettingsPageRoutine()
        {
            try
            {
                if (string.IsNullOrWhiteSpace(pageUrl))
                {
                    Main.Log("[WebUI] The local service is not running; cannot open the settings page.");
                    yield break;
                }

                string openMode = WebUiOpenTargets.Normalize(Main.Settings.WebUiOpenMode);
                if (openMode == WebUiOpenTargets.Browser)
                {
                    OpenDefaultBrowser("Configured to use the system browser");
                    yield break;
                }

                if (!IsSteamInitialized())
                {
                    OpenDefaultBrowser("Steam is not initialized");
                    yield break;
                }

                string failureReason = "Steam Overlay is unavailable.";
                for (int attempt = 0; attempt < SteamOverlayProbeAttempts; attempt++)
                {
                    if (TryOpenSteamOverlay(out failureReason))
                    {
                        yield break;
                    }

                    if (attempt + 1 < SteamOverlayProbeAttempts)
                    {
                        yield return new WaitForSecondsRealtime(SteamOverlayProbeIntervalSeconds);
                    }
                }

                OpenDefaultBrowser(failureReason);
            }
            finally
            {
                openSettingsPageRoutine = null;
            }
        }

        private static bool IsSteamInitialized()
        {
            try
            {
                return SteamController.initialized;
            }
            catch
            {
                return false;
            }
        }

        private bool TryOpenSteamOverlay(out string failureReason)
        {
            try
            {
                if (!SteamController.initialized)
                {
                    failureReason = "Steam is not initialized";
                    return false;
                }

                if (!SteamUtils.IsOverlayEnabled)
                {
                    failureReason = "Steam Overlay is unavailable or still loading";
                    return false;
                }

                SteamFriends.OpenWebOverlay(pageUrl, true);
                Main.Log("[WebUI] Opened the settings page in Steam Overlay.");
                failureReason = string.Empty;
                return true;
            }
            catch (Exception exception)
            {
                failureReason = "Steam Overlay could not be opened: " + exception.Message;
                return false;
            }
        }

        private void OpenDefaultBrowser(string reason)
        {
            try
            {
                Process.Start(new ProcessStartInfo(pageUrl)
                {
                    UseShellExecute = true
                });
                Main.Log("[WebUI] " + reason + "; opened the system default browser.");
            }
            catch (Exception exception)
            {
                Main.Log("[WebUI] Could not open the system default browser: " + exception.Message + ".");
            }
        }

        private void Shutdown()
        {
            if (Interlocked.Exchange(ref shutdownRequested, 1) != 0)
            {
                return;
            }

            StopLanProxy();

            if (openSettingsPageRoutine != null)
            {
                StopCoroutine(openSettingsPageRoutine);
                openSettingsPageRoutine = null;
            }

            if (renderTask != null)
            {
                renderTask.StateChanged -= OnRenderStateChanged;
                renderTask.ProgressChanged -= OnRenderProgressChanged;
                renderTask.Completed -= OnRenderCompleted;
                renderTask = null;
            }

            ChartRenderApi.CurrentTask?.Cancel();
            eventHub.CloseAll();
            listener?.Stop();
            listener?.Close();
            listener = null;
            if (listenerThread != null && listenerThread != Thread.CurrentThread)
            {
                listenerThread.Join(1000);
            }

            listenerThread = null;
            Main.Log("[WebUI] HTTP service stopped.");
        }

        private bool IsAuthorized(HttpListenerContext context)
        {
            string token = context.Request.Headers["X-EditorTweaks-Token"] ?? GetQueryParameter(context.Request.Url, "token");
            return !string.IsNullOrEmpty(token) && string.Equals(token, accessToken, StringComparison.Ordinal);
        }

        private static string GetQueryParameter(Uri? uri, string key)
        {
            if (uri == null || string.IsNullOrEmpty(uri.Query))
            {
                return string.Empty;
            }

            string query = uri.Query.TrimStart('?');
            foreach (string part in query.Split('&'))
            {
                string[] pair = part.Split(new[] { '=' }, 2);
                if (pair.Length == 2 && string.Equals(Uri.UnescapeDataString(pair[0]), key, StringComparison.OrdinalIgnoreCase))
                {
                    return Uri.UnescapeDataString(pair[1]);
                }
            }

            return string.Empty;
        }

        private static Dictionary<string, object> ReadJsonBody(HttpListenerContext context)
        {
            if (context.Request.ContentLength64 > 1024 * 1024)
            {
                throw new InvalidDataException("WebUI request body is too large.");
            }

            using (StreamReader reader = new StreamReader(context.Request.InputStream, Encoding.UTF8))
            {
                string json = reader.ReadToEnd();
                return Json.Deserialize(json) as Dictionary<string, object> ?? new Dictionary<string, object>();
            }
        }

        private Dictionary<string, object> GetLatestState()
        {
            lock (stateGate)
            {
                return Json.Deserialize(latestStateJson) as Dictionary<string, object>
                    ?? new Dictionary<string, object>();
            }
        }

        private string GetLatestStateJson()
        {
            lock (stateGate)
            {
                return latestStateJson;
            }
        }

        private string GetLatestViewerStateJson()
        {
            lock (stateGate)
            {
                return latestViewerStateJson;
            }
        }

        // Diagnostics: system facts need the Unity main thread; the zip is written on this thread.
        private void ExportDiagnostics(HttpListenerContext context)
        {
            MainThreadCommand command = new MainThreadCommand(() =>
                new CommandResult(200, new Dictionary<string, object> { ["info"] = ChartRenderDiagnosticsExport.CollectSystemInfo() }));
            commands.Enqueue(command);
            if (!command.Completion.Task.Wait(CommandTimeoutMilliseconds))
            {
                WriteJson(context, 503, Error("Unity main thread did not respond in time."));
                return;
            }

            string info = command.Completion.Task.Result.Body["info"] as string ?? string.Empty;
            try
            {
                string zip = ChartRenderDiagnosticsExport.Export(Main.Settings, info, accessToken);
                Main.Log("[WebUI] Diagnostics exported: " + Path.GetFileName(zip));
                WriteJson(context, 200, new Dictionary<string, object>
                {
                    ["file"] = Path.GetFileName(zip),
                    ["path"] = zip,
                    ["bytes"] = new FileInfo(zip).Length,
                });
            }
            catch (Exception exception)
            {
                WriteJson(context, 500, Error("导出诊断包失败：" + exception.Message));
            }
        }

        private static void ServeDiagnosticsDownload(HttpListenerContext context)
        {
            string name = WebUiAccess.GetQuery(context.Request.Url, "file");
            string? path = ChartRenderDiagnosticsExport.Resolve(Main.Settings, name);
            if (path == null)
            {
                WriteJson(context, 404, Error("Diagnostics file not found."));
                return;
            }

            context.Response.StatusCode = 200;
            context.Response.ContentType = "application/zip";
            context.Response.AddHeader("Content-Disposition", "attachment; filename=\"" + Path.GetFileName(path) + "\"");
            using (FileStream stream = File.OpenRead(path))
            {
                context.Response.ContentLength64 = stream.Length;
                stream.CopyTo(context.Response.OutputStream);
            }

            context.Response.Close();
        }

        private Dictionary<string, object> StateResponse()
        {
            return new Dictionary<string, object> { ["state"] = GetLatestState() };
        }

        private CommandResult StateResult()
        {
            PublishState();
            return new CommandResult(200, StateResponse());
        }

        private static Dictionary<string, object> Error(string message)
        {
            return new Dictionary<string, object> { ["error"] = message };
        }

        private static void WriteJson(HttpListenerContext context, int statusCode, Dictionary<string, object> body)
        {
            byte[] data = Encoding.UTF8.GetBytes(Json.Serialize(body));
            context.Response.StatusCode = statusCode;
            context.Response.ContentType = "application/json; charset=utf-8";
            context.Response.ContentLength64 = data.Length;
            context.Response.OutputStream.Write(data, 0, data.Length);
            context.Response.Close();
        }

        private static void WriteSse(StreamWriter writer, string eventName, string data)
        {
            writer.Write(WebUiEventHub.FormatEvent(eventName, data));
        }

        private static string GetContentType(string path)
        {
            switch (Path.GetExtension(path).ToLowerInvariant())
            {
                case ".html": return "text/html; charset=utf-8";
                case ".css": return "text/css; charset=utf-8";
                case ".js": return "text/javascript; charset=utf-8";
                case ".json": return "application/json; charset=utf-8";
                case ".svg": return "image/svg+xml";
                case ".png": return "image/png";
                case ".woff": return "font/woff";
                case ".woff2": return "font/woff2";
                default: return "application/octet-stream";
            }
        }

        private static bool GetBool(object value, bool fallback)
        {
            return value is bool boolean ? boolean : fallback;
        }

        private static int GetInt(object value, int fallback)
        {
            if (value is int integer) return integer;
            if (value is long longValue) return (int)longValue;
            if (value is double doubleValue) return (int)doubleValue;
            return fallback;
        }

        private static float GetFloat(object value, float fallback)
        {
            if (value is float single) return single;
            if (value is double doubleValue) return (float)doubleValue;
            if (value is long longValue) return longValue;
            if (value is int integer) return integer;
            return fallback;
        }

        private static string GetString(object value, string fallback)
        {
            return value as string ?? fallback;
        }

        private sealed class MainThreadCommand
        {
            public MainThreadCommand(Func<CommandResult> action)
            {
                Action = action;
                Completion = new TaskCompletionSource<CommandResult>(TaskCreationOptions.RunContinuationsAsynchronously);
            }

            public Func<CommandResult> Action { get; }

            public TaskCompletionSource<CommandResult> Completion { get; }
        }

        private sealed class CommandResult
        {
            public CommandResult(int statusCode, Dictionary<string, object> body)
            {
                StatusCode = statusCode;
                Body = body;
            }

            public int StatusCode { get; }

            public Dictionary<string, object> Body { get; }
        }
    }
}
