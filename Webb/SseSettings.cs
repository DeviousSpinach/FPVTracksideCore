using System;
using System.ComponentModel;
using Tools;

namespace Webb
{
    public class SseSettings
    {
        public static SseSettings Instance { get; private set; }

        [Category("SSE")]
        [DisplayName("Replay Buffer Size")]
        [Description("Number of recent events stored for catch-up when a client reconnects. Ephemeral events are never counted against this limit.")]
        public int ReplayBufferSize { get; set; }

        [Category("SSE")]
        [DisplayName("Ephemeral Events")]
        [Description("Events broadcast to live clients but never stored in the replay buffer. Comma-separated when edited by hand.")]
        public string[] EphemeralEvents { get; set; }

        public SseSettings()
        {
            ReplayBufferSize = 100;
            EphemeralEvents = new[] { "race_time_remaining" };
        }

        private const string Filename = "SseSettings.xml";
        private const string Directory = "data";

        public static SseSettings Load()
        {
            SseSettings settings = null;
            try
            {
                SseSettings[] s = IOTools.Read<SseSettings>(Directory, Filename);
                if (s != null && s.Length > 0)
                    settings = s[0];
            }
            catch { }

            if (settings == null)
            {
                settings = new SseSettings();
                Save(settings);
            }

            Instance = settings;
            return settings;
        }

        public static void Save(SseSettings s)
        {
            IOTools.Write(Directory, Filename, s);
        }
    }
}
