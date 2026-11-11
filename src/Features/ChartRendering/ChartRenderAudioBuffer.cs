using System;
using UnityEngine;

namespace ADOFAI.EditorTweaks.ChartRendering.Features.ChartRendering
{
    // Ported from CheryTools AET Recorder (sui), with the reload wait limited to external songs.
    //
    // Unity's AudioRenderer returns no samples at all when the DSP buffer is larger than
    // RenderDspBufferSize (a 1024 buffer gave zero samples for a whole render), so renders switch the
    // buffer temporarily and switch it back afterwards.
    //
    // Changing the buffer needs AudioSettings.Reset, which restarts the audio system and drops the data
    // of every AudioClip loaded at runtime: a custom level's song, and its custom sounds. Hit sounds are
    // game assets and survive. The original code called Reset at the end of every render even when the
    // buffer had not changed, so every render after the first had hit sounds but no music until the
    // level was reloaded. Reset now only runs when the buffer really changes, and the level song and
    // custom sounds are reloaded through scnGame after each reset; the render waits for the song
    // (IsReloadingSong) before starting playback. Clips owned by other mods (e.g. CReplay key sounds)
    // cannot be reloaded from here. Players whose buffer is already at most RenderDspBufferSize never
    // trigger a reset.
    internal sealed class ChartRenderAudioBuffer
    {
        private const int RenderDspBufferSize = 256;

        private AudioConfiguration savedConfiguration;
        private bool waitForSongReload;
        private bool active;
        private bool changed;

        public bool IsActive => active;

        public static int RequiredDspBufferSize => RenderDspBufferSize;

        // True while an external song still has no clip or is still loading after the reset. This
        // also covers a clip that is cleared and then reused, where comparing object references
        // would either return too early (null) or wait forever (same reference).
        public bool IsReloadingSong
        {
            get
            {
                if (!waitForSongReload)
                {
                    return false;
                }

                AudioSource? song = ADOBase.conductor?.song;
                if (song == null || song.clip == null)
                {
                    return true;
                }

                return song.clip.loadState != AudioDataLoadState.Loaded;
            }
        }

        public void Begin()
        {
            if (active)
            {
                return;
            }

            savedConfiguration = AudioSettings.GetConfiguration();
            active = true;
            int originalSize = savedConfiguration.dspBufferSize;
            if (originalSize <= RenderDspBufferSize)
            {
                ChartRenderDiagnostics.Log("Render audio DSP buffer unchanged. dspBufferSize=" + originalSize
                    + " sampleRate=" + savedConfiguration.sampleRate + ".");
                return;
            }

            AudioConfiguration renderConfiguration = savedConfiguration;
            renderConfiguration.dspBufferSize = RenderDspBufferSize;
            // Mark the change before resetting: if ReloadSong/ReloadCustomSounds throws, Restore
            // must still put the original DSP buffer back instead of leaving it at 256.
            changed = true;
            ResetAndReloadLevelAudio(renderConfiguration);
            ChartRenderDiagnostics.Log("Render audio DSP buffer set temporarily. original=" + originalSize
                + " render=" + RenderDspBufferSize + "; reloading level song and custom sounds.");
        }

        public void Restore()
        {
            if (!active)
            {
                return;
            }

            active = false;
            if (!changed)
            {
                return;
            }

            changed = false;
            try
            {
                ResetAndReloadLevelAudio(savedConfiguration);
                ChartRenderDiagnostics.Log("Render audio DSP buffer restored. dspBufferSize="
                    + savedConfiguration.dspBufferSize + "; reloading level song and custom sounds.");
            }
            catch (Exception exception)
            {
                ChartRenderDiagnostics.Log("Failed to restore render audio DSP buffer: " + exception.Message);
            }
        }

        private void ResetAndReloadLevelAudio(AudioConfiguration configuration)
        {
            // External songs are loaded at runtime and lose their data in AudioSettings.Reset;
            // official and bundled levels use game assets, which survive the reset and may come
            // back as the same clip. Wait on the clip's load state instead of object identity so a
            // cleared clip and a reused clip are both handled.
            waitForSongReload = !ADOBase.isInternalLevel && !ADOBase.isBundleLevel;
            AudioSettings.Reset(configuration);
            scnGame? game = scnGame.instance;
            if (game == null)
            {
                waitForSongReload = false;
                return;
            }

            game.ReloadSong(force: true);
            game.ReloadCustomSounds(force: true);
        }
    }
}
