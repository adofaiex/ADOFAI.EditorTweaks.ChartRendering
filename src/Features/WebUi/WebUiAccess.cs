using System;
using System.Net;
using System.Security.Cryptography;
using System.Text;

namespace ADOFAI.EditorTweaks.ChartRendering.Features.WebUi
{
    internal enum WebUiAccessLevel
    {
        None = 0,
        Viewer = 1,
        Full = 2
    }

    // Who may use the settings page.
    //
    // - The per-launch access token (in the link the mod opens, and in the QR code) gives full control.
    // - Opening the page once with that token stores a "remember this device" cookie. Its value is a
    //   key kept in Settings, so the device stays signed in across game restarts without copying a
    //   new link each time; the tablet can simply bookmark http://<pc>:<port>/.
    // - A separate view key gives a read-only link: progress and settings summary only, no start,
    //   cancel or settings changes, and no addresses, tokens or local paths in the state it receives.
    // - "Forget all devices" regenerates both keys and the current launch's pairing token.
    //
    // Cookies are HttpOnly and SameSite=Strict, so another website in the same browser cannot use them.
    internal static class WebUiAccess
    {
        public const int DefaultLanPort = 18200;
        public const string DeviceCookie = "aetc_device";
        public const string ViewCookie = "aetc_view";
        private const int CookieMaxAgeSeconds = 365 * 24 * 3600;

        public static int NormalizePort(int port)
        {
            return port >= 1024 && port <= 65535 ? port : DefaultLanPort;
        }

        // Creates the device and view keys the first time. Returns true when settings changed.
        public static bool EnsureKeys(Settings settings)
        {
            bool changed = false;
            if (string.IsNullOrEmpty(settings.WebUiDeviceKey) || settings.WebUiDeviceKey.Length < 32)
            {
                settings.WebUiDeviceKey = NewKey();
                changed = true;
            }

            if (string.IsNullOrEmpty(settings.WebUiViewKey) || settings.WebUiViewKey.Length < 32)
            {
                settings.WebUiViewKey = NewKey();
                changed = true;
            }

            return changed;
        }

        public static string ForgetAllDevices(Settings settings)
        {
            string deviceKey = NewKey();
            string viewKey = NewKey();
            string pairingToken = NewKey();
            settings.WebUiDeviceKey = deviceKey;
            settings.WebUiViewKey = viewKey;
            return pairingToken;
        }

        public static string NewKey()
        {
            byte[] bytes = new byte[24];
            using (RandomNumberGenerator rng = RandomNumberGenerator.Create())
            {
                rng.GetBytes(bytes);
            }

            StringBuilder builder = new StringBuilder(bytes.Length * 2);
            foreach (byte b in bytes)
            {
                builder.Append(b.ToString("x2"));
            }

            return builder.ToString();
        }

        public static WebUiAccessLevel Classify(HttpListenerRequest request, string accessToken, Settings? settings, out bool viaToken, out bool viaView)
        {
            viaToken = false;
            viaView = false;
            string token = request.Headers["X-EditorTweaks-Token"] ?? GetQuery(request.Url, "token");
            if (!string.IsNullOrEmpty(token) && FixedEquals(token, accessToken))
            {
                viaToken = true;
                return WebUiAccessLevel.Full;
            }

            if (settings == null)
            {
                return WebUiAccessLevel.None;
            }

            if (FixedEquals(GetCookie(request, DeviceCookie), settings.WebUiDeviceKey))
            {
                return WebUiAccessLevel.Full;
            }

            string view = GetQuery(request.Url, "view");
            if (!string.IsNullOrEmpty(view) && FixedEquals(view, settings.WebUiViewKey))
            {
                viaView = true;
                return WebUiAccessLevel.Viewer;
            }

            if (FixedEquals(GetCookie(request, ViewCookie), settings.WebUiViewKey))
            {
                return WebUiAccessLevel.Viewer;
            }

            return WebUiAccessLevel.None;
        }

        // Called when a page is loaded with ?token= or ?view=, so the device is remembered.
        public static void Remember(HttpListenerResponse response, Settings settings, WebUiAccessLevel level)
        {
            if (level == WebUiAccessLevel.Full)
            {
                response.AppendHeader("Set-Cookie", BuildCookie(DeviceCookie, settings.WebUiDeviceKey));
            }
            else if (level == WebUiAccessLevel.Viewer)
            {
                response.AppendHeader("Set-Cookie", BuildCookie(ViewCookie, settings.WebUiViewKey));
            }
        }

        private static string BuildCookie(string name, string value)
        {
            return name + "=" + value + "; Path=/; Max-Age=" + CookieMaxAgeSeconds + "; HttpOnly; SameSite=Strict";
        }

        private static string GetCookie(HttpListenerRequest request, string name)
        {
            string? header = request.Headers["Cookie"];
            if (string.IsNullOrEmpty(header))
            {
                return string.Empty;
            }

            foreach (string part in header.Split(';'))
            {
                string trimmed = part.Trim();
                if (trimmed.StartsWith(name + "=", StringComparison.Ordinal))
                {
                    return trimmed.Substring(name.Length + 1);
                }
            }

            return string.Empty;
        }

        public static string GetQuery(Uri? uri, string key)
        {
            if (uri == null || string.IsNullOrEmpty(uri.Query))
            {
                return string.Empty;
            }

            foreach (string part in uri.Query.TrimStart('?').Split('&'))
            {
                int eq = part.IndexOf('=');
                string name = eq < 0 ? part : part.Substring(0, eq);
                if (string.Equals(Uri.UnescapeDataString(name), key, StringComparison.Ordinal))
                {
                    return eq < 0 ? string.Empty : Uri.UnescapeDataString(part.Substring(eq + 1));
                }
            }

            return string.Empty;
        }

        // Constant-time comparison so response timing does not leak how much of a key matched.
        private static bool FixedEquals(string a, string b)
        {
            if (string.IsNullOrEmpty(a) || string.IsNullOrEmpty(b) || a.Length != b.Length)
            {
                return false;
            }

            int diff = 0;
            for (int i = 0; i < a.Length; i++)
            {
                diff |= a[i] ^ b[i];
            }

            return diff == 0;
        }

        // For logs: never write a token or key.
        public static string Redact(string text, string accessToken, Settings? settings)
        {
            if (string.IsNullOrEmpty(text))
            {
                return text;
            }

            if (!string.IsNullOrEmpty(accessToken))
            {
                text = text.Replace(accessToken, "<hidden>");
            }

            if (settings != null)
            {
                if (!string.IsNullOrEmpty(settings.WebUiDeviceKey))
                {
                    text = text.Replace(settings.WebUiDeviceKey, "<hidden>");
                }

                if (!string.IsNullOrEmpty(settings.WebUiViewKey))
                {
                    text = text.Replace(settings.WebUiViewKey, "<hidden>");
                }
            }

            return text;
        }
    }
}
