using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Windowing;
using MediaPlayerCore.Compositing;
using XivMediaPlayer.Compositing;
using System;
using System.Linq;
using System.Numerics;
using Dalamud.Plugin.Services;
using XivMediaPlayer.Networking.Models;
using XivMediaPlayer.Localization;

namespace XivMediaPlayer.Windows {
  /// <summary>
  /// ImGui settings window for interactively positioning the world-space video screen.
  /// Provides drag controls for position, rotation, and scale, plus quick-action buttons.
  /// </summary>
  internal class ScreenSettingsWindow : Window {
    private readonly WorldScreenTransform _transform;
    private readonly WorldVideoRenderer _renderer;
    private readonly Action _onSave;
    private readonly Action _onPlaceAtCamera;
    private readonly Plugin _plugin;
    private readonly IGameGui _gameGui;

    private string _statusMessage = "";
    private Vector4 _statusColor = new Vector4(1, 1, 1, 1);

    private Vector3 _position;
    private Vector2 _rotation; // yaw, pitch
    private Vector2 _scale;
    private bool _enabled;
    private bool _wasShiftPressed;
    private int _aspectRatio = 0; // 0 = 16:9, 1 = 4:3
    
    private float _opacity = 1.0f;
    private bool _isProjectorMode = false;
    private Vector3 _screensaverColor = new Vector3(0.0f, 0.0f, 0.0f);
    private int _screensaverStyle = 0;
    private int _visualEffectMode = 0;
    private float _effectIntensity = 0.65f;
    private float _effectSpeed = 1.0f;
    private string _idleBrandingUrl = "";
    private string _bannerImageUrl = "";

    // Drag state for world-space interaction
    private bool _isDragging;
    private Vector2 _dragStartMouse;
    private Vector3 _dragStartPosition;

    public ScreenSettingsWindow(
        Plugin plugin,
        IGameGui gameGui,
        WorldScreenTransform transform,
        WorldVideoRenderer renderer,
        Action onSave,
        Action onPlaceAtCamera) :
      base("Screen Placement###ScreenPlacement",
        ImGuiWindowFlags.NoCollapse,
        false) {
      _plugin = plugin;
      _gameGui = gameGui;
      _transform = transform;
      _renderer = renderer;
      _onSave = onSave;
      _onPlaceAtCamera = onPlaceAtCamera;

      Size = new Vector2(620, 540);
      SizeCondition = ImGuiCond.FirstUseEver;

      SyncFromTransform();
    }

    public void SyncFromTransform() {
      _position = _transform.Position;
      _rotation = new Vector2(_transform.RotationDegrees.Y, _transform.RotationDegrees.X); // yaw, pitch
      _scale = _transform.Scale;
      _aspectRatio = ResolveAspectMode(_scale, _transform.ScaleAspectMode);
      _enabled = _transform.Enabled;
      _opacity = _transform.Opacity;
      _isProjectorMode = _transform.IsProjectorMode;
      _screensaverColor = _transform.ScreensaverColor;
      _screensaverStyle = _transform.ScreensaverStyle;
      _visualEffectMode = _transform.VisualEffectMode;
      _effectIntensity = _transform.EffectIntensity;
      _effectSpeed = _transform.EffectSpeed;
      _idleBrandingUrl = _transform.IdleBrandingUrl ?? string.Empty;
      if (_plugin.CurrentTvPlacement != null && string.IsNullOrWhiteSpace(_idleBrandingUrl))
      {
        _idleBrandingUrl = _plugin.CurrentTvPlacement.IdleBrandingUrl ?? string.Empty;
      }
      if (_plugin.CurrentBannerPlacement != null)
      {
        _bannerImageUrl = _plugin.CurrentBannerPlacement.ImageUrl ?? string.Empty;
      }
    }

    private static int ResolveAspectMode(Vector2 scale, int storedMode) {
      if (storedMode == 2) return 2;
      if (scale.X > 0.001f) {
        float ratio = scale.Y / scale.X;
        if (MathF.Abs(ratio - (9f / 16f)) < 0.05f) return 0;
        if (MathF.Abs(ratio - (3f / 4f)) < 0.05f) return 1;
      }

      return storedMode is 0 or 1 ? storedMode : 2;
    }

    internal void FlushUiToTransform() {
      SyncToTransform();
    }

    private void SyncToTransform() {
      _transform.Position = _position;
      _transform.RotationDegrees = new Vector3(_rotation.Y, _rotation.X, 0); // pitch, yaw, roll
      _transform.Scale = _scale;
      _transform.ScaleAspectMode = _aspectRatio;
      _transform.Enabled = _enabled;
      _transform.Opacity = _opacity;
      _transform.IsProjectorMode = _isProjectorMode;
      _transform.ScreensaverColor = _screensaverColor;
      _transform.ScreensaverStyle = _screensaverStyle;
      _transform.IdleBrandingUrl = _idleBrandingUrl?.Trim() ?? string.Empty;
      _transform.VisualEffectMode = _visualEffectMode;
      _transform.EffectIntensity = _effectIntensity;
      _transform.EffectSpeed = _effectSpeed;
      _plugin.SyncPlacementManipulatorFromWorkingTransform();
    }

    private string Localize(string text) => _plugin.Translate(text);

    private void PrintStatus(string statusKey) => _plugin.PrintChatWithBody(statusKey);

    private void PrintStatusError(string statusKey) => _plugin.PrintErrorChatWithBody(statusKey);

    public override void Draw() {
      _ = _plugin.TranslationRevision;
      WindowName = Localize("Screen Placement");
      string locKey = _plugin.LocationKey;
      bool isOutdoors = !string.IsNullOrEmpty(locKey) && locKey.StartsWith("zone_");
      bool isIsland = !string.IsNullOrEmpty(locKey) && locKey.StartsWith("island_");
      bool hasHousingMenuOpen = _plugin.IsHousingMenuOpen;
      bool hasPrivileges = isOutdoors || isIsland || hasHousingMenuOpen;

      if (!hasPrivileges) {
          ImGui.TextColored(new Vector4(1f, 0.4f, 0.4f, 1f), Localize("Housing Menu Required"));
          ImGui.TextWrapped(Localize("To place or sync a screen, please open the 'Indoor Furnishings' menu in-game."));
          ImGui.Spacing();
          if (ImGui.Button(Localize("Tutorial Video"))) {
              System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo {
                  FileName = "https://youtu.be/RyvphbJxf5s",
                  UseShellExecute = true
              });
          }
          return;
      }

      if (isOutdoors && !_plugin.Config.EnableOutdoorPublicScreens) {
          ImGui.TextColored(new Vector4(1f, 0.4f, 0.4f, 1f), Localize("Outdoor Screens Disabled"));
          ImGui.TextWrapped(Localize("You must enable 'Enable Outdoor Public Screens' in the main settings menu to place TVs outdoors."));
          return;
      }

      // Enable toggle 
      if (ImGui.Checkbox(Localize("Render in World"), ref _enabled)) {
        _transform.Enabled = _enabled;

        // Auto-delete from server if turning off and we own it or have privileges
        if (!_enabled && !string.IsNullOrEmpty(locKey) &&
            _plugin.CurrentTvPlacement != null && (_plugin.CurrentTvPlacement.OwnerId == _plugin.Config.OwnerId || hasPrivileges)) {
            _ = DeleteTvAsync(locKey, restoreOnFailure: true);
        } else if (_enabled && !string.IsNullOrEmpty(locKey)
            && !_plugin.RoomTvPlacements.Any(t => t.LocationKey == locKey)) {
            var tv = _plugin.MaterializeTvFromWorkingTransform(locKey);
            _plugin.SelectTvForEditing(tv);
            SyncFromTransform();
            _onSave?.Invoke();
        } else {
            _onSave?.Invoke();
        }
      }

