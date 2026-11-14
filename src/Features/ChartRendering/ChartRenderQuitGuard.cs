using System;
using UnityEngine;

namespace ADOFAI.EditorTweaks.ChartRendering.Features.ChartRendering
{
    // Closing the game while a render runs loses the video, and after the last frame is captured the
    // render still needs FFmpeg to finish the file, which players took for "already done". The first
    // close request during a render is refused with a notice; a second one within ConfirmSeconds goes
    // through, so the game can always be closed on purpose (for example when it seems stuck).
    // Ending the process from Task Manager cannot be intercepted.
    internal static class ChartRenderQuitGuard
    {
        private const float ConfirmSeconds = 10f;

        private static bool installed;
        private static float lastRefusedAt = float.NegativeInfinity;

        // Real time of the last refused close request, for the Web UI; negative infinity when none.
        public static float LastRefusedAt => lastRefusedAt;

        public static void Install()
        {
            if (installed)
            {
                return;
            }

            Application.wantsToQuit += OnWantsToQuit;
            installed = true;
        }

        public static void Uninstall()
        {
            if (!installed)
            {
                return;
            }

            Application.wantsToQuit -= OnWantsToQuit;
            installed = false;
        }

        private static bool OnWantsToQuit()
        {
            if (!ChartRenderService.IsActive)
            {
                return true;
            }

            float now = Time.realtimeSinceStartup;
            if (now - lastRefusedAt <= ConfirmSeconds)
            {
                Main.Log("Game closed during a render after a second close request; the video will be incomplete.");
                return true;
            }

            lastRefusedAt = now;
            string message = Settings.Text(ChartRenderService.IsWritingOutput ? "chartRendererQuitBlockedWriting" : "chartRendererQuitBlocked");
            Main.Log("Close request refused during a render: " + message);
            try
            {
                ADOBase.editor?.ShowNotification(message, null, 4f);
            }
            catch (Exception ex)
            {
                Main.Log("Failed to show the close notice: " + ex.Message);
            }

            return false;
        }
    }
}
