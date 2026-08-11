using Dalamud.Interface.Windowing;
using Dalamud.Bindings.ImGui;
using System;
using System.Numerics;
using System.Threading.Tasks;
using XivMediaPlayer.Localization;

namespace XivMediaPlayer.Windows {
  internal class SettingsWindow : Window {
    private Plugin _plugin;
    private Action _onVolumeFix;
    private string _diagnosticUserNote = string.Empty;

    public SettingsWindow(Plugin plugin, Action onVolumeFix = null) :
      base("Media Player Settings", ImGuiWindowFlags.NoCollapse, false) {
      _plugin = plugin;
      _onVolumeFix = onVolumeFix;
      Size = new Vector2(440, 520);
      SizeCondition = ImGuiCond.FirstUseEver;
    }

    private string Localize(string text) => _plugin.Translate(text);

    public override void Draw() {
      WindowName = Localize("Media Player Settings");
      _ = _plugin.TranslationRevision;

      if (ImGui.BeginTabBar("MediaPlayerSettingsTabs")) {
        if (ImGui.BeginTabItem(Localize("General"))) {
          DrawGeneralTab();
          ImGui.EndTabItem();
        }
        if (ImGui.BeginTabItem(Localize("Display"))) {
          DrawDisplayTab();
          ImGui.EndTabItem();
        }
        if (ImGui.BeginTabItem(Localize("Outdoor"))) {
          DrawOutdoorTab();
          ImGui.EndTabItem();
        }
        if (ImGui.BeginTabItem(Localize("Sources"))) {
          DrawSourcesTab();
          ImGui.EndTabItem();
        }
        if (ImGui.BeginTabItem(Localize("Advanced"))) {
          DrawAdvancedTab();
          ImGui.EndTabItem();
        }
        ImGui.EndTabBar();
      }

      ImGui.Spacing();
      ImGui.Separator();
      DrawAboutSection();
      DrawSafeModePopup();
    }

    private void DrawTranslationStatus(int langIdx)
    {
      if (langIdx == (int)LanguageEnum.English)
      {
        ImGui.TextColored(new Vector4(0.6f, 0.6f, 0.6f, 1f), Localize("English selected — no online translation needed."));
        return;
      }

      int cached = Translator.GetCachedCount((LanguageEnum)langIdx);
      if (!string.IsNullOrWhiteSpace(Translator.LastErrorMessage))
      {
        ImGui.TextColored(new Vector4(1f, 0.35f, 0.35f, 1f),
          _plugin.Config.DevMode
            ? string.Format(Localize("Translation server unreachable. UI stays in English until {0} responds."), Translator.ServerUrlDisplay)
            : Localize("Translation server unreachable. UI stays in English until the translation service responds."));
        ImGui.TextWrapped(Translator.LastErrorMessage);
      }
      else if (Translator.ServerRespondedSuccessfully || cached > 0)
      {
        ImGui.TextColored(new Vector4(0.4f, 1f, 0.5f, 1f),
          string.Format(Localize("{0} strings cached. New text appears as it is translated."), cached));
        if (Translator.PendingRequestCount > 0)
        {
          ImGui.TextColored(new Vector4(1f, 0.85f, 0.3f, 1f),
            string.Format(Localize("Fetching translations... ({0} in progress)"), Translator.PendingRequestCount));
        }
      }
      else
      {
        ImGui.TextColored(new Vector4(1f, 0.85f, 0.3f, 1f), Localize("Contacting translation server..."));
      }
    }

