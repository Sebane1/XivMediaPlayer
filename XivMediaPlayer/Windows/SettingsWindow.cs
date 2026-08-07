using Dalamud.Interface.Windowing;
using Dalamud.Bindings.ImGui;
using System;
using System.Numerics;

namespace XivMediaPlayer.Windows {
  internal class SettingsWindow : Window {
    private Plugin _plugin;
    private Action _onVolumeFix;

    public SettingsWindow(Plugin plugin, Action onVolumeFix = null) :
      base("Media Player Settings", ImGuiWindowFlags.NoCollapse | ImGuiWindowFlags.AlwaysAutoResize, false) {
      _plugin = plugin;
      _onVolumeFix = onVolumeFix;
      Size = new Vector2(420, 0);
      SizeCondition = ImGuiCond.FirstUseEver;
    }

    public override void Draw() {
      // Volume 
      ImGui.TextColored(new Vector4(0.7f, 0.9f, 1.0f, 1.0f), "Audio");
      ImGui.Separator();

      float volume = _plugin.Config.LivestreamVolume;
      if (ImGui.SliderFloat("Stream Volume", ref volume, 0f, 3f)) {
        _plugin.Config.LivestreamVolume = volume;
        if (_plugin.MediaManager != null) {
            _plugin.MediaManager.LiveStreamVolume = volume;
        }
        _plugin.Config.Save();
      }

      if (_onVolumeFix != null && ImGui.Button("Fix Game Volume")) {
        _onVolumeFix.Invoke();
      }

      ImGui.Spacing();
      ImGui.Spacing();

      // Twitch 
      ImGui.TextColored(new Vector4(0.7f, 0.9f, 1.0f, 1.0f), "Twitch");
      ImGui.Separator();

      bool tuneInto = _plugin.Config.TuneIntoTwitchStreams;
      if (ImGui.Checkbox("Auto-tune into Twitch streams (in residential areas)", ref tuneInto)) {
        _plugin.Config.TuneIntoTwitchStreams = tuneInto;
        _plugin.Config.Save();
      }

      bool streamPrompt = _plugin.Config.TuneIntoTwitchStreamPrompt;
      if (ImGui.Checkbox("Show stream prompts in chat", ref streamPrompt)) {
        _plugin.Config.TuneIntoTwitchStreamPrompt = streamPrompt;
        _plugin.Config.Save();
      }

      ImGui.Spacing();
      ImGui.Spacing();

      // Video 
      ImGui.TextColored(new Vector4(0.7f, 0.9f, 1.0f, 1.0f), "Video");
      ImGui.Separator();

      bool defaultOpen = _plugin.Config.DefaultVideoOpen == 0;
      if (ImGui.Checkbox("Open video window by default when stream starts", ref defaultOpen)) {
        _plugin.Config.DefaultVideoOpen = defaultOpen ? 0 : 1;
        _plugin.Config.Save();
      }

      bool autoResume = _plugin.Config.AutoResumeMedia;
      if (ImGui.Checkbox("Auto-resume media when entering locations", ref autoResume)) {
        _plugin.Config.AutoResumeMedia = autoResume;
        _plugin.Config.Save();
      }
      bool tvGlow = _plugin.Config.TvGlowEnabled;
      if (ImGui.Checkbox("Enable TV Glow (Ambient Lighting)", ref tvGlow)) {
        _plugin.Config.TvGlowEnabled = tvGlow;
        _plugin.Config.Save();
      }
      if (ImGui.IsItemHovered()) {
        ImGui.SetTooltip("Enables the realistic ambient light that shines on the walls around the TV.");
      }

      bool uiCulling = _plugin.Config.EnableUiCulling;
      if (ImGui.Checkbox("Enable UI Culling", ref uiCulling)) {
        _plugin.Config.EnableUiCulling = uiCulling;
        _plugin.Config.Save();
      }
      if (ImGui.IsItemHovered()) {
        ImGui.SetTooltip("When enabled, the TV will render underneath the games user interface. Disable as a last resort to Reshade ruining the UI buffer.");
      }


      bool strictMasking = _plugin.Config.UIBlendThreshold > 0.5f;
      if (ImGui.Checkbox("Strict UI Masking (AMD Fix / Invisible Drop Shadows)", ref strictMasking)) {
        _plugin.Config.UIBlendThreshold = strictMasking ? (171.0f / 255.0f) : 0.0f;
        _plugin.Config.Save();
      }
      if (ImGui.IsItemHovered()) {
        ImGui.SetTooltip("Enable this if you have an AMD card and notice that the TV does not render. UI dropshadows are lost.");
      }

      /*
      bool reshadeCompat = _plugin.Config.ReShadeCompatibilityMode;
      if (ImGui.Checkbox("ReShade Compatibility Mode", ref reshadeCompat)) {
        _plugin.Config.ReShadeCompatibilityMode = reshadeCompat;
        _plugin.Config.Save();
      }
      if (ImGui.IsItemHovered()) {
        ImGui.SetTooltip("Enable this if you use ReShade and the TV disapears using the lightroom effect.\nThis bypasses the UI alpha channel it breaks by comparing game depth to a grayscale game render to mask out the UI. This fix is very rough.");
      }
      */

      bool disableUiBlock = _plugin.Config.DisableUIBlockDetection;
      if (ImGui.Checkbox("Disable UI Block Detection", ref disableUiBlock)) {
        _plugin.Config.DisableUIBlockDetection = disableUiBlock;
        _plugin.Config.Save();
      }
      if (ImGui.IsItemHovered()) {
        ImGui.SetTooltip("Allows clicking the TV even if the game UI overlaps it. Useful if your visual mods heavily interfere with UI mask detection.");
      }

      bool enableWanderersCampfireFix = _plugin.Config.EnableWanderersCampfireFix;
      if (ImGui.Checkbox("Enable Wanderer's Campfire Fix (For Modded Campfires)", ref enableWanderersCampfireFix)) {
        _plugin.Config.EnableWanderersCampfireFix = enableWanderersCampfireFix;
        _plugin.Config.Save();
      }
      if (ImGui.IsItemHovered()) {
        ImGui.SetTooltip("Enable this if you use modded skybox mods that replace Wanderer's Campfire.");
      }

      if (ImGui.Button("Clear Watch History")) {
        _plugin.Config.WatchHistory.Clear();
        _plugin.Config.Save();
        _plugin.Chat.Print("[Media Player] Watch history cleared.");
      }

      ImGui.Spacing();
      ImGui.Spacing();

      // Outdoor TVs
      ImGui.TextColored(new Vector4(0.7f, 0.9f, 1.0f, 1.0f), "Outdoor TVs");
      ImGui.Separator();

      bool enableOutdoor = _plugin.Config.EnableOutdoorPublicScreens;
      if (ImGui.Checkbox("Enable Public Outdoor Screens", ref enableOutdoor)) {
        _plugin.Config.EnableOutdoorPublicScreens = enableOutdoor;
        _plugin.Config.Save();
        _plugin.HandleOutdoorSettingToggled();
      }

      bool safeMode = _plugin.Config.OnlySafeDomainsPublicScreens;
      if (ImGui.Checkbox("Safe Mode (Only allow safe domains outside)", ref safeMode)) {
        if (!safeMode) {
            ImGui.OpenPopup("Disable Safe Mode Warning");
        } else {
            _plugin.Config.OnlySafeDomainsPublicScreens = true;
            _plugin.Config.Save();
        }
      }
      ImGui.TextColored(new Vector4(0.5f, 0.5f, 0.5f, 1f),
        "Blocks unverified URLs on outdoor screens to prevent abuse.");

      var viewportCenter = ImGui.GetMainViewport().GetCenter();
      ImGui.SetNextWindowPos(viewportCenter, ImGuiCond.Appearing, new Vector2(0.5f, 0.5f));
      if (ImGui.BeginPopupModal("Disable Safe Mode Warning", ImGuiWindowFlags.AlwaysAutoResize | ImGuiWindowFlags.NoSavedSettings)) {
          ImGui.Text("WARNING: Disabling Safe Mode will allow almost any domain to play on outdoor screens (unless otherwise blacklisted by your current server).");
          ImGui.Text("You may be exposed to content that you may not wish to see from unmoderated domains.");
          ImGui.Spacing();
          ImGui.TextColored(new Vector4(1f, 0.3f, 0.3f, 1f), "By clicking 'I Agree', you accept full responsibility for your own screen,");
          ImGui.TextColored(new Vector4(1f, 0.3f, 0.3f, 1f), "and you explicitly agree that you WILL NOT play illegal content.");
          ImGui.Separator();
          ImGui.Spacing();
          
          if (ImGui.Button("I Agree, Disable Safe Mode", new Vector2(250, 0))) {
              _plugin.Config.OnlySafeDomainsPublicScreens = false;
              _plugin.Config.Save();
              ImGui.CloseCurrentPopup();
          }
          ImGui.SameLine();
          if (ImGui.Button("Cancel", new Vector2(120, 0))) {
              ImGui.CloseCurrentPopup();
          }
          ImGui.EndPopup();
      }

      ImGui.Separator();

      bool spatialAudio = _plugin.Config.SpatialAudioEnabled;
      if (ImGui.Checkbox("Enable 3D Spatial Audio", ref spatialAudio)) {
        _plugin.Config.SpatialAudioEnabled = spatialAudio;
        _plugin.Config.Save();
        _plugin.DoRefreshCurrentMedia();
      }
      ImGui.TextColored(new Vector4(0.5f, 0.5f, 0.5f, 1f),
        "Dynamically pans audio to simulate physical TV locations. If you experience A/V sync issues, disable this.");

      ImGui.Separator();
      
      bool showGrid = _plugin.Config.ShowOutdoorGridDebug;
      if (ImGui.Checkbox("Show Outdoor Grid Overlay (Debug)", ref showGrid)) {
        _plugin.Config.ShowOutdoorGridDebug = showGrid;
        _plugin.Config.Save();
      }

      ImGui.Spacing();
      ImGui.Spacing();

      // Playback
      ImGui.TextColored(new Vector4(0.7f, 0.9f, 1.0f, 1.0f), "Playback");
      ImGui.Separator();

      int seekIncrement = _plugin.Config.SeekIncrementSeconds;
      if (ImGui.SliderInt("Seek Increment (seconds)", ref seekIncrement, 1, 60)) {
        _plugin.Config.SeekIncrementSeconds = seekIncrement;
        _plugin.Config.Save();
      }
      ImGui.TextColored(new Vector4(0.5f, 0.5f, 0.5f, 1f),
        "How many seconds the << and >> buttons skip.");

      ImGui.Spacing();
      ImGui.Spacing();

      // Debug
      ImGui.TextColored(new Vector4(0.7f, 0.9f, 1.0f, 1.0f), "Debug");
      ImGui.Separator();

      unsafe
      {
          var housingMgr = FFXIVClientStructs.FFXIV.Client.Game.HousingManager.Instance();
          if (housingMgr != null && !housingMgr->IsInside() && housingMgr->GetCurrentPlot() >= 0 && housingMgr->GetCurrentWard() >= 0)
          {
              ImGui.TextColored(new Vector4(0.4f, 1f, 0.4f, 1f), $"You are standing in Plot {housingMgr->GetCurrentPlot() + 1}");
          }
      }

      string locationKey = _plugin.LocationKey;
      ImGui.TextColored(new Vector4(0.7f, 0.7f, 0.7f, 1f), "Placement Key:");
      ImGui.SameLine();
      ImGui.Text(locationKey ?? "Unknown");
      if (locationKey != null) {
          ImGui.SameLine();
          if (ImGui.Button("Copy##copyloc")) {
              ImGui.SetClipboardText(locationKey);
          }
      }

      if (_plugin.CurrentTvPlacement != null)
      {
          ImGui.TextColored(new Vector4(0.4f, 1f, 0.4f, 1f), "Synced TV Key:");
          ImGui.SameLine();
          ImGui.Text(_plugin.CurrentTvPlacement.LocationKey);
          ImGui.SameLine();
          if (ImGui.Button("Copy##copysyncloc")) {
              ImGui.SetClipboardText(_plugin.CurrentTvPlacement.LocationKey);
          }
      }
      ImGui.Spacing();

      bool verboseChat = _plugin.Config.VerboseChatLogging;
      if (ImGui.Checkbox("Enable Verbose Chat Logging", ref verboseChat)) {
        _plugin.Config.VerboseChatLogging = verboseChat;
        _plugin.Config.Save();
      }
      ImGui.TextColored(new Vector4(0.5f, 0.5f, 0.5f, 1f),
        "Shows detailed plugin status messages in the chat.");

      ImGui.Spacing();
      ImGui.Spacing();

      // yt-dlp quality
      ImGui.TextColored(new Vector4(0.7f, 0.9f, 1.0f, 1.0f), "yt-dlp");
      ImGui.Separator();

      string[] qualityLabels = new string[] { "360p", "480p", "720p", "1080p", "Best" };
      int[] qualityValues = new int[] { 360, 480, 720, 1080, 0 };
      int currentQualityIdx = Array.IndexOf(qualityValues, _plugin.Config.PreferredQuality);
      if (currentQualityIdx < 0) currentQualityIdx = 2; // default 720p
      if (ImGui.Combo("Preferred Quality", ref currentQualityIdx, qualityLabels, qualityLabels.Length)) {
        _plugin.Config.PreferredQuality = qualityValues[currentQualityIdx];
        _plugin.Config.Save();
      }

      bool sabrProxy = _plugin.Config.EnableSabrProxy;
      if (ImGui.Checkbox("Enable SABR Proxy (Fixes YouTube playback errors)", ref sabrProxy)) {
        _plugin.Config.EnableSabrProxy = sabrProxy;
        if (_plugin.YtDlpManager != null) _plugin.YtDlpManager.EnableSabrProxy = sabrProxy;
        _plugin.Config.Save();
      }
      ImGui.TextColored(new Vector4(0.5f, 0.5f, 0.5f, 1f),
        "Pipes the stream through a local server to bypass YouTube's new protocol. Note: Seeking/Scrubbing is currently unsupported in this mode.");

      ImGui.Spacing();

      ImGui.TextColored(new Vector4(0.5f, 0.5f, 0.5f, 1f),
        "yt-dlp is automatically downloaded and updated.");

      if (_plugin.YtDlpManager != null && !_plugin.YtDlpManager.HasCookiesFile) {
        ImGui.Spacing();
        ImGui.TextColored(new Vector4(1f, 0.3f, 0.3f, 1f), "Warning: No cookies.txt found!");
        ImGui.TextWrapped("YouTube now heavily blocks players without cookies. To fix this, you must install the VRCVideoCacher extension in your browser, which locally syncs your cookie data.");
        
        if (ImGui.Button("Chrome/Edge/Brave Extension")) {
            try {
                System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo {
                    FileName = "https://chromewebstore.google.com/detail/vrcvideocacher-cookies-ex/kfgelknbegappcajiflgfbjbdpbpokge",
                    UseShellExecute = true
                });
            } catch { }
        }
        ImGui.SameLine();
        if (ImGui.Button("Firefox Extension")) {
            try {
                System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo {
                    FileName = "https://addons.mozilla.org/en-US/firefox/addon/vrcvideocachercookiesexporter/",
                    UseShellExecute = true
                });
            } catch { }
        }
      }

