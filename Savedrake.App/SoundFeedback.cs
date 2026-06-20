using System;
using System.IO;
using System.Media;

namespace Savedrake.App
{
    // Short success/error feedback chimes played after a backup, ported from the WinForms PlayBackupSound. The .wav
    // files ship next to Savedrake.App.exe and are resolved against the install directory (AppDomain base dir), not
    // the working directory, so they play no matter how the app was launched. One reusable SoundPlayer is kept and
    // re-pointed (Play() with SND_ASYNC replaces whatever was playing, so this is correct and leak-free). Best-effort:
    // a missing file or any audio/IO error is swallowed — sound is never allowed to disrupt or fail a backup.
    internal sealed class SoundFeedback
    {
        private SoundPlayer _player;

        public void Success() => Play("success.wav");
        public void Error() => Play("error.wav");

        private void Play(string wavFileName)
        {
            try
            {
                string path = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, wavFileName);
                if (!File.Exists(path)) return;
                if (_player == null) _player = new SoundPlayer();
                _player.SoundLocation = path;
                _player.Play(); // async (SND_ASYNC); does not block the UI thread
            }
            catch
            {
                // Sound is non-essential; swallow any audio/IO failure.
            }
        }
    }
}