    private void DrawGeneralTab() {
      ImGui.TextColored(new Vector4(0.7f, 0.9f, 1.0f, 1.0f), Localize("Language"));
      ImGui.Separator();

      int langIdx = Math.Clamp(_plugin.Config.UiLanguage, 0, Translator.LanguageStringsDisplay.Length - 1);
      if (ImGui.Combo(Localize("Interface Language"), ref langIdx, Translator.LanguageStringsDisplay, Translator.LanguageStringsDisplay.Length)) {
        _plugin.Config.UiLanguage = langIdx;
        _plugin.Config.Save();
        _plugin.ApplyUiLanguageFromConfig();
      }
      if (ImGui.IsItemHovered()) {
        ImGui.SetTooltip(Localize("Choose the language for plugin menus and controls. Translations are fetched online and cached locally."));
      }

      DrawTranslationStatus(langIdx);

      if (_plugin.Config.DevMode) {
        ImGui.Spacing();
        ImGui.TextColored(new Vector4(0.7f, 0.9f, 1.0f, 1.0f), Localize("Developer"));
        ImGui.Separator();

        string translationServerUrl = _plugin.Config.TranslationServerUrl ?? Configuration.DefaultTranslationServerUrl;
        if (ImGui.InputText(Localize("Translation Server URL"), ref translationServerUrl, 256)) {
          _plugin.Config.TranslationServerUrl = translationServerUrl;
          _plugin.Config.Save();
          _plugin.ApplyUiLanguageFromConfig();
        }
        if (ImGui.IsItemHovered()) {
          ImGui.SetTooltip(Localize("Override the RoleplayingQuestCore-compatible translation proxy (e.g. local loopback or LAN IP)."));
        }
      }

      ImGui.Spacing();
      ImGui.TextColored(new Vector4(0.7f, 0.9f, 1.0f, 1.0f), Localize("Audio"));
      ImGui.Separator();

      float volume = _plugin.Config.LivestreamVolume;
      if (ImGui.SliderFloat(Localize("Stream Volume"), ref volume, 0f, 3f)) {
        _plugin.Config.LivestreamVolume = volume;
        if (_plugin.MediaManager != null) {
          _plugin.MediaManager.LiveStreamVolume = volume;
        }
        _plugin.Config.Save();
      }

      if (_onVolumeFix != null && ImGui.Button(Localize("Fix Game Volume"))) {
        _onVolumeFix.Invoke();
      }

      bool spatialAudio = _plugin.Config.SpatialAudioEnabled;
      if (ImGui.Checkbox(Localize("Enable 3D Spatial Audio"), ref spatialAudio)) {
        _plugin.Config.SpatialAudioEnabled = spatialAudio;
        _plugin.Config.Save();
        _plugin.DoRefreshCurrentMedia();
      }
      if (ImGui.IsItemHovered()) {
        ImGui.SetTooltip(Localize("Dynamically pans audio to simulate physical TV locations. If you experience A/V sync issues, disable this."));
      }

      ImGui.Spacing();
      ImGui.TextColored(new Vector4(0.7f, 0.9f, 1.0f, 1.0f), Localize("Twitch"));
      ImGui.Separator();

      bool tuneInto = _plugin.Config.TuneIntoTwitchStreams;
      if (ImGui.Checkbox(Localize("Auto-tune into Twitch streams (in residential areas)"), ref tuneInto)) {
        _plugin.Config.TuneIntoTwitchStreams = tuneInto;
        _plugin.Config.Save();
      }

      bool streamPrompt = _plugin.Config.TuneIntoTwitchStreamPrompt;
      if (ImGui.Checkbox(Localize("Show stream prompts in chat"), ref streamPrompt)) {
        _plugin.Config.TuneIntoTwitchStreamPrompt = streamPrompt;
        _plugin.Config.Save();
      }

      ImGui.Spacing();
      ImGui.TextColored(new Vector4(0.7f, 0.9f, 1.0f, 1.0f), Localize("Playback"));
      ImGui.Separator();

      bool defaultOpen = _plugin.Config.DefaultVideoOpen == 0;
      if (ImGui.Checkbox(Localize("Open video window by default when stream starts"), ref defaultOpen)) {
        _plugin.Config.DefaultVideoOpen = defaultOpen ? 0 : 1;
        _plugin.Config.Save();
      }

      bool autoResume = _plugin.Config.AutoResumeMedia;
      if (ImGui.Checkbox(Localize("Auto-resume media when entering locations"), ref autoResume)) {
        _plugin.Config.AutoResumeMedia = autoResume;
        _plugin.Config.Save();
      }

      int seekIncrement = _plugin.Config.SeekIncrementSeconds;
      if (ImGui.SliderInt(Localize("Seek Increment (seconds)"), ref seekIncrement, 1, 60)) {
        _plugin.Config.SeekIncrementSeconds = seekIncrement;
        _plugin.Config.Save();
      }
      if (ImGui.IsItemHovered()) {
        ImGui.SetTooltip(Localize("How many seconds the << and >> buttons skip."));
      }

      ImGui.Spacing();
      if (ImGui.Button(Localize("Clear Watch History"))) {
        _plugin.Config.WatchHistory.Clear();
        _plugin.Config.Save();
        _plugin.PrintChat("[Media Player] Watch history cleared.");
      }
    }

