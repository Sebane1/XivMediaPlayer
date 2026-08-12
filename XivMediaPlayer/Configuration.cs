using Dalamud.Configuration;
using Dalamud.Plugin;
using System;
using System.Collections.Generic;
using XivMediaPlayer.Localization;

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
    public bool EnableSabrProxy { get; set; } = true;

    /// <summary>Automatically upload XivMediaPlayer warnings/errors from dalamud.log to the sync server.</summary>
    public bool AutoSendDiagnosticLogs { get; set; } = false;

    /// <summary>Show a chat hint when new plugin warnings/errors are detected in dalamud.log.</summary>
    public bool NotifyOnDiagnosticLogs { get; set; } = true;

    public const int CurrentConfigVersion = 7;

    public const string DefaultTranslationServerUrl = "http://ai.hubujubu.com:5681";

    /// <summary>UI language index matching <see cref="LanguageEnum"/>.</summary>
    public int UiLanguage { get; set; } = (int)LanguageEnum.English;

    /// <summary>Dev-only override for the translation proxy. Ignored unless <see cref="DevMode"/> is enabled.</summary>
    public string TranslationServerUrl { get; set; } = DefaultTranslationServerUrl;

    /// <summary>Unlocks developer settings such as the translation server URL override.</summary>
    public bool DevMode { get; set; } = false;

    public string GetEffectiveTranslationServerUrl()
    {
      if (DevMode && !string.IsNullOrWhiteSpace(TranslationServerUrl))
      {
        return TranslationServerUrl.Trim().TrimEnd('/');
      }

      return DefaultTranslationServerUrl;
    }

    // World screen compositing settings (legacy single placement)
    public MediaPlayerCore.Compositing.WorldScreenTransform WorldScreen { get; set; } = new MediaPlayerCore.Compositing.WorldScreenTransform();

    // Per-location screen placements: key = location string, value = transform
    public Dictionary<string, MediaPlayerCore.Compositing.WorldScreenTransform> ScreenPlacements { get; set; }
      = new Dictionary<string, MediaPlayerCore.Compositing.WorldScreenTransform>();

    // Per-TV screen placements: key = "{locationKey}#{tvId}"
    public Dictionary<string, MediaPlayerCore.Compositing.WorldScreenTransform> ScreenPlacementsByTvId { get; set; }
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

    /// <summary>
    /// Applies one-time config upgrades for existing installs.
    /// </summary>
    public void Migrate() {
      var versioned = (IPluginConfiguration)this;
      if (versioned.Version >= CurrentConfigVersion) {
        return;
      }

      // v2: SABR for YouTube VOD + automatic live detection became the default path.
      if (versioned.Version < 2) {
        EnableSabrProxy = true;
      }

      if (versioned.Version < 3) {
        UiLanguage = (int)LanguageEnum.English;
      }

      if (versioned.Version < 4) {
        if (string.IsNullOrWhiteSpace(TranslationServerUrl)) {
          TranslationServerUrl = "http://ai.hubujubu.com:5681";
        }
      }

      // v5: revert mistaken localhost default from an earlier v4 build
      if (versioned.Version < 5) {
        if (string.Equals(TranslationServerUrl, "http://127.0.0.1:5681", StringComparison.OrdinalIgnoreCase)
            || string.Equals(TranslationServerUrl, "http://localhost:5681", StringComparison.OrdinalIgnoreCase)) {
          TranslationServerUrl = "http://ai.hubujubu.com:5681";
        }
      }

      if (versioned.Version < 6) {
        AutoSendDiagnosticLogs = false;
        NotifyOnDiagnosticLogs = true;
      }

      versioned.Version = CurrentConfigVersion;
      Save();
    }

    public void Save() {
      if (this.pluginInterface != null) {
        this.pluginInterface.SavePluginConfig(this);
      }
      OnConfigurationChanged?.Invoke(this, EventArgs.Empty);
    }
  }
}