      ImGui.SameLine();
      float tutorialButtonWidth = 110f;
      float tutorialSpacing = ImGui.GetContentRegionAvail().X - tutorialButtonWidth;
      if (tutorialSpacing > 0f) {
        ImGui.SetCursorPosX(ImGui.GetCursorPosX() + tutorialSpacing);
      }
      if (ImGui.Button(Localize("Tutorial Video"))) {
          System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo {
              FileName = "https://youtu.be/RyvphbJxf5s",
              UseShellExecute = true
          });
      }

      if (!_enabled) {
        ImGui.TextColored(new Vector4(0.6f, 0.6f, 0.6f, 1f),
          Localize("Enable to place the video in the game world."));
        return;
      }

      // Ctrl+Shift quick-snap logic (active on any tab)
      bool isSnapKeyPressed = ImGui.GetIO().KeyShift && ImGui.GetIO().KeyCtrl;
      if (isSnapKeyPressed && !_wasShiftPressed) {
          unsafe {
              var hm = FFXIVClientStructs.FFXIV.Client.Game.HousingManager.Instance();
              if (hm != null && hm->IndoorTerritory != null) {
                  var hover = hm->IndoorTerritory->HoveredHousingObject;
                  var target = hm->IndoorTerritory->TargetedHousingObject;
                  var objToSnap = hover != null ? hover : target;

                  if (objToSnap != null) {
                      _position = objToSnap->Position;
                      _rotation.X = objToSnap->Rotation * (180f / (float)Math.PI);
                      _rotation.Y = 0f;
                      SyncToTransform();
                      _onSave?.Invoke();
                  }
              }
          }
      }
      _wasShiftPressed = isSnapKeyPressed;

      ImGui.BeginChild("ScreenSettingsHost", Vector2.Zero, false);

      ImGui.BeginChild("ObjectSidebar", new Vector2(PlacementSidebarWidth, 0), true);
      DrawPlacementSidebar(locKey);
      ImGui.EndChild();

      ImGui.SameLine();

      ImGui.BeginChild("ScreenSettingsMain", Vector2.Zero, false, ImGuiWindowFlags.AlwaysVerticalScrollbar);
      bool editingBanner = _plugin.IsBannerEditActive();
      if (ImGui.BeginTabBar("ScreenPlacementTabs")) {
        if (ImGui.BeginTabItem(Localize("Placement"))) {
          DrawPlacementEditor(locKey, hasPrivileges);
          ImGui.EndTabItem();
        }
        if (ImGui.BeginTabItem(Localize("Appearance"))) {
          DrawAppearanceTab(editingBanner);
          ImGui.EndTabItem();
        }
        if (ImGui.BeginTabItem(Localize("Sync"))) {
          DrawSyncTab(locKey);
          ImGui.EndTabItem();
        }
        ImGui.EndTabBar();
      }
      ImGui.EndChild();