    private void DrawDisplayTab() {
      ImGui.TextColored(new Vector4(0.7f, 0.9f, 1.0f, 1.0f), Localize("Rendering"));
      ImGui.Separator();

      bool tvGlow = _plugin.Config.TvGlowEnabled;
      if (ImGui.Checkbox(Localize("Enable TV Glow (Ambient Lighting)"), ref tvGlow)) {
        _plugin.Config.TvGlowEnabled = tvGlow;
        _plugin.Config.Save();
      }
      if (ImGui.IsItemHovered()) {
        ImGui.SetTooltip(Localize("Enables the realistic ambient light that shines on the walls around the TV."));
      }

      bool uiCulling = _plugin.Config.EnableUiCulling;
      if (ImGui.Checkbox(Localize("Enable UI Culling"), ref uiCulling)) {
        _plugin.Config.EnableUiCulling = uiCulling;
        _plugin.Config.Save();
      }
      if (ImGui.IsItemHovered()) {
        ImGui.SetTooltip(Localize("When enabled, the TV will render underneath the games user interface. Disable as a last resort to Reshade ruining the UI buffer."));
      }

      bool strictMasking = _plugin.Config.UIBlendThreshold > 0.5f;
      if (ImGui.Checkbox(Localize("Strict UI Masking (AMD Fix / Invisible Drop Shadows)"), ref strictMasking)) {
        _plugin.Config.UIBlendThreshold = strictMasking ? (171.0f / 255.0f) : 0.0f;
        _plugin.Config.Save();
      }
      if (ImGui.IsItemHovered()) {
        ImGui.SetTooltip(Localize("Enable this if you have an AMD card and notice that the TV does not render. UI dropshadows are lost."));
      }

      bool disableUiBlock = _plugin.Config.DisableUIBlockDetection;
      if (ImGui.Checkbox(Localize("Disable UI Block Detection"), ref disableUiBlock)) {
        _plugin.Config.DisableUIBlockDetection = disableUiBlock;
        _plugin.Config.Save();
      }
      if (ImGui.IsItemHovered()) {
        ImGui.SetTooltip(Localize("Allows clicking the TV even if the game UI overlaps it. Useful if your visual mods heavily interfere with UI mask detection."));
      }

      bool enableWanderersCampfireFix = _plugin.Config.EnableWanderersCampfireFix;
      if (ImGui.Checkbox(Localize("Enable Wanderer's Campfire Fix (For Modded Campfires)"), ref enableWanderersCampfireFix)) {
        _plugin.Config.EnableWanderersCampfireFix = enableWanderersCampfireFix;
        _plugin.Config.Save();
      }
      if (ImGui.IsItemHovered()) {
        ImGui.SetTooltip(Localize("Enable this if you use modded skybox mods that replace Wanderer's Campfire."));
      }
    }

    private void DrawOutdoorTab() {
      ImGui.TextColored(new Vector4(0.7f, 0.9f, 1.0f, 1.0f), Localize("Public Screens"));
      ImGui.Separator();

      bool enableOutdoor = _plugin.Config.EnableOutdoorPublicScreens;
      if (ImGui.Checkbox(Localize("Enable Public Outdoor Screens"), ref enableOutdoor)) {
        _plugin.Config.EnableOutdoorPublicScreens = enableOutdoor;
        _plugin.Config.Save();
        _plugin.HandleOutdoorSettingToggled();
      }

      bool safeMode = _plugin.Config.OnlySafeDomainsPublicScreens;
      if (ImGui.Checkbox(Localize("Safe Mode (Only allow safe domains outside)"), ref safeMode)) {
        if (!safeMode) {
          ImGui.OpenPopup("DisableSafeModeWarning");
        } else {
          _plugin.Config.OnlySafeDomainsPublicScreens = true;
          _plugin.Config.Save();
        }
      }
      ImGui.TextColored(new Vector4(0.5f, 0.5f, 0.5f, 1f),
        Localize("Blocks unverified URLs on outdoor screens to prevent abuse."));

      ImGui.Spacing();
      bool showGrid = _plugin.Config.ShowOutdoorGridDebug;
      if (ImGui.Checkbox(Localize("Show Outdoor Grid Overlay (Debug)"), ref showGrid)) {
        _plugin.Config.ShowOutdoorGridDebug = showGrid;
        _plugin.Config.Save();
      }
    }

