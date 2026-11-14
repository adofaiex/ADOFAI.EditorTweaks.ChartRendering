using System;

namespace ADOFAI.EditorTweaks.ChartRendering.Features.WebUi
{
    // How the hotkey opens the settings page. Auto tries the Steam overlay and falls back to the
    // system browser; Steam keeps the overlay preference; Browser always uses the system browser.
    internal static class WebUiOpenTargets
    {
        public const string Auto = "auto";
        public const string Steam = "steam";
        public const string Browser = "browser";

        public static string Normalize(string? value)
        {
            if (string.Equals(value, Steam, StringComparison.OrdinalIgnoreCase))
            {
                return Steam;
            }

            if (string.Equals(value, Browser, StringComparison.OrdinalIgnoreCase))
            {
                return Browser;
            }

            return Auto;
        }
    }
}