      ImGui.EndChild();
    }

    private DateTime _lastAddScreenTime = DateTime.MinValue;
    private DateTime _lastAddBannerTime = DateTime.MinValue;

    private void DrawPlacementStatus() {
      if (!string.IsNullOrEmpty(_statusMessage)) {
        ImGui.Spacing();
        ImGui.TextColored(_statusColor, Localize(_statusMessage));
      }
    }

    private const float PlacementSidebarWidth = 196f;

    private void DrawPlacementSidebar(string locationKey) {
      ImGui.PushTextWrapPos(0f);

      var roomTvs = _plugin.RoomTvPlacements
        .Where(t => t.LocationKey == locationKey)
        .OrderBy(t => t.LastUpdated)
        .ToList();
      if (roomTvs.Count == 0 && _plugin.CurrentTvPlacement != null) {
        roomTvs.Add(_plugin.CurrentTvPlacement);
      }

      var roomBanners = _plugin.RoomBannerPlacements
        .Where(b => b.LocationKey == locationKey)
        .OrderBy(b => b.LastUpdated)
        .ToList();

      bool editingBanner = _plugin.IsBannerEditActive();
      string currentTvId = _plugin.CurrentTvPlacement?.Id ?? string.Empty;
      string currentBannerId = _plugin.CurrentBannerPlacement?.Id ?? string.Empty;

      ImGui.TextDisabled(Localize("Objects"));
      ImGui.Separator();

      if (roomTvs.Count == 0 && roomBanners.Count == 0) {
        ImGui.TextWrapped(Localize("No screens or banners yet."));
        ImGui.Spacing();
      } else {
        if (roomTvs.Count > 0) {
          ImGui.TextDisabled(Localize("TV Screens"));
          for (int i = 0; i < roomTvs.Count; i++) {
            var tv = roomTvs[i];
            string label = string.Format(Localize("Screen {0}"), i + 1);
            bool selected = !editingBanner && tv.Id == currentTvId;
            if (DrawSidebarEntry(label, selected)) {
              _plugin.SelectTvForEditing(tv);
              SyncFromTransform();
            }
          }

          if (roomBanners.Count > 0) {
            ImGui.Spacing();
          }
        }

        if (roomBanners.Count > 0) {
          ImGui.TextDisabled(Localize("Banners"));
          for (int i = 0; i < roomBanners.Count; i++) {
            var banner = roomBanners[i];
            string label = string.Format(Localize("Banner {0}"), i + 1);
            bool selected = editingBanner && banner.Id == currentBannerId;
            if (DrawSidebarEntry(label, selected)) {
              _plugin.SelectBannerForEditing(banner);
              _bannerImageUrl = banner.ImageUrl;
              SyncFromTransform();
            }
          }
        }
      }

      ImGui.Spacing();
      ImGui.Separator();
      if (ImGui.Button(Localize("+ Screen"), new Vector2(-1, 0))) {
        RegisterAdditionalTvAsync(locationKey);
      }
      if (ImGui.Button(Localize("+ Banner"), new Vector2(-1, 0))) {
        RegisterAdditionalBannerAsync(locationKey);
      }

      ImGui.PopTextWrapPos();
    }

    private static bool DrawSidebarEntry(string label, bool selected) {
      var accent = new Vector4(0.35f, 0.75f, 1f, 0.45f);
      var accentHover = new Vector4(0.45f, 0.85f, 1f, 0.65f);
      if (selected) {
        ImGui.PushStyleColor(ImGuiCol.Header, accent);
        ImGui.PushStyleColor(ImGuiCol.HeaderHovered, accentHover);
        ImGui.PushStyleColor(ImGuiCol.HeaderActive, accentHover);
      }

      float width = ImGui.GetContentRegionAvail().X;
      bool clicked = ImGui.Selectable(label, selected, ImGuiSelectableFlags.None, new Vector2(width, 0f));

      if (selected) {
        ImGui.PopStyleColor(3);
      }

      return clicked;
    }

    private void DrawPlacementEditor(string locKey, bool hasPrivileges) {
      bool editingBanner = _plugin.IsBannerEditActive();
      bool hasObjects = _plugin.RoomTvPlacements.Any(t => t.LocationKey == locKey)
          || _plugin.RoomBannerPlacements.Any(b => b.LocationKey == locKey)
          || _plugin.CurrentTvPlacement != null;

      if (!hasObjects) {
        ImGui.TextWrapped(Localize("Add a screen or banner from the sidebar. You can set the banner media URL after creating it."));
        ImGui.Spacing();
        ImGui.Separator();
        ImGui.Spacing();
      } else if (editingBanner) {
        DrawBannerImageUrlField(applyLive: true);
        if (_plugin.CurrentBannerPlacement != null) {
          if (ImGui.Button(Localize("Delete Banner"))) {
            _ = DeleteBannerAsync(locKey, _plugin.CurrentBannerPlacement.Id);
          }
        }
        ImGui.Spacing();
        ImGui.Separator();
        ImGui.Spacing();
      } else {
        int tvCount = _plugin.RoomTvPlacements.Count(t => t.LocationKey == locKey);
        if (tvCount > 1) {
          ImGui.TextDisabled(string.Format(Localize("{0} screens in this area share the same playback."), tvCount));
          ImGui.Spacing();
        }

        if (_plugin.CurrentTvPlacement != null
            && (_plugin.CurrentTvPlacement.OwnerId == _plugin.Config.OwnerId || hasPrivileges)) {
          if (ImGui.Button(Localize("Remove Screen"))) {
            _ = DeleteTvAsync(locKey);
          }
          ImGui.Spacing();
          ImGui.Separator();
          ImGui.Spacing();
        }
      }

      if (ImGui.Button(Localize("Place at Camera"))) {
        _onPlaceAtCamera?.Invoke();
        SyncFromTransform();
        _onSave?.Invoke();
      }

      ImGui.Spacing();
      ImGui.TextColored(new Vector4(0.7f, 1f, 0.7f, 1f), Localize("Quick Snap:"));
      ImGui.TextWrapped(Localize("Hold CTRL + SHIFT while hovering over or selecting a furnishing in Edit Mode to instantly snap the selected object to it."));
      ImGui.Spacing();

      if (ImGui.Button(Localize("Save"))) {
        SyncToTransform();
        _onSave?.Invoke();
        _plugin.FlushPlacementServerSync();
      }

      DrawPlacementStatus();

      ImGui.TextColored(new Vector4(0.7f, 0.9f, 1f, 1f), Localize("Position"));

      bool posChanged = false;
      posChanged |= ImGui.DragFloat("X##pos", ref _position.X, 0.05f, -1000f, 1000f, "%.2f");
      bool savePos = ImGui.IsItemDeactivatedAfterEdit();
      posChanged |= ImGui.DragFloat("Y##pos", ref _position.Y, 0.05f, -1000f, 1000f, "%.2f");
      savePos |= ImGui.IsItemDeactivatedAfterEdit();
      posChanged |= ImGui.DragFloat("Z##pos", ref _position.Z, 0.05f, -1000f, 1000f, "%.2f");
      savePos |= ImGui.IsItemDeactivatedAfterEdit();

      if (posChanged) {
        _transform.Position = _position;
      }
      if (savePos) {
        _onSave?.Invoke();
      }

      float nudge = 0.25f;
      if (ImGui.Button("\u2190##posX")) { _position.X -= nudge; _transform.Position = _position; _onSave?.Invoke(); }
      ImGui.SameLine();
      if (ImGui.Button("\u2192##posX")) { _position.X += nudge; _transform.Position = _position; _onSave?.Invoke(); }
      ImGui.SameLine();
      if (ImGui.Button("\u2193##posY")) { _position.Y -= nudge; _transform.Position = _position; _onSave?.Invoke(); }
      ImGui.SameLine();
      if (ImGui.Button("\u2191##posY")) { _position.Y += nudge; _transform.Position = _position; _onSave?.Invoke(); }
      ImGui.SameLine();
      if (ImGui.Button(Localize("Near##posZ"))) { _position.Z -= nudge; _transform.Position = _position; _onSave?.Invoke(); }
      ImGui.SameLine();
      if (ImGui.Button(Localize("Far##posZ"))) { _position.Z += nudge; _transform.Position = _position; _onSave?.Invoke(); }

      ImGui.Spacing();
      ImGui.Separator();

      ImGui.TextColored(new Vector4(0.7f, 0.9f, 1f, 1f), Localize("Rotation"));

      bool rotChanged = false;
      rotChanged |= ImGui.SliderFloat(Localize("Yaw##rot"), ref _rotation.X, -180f, 180f, "%.1f\u00b0");
      bool saveRot = ImGui.IsItemDeactivatedAfterEdit();
      rotChanged |= ImGui.SliderFloat(Localize("Pitch##rot"), ref _rotation.Y, -90f, 90f, "%.1f\u00b0");
      saveRot |= ImGui.IsItemDeactivatedAfterEdit();
      if (rotChanged) {
        _transform.RotationDegrees = new Vector3(_rotation.Y, _rotation.X, 0);
      }
      if (saveRot) {
        _onSave?.Invoke();
      }

      if (ImGui.Button(Localize("Face North"))) { _rotation.X = 0; _transform.RotationDegrees = new Vector3(_rotation.Y, 0, 0); _onSave?.Invoke(); }
      ImGui.SameLine();
      if (ImGui.Button(Localize("Face East"))) { _rotation.X = 90; _transform.RotationDegrees = new Vector3(_rotation.Y, 90, 0); _onSave?.Invoke(); }
      ImGui.SameLine();
      if (ImGui.Button(Localize("Face South"))) { _rotation.X = 180; _transform.RotationDegrees = new Vector3(_rotation.Y, 180, 0); _onSave?.Invoke(); }
      ImGui.SameLine();
      if (ImGui.Button(Localize("Face West"))) { _rotation.X = -90; _transform.RotationDegrees = new Vector3(_rotation.Y, -90, 0); _onSave?.Invoke(); }

      ImGui.Spacing();
      ImGui.Separator();

      if (_plugin.CurrentBannerPlacement != null) {
        DrawBannerSizeControls();
      } else {
        DrawTvSizeControls();
      }

      ImGui.Spacing();
      ImGui.Separator();
      ImGui.TextColored(new Vector4(0.5f, 0.5f, 0.5f, 1f),
        string.Format(Localize("Screen: {0:F1}m x {1:F1}m at ({2:F1}, {3:F1}, {4:F1})"), _scale.X, _scale.Y, _position.X, _position.Y, _position.Z));
    }

    private void DrawIdleBrandingUrlField(bool applyLive) {
      bool urlChanged = ImGui.InputText(Localize("Screensaver Media URL"), ref _idleBrandingUrl, 512);
      if (applyLive && (urlChanged || ImGui.IsItemDeactivatedAfterEdit()))
      {
        if (!string.IsNullOrWhiteSpace(_idleBrandingUrl))
        {
          _screensaverStyle = 6;
          _transform.ScreensaverStyle = 6;
        }
        _plugin.ApplyIdleBrandingUrl(_idleBrandingUrl);
      }
      ImGui.TextWrapped(Localize("Direct link to a static image (PNG, JPG, WebP), animated GIF, or short looping MP4/WebM/MOV. Video is scaled to screen size on first load."));
    }

    private void DrawBannerImageUrlField(bool applyLive) {
      bool urlChanged = ImGui.InputText(Localize("Banner Media URL"), ref _bannerImageUrl, 512);
      if (applyLive && (urlChanged || ImGui.IsItemDeactivatedAfterEdit())) {
        _plugin.ApplyBannerImageUrl(_bannerImageUrl);
      }
      ImGui.TextWrapped(Localize("Direct link to a static image (PNG, JPG, WebP), animated GIF, or short looping MP4/WebM/MOV. Video is scaled to banner size on first load."));
    }

    private void ApplyBannerScaleFromImageAspect() {
      if (_plugin.CurrentBannerPlacement == null) return;
      if (_plugin.TryGetBannerImageAspect(_plugin.CurrentBannerPlacement, out float imageAspect)) {
        _scale.Y = _scale.X / imageAspect;
      }
    }

    private void DrawBannerSizeControls() {
      ImGui.TextColored(new Vector4(0.7f, 0.9f, 1f, 1f), Localize("Banner Size (world units)"));

      if (_plugin.TryGetBannerImageAspect(_plugin.CurrentBannerPlacement!, out float imageAspect)) {
        ImGui.TextDisabled(string.Format(Localize("Media aspect: {0:F2}:1"), imageAspect));
      } else {
        ImGui.TextDisabled(Localize("Media aspect will apply once the banner loads."));
      }

      bool scaleChanged = ImGui.DragFloat(Localize("Width##bannerScale"), ref _scale.X, 0.1f, 0.5f, 200f, "%.1f");
      bool saveScale = ImGui.IsItemDeactivatedAfterEdit();
      if (scaleChanged) {
        ApplyBannerScaleFromImageAspect();
        _transform.Scale = _scale;
      }
      if (saveScale) {
        _onSave?.Invoke();
      }

      if (ImGui.Button(Localize("Small (2m)"))) { _scale.X = 2f; ApplyBannerScaleFromImageAspect(); _transform.Scale = _scale; _onSave?.Invoke(); }
      ImGui.SameLine();
      if (ImGui.Button(Localize("Medium (4m)"))) { _scale.X = 4f; ApplyBannerScaleFromImageAspect(); _transform.Scale = _scale; _onSave?.Invoke(); }
      ImGui.SameLine();
      if (ImGui.Button(Localize("Large (8m)"))) { _scale.X = 8f; ApplyBannerScaleFromImageAspect(); _transform.Scale = _scale; _onSave?.Invoke(); }
      ImGui.SameLine();
      if (ImGui.Button(Localize("Cinema (12m)"))) { _scale.X = 12f; ApplyBannerScaleFromImageAspect(); _transform.Scale = _scale; _onSave?.Invoke(); }

      ImGui.TextDisabled(string.Format(Localize("Height: {0:F1}m (from media aspect)"), _scale.Y));
      ImGui.TextWrapped(Localize("You can also drag the green corner handles on the banner in-world to scale it."));
    }

    private void DrawTvSizeControls() {
      ImGui.TextColored(new Vector4(0.7f, 0.9f, 1f, 1f), Localize("Size (world units)"));

      bool aspectChanged = false;
      aspectChanged |= ImGui.RadioButton("16:9", ref _aspectRatio, 0);
      ImGui.SameLine();
      aspectChanged |= ImGui.RadioButton("4:3", ref _aspectRatio, 1);
      ImGui.SameLine();
      aspectChanged |= ImGui.RadioButton(Localize("Custom / Free"), ref _aspectRatio, 2);

      bool scaleChanged = false;
      if (_aspectRatio != 2) {
        scaleChanged |= ImGui.DragFloat(Localize("Diagonal Size##scale"), ref _scale.X, 0.1f, 0.5f, 200f, "%.1f");
      } else {
        scaleChanged |= ImGui.DragFloat(Localize("Width##scaleX"), ref _scale.X, 0.1f, 0.5f, 200f, "%.1f");
        scaleChanged |= ImGui.DragFloat(Localize("Height##scaleY"), ref _scale.Y, 0.1f, 0.5f, 200f, "%.1f");
      }
      bool saveScale = ImGui.IsItemDeactivatedAfterEdit();

      if (aspectChanged || scaleChanged) {
        if (_aspectRatio != 2) {
          float ratio = _aspectRatio == 0 ? (9f / 16f) : (3f / 4f);
          _scale.Y = _scale.X * ratio;
        }
        _transform.Scale = _scale;
      }
      if (saveScale || aspectChanged) {
        _onSave?.Invoke();
      }

      if (ImGui.Button(Localize("Small (2m)"))) { _scale.X = 2f; _scale.Y = _scale.X * (_aspectRatio == 1 ? (3f/4f) : (9f/16f)); _transform.Scale = _scale; _onSave?.Invoke(); }
      ImGui.SameLine();
      if (ImGui.Button(Localize("Medium (4m)"))) { _scale.X = 4f; _scale.Y = _scale.X * (_aspectRatio == 1 ? (3f/4f) : (9f/16f)); _transform.Scale = _scale; _onSave?.Invoke(); }
      ImGui.SameLine();
      if (ImGui.Button(Localize("Large (8m)"))) { _scale.X = 8f; _scale.Y = _scale.X * (_aspectRatio == 1 ? (3f/4f) : (9f/16f)); _transform.Scale = _scale; _onSave?.Invoke(); }
      ImGui.SameLine();
      if (ImGui.Button(Localize("Cinema (12m)"))) { _scale.X = 12f; _scale.Y = _scale.X * (_aspectRatio == 1 ? (3f/4f) : (9f/16f)); _transform.Scale = _scale; _onSave?.Invoke(); }
    }

    private void DrawAppearanceTab(bool editingBanner) {
      ImGui.TextColored(new Vector4(0.7f, 0.9f, 1f, 1f), editingBanner
        ? Localize("Banner Appearance")
        : Localize("Projector & Transparency"));

      bool appearanceChanged = false;
      bool persistAppearance = false;
      if (!editingBanner) {
        if (ImGui.Checkbox(Localize("Projector Mode (Additive Blend)"), ref _isProjectorMode)) {
          appearanceChanged = true;
          persistAppearance = true;
        }
        appearanceChanged |= ImGui.SliderFloat(Localize("Opacity"), ref _opacity, 0.05f, 1.0f, "%.2f");
        persistAppearance |= ImGui.IsItemDeactivatedAfterEdit();
        appearanceChanged |= ImGui.ColorEdit3(Localize("Screensaver Color"), ref _screensaverColor);
        persistAppearance |= ImGui.IsItemDeactivatedAfterEdit();

        string[] screensaverStyles = new string[] {
          Localize("Bouncing Logo"), Localize("VCR"), Localize("No Signal"), Localize("Static"), Localize("Test Pattern"), Localize("Matrix Rain"), Localize("Custom Media")
        };
        if (ImGui.Combo(Localize("Screensaver Style"), ref _screensaverStyle, screensaverStyles, screensaverStyles.Length)) {
          appearanceChanged = true;
          persistAppearance = true;
        }

        if (_screensaverStyle == 6) {
          DrawIdleBrandingUrlField(applyLive: true);
          if (_plugin.IsImageTextureReady(_idleBrandingUrl, _scale.X))
          {
            ImGui.TextDisabled(Localize("Media loaded. Preview shows on the TV while this window is open and nothing is playing."));
          }
          else if (!string.IsNullOrWhiteSpace(_idleBrandingUrl))
          {
            ImGui.TextDisabled(Localize("Downloading or converting media... It appears once ready, or after ~5 seconds idle with nothing playing."));
          }
          else
          {
            ImGui.TextWrapped(Localize("Paste a direct HTTPS media link. Stop playback to preview the idle screensaver."));
          }
        }
      } else {
        appearanceChanged |= ImGui.SliderFloat(Localize("Opacity"), ref _opacity, 0.05f, 1.0f, "%.2f");
        persistAppearance |= ImGui.IsItemDeactivatedAfterEdit();
      }

      if (appearanceChanged) {
        _transform.Opacity = _opacity;
        if (!editingBanner) {
          _transform.IsProjectorMode = _isProjectorMode;
          _transform.ScreensaverColor = _screensaverColor;
          _transform.ScreensaverStyle = _screensaverStyle;
          _transform.IdleBrandingUrl = _idleBrandingUrl?.Trim() ?? string.Empty;
          if (_screensaverStyle == 6 && !string.IsNullOrWhiteSpace(_idleBrandingUrl))
          {
            _plugin.ApplyIdleBrandingUrl(_idleBrandingUrl);
          }
        }
      }
      if (persistAppearance) {
        _onSave?.Invoke();
      }

      ImGui.Spacing();
      ImGui.TextColored(new Vector4(0.7f, 0.9f, 1f, 1f), Localize("Visual Effects (Experimental)"));
      ImGui.TextWrapped(editingBanner
        ? Localize("Shader post-effects on the banner media.")
        : Localize("Shader post-effects on the TV/banner media. Audio modes react to desktop playback (Settings → Audio) or spatial audio when that option is off."));

      string[] visualEffects = new string[] {
        Localize("None"),
        Localize("CRT (scanlines + chromatic)"),
        Localize("Ripple"),
        Localize("Glitch"),
        Localize("Parallax"),
        Localize("Audio Pulse"),
        Localize("Audio Spectrum"),
        Localize("Kaleidoscope"),
      };
      bool effectValuesChanged = false;
      bool persistEffects = false;
      if (ImGui.Combo(Localize("Effect Mode"), ref _visualEffectMode, visualEffects, visualEffects.Length)) {
        effectValuesChanged = true;
        persistEffects = true;
      }
      effectValuesChanged |= ImGui.SliderFloat(Localize("Effect Intensity"), ref _effectIntensity, 0f, 2f, "%.2fx");
      persistEffects |= ImGui.IsItemDeactivatedAfterEdit();
      effectValuesChanged |= ImGui.SliderFloat(Localize("Effect Speed"), ref _effectSpeed, 0.1f, 3f, "%.1fx");
      persistEffects |= ImGui.IsItemDeactivatedAfterEdit();

      if (effectValuesChanged) {
        _transform.VisualEffectMode = _visualEffectMode;
        _transform.EffectIntensity = _effectIntensity;
        _transform.EffectSpeed = _effectSpeed;
      }
      if (persistEffects) {
        _onSave?.Invoke();
      }
    }

    private void DrawSyncTab(string locationKey) {
      ImGui.TextColored(new Vector4(0.7f, 0.9f, 1f, 1f), Localize("Room Sync"));
      ImGui.TextWrapped(Localize("Saving above only saves locally. To make the TV visible to other players, you must sync it to the room."));

      bool isOutdoorsSync = !string.IsNullOrEmpty(locationKey) && locationKey.StartsWith("zone_");
      bool isIslandSync = !string.IsNullOrEmpty(locationKey) && locationKey.StartsWith("island_");

      if (string.IsNullOrEmpty(locationKey) || (!locationKey.StartsWith("house_") && !locationKey.StartsWith("zone_") && !locationKey.StartsWith("island_"))) {
        ImGui.TextColored(new Vector4(1f, 0.4f, 0.4f, 1f), Localize("You must be inside a housing area or valid outdoor zone to sync TVs."));
      } else {
        unsafe
        {
          var housingMgr = FFXIVClientStructs.FFXIV.Client.Game.HousingManager.Instance();
          if (housingMgr != null && !housingMgr->IsInside() && housingMgr->GetCurrentPlot() >= 0 && housingMgr->GetCurrentWard() >= 0)
          {
            ImGui.TextColored(new Vector4(0.4f, 1f, 0.4f, 1f), string.Format(Localize("You are standing in Plot {0}"), housingMgr->GetCurrentPlot() + 1));
          }
        }

        ImGui.TextColored(new Vector4(0.7f, 0.7f, 0.7f, 1f), Localize("Placement Key:"));
        ImGui.SameLine();
        ImGui.Text(locationKey);

        if (_plugin.CurrentTvPlacement != null)
        {
          ImGui.TextColored(new Vector4(0.4f, 1f, 0.4f, 1f), Localize("Synced TV Key:"));
          ImGui.SameLine();
          ImGui.Text(_plugin.CurrentTvPlacement.LocationKey);
        }

        if (_plugin.CurrentTvPlacement == null || _plugin.CurrentTvPlacement.OwnerId == _plugin.Config.OwnerId) {
          bool isLocked = _plugin.CurrentTvPlacement?.IsLocked ?? !isOutdoorsSync;
          if (!isOutdoorsSync) {
            if (ImGui.Checkbox(Localize("Lock TV to Owner Only"), ref isLocked)) {
              if (_plugin.CurrentTvPlacement != null) {
                _plugin.CurrentTvPlacement.IsLocked = isLocked;
              } else {
                _plugin.CurrentTvPlacement = new Networking.Models.TvPlacement {
                  OwnerId = _plugin.Config.OwnerId,
                  IsLocked = isLocked
                };
              }
              RegisterTvAsync(locationKey);
            }
          }

          ImGui.Spacing();
          if (ImGui.Button(Localize("Sync Placements to Area"))) {
            RegisterTvAsync(locationKey);
          }
          ImGui.SameLine();
          if (ImGui.Button(Localize("Remove TV from Area"))) {
            _ = DeleteTvAsync(locationKey);
          }
        } else {
          if (_plugin.IsHousingMenuOpen || isOutdoorsSync || isIslandSync) {
            if (ImGui.Button(Localize("Take Ownership of TV"))) {
              RegisterTvAsync(locationKey);
            }
            ImGui.TextColored(new Vector4(1f, 0.8f, 0.2f, 1f), Localize("You can override this locked TV because you have privileges here."));
          } else {
            if (_plugin.CurrentTvPlacement.IsLocked) {
              ImGui.TextColored(new Vector4(1f, 0.4f, 0.4f, 1f), Localize("This TV is locked by its owner."));
            }
          }
        }

        if (!string.IsNullOrEmpty(_statusMessage)) {
          ImGui.TextColored(_statusColor, Localize(_statusMessage));
        }
      }

      var depthDebug = _renderer.DepthDebugInfo;
      if (!string.IsNullOrEmpty(depthDebug)) {
        ImGui.Spacing();
        ImGui.Separator();
        ImGui.TextColored(new Vector4(1f, 0.8f, 0.3f, 1f), Localize("Depth Debug"));
        ImGui.TextWrapped(depthDebug);
      }
      var rendererError = _renderer.DepthRendererError;
      if (!string.IsNullOrEmpty(rendererError)) {
        ImGui.TextColored(new Vector4(1f, 0.3f, 0.3f, 1f), string.Format(Localize("GPU Error: {0}"), rendererError));
      }
    }

    public async void RegisterBannerAsync(string locationKey)
    {
      if (string.IsNullOrEmpty(locationKey)) {
        _statusMessage = "You must be in a housing area to add banners.";
        _statusColor = new Vector4(1, 0.3f, 0.3f, 1);
        return;
      }

      SyncToTransform();
      var placement = BuildBannerFromTransform(locationKey, createNewId: true);

      _statusMessage = "Adding banner...";
      _statusColor = new Vector4(1, 1, 1, 1);

      try {
        var result = await _plugin.ServerClient.RegisterBannerAsync(locationKey, placement, create: true);
        _plugin.RunOnFrameworkThread(() =>
        {
          if (result != null) {
            result = Plugin.MergeBannerPlacementFromServer(result, placement);
            _plugin.UpsertRoomBanner(result);
            _plugin.SelectBannerForEditing(result);
            _statusMessage = string.IsNullOrWhiteSpace(result.ImageUrl)
                ? "Banner added! Set its media URL below, then Save."
                : "Banner added! Use Placement controls or the gizmo, then Save.";
            _statusColor = new Vector4(0.3f, 1f, 0.3f, 1);
          } else {
            _plugin.UpsertRoomBanner(placement);
            _plugin.SelectBannerForEditing(placement);
            _statusMessage = string.IsNullOrWhiteSpace(placement.ImageUrl)
                ? "Banner added locally! Set its media URL below, then Save."
                : "Banner added locally, but sync server rejected it. You can still move it here.";
            _statusColor = new Vector4(1, 0.6f, 0.2f, 1);
          }
        });
      } catch (Exception) {
        _plugin.RunOnFrameworkThread(() =>
        {
          _plugin.UpsertRoomBanner(placement);
          _plugin.SelectBannerForEditing(placement);
          _statusMessage = "Banner added locally, but failed to reach sync server.";
          _statusColor = new Vector4(1, 0.6f, 0.2f, 1);
        });
      }
    }

    public async void RegisterAdditionalBannerAsync(string locationKey)
    {
      if (string.IsNullOrEmpty(locationKey)) {
        _statusMessage = "You must be in a housing area to add banners.";
        _statusColor = new Vector4(1, 0.3f, 0.3f, 1);
        return;
      }

      if ((DateTime.UtcNow - _lastAddBannerTime).TotalSeconds < 1) return;
      _lastAddBannerTime = DateTime.UtcNow;

      SyncToTransform();
      _plugin.ApplyWorkingTransformToCurrentSelection();

      var existing = _plugin.RoomBannerPlacements
          .Where(b => b.LocationKey == locationKey)
          .ToList();

      string? imageUrlOverride = string.IsNullOrWhiteSpace(_bannerImageUrl) ? null : _bannerImageUrl.Trim();
      BannerPlacement placement;
      if (existing.Count == 0 || !_plugin.IsBannerEditActive()) {
        placement = BuildBannerFromTransform(locationKey, createNewId: true);
        if (imageUrlOverride != null)
        {
          placement.ImageUrl = imageUrlOverride;
        }
      } else {
        var anchor = _plugin.CurrentBannerPlacement ?? existing[^1];
        placement = Plugin.CloneBannerPlacement(anchor, locationKey, imageUrlOverride);
        Plugin.OffsetDuplicateBannerPlacement(placement, anchor);
      }

      _statusMessage = "Adding banner...";
      _statusColor = new Vector4(1, 1, 1, 1);

      try {
        var result = await _plugin.ServerClient.RegisterBannerAsync(locationKey, placement, create: true);
        _plugin.RunOnFrameworkThread(() =>
        {
          if (result != null) {
            result = Plugin.MergeBannerPlacementFromServer(result, placement);
            _plugin.UpsertRoomBanner(result);
            _plugin.SelectBannerForEditing(result);
            _statusMessage = string.IsNullOrWhiteSpace(result.ImageUrl)
                ? "Banner added! Set its media URL below, then Save."
                : "Banner added! Use Placement controls or Save to sync.";
            _statusColor = new Vector4(0.3f, 1f, 0.3f, 1);
          } else {
            _plugin.UpsertRoomBanner(placement);
            _plugin.SelectBannerForEditing(placement);
            _statusMessage = string.IsNullOrWhiteSpace(placement.ImageUrl)
                ? "Banner added locally! Set its media URL below, then Save."
                : "Banner added locally, but sync server rejected it.";
            _statusColor = new Vector4(1, 0.6f, 0.2f, 1);
          }
        });
      } catch (Exception) {
        _plugin.RunOnFrameworkThread(() =>
        {
          _plugin.UpsertRoomBanner(placement);
          _plugin.SelectBannerForEditing(placement);
          _statusMessage = "Banner added locally, but failed to reach sync server.";
          _statusColor = new Vector4(1, 0.6f, 0.2f, 1);
        });
      }
    }

    public async void UpdateBannerAsync(string locationKey, bool quiet = false, bool bypassDebounce = false)
    {
      if (_plugin.CurrentBannerPlacement == null || string.IsNullOrEmpty(locationKey)) return;

      if (!bypassDebounce && (DateTime.UtcNow - _lastBannerUpdateTime).TotalSeconds < 1) return;
      _lastBannerUpdateTime = DateTime.UtcNow;

      SyncToTransform();
      var placement = BuildBannerFromTransform(locationKey, createNewId: false);
      placement.Id = _plugin.CurrentBannerPlacement.Id;
      placement.ImageUrl = string.IsNullOrWhiteSpace(_bannerImageUrl)
          ? _plugin.CurrentBannerPlacement.ImageUrl
          : _bannerImageUrl.Trim();

      if (!quiet) {
        _statusMessage = "Updating banner...";
        _statusColor = new Vector4(1, 1, 1, 1);
      }

      try {
        var result = await _plugin.ServerClient.RegisterBannerAsync(locationKey, placement, create: false);
        _plugin.RunOnFrameworkThread(() =>
        {
          if (result != null) {
            result = Plugin.MergeBannerPlacementFromServer(result, placement);
            _plugin.UpsertRoomBanner(result);
            _plugin.SelectBannerForEditing(result);
            if (!quiet) {
              _statusMessage = "Banner updated!";
              _statusColor = new Vector4(0.3f, 1f, 0.3f, 1);
            }
          } else {
            _plugin.UpsertRoomBanner(placement);
            _plugin.SelectBannerForEditing(placement);
            _statusMessage = "Banner saved locally, but sync server rejected the update.";
            _statusColor = new Vector4(1, 0.6f, 0.2f, 1);
          }
        });
      } catch (Exception) {
        _plugin.RunOnFrameworkThread(() =>
        {
          _plugin.UpsertRoomBanner(placement);
          _plugin.SelectBannerForEditing(placement);
          _statusMessage = "Banner saved locally, but failed to reach sync server.";
          _statusColor = new Vector4(1, 0.6f, 0.2f, 1);
        });
      }
    }

    public async System.Threading.Tasks.Task DeleteBannerAsync(string locationKey, string bannerId)
    {
      try {
        bool isOutdoorsSync = locationKey.StartsWith("zone_");
        bool isIslandSync = locationKey.StartsWith("island_");
        bool success = await _plugin.ServerClient.DeleteBannerAsync(
            locationKey, bannerId, _plugin.Config.OwnerId,
            _plugin.IsHousingMenuOpen || isOutdoorsSync || isIslandSync);
        if (success) {
          _plugin.RemoveRoomBanner(bannerId);
          if (_plugin.CurrentBannerPlacement?.Id == bannerId) {
            _plugin.CurrentBannerPlacement = null;
            var tv = _plugin.RoomTvPlacements.FirstOrDefault(t => t.LocationKey == locationKey);
            if (tv != null) {
              _plugin.SelectTvForEditing(tv);
              SyncFromTransform();
            }
          }
          _statusMessage = "Banner removed.";
          _statusColor = new Vector4(0.3f, 1f, 0.3f, 1);
        }
      } catch (Exception) {
        _statusMessage = "Failed to remove banner.";
        _statusColor = new Vector4(1, 0.3f, 0.3f, 1);
      }
    }

    private BannerPlacement BuildBannerFromTransform(string locationKey, bool createNewId)
    {
      bool isOutdoorsSync = locationKey.StartsWith("zone_");
      bool isIslandSync = locationKey.StartsWith("island_");
      return new BannerPlacement {
        Id = createNewId ? Guid.NewGuid().ToString() : (_plugin.CurrentBannerPlacement?.Id ?? Guid.NewGuid().ToString()),
        LocationKey = locationKey,
        PositionX = _position.X,
        PositionY = _position.Y,
        PositionZ = _position.Z,
        RotationX = _transform.RotationDegrees.X,
        RotationY = _transform.RotationDegrees.Y,
        RotationZ = _transform.RotationDegrees.Z,
        ScaleX = _scale.X,
        ScaleY = _scale.Y,
        Opacity = _opacity,
        ImageUrl = _bannerImageUrl?.Trim() ?? string.Empty,
        VisualEffectMode = _visualEffectMode,
        EffectIntensity = _effectIntensity,
        EffectSpeed = _effectSpeed,
        OwnerId = _plugin.Config.OwnerId,
        BypassLock = _plugin.IsHousingMenuOpen || isOutdoorsSync || isIslandSync
      };
    }

    public async System.Threading.Tasks.Task<bool> DeleteTvAsync(string locationKey, bool restoreOnFailure = false) {
        if (_plugin.CurrentTvPlacement == null) return false;
        var currentPlacement = _plugin.CurrentTvPlacement;
        var serverLocationKey = string.IsNullOrEmpty(currentPlacement.LocationKey) ? locationKey : currentPlacement.LocationKey;

        _statusMessage = "Deleting TV from server...";
        _statusColor = new Vector4(1, 1, 1, 1);

        try {
            bool isOutdoorsSync = !string.IsNullOrEmpty(serverLocationKey) && serverLocationKey.StartsWith("zone_");
            bool isIslandSync = !string.IsNullOrEmpty(serverLocationKey) && serverLocationKey.StartsWith("island_");
            bool success = await _plugin.ServerClient.DeleteTvAsync(
                serverLocationKey,
                currentPlacement.Id,
                _plugin.Config.OwnerId,
                _plugin.IsHousingMenuOpen || isOutdoorsSync || isIslandSync);

            if (success) {
                string deletedId = currentPlacement.Id;
                _plugin.RunOnFrameworkThread(() =>
                {
                    ApplyTvDeletedLocally(deletedId, serverLocationKey, locationKey);
                    _statusMessage = "Successfully removed TV from the room!";
                    _statusColor = new Vector4(0.3f, 1f, 0.3f, 1);
                    PrintStatus("Successfully removed TV from the room!");
                });
                return true;
            }

            _plugin.RunOnFrameworkThread(() =>
            {
                RestoreEnabledAfterDeleteFailure(restoreOnFailure);
                _statusMessage = "Failed to remove TV.";
                _statusColor = new Vector4(1, 0.3f, 0.3f, 1);
                PrintStatusError("Failed to remove TV.");
            });
            return false;
        } catch (UnauthorizedAccessException) {
            _plugin.RunOnFrameworkThread(() =>
            {
                RestoreEnabledAfterDeleteFailure(restoreOnFailure);
                _statusMessage = "Cannot delete TV: It is locked by its owner.";
                _statusColor = new Vector4(1, 0.3f, 0.3f, 1);
                PrintStatusError("Cannot delete TV: It is locked by its owner.");
            });
        } catch (Exception) {
            _plugin.RunOnFrameworkThread(() =>
            {
                RestoreEnabledAfterDeleteFailure(restoreOnFailure);
                _statusMessage = "Network error while deleting TV.";
                _statusColor = new Vector4(1, 0.3f, 0.3f, 1);
                PrintStatusError("Network error while deleting TV.");
            });
        }

        return false;
    }

    private void ApplyTvDeletedLocally(string deletedTvId, string serverLocationKey, string locationKey) {
        _plugin.RemoveRoomTv(deletedTvId);
        _plugin.StopMediaIfNoPlayableTargetForUi();

        if (_plugin.CurrentTvPlacement?.Id == deletedTvId) {
            _plugin.CurrentTvPlacement = null;
        }

        var remaining = _plugin.RoomTvPlacements
            .Where(t => t.LocationKey == serverLocationKey)
            .ToList();

        if (remaining.Count > 0) {
            _plugin.SelectTvForEditing(remaining[0]);
            SyncFromTransform();
            _enabled = true;
            _transform.Enabled = true;
        } else {
            _plugin.CurrentTvPlacement = null;
            _transform.Enabled = false;
            _enabled = false;
            _plugin.Config.ScreenPlacements.Remove(locationKey);
            _plugin.Config.ScreenPlacements.Remove(serverLocationKey);
            _plugin.ClearPlacementSelection();
            _plugin.DisableOrphanWorldScreenForUi();
            SyncFromTransform();
        }

        _plugin.Config.Save();
    }

    private void RestoreEnabledAfterDeleteFailure(bool restoreOnFailure) {
        if (!restoreOnFailure) return;

        _enabled = true;
        _transform.Enabled = true;
    }

    /// <summary>
    /// Handles world-space click-drag interaction on the video quad.
    /// Call this from the main draw loop with the projected screen coordinates
    /// of the quad center. Returns true if drag is active.
    /// </summary>
    public bool HandleWorldDrag(Vector2 screenCenter, float screenRadius) {
      if (!_enabled) return false;

      var mousePos = ImGui.GetMousePos();
      float dist = Vector2.Distance(mousePos, screenCenter);

      if (ImGui.IsMouseClicked(ImGuiMouseButton.Left) && dist < screenRadius) {
        _isDragging = true;
        _dragStartMouse = mousePos;
        _dragStartPosition = _transform.Position;
      }

      if (_isDragging) {
        if (ImGui.IsMouseDown(ImGuiMouseButton.Left)) {
          var delta = ImGui.GetMousePos() - _dragStartMouse;
          // Convert screen delta to world delta (approximate: 0.01 world units per pixel)
          float sensitivity = 0.01f;
          _transform.Position = _dragStartPosition + new Vector3(
            delta.X * sensitivity,
            -delta.Y * sensitivity,
            0);
          SyncFromTransform();
          return true;
        } else {
          if (_isDragging) {
             _onSave?.Invoke();
          }
          _isDragging = false;
        }
      }

      return false;
    }

    private DateTime _lastRegistrationTime = DateTime.MinValue;
    private DateTime _lastBannerUpdateTime = DateTime.MinValue;

    private TvPlacement BuildPlacementFromTransform(string locationKey, bool createNewId) {
      return new TvPlacement {
        Id = _plugin.ResolveTvIdForSync(locationKey, createNewId),
        LocationKey = locationKey,
        PositionX = _position.X,
        PositionY = _position.Y,
        PositionZ = _position.Z,
        RotationX = _transform.RotationDegrees.X,
        RotationY = _transform.RotationDegrees.Y,
        RotationZ = _transform.RotationDegrees.Z,
        ScaleX = _scale.X,
        ScaleY = _scale.Y,
        ScaleAspectMode = _aspectRatio,
        Opacity = _opacity,
        IsProjectorMode = _isProjectorMode,
        ScreensaverColorR = _screensaverColor.X,
        ScreensaverColorG = _screensaverColor.Y,
        ScreensaverColorB = _screensaverColor.Z,
        ScreensaverStyle = _screensaverStyle,
        IdleBrandingUrl = _idleBrandingUrl?.Trim() ?? string.Empty,
        VisualEffectMode = _visualEffectMode,
        EffectIntensity = _effectIntensity,
        EffectSpeed = _effectSpeed,
        OwnerId = _plugin.Config.OwnerId,
        IsLocked = _plugin.CurrentTvPlacement?.IsLocked ?? (!locationKey.StartsWith("zone_") && !locationKey.StartsWith("island_")),
        BypassLock = _plugin.IsHousingMenuOpen || locationKey.StartsWith("zone_") || locationKey.StartsWith("island_")
      };
    }

    public async void RegisterAdditionalTvAsync(string locationKey) {
      if (!_enabled) {
        _statusMessage = "Enable Render in World before adding another screen.";
        _statusColor = new Vector4(1, 0.3f, 0.3f, 1);
        return;
      }

      if ((DateTime.UtcNow - _lastAddScreenTime).TotalSeconds < 1) return;
      _lastAddScreenTime = DateTime.UtcNow;

      SyncToTransform();
      _plugin.ApplyWorkingTransformToCurrentSelection();

      _statusMessage = "Adding another screen to this area...";
      _statusColor = new Vector4(1, 1, 1, 1);

      try {
        var existingTvs = _plugin.RoomTvPlacements
            .Where(t => t != null && t.LocationKey == locationKey)
            .ToList();

        TvPlacement anchorTv;
        TvPlacement? syncedAnchor = null;
        if (existingTvs.Count > 0) {
          anchorTv = _plugin.CurrentTvPlacement ?? existingTvs[0];
        } else {
          anchorTv = _plugin.MaterializeTvFromWorkingTransform(locationKey);
          anchorTv.BypassLock = _plugin.IsHousingMenuOpen || locationKey.StartsWith("zone_") || locationKey.StartsWith("island_");
          syncedAnchor = await _plugin.ServerClient.RegisterTvAsync(locationKey, Plugin.CopyTvPlacementForSync(anchorTv), create: false);
          if (syncedAnchor == null) {
            syncedAnchor = await _plugin.ServerClient.RegisterTvAsync(locationKey, Plugin.CopyTvPlacementForSync(anchorTv), create: true);
          }
        }

        var anchorForDuplicate = syncedAnchor ?? anchorTv;
        var placement = Plugin.CloneTvPlacement(anchorForDuplicate, locationKey);
        placement.OwnerId = _plugin.Config.OwnerId;
        placement.IsLocked = anchorForDuplicate.IsLocked;
        placement.BypassLock = _plugin.IsHousingMenuOpen || locationKey.StartsWith("zone_") || locationKey.StartsWith("island_");
        Plugin.OffsetDuplicateScreenPlacement(placement, anchorForDuplicate);

        var result = await _plugin.ServerClient.RegisterTvAsync(locationKey, placement, create: true);
        var syncedAnchorFinal = syncedAnchor;
        var localPlacement = placement;

        _plugin.RunOnFrameworkThread(() =>
        {
          if (syncedAnchorFinal != null) {
            _plugin.UpsertRoomTv(syncedAnchorFinal);
          }

          if (result != null) {
            result = Plugin.MergeTvPlacementFromServer(result, placement);
            _plugin.UpsertRoomTv(result);
            _plugin.SelectTvForEditing(result);
            SyncFromTransform();
            _statusMessage = "Added another screen for all visitors!";
            _statusColor = new Vector4(0.3f, 1f, 0.3f, 1);
            PrintStatus("Added another screen for all visitors!");
          } else {
            _plugin.UpsertRoomTv(localPlacement);
            _plugin.SelectTvForEditing(localPlacement);
            SyncFromTransform();
            _statusMessage = "Added screen locally, but the sync server rejected it. Is the server updated for multi-TV?";
            _statusColor = new Vector4(1, 0.6f, 0.2f, 1);
            PrintStatusError("Added screen locally, but the sync server rejected it. Is the server updated for multi-TV?");
          }
        });
      } catch (UnauthorizedAccessException) {
        _statusMessage = "Cannot add screen: the current TV is locked by its owner.";
        _statusColor = new Vector4(1, 0.3f, 0.3f, 1);
        PrintStatusError("Cannot add screen: the current TV is locked by its owner.");
      } catch (Exception) {
        _statusMessage = "Network error while adding screen.";
        _statusColor = new Vector4(1, 0.3f, 0.3f, 1);
      }
    }

    public async void RegisterTvAsync(string locationKey, bool quiet = false, bool bypassDebounce = false) {
      if (!_enabled) {
        if (!quiet) {
          _statusMessage = "World screen is not enabled!";
          _statusColor = new Vector4(1, 0.3f, 0.3f, 1);
        }
        return;
      }

      if (!bypassDebounce && (DateTime.UtcNow - _lastRegistrationTime).TotalSeconds < 2) {
          return; // Debounce to prevent double-logs from FFXIV UI flickering
      }
      _lastRegistrationTime = DateTime.UtcNow;

      if (!quiet) {
        _statusMessage = "Registering TV on server...";
        _statusColor = new Vector4(1, 1, 1, 1);
      }

      SyncToTransform();
      _plugin.ApplyWorkingTransformToCurrentSelection();
      _plugin.EnsureCurrentTvForSync(locationKey);
      if (!quiet) {
        _onSave?.Invoke();
      }
      var placement = BuildPlacementFromTransform(locationKey, createNewId: false);

      try 
      {
        var result = await _plugin.ServerClient.RegisterTvAsync(locationKey, placement, create: false);
        _plugin.RunOnFrameworkThread(() =>
        {
          if (result != null) {
            result = Plugin.MergeTvPlacementFromServer(result, placement);
            _plugin.UpsertRoomTv(result);
            _plugin.SelectTvForEditing(result);
            if (!quiet) {
              _statusMessage = "Successfully registered changes for all visitors!";
              _statusColor = new Vector4(0.3f, 1f, 0.3f, 1);
              PrintStatus("Successfully registered changes for all visitors!");
            }
          } else if (!quiet) {
            _statusMessage = "Saved locally, but failed to reach the sync server.";
            _statusColor = new Vector4(1, 0.6f, 0.2f, 1);
            PrintStatusError("Saved locally, but failed to reach the sync server.");
          }
        });
      } 
      catch (UnauthorizedAccessException) 
      {
        _statusMessage = "Cannot move TV: It is locked by its owner.";
        _statusColor = new Vector4(1, 0.3f, 0.3f, 1);
        PrintStatusError("Cannot move TV: It is locked by its owner.");
      }
      catch (Exception)
      {
        _statusMessage = "Network error while syncing TV.";
        _statusColor = new Vector4(1, 0.3f, 0.3f, 1);
        PrintStatusError("Network error while syncing TV.");
      }
    }

  }
}