    private void DrawSourcesTab() {
      ImGui.TextColored(new Vector4(0.7f, 0.9f, 1.0f, 1.0f), Localize("yt-dlp"));
      ImGui.Separator();

      string[] qualityLabels = new string[] { "360p", "480p", "720p", "1080p", Localize("Best") };
      int[] qualityValues = new int[] { 360, 480, 720, 1080, 0 };
      int currentQualityIdx = Array.IndexOf(qualityValues, _plugin.Config.PreferredQuality);
      if (currentQualityIdx < 0) currentQualityIdx = 2;
      if (ImGui.Combo(Localize("Preferred Quality"), ref currentQualityIdx, qualityLabels, qualityLabels.Length)) {
        _plugin.Config.PreferredQuality = qualityValues[currentQualityIdx];
        _plugin.Config.Save();
      }

      bool sabrProxy = _plugin.Config.EnableSabrProxy;
      if (ImGui.Checkbox(Localize("YouTube SABR mode (recommended for videos)"), ref sabrProxy)) {
        _plugin.Config.EnableSabrProxy = sabrProxy;
        if (_plugin.YtDlpManager != null) {
          _plugin.YtDlpManager.EnableSabrProxy = sabrProxy;
          if (sabrProxy) {
            _ = Task.Run(async () => await _plugin.YtDlpManager.EnsureAvailableAsync());
          }
        }
        _plugin.Config.Save();
      }
      ImGui.TextWrapped(
        Localize("Videos: buffered local download for reliable playback and seeking. Live streams are detected automatically and play via HLS instead. Requires cookies for most YouTube content."));

      DrawYouTubeHelperSection();

      ImGui.Spacing();
      ImGui.TextColored(new Vector4(0.5f, 0.5f, 0.5f, 1f),
        Localize("yt-dlp is automatically downloaded and updated."));

      if (_plugin.YtDlpManager != null && !_plugin.YtDlpManager.HasCookiesFile) {
        ImGui.Spacing();
        ImGui.TextColored(new Vector4(1f, 0.3f, 0.3f, 1f), Localize("Warning: No cookies.txt found!"));
        ImGui.TextWrapped(Localize("YouTube now heavily blocks players without cookies. To fix this, install the VRCVideoCacher extension in your browser to sync cookie data locally."));

        if (ImGui.Button(Localize("Chrome/Edge/Brave Extension"))) {
          try {
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo {
              FileName = "https://chromewebstore.google.com/detail/vrcvideocacher-cookies-ex/kfgelknbegappcajiflgfbjbdpbpokge",
              UseShellExecute = true
            });
          } catch { }
        }
        ImGui.SameLine();
        if (ImGui.Button(Localize("Firefox Extension"))) {
          try {
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo {
              FileName = "https://addons.mozilla.org/en-US/firefox/addon/vrcvideocachercookiesexporter/",
              UseShellExecute = true
            });
          } catch { }
        }
      }
    }

    private void DrawYouTubeHelperSection()
    {
      if (!_plugin.Config.EnableSabrProxy || _plugin.YtDlpManager == null)
      {
        return;
      }

      ImGui.Spacing();
      ImGui.TextColored(new Vector4(0.7f, 0.9f, 1.0f, 1.0f), Localize("YouTube helper"));
      ImGui.Separator();

      var yt = _plugin.YtDlpManager;
      if (yt.IsPoTokenServerReady)
      {
        ImGui.TextColored(new Vector4(0.4f, 1f, 0.4f, 1f), Localize("Status: Ready"));
      }
      else
      {
        ImGui.TextColored(new Vector4(1f, 0.7f, 0.3f, 1f), Localize("Status: Setup needed"));
        ImGui.TextWrapped(
          Localize("If a Windows popup asked about internet access and you clicked Block or No, use the button below — no game restart required."));
      }

      if (yt.IsYouTubeSetupRunning)
      {
        ImGui.Spacing();
        ImGui.TextWrapped(Localize("Setting up... This can take 1–2 minutes. If Windows asks to allow internet access, click Allow."));
      }
      else if (ImGui.Button(Localize("Fix YouTube setup")))
      {
        _plugin.RetryYouTubeSetup();
      }
    }

    private void DrawAdvancedTab() {
      ImGui.TextColored(new Vector4(0.7f, 0.9f, 1.0f, 1.0f), Localize("Debug"));
      ImGui.Separator();

      unsafe {
        var housingMgr = FFXIVClientStructs.FFXIV.Client.Game.HousingManager.Instance();
        if (housingMgr != null && !housingMgr->IsInside() && housingMgr->GetCurrentPlot() >= 0 && housingMgr->GetCurrentWard() >= 0) {
          ImGui.TextColored(new Vector4(0.4f, 1f, 0.4f, 1f), string.Format(Localize("You are standing in Plot {0}"), housingMgr->GetCurrentPlot() + 1));
        }
      }

      string locationKey = _plugin.LocationKey;
      ImGui.TextColored(new Vector4(0.7f, 0.7f, 0.7f, 1f), Localize("Placement Key:"));
      ImGui.SameLine();
      ImGui.Text(locationKey ?? Localize("Unknown"));
      if (locationKey != null) {
        ImGui.SameLine();
        if (ImGui.Button(Localize("Copy##copyloc"))) {
          ImGui.SetClipboardText(locationKey);
        }
      }

      if (_plugin.CurrentTvPlacement != null) {
        ImGui.TextColored(new Vector4(0.4f, 1f, 0.4f, 1f), Localize("Synced TV Key:"));
        ImGui.SameLine();
        ImGui.Text(_plugin.CurrentTvPlacement.LocationKey);
        ImGui.SameLine();
        if (ImGui.Button(Localize("Copy##copysyncloc"))) {
          ImGui.SetClipboardText(_plugin.CurrentTvPlacement.LocationKey);
        }
      }

      bool verboseChat = _plugin.Config.VerboseChatLogging;
      if (ImGui.Checkbox(Localize("Enable Verbose Chat Logging"), ref verboseChat)) {
        _plugin.Config.VerboseChatLogging = verboseChat;
        _plugin.Config.Save();
      }
      if (ImGui.IsItemHovered()) {
        ImGui.SetTooltip(Localize("Shows detailed plugin status messages in the chat."));
      }

      bool devMode = _plugin.Config.DevMode;
      if (ImGui.Checkbox(Localize("Developer Mode"), ref devMode)) {
        _plugin.Config.DevMode = devMode;
        _plugin.Config.Save();
        _plugin.ApplyUiLanguageFromConfig();
      }
      if (ImGui.IsItemHovered()) {
        ImGui.SetTooltip(Localize("Shows developer-only settings such as the translation server URL override."));
      }

      ImGui.Spacing();
      ImGui.TextColored(new Vector4(0.7f, 0.9f, 1.0f, 1.0f), Localize("Server Sync"));
      ImGui.Separator();

      string serverUrl = _plugin.Config.ServerUrl;
      if (ImGui.InputText(Localize("Server URL"), ref serverUrl, 256)) {
        _plugin.Config.ServerUrl = serverUrl;
        _plugin.Config.Save();
      }
      if (ImGui.IsItemHovered()) {
        ImGui.SetTooltip(Localize("URL of the backend server used to sync TVs."));
      }
    }

    private void DrawAboutSection() {
      ImGui.TextColored(new Vector4(0.7f, 0.9f, 1.0f, 1.0f), Localize("Help & Support"));
      ImGui.Separator();

      if (ImGui.Button(Localize("Tutorial Video (How to Place TVs)"))) {
        try {
          System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo {
            FileName = "https://www.youtube.com/watch?v=ZgLs2OJQ8ks",
            UseShellExecute = true
          });
        } catch { }
      }

      ImGui.SameLine();

      if (ImGui.Button(Localize("Join Support Discord"))) {
        try {
          System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo {
            FileName = "https://discord.gg/rtGXwMn7pX",
            UseShellExecute = true
          });
        } catch { }
      }

      ImGui.Spacing();

      if (ImGui.Button(Localize("Support the Developer on Ko-fi"))) {
        try {
          System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo {
            FileName = "https://ko-fi.com/sebastina",
            UseShellExecute = true
          });
        } catch { }
      }

      DrawDiagnosticReportSection();
    }

    private void DrawDiagnosticReportSection()
    {
      ImGui.Spacing();
      ImGui.TextColored(new Vector4(0.7f, 0.9f, 1.0f, 1.0f), Localize("Error reports"));
      ImGui.Separator();
      ImGui.TextWrapped(
        Localize("If something isn't working, you can send recent plugin warnings and errors from your Dalamud log. Only XivMediaPlayer messages are included, not your whole log file."));

      if (_plugin.HasPendingDiagnosticReports)
      {
        ImGui.TextColored(new Vector4(1f, 0.7f, 0.3f, 1f),
          string.Format(Localize("Detected {0} recent issue(s)."), _plugin.DiagnosticPendingCount));
      }
      else
      {
        ImGui.TextColored(new Vector4(0.5f, 0.8f, 0.5f, 1f), Localize("No recent plugin errors detected."));
      }

      bool autoSend = _plugin.Config.AutoSendDiagnosticLogs;
      if (ImGui.Checkbox(Localize("Automatically send error reports"), ref autoSend))
      {
        _plugin.Config.AutoSendDiagnosticLogs = autoSend;
        _plugin.Config.Save();
      }
      if (ImGui.IsItemHovered())
      {
        ImGui.SetTooltip(Localize("When enabled, recent plugin errors are uploaded automatically (at most once every 10 minutes)."));
      }

      ImGui.InputText(Localize("What went wrong? (optional)"), ref _diagnosticUserNote, 256);

      if (_plugin.IsSendingDiagnosticLogs)
      {
        ImGui.BeginDisabled();
        ImGui.Button(Localize("Sending error report..."));
        ImGui.EndDisabled();
      }
      else if (ImGui.Button(Localize("Send error report")))
      {
        string note = _diagnosticUserNote;
        _plugin.SendDiagnosticReport(note);
        _diagnosticUserNote = string.Empty;
      }
    }

    private void DrawSafeModePopup() {
      var viewportCenter = ImGui.GetMainViewport().GetCenter();
      ImGui.SetNextWindowPos(viewportCenter, ImGuiCond.Appearing, new Vector2(0.5f, 0.5f));
      if (ImGui.BeginPopupModal("DisableSafeModeWarning", ImGuiWindowFlags.AlwaysAutoResize | ImGuiWindowFlags.NoSavedSettings)) {
        ImGui.Text(Localize("Disable Safe Mode Warning"));
        ImGui.Spacing();
        ImGui.Text(Localize("WARNING: Disabling Safe Mode will allow almost any domain to play on outdoor screens (unless otherwise blacklisted by your current server)."));
        ImGui.Text(Localize("You may be exposed to content that you may not wish to see from unmoderated domains."));
        ImGui.Spacing();
        ImGui.TextColored(new Vector4(1f, 0.3f, 0.3f, 1f), Localize("By clicking 'I Agree', you accept full responsibility for your own screen,"));
        ImGui.TextColored(new Vector4(1f, 0.3f, 0.3f, 1f), Localize("and you explicitly agree that you WILL NOT play illegal content."));
        ImGui.Separator();
        ImGui.Spacing();

        if (ImGui.Button(Localize("I Agree, Disable Safe Mode"), new Vector2(250, 0))) {
          _plugin.Config.OnlySafeDomainsPublicScreens = false;
          _plugin.Config.Save();
          ImGui.CloseCurrentPopup();
        }
        ImGui.SameLine();
        if (ImGui.Button(Localize("Cancel"), new Vector2(120, 0))) {
          ImGui.CloseCurrentPopup();
        }
        ImGui.EndPopup();
      }
    }
  }
}