      ImGui.Spacing();
      ImGui.Spacing();

      // Server Sync
      ImGui.TextColored(new Vector4(0.7f, 0.9f, 1.0f, 1.0f), "Server Sync");
      ImGui.Separator();

      string serverUrl = _plugin.Config.ServerUrl;
      if (ImGui.InputText("Server URL", ref serverUrl, 256)) {
        _plugin.Config.ServerUrl = serverUrl;
        _plugin.Config.Save();
      }
      ImGui.TextColored(new Vector4(0.5f, 0.5f, 0.5f, 1f),
        "URL of the backend server used to sync TVs.");

      ImGui.Spacing();
      ImGui.Spacing();

      // Help & Support
      ImGui.TextColored(new Vector4(0.7f, 0.9f, 1.0f, 1.0f), "Help & Support");
      ImGui.Separator();

      if (ImGui.Button("Tutorial Video (How to Place TVs)")) {
          try {
              System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo {
                  FileName = "https://www.youtube.com/watch?v=ZgLs2OJQ8ks",
                  UseShellExecute = true
              });
          } catch { }
      }

      ImGui.SameLine();

      if (ImGui.Button("Join Support Discord")) {
          try {
              System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo {
                  FileName = "https://discord.gg/rtGXwMn7pX",
                  UseShellExecute = true
              });
          } catch { }
      }

      ImGui.Spacing();

      if (ImGui.Button("Support the Developer on Ko-fi")) {
          try {
              System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo {
                  FileName = "https://ko-fi.com/sebastina",
                  UseShellExecute = true
              });
          } catch { }
      }
    }
  }
}
