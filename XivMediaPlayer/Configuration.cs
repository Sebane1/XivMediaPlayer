using Dalamud.Configuration;
using Dalamud.Plugin;
using System;
using System.Collections.Generic;

namespace XivMediaPlayer {
  [Serializable]
  public class RoomMediaState {
      public string CurrentUrl { get; set; } = "";
      public long TimecodeMs { get; set; } = 0;
      public List<string> Playlist { get; set; } = new List<string>();
  }

  [Serializable]
  public class MediaHistoryEntry {
      public string Url { get; set; } = "";
      public string Title { get; set; } = "";
      public long TimecodeMs { get; set; } = 0;
      public DateTime LastPlayed { get; set; } = DateTime.UtcNow;
  }

  [Serializable]
  public class Configuration : IPluginConfiguration {
    public event EventHandler OnConfigurationChanged;

    private float _livestreamVolume = 0.5f;
    private bool _tuneIntoTwitchStreams = true;
    private bool _tuneIntoTwitchStreamPrompt = true;
    private int _defaultVideoOpen = 1; // 0 = open, 1 = closed
    private bool _enableOutdoorPublicScreens = false; // Opt-in
    private bool _onlySafeDomainsPublicScreens = true;
    private bool _spatialAudioEnabled = true;

    int IPluginConfiguration.Version { get; set; }

    #region Saved configuration values

    public float LivestreamVolume { get => _livestreamVolume; set => _livestreamVolume = value; }
    public bool TuneIntoTwitchStreams { get => _tuneIntoTwitchStreams; set => _tuneIntoTwitchStreams = value; }
    public bool TuneIntoTwitchStreamPrompt { get => _tuneIntoTwitchStreamPrompt; set => _tuneIntoTwitchStreamPrompt = value; }
    public int DefaultVideoOpen { get => _defaultVideoOpen; set => _defaultVideoOpen = value; }
    
    public bool EnableOutdoorPublicScreens { get => _enableOutdoorPublicScreens; set => _enableOutdoorPublicScreens = value; }
    public bool OnlySafeDomainsPublicScreens { get => _onlySafeDomainsPublicScreens; set => _onlySafeDomainsPublicScreens = value; }
    public bool SpatialAudioEnabled { get => _spatialAudioEnabled; set => _spatialAudioEnabled = value; }
    public bool ShowOutdoorGridDebug { get; set; } = false;
    public bool HasAutoDetectedAMD_v2 { get; set; } = false;
    public bool DepthOcclusionEnabled { get; set; } = true;
    public bool TvGlowEnabled { get; set; } = true;
    public bool DisableUIBlockDetection { get; set; } = false;
    public bool AutoResumeMedia { get; set; } = true;
    public bool VerboseChatLogging { get; set; } = false;
    public bool EnableWanderersCampfireFix { get; set; } = false;
    public bool EnableUiCulling { get; set; } = true;
    public float UIBlendThreshold { get; set; } = 0.0f;

    // yt-dlp settings
    public int PreferredQuality { get; set; } = 720;
    public bool EnableSabrProxy { get; set; } = false;

    // World screen compositing settings (legacy single placement)
    public MediaPlayerCore.Compositing.WorldScreenTransform WorldScreen { get; set; } = new MediaPlayerCore.Compositing.WorldScreenTransform();

    // Per-location screen placements: key = location string, value = transform
    public Dictionary<string, MediaPlayerCore.Compositing.WorldScreenTransform> ScreenPlacements { get; set; }
      = new Dictionary<string, MediaPlayerCore.Compositing.WorldScreenTransform>();

    public Dictionary<string, RoomMediaState> RoomMediaStates { get; set; } = new Dictionary<string, RoomMediaState>();
    public Dictionary<string, MediaHistoryEntry> WatchHistory { get; set; } = new Dictionary<string, MediaHistoryEntry>();

    public string ServerUrl { get; set; } = "http://24.77.70.65:5000";

    // Unique identity for the local user to establish TV ownership
    public string OwnerId { get; set; } = Guid.NewGuid().ToString();

    // Playback controls
    public int SeekIncrementSeconds { get; set; } = 10;
    public bool LoopEnabled { get; set; } = false;
    public bool ShuffleEnabled { get; set; } = false;

    #endregion

    [NonSerialized]
    private IDalamudPluginInterface pluginInterface;

    /// <summary>
    /// Parameterless constructor required for Dalamud deserialization.
    /// </summary>
    public Configuration() { }

    /// <summary>
    /// Call after construction or deserialization to wire up the save interface.
    /// </summary>
    public void Initialize(IDalamudPluginInterface pi) {
      this.pluginInterface = pi;
    }

    public void Save() {
      if (this.pluginInterface != null) {
        this.pluginInterface.SavePluginConfig(this);
      }
      OnConfigurationChanged?.Invoke(this, EventArgs.Empty);
    }
  }
}
