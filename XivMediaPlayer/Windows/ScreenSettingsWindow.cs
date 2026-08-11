using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Windowing;
using MediaPlayerCore.Compositing;
using XivMediaPlayer.Compositing;
using System;
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
        ImGuiWindowFlags.NoCollapse | ImGuiWindowFlags.AlwaysAutoResize,
        false) {
      _plugin = plugin;
      _gameGui = gameGui;
      _transform = transform;
      _renderer = renderer;
      _onSave = onSave;
      _onPlaceAtCamera = onPlaceAtCamera;

      Size = new Vector2(340, 0);
      SizeCondition = ImGuiCond.FirstUseEver;

      SyncFromTransform();
    }

    public void SyncFromTransform() {
      _position = _transform.Position;
      _rotation = new Vector2(_transform.RotationDegrees.Y, _transform.RotationDegrees.X); // yaw, pitch
      _scale = _transform.Scale;
      _enabled = _transform.Enabled;
      _opacity = _transform.Opacity;
      _isProjectorMode = _transform.IsProjectorMode;
      _screensaverColor = _transform.ScreensaverColor;
      _screensaverStyle = _transform.ScreensaverStyle;
    }

    private void SyncToTransform() {
      _transform.Position = _position;
      _transform.RotationDegrees = new Vector3(_rotation.Y, _rotation.X, 0); // pitch, yaw, roll
      _transform.Scale = _scale;
      _transform.Enabled = _enabled;
      _transform.Opacity = _opacity;
      _transform.IsProjectorMode = _isProjectorMode;
      _transform.ScreensaverColor = _screensaverColor;
      _transform.ScreensaverStyle = _screensaverStyle;
    }

    private string L(string text) => _plugin.Translate(text);

    private void PrintStatus(string statusKey) => _plugin.PrintChatWithBody(statusKey);

    private void PrintStatusError(string statusKey) => _plugin.PrintErrorChatWithBody(statusKey);

    public override void Draw() {
      _ = _plugin.TranslationRevision;
      WindowName = L("Screen Placement");
      string locKey = _plugin.LocationKey;
      bool isOutdoors = !string.IsNullOrEmpty(locKey) && locKey.StartsWith("zone_");
      bool isIsland = !string.IsNullOrEmpty(locKey) && locKey.StartsWith("island_");
      bool hasHousingMenuOpen = _plugin.IsHousingMenuOpen;
      bool hasPrivileges = isOutdoors || isIsland || hasHousingMenuOpen;

      if (!hasPrivileges) {
          ImGui.TextColored(new Vector4(1f, 0.4f, 0.4f, 1f), L("Housing Menu Required"));
          ImGui.TextWrapped(L("To place or sync a screen, please open the 'Indoor Furnishings' menu in-game."));
          ImGui.Spacing();
          if (ImGui.Button(L("Tutorial Video"))) {
              System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo {
                  FileName = "https://www.youtube.com/watch?v=ZgLs2OJQ8ks",
                  UseShellExecute = true
              });
          }
          return;
      }

      if (isOutdoors && !_plugin.Config.EnableOutdoorPublicScreens) {
          ImGui.TextColored(new Vector4(1f, 0.4f, 0.4f, 1f), L("Outdoor Screens Disabled"));
          ImGui.TextWrapped(L("You must enable 'Enable Outdoor Public Screens' in the main settings menu to place TVs outdoors."));
          return;
      }

      // Enable toggle 
      if (ImGui.Checkbox(L("Render in World"), ref _enabled)) {
        _transform.Enabled = _enabled;
        
        // Auto-delete from server if turning off and we own it or have privileges
        if (!_enabled && !string.IsNullOrEmpty(locKey) &&
            _plugin.CurrentTvPlacement != null && (_plugin.CurrentTvPlacement.OwnerId == _plugin.Config.OwnerId || hasPrivileges)) {
            _ = DeleteTvAsync(locKey, restoreOnFailure: true);
        } else {
            _onSave?.Invoke();
        }
      }

      ImGui.SameLine();
      ImGui.SetCursorPosX(ImGui.GetWindowWidth() - 110);
      if (ImGui.Button(L("Tutorial Video"))) {
          System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo {
              FileName = "https://www.youtube.com/watch?v=ZgLs2OJQ8ks",
              UseShellExecute = true
          });
      }

      if (!_enabled) {
        ImGui.TextColored(new Vector4(0.6f, 0.6f, 0.6f, 1f),
          L("Enable to place the video in the game world."));
        return;
      }

      ImGui.Separator();

      // Ctrl+Shift quick-snap logic
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

      // Quick actions 
      if (ImGui.Button(L("Place at Camera"))) {
        _onPlaceAtCamera?.Invoke();
        SyncFromTransform();
        _onSave?.Invoke();
      }
      
      ImGui.Spacing();
      ImGui.TextColored(new Vector4(0.7f, 1f, 0.7f, 1f), L("Quick Snap:"));
      ImGui.TextWrapped(L("Hold CTRL + SHIFT while hovering over or selecting a furnishing in Edit Mode to instantly snap the TV to it."));
      ImGui.Spacing();
      
      if (ImGui.Button(L("Save"))) {
        SyncToTransform();
        _onSave?.Invoke();
      }
      ImGui.SameLine();
      if (ImGui.Button(L("Reset"))) {
        _transform.Enabled = false;
        _enabled = false;
        SyncFromTransform();
        
        string locKey2 = _plugin.LocationKey;
        if (!string.IsNullOrEmpty(locKey2) && _plugin.CurrentTvPlacement != null && (_plugin.CurrentTvPlacement.OwnerId == _plugin.Config.OwnerId || hasPrivileges)) {
            _ = DeleteTvAsync(locKey2, restoreOnFailure: true);
        } else {
            _onSave?.Invoke();
        }
      }

      ImGui.Spacing();
      ImGui.Separator();

      // Position 
      ImGui.TextColored(new Vector4(0.7f, 0.9f, 1f, 1f), L("Position"));

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

      // Nudge buttons
      float nudge = 0.25f;
      if (ImGui.Button("\u2190##posX")) { _position.X -= nudge; _transform.Position = _position; _onSave?.Invoke(); }
      ImGui.SameLine();
      if (ImGui.Button("\u2192##posX")) { _position.X += nudge; _transform.Position = _position; _onSave?.Invoke(); }
      ImGui.SameLine();
      if (ImGui.Button("\u2193##posY")) { _position.Y -= nudge; _transform.Position = _position; _onSave?.Invoke(); }
      ImGui.SameLine();
      if (ImGui.Button("\u2191##posY")) { _position.Y += nudge; _transform.Position = _position; _onSave?.Invoke(); }
      ImGui.SameLine();
      if (ImGui.Button(L("Near##posZ"))) { _position.Z -= nudge; _transform.Position = _position; _onSave?.Invoke(); }
      ImGui.SameLine();
      if (ImGui.Button(L("Far##posZ"))) { _position.Z += nudge; _transform.Position = _position; _onSave?.Invoke(); }

      ImGui.Spacing();
      ImGui.Separator();

      // Rotation 
      ImGui.TextColored(new Vector4(0.7f, 0.9f, 1f, 1f), L("Rotation"));

      bool rotChanged = false;
      rotChanged |= ImGui.SliderFloat(L("Yaw##rot"), ref _rotation.X, -180f, 180f, "%.1f\u00b0");
      bool saveRot = ImGui.IsItemDeactivatedAfterEdit();
      rotChanged |= ImGui.SliderFloat(L("Pitch##rot"), ref _rotation.Y, -90f, 90f, "%.1f\u00b0");
      saveRot |= ImGui.IsItemDeactivatedAfterEdit();
      if (rotChanged) {
        _transform.RotationDegrees = new Vector3(_rotation.Y, _rotation.X, 0);
      }
      if (saveRot) {
        _onSave?.Invoke();
      }

      // Quick rotation presets
      if (ImGui.Button(L("Face North"))) { _rotation.X = 0; _transform.RotationDegrees = new Vector3(_rotation.Y, 0, 0); _onSave?.Invoke(); }
      ImGui.SameLine();
      if (ImGui.Button(L("Face East"))) { _rotation.X = 90; _transform.RotationDegrees = new Vector3(_rotation.Y, 90, 0); _onSave?.Invoke(); }
      ImGui.SameLine();
      if (ImGui.Button(L("Face South"))) { _rotation.X = 180; _transform.RotationDegrees = new Vector3(_rotation.Y, 180, 0); _onSave?.Invoke(); }
      ImGui.SameLine();
      if (ImGui.Button(L("Face West"))) { _rotation.X = -90; _transform.RotationDegrees = new Vector3(_rotation.Y, -90, 0); _onSave?.Invoke(); }

      ImGui.Spacing();
      ImGui.Separator();

      // Scale 
      ImGui.TextColored(new Vector4(0.7f, 0.9f, 1f, 1f), L("Size (world units)"));

      bool aspectChanged = false;
      aspectChanged |= ImGui.RadioButton("16:9", ref _aspectRatio, 0);
        ImGui.SameLine();
        aspectChanged |= ImGui.RadioButton("4:3", ref _aspectRatio, 1);
        ImGui.SameLine();
        aspectChanged |= ImGui.RadioButton(L("Custom / Free"), ref _aspectRatio, 2);
      
      bool scaleChanged = false;
        if (_aspectRatio != 2) {
            scaleChanged |= ImGui.DragFloat(L("Diagonal Size##scale"), ref _scale.X, 0.1f, 0.5f, 200f, "%.1f");
        } else {
            scaleChanged |= ImGui.DragFloat(L("Width##scaleX"), ref _scale.X, 0.1f, 0.5f, 200f, "%.1f");
            scaleChanged |= ImGui.DragFloat(L("Height##scaleY"), ref _scale.Y, 0.1f, 0.5f, 200f, "%.1f");
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

      // Preset sizes
      if (ImGui.Button(L("Small (2m)"))) { _scale.X = 2f; _scale.Y = _scale.X * (_aspectRatio == 1 ? (3f/4f) : (9f/16f)); _transform.Scale = _scale; _onSave?.Invoke(); }
      ImGui.SameLine();
      if (ImGui.Button(L("Medium (4m)"))) { _scale.X = 4f; _scale.Y = _scale.X * (_aspectRatio == 1 ? (3f/4f) : (9f/16f)); _transform.Scale = _scale; _onSave?.Invoke(); }
      ImGui.SameLine();
      if (ImGui.Button(L("Large (8m)"))) { _scale.X = 8f; _scale.Y = _scale.X * (_aspectRatio == 1 ? (3f/4f) : (9f/16f)); _transform.Scale = _scale; _onSave?.Invoke(); }
      ImGui.SameLine();
      if (ImGui.Button(L("Cinema (12m)"))) { _scale.X = 12f; _scale.Y = _scale.X * (_aspectRatio == 1 ? (3f/4f) : (9f/16f)); _transform.Scale = _scale; _onSave?.Invoke(); }

      ImGui.Spacing();
      ImGui.Separator();

      // Projector & Transparency
      ImGui.TextColored(new Vector4(0.7f, 0.9f, 1f, 1f), L("Projector & Transparency"));
      
      bool appearanceChanged = false;
      appearanceChanged |= ImGui.Checkbox(L("Projector Mode (Additive Blend)"), ref _isProjectorMode);
      
      appearanceChanged |= ImGui.SliderFloat(L("Opacity"), ref _opacity, 0.05f, 1.0f, "%.2f");
      appearanceChanged |= ImGui.ColorEdit3(L("Screensaver Color"), ref _screensaverColor);

      string[] screensaverStyles = new string[] {
        L("Bouncing Logo"), L("VCR"), L("No Signal"), L("Static"), L("Test Pattern"), L("Matrix Rain")
      };
      appearanceChanged |= ImGui.Combo(L("Screensaver Style"), ref _screensaverStyle, screensaverStyles, screensaverStyles.Length);
      
      bool saveAppearance = ImGui.IsItemDeactivatedAfterEdit() || ImGui.IsItemDeactivated();
      
      if (appearanceChanged) {
        _transform.Opacity = _opacity;
        _transform.IsProjectorMode = _isProjectorMode;
        _transform.ScreensaverColor = _screensaverColor;
        _transform.ScreensaverStyle = _screensaverStyle;
      }
      if (saveAppearance || appearanceChanged) {
        _onSave?.Invoke();
      }

      ImGui.Spacing();
      ImGui.Separator();

      // Info 
      ImGui.TextColored(new Vector4(0.5f, 0.5f, 0.5f, 1f),
        string.Format(L("Screen: {0:F1}m x {1:F1}m at ({2:F1}, {3:F1}, {4:F1})"), _scale.X, _scale.Y, _position.X, _position.Y, _position.Z));

      var depthDebug = _renderer.DepthDebugInfo;
      if (!string.IsNullOrEmpty(depthDebug)) {
        ImGui.Spacing();
        ImGui.Separator();
        ImGui.TextColored(new Vector4(1f, 0.8f, 0.3f, 1f), L("Depth Debug"));
        ImGui.TextWrapped(depthDebug);
      }
      var rendererError = _renderer.DepthRendererError;
      if (!string.IsNullOrEmpty(rendererError)) {
        ImGui.TextColored(new Vector4(1f, 0.3f, 0.3f, 1f), string.Format(L("GPU Error: {0}"), rendererError));
      }

      ImGui.Spacing();
      ImGui.Separator();

      ImGui.TextColored(new Vector4(0.7f, 0.9f, 1f, 1f), L("Room Sync"));
      ImGui.TextWrapped(L("Saving above only saves locally. To make the TV visible to other players, you must sync it to the room."));
      
      string locationKey = _plugin.LocationKey;
      bool isOutdoorsSync = !string.IsNullOrEmpty(locationKey) && locationKey.StartsWith("zone_");
      bool isIslandSync = !string.IsNullOrEmpty(locationKey) && locationKey.StartsWith("island_");
      
      if (string.IsNullOrEmpty(locationKey) || (!locationKey.StartsWith("house_") && !locationKey.StartsWith("zone_") && !locationKey.StartsWith("island_"))) {
          ImGui.TextColored(new Vector4(1f, 0.4f, 0.4f, 1f), L("You must be inside a housing area or valid outdoor zone to sync TVs."));
      } else {
          unsafe
          {
              var housingMgr = FFXIVClientStructs.FFXIV.Client.Game.HousingManager.Instance();
              if (housingMgr != null && !housingMgr->IsInside() && housingMgr->GetCurrentPlot() >= 0 && housingMgr->GetCurrentWard() >= 0)
              {
                  ImGui.TextColored(new Vector4(0.4f, 1f, 0.4f, 1f), string.Format(L("You are standing in Plot {0}"), housingMgr->GetCurrentPlot() + 1));
              }
          }
          
          ImGui.TextColored(new Vector4(0.7f, 0.7f, 0.7f, 1f), L("Placement Key:"));
          ImGui.SameLine();
          ImGui.Text(locationKey);

          if (_plugin.CurrentTvPlacement != null)
          {
              ImGui.TextColored(new Vector4(0.4f, 1f, 0.4f, 1f), L("Synced TV Key:"));
              ImGui.SameLine();
              ImGui.Text(_plugin.CurrentTvPlacement.LocationKey);
          }

          if (_plugin.CurrentTvPlacement == null || _plugin.CurrentTvPlacement.OwnerId == _plugin.Config.OwnerId) {
              bool isLocked = _plugin.CurrentTvPlacement?.IsLocked ?? !isOutdoorsSync;
              if (!isOutdoorsSync) {
                  if (ImGui.Checkbox(L("Lock TV to Owner Only"), ref isLocked)) {
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
              if (ImGui.Button(L("Sync Placements to Area"))) {
                  RegisterTvAsync(locationKey);
              }
              ImGui.SameLine();
              if (ImGui.Button(L("Remove TV from Area"))) {
                  _ = DeleteTvAsync(locationKey);
              }
          } else {
              if (_plugin.IsHousingMenuOpen || isOutdoorsSync || isIslandSync) {
                  if (ImGui.Button(L("Take Ownership of TV"))) {
                      RegisterTvAsync(locationKey);
                  }
                  ImGui.TextColored(new Vector4(1f, 0.8f, 0.2f, 1f), L("You can override this locked TV because you have privileges here."));
              } else {
                  if (_plugin.CurrentTvPlacement.IsLocked) {
                      ImGui.TextColored(new Vector4(1f, 0.4f, 0.4f, 1f), L("This TV is locked by its owner."));
                  }
              }
          }

          if (!string.IsNullOrEmpty(_statusMessage)) {
              ImGui.TextColored(_statusColor, L(_statusMessage));
          }
      }
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
            bool success = await _plugin.ServerClient.DeleteTvAsync(serverLocationKey, currentPlacement.Id, _plugin.Config.OwnerId, _plugin.IsHousingMenuOpen || isOutdoorsSync || isIslandSync);
            if (success) {
                _plugin.CurrentTvPlacement = null;
                _plugin.Config.ScreenPlacements.Remove(locationKey);
                _plugin.Config.ScreenPlacements.Remove(serverLocationKey);
                _transform.Enabled = false;
                _enabled = false;
                _plugin.Config.Save();
                _statusMessage = "Successfully removed TV from the room!";
                _statusColor = new Vector4(0.3f, 1f, 0.3f, 1);
                PrintStatus("Successfully removed TV from the room!");
                return true;
            } else {
                RestoreEnabledAfterDeleteFailure(restoreOnFailure);
                _statusMessage = "Failed to remove TV.";
                _statusColor = new Vector4(1, 0.3f, 0.3f, 1);
                PrintStatusError("Failed to remove TV.");
                return false;
            }
        } catch (UnauthorizedAccessException) {
            RestoreEnabledAfterDeleteFailure(restoreOnFailure);
            _statusMessage = "Cannot delete TV: It is locked by its owner.";
            _statusColor = new Vector4(1, 0.3f, 0.3f, 1);
            PrintStatusError("Cannot delete TV: It is locked by its owner.");
        } catch (Exception) {
            RestoreEnabledAfterDeleteFailure(restoreOnFailure);
            _statusMessage = "Network error while deleting TV.";
            _statusColor = new Vector4(1, 0.3f, 0.3f, 1);
            PrintStatusError("Network error while deleting TV.");
        }

        return false;
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

    public async void RegisterTvAsync(string locationKey) {
      if (!_enabled) {
        _statusMessage = "World screen is not enabled!";
        _statusColor = new Vector4(1, 0.3f, 0.3f, 1);
        return;
      }

      if ((DateTime.UtcNow - _lastRegistrationTime).TotalSeconds < 2) {
          return; // Debounce to prevent double-logs from FFXIV UI flickering
      }
      _lastRegistrationTime = DateTime.UtcNow;

      _statusMessage = "Registering TV on server...";
      _statusColor = new Vector4(1, 1, 1, 1);

      var placement = new TvPlacement {
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
        IsProjectorMode = _isProjectorMode,
        ScreensaverColorR = _screensaverColor.X,
        ScreensaverColorG = _screensaverColor.Y,
        ScreensaverColorB = _screensaverColor.Z,
        ScreensaverStyle = _screensaverStyle,
        OwnerId = _plugin.Config.OwnerId,
        IsLocked = _plugin.CurrentTvPlacement?.IsLocked ?? (!locationKey.StartsWith("zone_") && !locationKey.StartsWith("island_")),
        BypassLock = _plugin.IsHousingMenuOpen || locationKey.StartsWith("zone_") || locationKey.StartsWith("island_")
      };

      SyncToTransform();
      _onSave?.Invoke();

      try 
      {
        var result = await _plugin.ServerClient.RegisterTvAsync(locationKey, placement);
        if (result != null) {
          _plugin.CurrentTvPlacement = result;
          _statusMessage = "Successfully registered TV for all visitors!";
          _statusColor = new Vector4(0.3f, 1f, 0.3f, 1);
          PrintStatus("Successfully registered TV for all visitors!");
        } else {
          _statusMessage = "Saved locally, but failed to reach the sync server.";
          _statusColor = new Vector4(1, 0.6f, 0.2f, 1);
          PrintStatusError("Saved locally, but failed to reach the sync server.");
        }
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


