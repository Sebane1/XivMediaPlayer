using Newtonsoft.Json;
using System;
using System.Numerics;

namespace MediaPlayerCore.Compositing {
  /// <summary>
  /// Describes the placement of a video screen in 3D world space.
  /// Pure math. No game engine dependencies.
  /// </summary>
  [Serializable]
  public class WorldScreenTransform {
    /// <summary>
    /// World-space position of the screen center.
    /// </summary>
    [JsonProperty("position")]
    public Vector3 Position { get; set; } = Vector3.Zero;

    /// <summary>
    /// Rotation in degrees: X = pitch, Y = yaw, Z = roll.
    /// Yaw 0 = facing north (+Z), 90 = facing east (+X).
    /// </summary>
    [JsonProperty("rotation")]
    public Vector3 RotationDegrees { get; set; } = Vector3.Zero;

    /// <summary>
    /// Screen dimensions in world units (width, height).
    /// Default: 3m x ~1.7m (roughly 16:9 at 3m wide).
    /// </summary>
    [JsonProperty("scale")]
    public Vector2 Scale { get; set; } = new Vector2(3.0f, 1.6875f);

    /// <summary>0 = 16:9 locked, 1 = 4:3 locked, 2 = custom width/height.</summary>
    [JsonProperty("scaleAspectMode")]
    public int ScaleAspectMode { get; set; } = 0;

    /// <summary>
    /// Whether world rendering is currently active.
    /// </summary>
    [JsonProperty("enabled")]
    public bool Enabled { get; set; } = false;

    [JsonProperty("opacity")]
    public float Opacity { get; set; } = 1.0f;

    [JsonProperty("isProjectorMode")]
    public bool IsProjectorMode { get; set; } = false;

    [JsonProperty("screensaverColor")]
    public System.Numerics.Vector3 ScreensaverColor { get; set; } = new System.Numerics.Vector3(0.0f, 0.0f, 0.0f);

    [JsonProperty("screensaverStyle")]
    public int ScreensaverStyle { get; set; } = 0;

    [JsonProperty("idleBrandingUrl")]
    public string IdleBrandingUrl { get; set; } = string.Empty;

    [JsonProperty("visualEffectMode")]
    public int VisualEffectMode { get; set; } = 0;

    [JsonProperty("effectIntensity")]
    public float EffectIntensity { get; set; } = 0.65f;

    [JsonProperty("effectSpeed")]
    public float EffectSpeed { get; set; } = 1.0f;

    /// <summary>Banner/static image sources use softer cast-light falloff than video TVs.</summary>
    [JsonIgnore]
    public bool IsStaticLightSource { get; set; }

    /// <summary>
    /// Returns the four corners of the screen quad in world space.
    /// Order: TopLeft, TopRight, BottomRight, BottomLeft (when facing the screen).
    /// </summary>
    [JsonIgnore]
    public (Vector3 TL, Vector3 TR, Vector3 BR, Vector3 BL) Corners {
      get {
        float halfW = Scale.X * 0.5f;
        float halfH = Scale.Y * 0.5f;

        // Local-space corners (screen faces -Z by default)
        var tl = new Vector3(-halfW, halfH, 0);
        var tr = new Vector3(halfW, halfH, 0);
        var br = new Vector3(halfW, -halfH, 0);
        var bl = new Vector3(-halfW, -halfH, 0);

        // Apply rotation
        var rotation = RotationMatrix;
        tl = Vector3.Transform(tl, rotation) + Position;
        tr = Vector3.Transform(tr, rotation) + Position;
        br = Vector3.Transform(br, rotation) + Position;
        bl = Vector3.Transform(bl, rotation) + Position;

        return (tl, tr, br, bl);
      }
    }

    /// <summary>
    /// The rotation matrix derived from Euler angles.
    /// </summary>
    [JsonIgnore]
    public Matrix4x4 RotationMatrix {
      get {
        float pitch = RotationDegrees.X * MathF.PI / 180f;
        float yaw = RotationDegrees.Y * MathF.PI / 180f;
        float roll = RotationDegrees.Z * MathF.PI / 180f;
        return Matrix4x4.CreateFromYawPitchRoll(yaw, pitch, roll);
      }
    }

    /// <summary>
    /// The forward direction the screen is facing (normal to the screen surface).
    /// </summary>
    [JsonIgnore]
    public Vector3 Forward => Vector3.Transform(-Vector3.UnitZ, RotationMatrix);

    /// <summary>
    /// Places the screen at the given position, facing toward a target point.
    /// </summary>
    public void PlaceLookingAt(Vector3 screenPosition, Vector3 lookAtTarget) {
      Position = screenPosition;
      var dir = Vector3.Normalize(lookAtTarget - screenPosition);
      float yaw = MathF.Atan2(dir.X, dir.Z) * 180f / MathF.PI;
      float pitch = MathF.Asin(-dir.Y) * 180f / MathF.PI;
      RotationDegrees = new Vector3(pitch, yaw, 0);
    }

    /// <summary>
    /// Creates a deep copy of this transform.
    /// </summary>
    public WorldScreenTransform Clone() {
      return new WorldScreenTransform {
        Position = Position,
        RotationDegrees = RotationDegrees,
        Scale = Scale,
        ScaleAspectMode = ScaleAspectMode,
        Enabled = Enabled,
        Opacity = Opacity,
        IsProjectorMode = IsProjectorMode,
        ScreensaverColor = ScreensaverColor,
        ScreensaverStyle = ScreensaverStyle,
        IdleBrandingUrl = IdleBrandingUrl,
        VisualEffectMode = VisualEffectMode,
        EffectIntensity = EffectIntensity,
        EffectSpeed = EffectSpeed,
        IsStaticLightSource = IsStaticLightSource,
      };
    }
  }
}
