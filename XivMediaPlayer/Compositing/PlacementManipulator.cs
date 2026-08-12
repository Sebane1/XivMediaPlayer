using Dalamud.Bindings.ImGui;
using Dalamud.Plugin.Services;
using MediaPlayerCore.Compositing;
using System;
using System.Collections.Generic;
using System.Numerics;

namespace XivMediaPlayer.Compositing
{
    internal sealed class PlacementManipulator
    {
        public enum TargetType { None, Tv, Banner }

        public enum DragMode { None, MoveScreen, MoveAxisX, MoveAxisY, MoveAxisZ, RotateYaw }

        private enum GizmoHandle { None, AxisX, AxisY, AxisZ, RotateYaw }

        public readonly struct Pickable
        {
            public Pickable(TargetType type, string id, WorldScreenTransform transform)
            {
                Type = type;
                Id = id;
                Transform = transform;
            }

            public TargetType Type { get; }
            public string Id { get; }
            public WorldScreenTransform Transform { get; }
        }

        private const float AxisHitPixels = 16f;
        private const float RingHitPixels = 14f;
        private const float GizmoDistanceFactor = 0.065f;
        private const float MinGizmoSize = 0.35f;
        private const float MaxGizmoSize = 1.85f;
        private const float RotateSensitivity = 0.35f;

        private static readonly Vector4 ColorAxisX = new(1f, 0.28f, 0.28f, 1f);
        private static readonly Vector4 ColorAxisY = new(0.32f, 1f, 0.36f, 1f);
        private static readonly Vector4 ColorAxisZ = new(0.35f, 0.55f, 1f, 1f);
        private static readonly Vector4 ColorRing = new(1f, 0.86f, 0.22f, 1f);
        private static readonly Vector4 ColorHighlight = new(1f, 1f, 1f, 1f);

        private TargetType _selectedType = TargetType.None;
        private string _selectedId = string.Empty;
        private readonly WorldScreenTransform _workingTransform = new() { Enabled = true };

        private DragMode _dragMode = DragMode.None;
        private GizmoHandle _hoverHandle = GizmoHandle.None;
        private WorldScreenTransform _dragStartTransform = new();
        private Vector3 _dragPlaneOrigin;
        private Vector3 _dragPlaneNormal;
        private Vector3 _dragStartHitWorld;
        private Vector2 _dragStartMouse;
        private Vector3 _dragAxisOrigin;
        private Vector3 _dragAxisDir;
        private float _dragStartAxisParam;
        private Vector2 _dragAxisScreenDir;
        private float _dragWorldUnitsPerPixel;

        private Action<TargetType, string, WorldScreenTransform>? _onSelectionChanged;
        private Action<TargetType, string, WorldScreenTransform>? _onTransformPreview;
        private Action? _onCommitRequested;

        public TargetType SelectedType => _selectedType;
        public string SelectedId => _selectedId;
        public bool HasSelection => _selectedType != TargetType.None;
        public bool IsDragging => _dragMode != DragMode.None;
        public WorldScreenTransform WorkingTransform => _workingTransform;

        public void Configure(
            Action<TargetType, string, WorldScreenTransform> onSelectionChanged,
            Action<TargetType, string, WorldScreenTransform> onTransformPreview,
            Action onCommitRequested)
        {
            _onSelectionChanged = onSelectionChanged;
            _onTransformPreview = onTransformPreview;
            _onCommitRequested = onCommitRequested;
        }

        public void ClearSelection()
        {
            _selectedType = TargetType.None;
            _selectedId = string.Empty;
            _dragMode = DragMode.None;
            _hoverHandle = GizmoHandle.None;
        }

        public void SetSelection(TargetType type, string id, WorldScreenTransform transform, bool notify = true)
        {
            _selectedType = type;
            _selectedId = id;
            CopyTransform(_workingTransform, transform);
            if (notify)
            {
                _onSelectionChanged?.Invoke(type, id, _workingTransform);
            }
        }

        /// <summary>Keep the in-world gizmo aligned with the Screen Settings working transform.</summary>
        public void SyncWorkingTransform(WorldScreenTransform source)
        {
            if (!HasSelection || source == null) return;
            CopyTransform(_workingTransform, source);
        }

        /// <summary>
        /// Housing furnishing edit input. Returns true when placement editing consumed the click/drag.
        /// </summary>
        public bool HandleInput(
            bool enabled,
            IGameGui gameGui,
            Vector3 cameraPos,
            Vector3 cameraForward,
            Vector3 cameraRight,
            Vector3 cameraUp,
            float fovY,
            float aspectRatio,
            Vector2 mousePos,
            bool isMouseClicked,
            bool isMouseReleased,
            bool isLeftMouseDown,
            IReadOnlyList<Pickable> pickables)
        {
            if (!enabled)
            {
                if (_dragMode != DragMode.None && isMouseReleased)
                {
                    EndDrag();
                }

                return false;
            }

            if (_dragMode != DragMode.None)
            {
                if (isLeftMouseDown)
                {
                    UpdateDrag(cameraPos, cameraForward, cameraRight, cameraUp, fovY, aspectRatio, mousePos);
                    return true;
                }

                if (isMouseReleased)
                {
                    EndDrag();
                }

                return _dragMode != DragMode.None;
            }

            if (HasSelection)
            {
                float gizmoSize = GetGizmoSize(cameraPos, _workingTransform.Position);
                _hoverHandle = HitTestGizmo(gameGui, cameraPos, cameraForward, cameraRight, cameraUp, fovY, aspectRatio, mousePos, gizmoSize);
            }
            else
            {
                _hoverHandle = GizmoHandle.None;
            }

            if (isMouseClicked && isLeftMouseDown)
            {
                if (HasSelection)
                {
                    float gizmoSize = GetGizmoSize(cameraPos, _workingTransform.Position);
                    var clickedHandle = HitTestGizmo(gameGui, cameraPos, cameraForward, cameraRight, cameraUp, fovY, aspectRatio, mousePos, gizmoSize);
                    if (clickedHandle != GizmoHandle.None)
                    {
                        BeginGizmoDrag(clickedHandle, gameGui, cameraPos, cameraForward, cameraRight, cameraUp, fovY, aspectRatio, mousePos, gizmoSize);
                        return true;
                    }
                }

                if (TryPickFace(cameraPos, cameraForward, cameraRight, cameraUp, fovY, aspectRatio, mousePos, pickables,
                        out var picked))
                {
                    if (!HasSelection || _selectedType != picked.Type || _selectedId != picked.Id)
                    {
                        SetSelection(picked.Type, picked.Id, picked.Transform);
                    }

                    BeginScreenMoveDrag(cameraPos, cameraForward, cameraRight, cameraUp, fovY, aspectRatio, mousePos);
                    return true;
                }
            }

            return false;
        }

        public void DrawOverlay(IGameGui gameGui, Vector3 cameraPos)
        {
            if (!HasSelection) return;

            var drawList = ImGui.GetBackgroundDrawList(ImGui.GetMainViewport());
            DrawQuadOutline(gameGui, drawList, _workingTransform, ImGui.ColorConvertFloat4ToU32(new Vector4(0.2f, 1f, 0.45f, 1f)), 3f);

            var center = _workingTransform.Position;
            float gizmoSize = GetGizmoSize(cameraPos, center);
            DrawTranslateGizmo(gameGui, drawList, center, gizmoSize);
            DrawYawRing(gameGui, drawList, center, gizmoSize * 0.9f);

            var viewport = ImGui.GetMainViewport();
            var hintPos = viewport.Pos + new Vector2(viewport.Size.X * 0.5f - 240f, viewport.Pos.Y + 28f);
            string hint = IsDragging
                ? _dragMode switch
                {
                    DragMode.MoveAxisX => "Moving on X (East/West)...",
                    DragMode.MoveAxisY => "Moving on Y (Up/Down)...",
                    DragMode.MoveAxisZ => "Moving on Z (North/South)...",
                    DragMode.RotateYaw => "Rotating yaw...",
                    _ => "Moving on screen plane..."
                }
                : "Click screen to select • Drag RGB arrows for X/Y/Z • Gold ring = yaw • Drag face = free slide";
            drawList.AddText(hintPos, ImGui.ColorConvertFloat4ToU32(new Vector4(0.85f, 1f, 0.9f, 0.95f)), hint);
        }

        private void BeginScreenMoveDrag(
            Vector3 cameraPos,
            Vector3 cameraForward,
            Vector3 cameraRight,
            Vector3 cameraUp,
            float fovY,
            float aspectRatio,
            Vector2 mousePos)
        {
            _dragMode = DragMode.MoveScreen;
            _dragStartTransform = _workingTransform.Clone();
            _dragPlaneOrigin = _workingTransform.Position;
            _dragPlaneNormal = Vector3.Normalize(_workingTransform.Forward);

            if (!TryRayPlaneHit(cameraPos, cameraForward, cameraRight, cameraUp, fovY, aspectRatio, mousePos,
                    _dragPlaneOrigin, _dragPlaneNormal, out _dragStartHitWorld))
            {
                _dragStartHitWorld = _workingTransform.Position;
            }
        }

        private void BeginGizmoDrag(
            GizmoHandle handle,
            IGameGui gameGui,
            Vector3 cameraPos,
            Vector3 cameraForward,
            Vector3 cameraRight,
            Vector3 cameraUp,
            float fovY,
            float aspectRatio,
            Vector2 mousePos,
            float gizmoSize)
        {
            _dragStartTransform = _workingTransform.Clone();
            _dragStartMouse = mousePos;

            switch (handle)
            {
                case GizmoHandle.AxisX:
                    _dragMode = DragMode.MoveAxisX;
                    _dragAxisDir = Vector3.UnitX;
                    break;
                case GizmoHandle.AxisY:
                    _dragMode = DragMode.MoveAxisY;
                    _dragAxisDir = Vector3.UnitY;
                    break;
                case GizmoHandle.AxisZ:
                    _dragMode = DragMode.MoveAxisZ;
                    _dragAxisDir = Vector3.UnitZ;
                    break;
                case GizmoHandle.RotateYaw:
                    _dragMode = DragMode.RotateYaw;
                    return;
                default:
                    return;
            }

            _dragAxisOrigin = _dragStartTransform.Position;
            SetupAxisScreenDrag(gameGui, gizmoSize);

            BuildRay(cameraPos, cameraForward, cameraRight, cameraUp, fovY, aspectRatio, mousePos,
                out var rayOrigin, out var rayDir);
            if (!TryRayAxisParam(rayOrigin, rayDir, _dragAxisOrigin, _dragAxisDir, out _dragStartAxisParam))
            {
                _dragStartAxisParam = 0f;
            }
        }

        private void SetupAxisScreenDrag(IGameGui gameGui, float gizmoSize)
        {
            var origin = _dragStartTransform.Position;
            if (!gameGui.WorldToScreen(origin, out var screenOrigin)
                || !gameGui.WorldToScreen(origin + _dragAxisDir * gizmoSize, out var screenEnd))
            {
                _dragAxisScreenDir = Vector2.UnitX;
                _dragWorldUnitsPerPixel = gizmoSize / 120f;
                return;
            }

            var screenAxis = screenEnd - screenOrigin;
            float screenLen = screenAxis.Length();
            if (screenLen < 4f)
            {
                _dragAxisScreenDir = Vector2.UnitX;
                _dragWorldUnitsPerPixel = gizmoSize / 120f;
                return;
            }

            _dragAxisScreenDir = screenAxis / screenLen;
            _dragWorldUnitsPerPixel = gizmoSize / screenLen;
        }

        private void UpdateDrag(
            Vector3 cameraPos,
            Vector3 cameraForward,
            Vector3 cameraRight,
            Vector3 cameraUp,
            float fovY,
            float aspectRatio,
            Vector2 mousePos)
        {
            switch (_dragMode)
            {
                case DragMode.MoveScreen:
                    if (TryRayPlaneHit(cameraPos, cameraForward, cameraRight, cameraUp, fovY, aspectRatio, mousePos,
                            _dragPlaneOrigin, _dragPlaneNormal, out var hit))
                    {
                        var delta = hit - _dragStartHitWorld;
                        _workingTransform.Position = _dragStartTransform.Position + delta;
                        PreviewTransform();
                    }
                    break;

                case DragMode.MoveAxisX:
                case DragMode.MoveAxisY:
                case DragMode.MoveAxisZ:
                    UpdateAxisDrag(cameraPos, cameraForward, cameraRight, cameraUp, fovY, aspectRatio, mousePos);
                    break;

                case DragMode.RotateYaw:
                    var mouseDelta = mousePos - _dragStartMouse;
                    _workingTransform.RotationDegrees = new Vector3(
                        _dragStartTransform.RotationDegrees.X,
                        _dragStartTransform.RotationDegrees.Y + mouseDelta.X * RotateSensitivity,
                        _dragStartTransform.RotationDegrees.Z);
                    PreviewTransform();
                    break;
            }
        }

        private void UpdateAxisDrag(
            Vector3 cameraPos,
            Vector3 cameraForward,
            Vector3 cameraRight,
            Vector3 cameraUp,
            float fovY,
            float aspectRatio,
            Vector2 mousePos)
        {
            BuildRay(cameraPos, cameraForward, cameraRight, cameraUp, fovY, aspectRatio, mousePos,
                out var rayOrigin, out var rayDir);

            float delta;
            if (TryRayAxisParam(rayOrigin, rayDir, _dragAxisOrigin, _dragAxisDir, out float axisParam))
            {
                delta = axisParam - _dragStartAxisParam;
            }
            else
            {
                var mouseDelta = mousePos - _dragStartMouse;
                delta = Vector2.Dot(mouseDelta, _dragAxisScreenDir) * _dragWorldUnitsPerPixel;
            }

            _workingTransform.Position = _dragStartTransform.Position + _dragAxisDir * delta;
            PreviewTransform();
        }

        private void EndDrag()
        {
            if (_dragMode == DragMode.None) return;
            _dragMode = DragMode.None;
            _onCommitRequested?.Invoke();
        }

        private void PreviewTransform()
        {
            if (!HasSelection) return;
            _onTransformPreview?.Invoke(_selectedType, _selectedId, _workingTransform);
        }

        private static float GetGizmoSize(Vector3 cameraPos, Vector3 targetPos)
        {
            float distance = Vector3.Distance(cameraPos, targetPos);
            return Math.Clamp(distance * GizmoDistanceFactor, MinGizmoSize, MaxGizmoSize);
        }

        private GizmoHandle HitTestGizmo(
            IGameGui gameGui,
            Vector3 cameraPos,
            Vector3 cameraForward,
            Vector3 cameraRight,
            Vector3 cameraUp,
            float fovY,
            float aspectRatio,
            Vector2 mousePos,
            float gizmoSize)
        {
            var center = _workingTransform.Position;
            float bestDist = float.MaxValue;
            GizmoHandle best = GizmoHandle.None;

            TestAxisHit(gameGui, mousePos, center, Vector3.UnitX, gizmoSize, GizmoHandle.AxisX, ref bestDist, ref best);
            TestAxisHit(gameGui, mousePos, center, Vector3.UnitY, gizmoSize, GizmoHandle.AxisY, ref bestDist, ref best);
            TestAxisHit(gameGui, mousePos, center, Vector3.UnitZ, gizmoSize, GizmoHandle.AxisZ, ref bestDist, ref best);

            if (TryHitYawRing(gameGui, center, gizmoSize * 0.9f, mousePos))
            {
                float ringDist = RingDistancePixels(gameGui, center, gizmoSize * 0.9f, mousePos);
                if (ringDist < bestDist)
                {
                    best = GizmoHandle.RotateYaw;
                }
            }

            return best;
        }

        private static void TestAxisHit(
            IGameGui gameGui,
            Vector2 mousePos,
            Vector3 center,
            Vector3 axisDir,
            float gizmoSize,
            GizmoHandle handle,
            ref float bestDist,
            ref GizmoHandle best)
        {
            if (!gameGui.WorldToScreen(center, out var s0)) return;
            if (!gameGui.WorldToScreen(center + axisDir * gizmoSize, out var s1)) return;

            float dist = DistancePointToSegment(mousePos, s0, s1);
            if (dist <= AxisHitPixels && dist < bestDist)
            {
                bestDist = dist;
                best = handle;
            }
        }

        private static float RingDistancePixels(IGameGui gameGui, Vector3 center, float radius, Vector2 mousePos)
        {
            if (!gameGui.WorldToScreen(center, out var sCenter)) return float.MaxValue;
            if (!gameGui.WorldToScreen(center + new Vector3(radius, 0f, 0f), out var sEdge)) return float.MaxValue;
            float screenRadius = Vector2.Distance(sCenter, sEdge);
            return MathF.Abs(Vector2.Distance(mousePos, sCenter) - screenRadius);
        }

        private static bool TryHitYawRing(IGameGui gameGui, Vector3 center, float radius, Vector2 mousePos)
        {
            return RingDistancePixels(gameGui, center, radius, mousePos) <= RingHitPixels;
        }

        private void DrawTranslateGizmo(IGameGui gameGui, ImDrawListPtr drawList, Vector3 center, float size)
        {
            DrawAxis(gameGui, drawList, center, Vector3.UnitX, size, ColorAxisX, GizmoHandle.AxisX, "X");
            DrawAxis(gameGui, drawList, center, Vector3.UnitY, size, ColorAxisY, GizmoHandle.AxisY, "Y");
            DrawAxis(gameGui, drawList, center, Vector3.UnitZ, size, ColorAxisZ, GizmoHandle.AxisZ, "Z");

            if (gameGui.WorldToScreen(center, out var sCenter))
            {
                drawList.AddCircleFilled(sCenter, 5f, ImGui.ColorConvertFloat4ToU32(new Vector4(0.95f, 0.95f, 0.95f, 0.95f)));
                drawList.AddCircle(sCenter, 5f, ImGui.ColorConvertFloat4ToU32(new Vector4(0f, 0f, 0f, 1f)), 0, 1.5f);
            }
        }

        private void DrawAxis(
            IGameGui gameGui,
            ImDrawListPtr drawList,
            Vector3 center,
            Vector3 axisDir,
            float size,
            Vector4 color,
            GizmoHandle handle,
            string label)
        {
            var end = center + axisDir * size;
            if (!gameGui.WorldToScreen(center, out var s0)) return;
            if (!gameGui.WorldToScreen(end, out var s1)) return;

            bool active = _dragMode switch
            {
                DragMode.MoveAxisX => handle == GizmoHandle.AxisX,
                DragMode.MoveAxisY => handle == GizmoHandle.AxisY,
                DragMode.MoveAxisZ => handle == GizmoHandle.AxisZ,
                _ => false
            };
            bool hover = _hoverHandle == handle;
            var drawColor = active || hover ? ColorHighlight : color;
            uint col = ImGui.ColorConvertFloat4ToU32(drawColor);
            float thickness = active ? 4.5f : hover ? 3.5f : 2.5f;

            drawList.AddLine(s0, s1, col, thickness);

            var dir2 = Vector2.Normalize(s1 - s0);
            var normal = new Vector2(-dir2.Y, dir2.X);
            float headLen = Math.Clamp(Vector2.Distance(s0, s1) * 0.18f, 8f, 18f);
            var tip = s1;
            var baseLeft = s1 - dir2 * headLen + normal * (headLen * 0.42f);
            var baseRight = s1 - dir2 * headLen - normal * (headLen * 0.42f);
            drawList.AddTriangleFilled(tip, baseLeft, baseRight, col);

            drawList.AddText(s1 + dir2 * 6f + normal * 2f, col, label);
        }

        private void DrawYawRing(IGameGui gameGui, ImDrawListPtr drawList, Vector3 center, float radius)
        {
            const int segments = 48;
            Vector2? prev = null;
            bool active = _dragMode == DragMode.RotateYaw;
            bool hover = _hoverHandle == GizmoHandle.RotateYaw;
            var color = active || hover ? ColorHighlight : ColorRing;
            uint col = ImGui.ColorConvertFloat4ToU32(color);
            float thickness = active ? 3.5f : hover ? 3f : 2f;

            for (int i = 0; i <= segments; i++)
            {
                float angle = i / (float)segments * MathF.PI * 2f;
                var point = center + new Vector3(MathF.Cos(angle) * radius, 0f, MathF.Sin(angle) * radius);
                if (!gameGui.WorldToScreen(point, out var screen)) continue;

                if (prev.HasValue)
                {
                    drawList.AddLine(prev.Value, screen, col, thickness);
                }

                prev = screen;
            }
        }

        private static float DistancePointToSegment(Vector2 p, Vector2 a, Vector2 b)
        {
            var ab = b - a;
            float lenSq = ab.LengthSquared();
            if (lenSq <= 1e-6f) return Vector2.Distance(p, a);
            float t = Math.Clamp(Vector2.Dot(p - a, ab) / lenSq, 0f, 1f);
            var closest = a + ab * t;
            return Vector2.Distance(p, closest);
        }

        private static bool TryRayAxisParam(
            Vector3 rayOrigin,
            Vector3 rayDir,
            Vector3 axisOrigin,
            Vector3 axisDir,
            out float param)
        {
            param = 0f;
            rayDir = Vector3.Normalize(rayDir);
            axisDir = Vector3.Normalize(axisDir);

            Vector3 w0 = rayOrigin - axisOrigin;
            float b = Vector3.Dot(rayDir, axisDir);
            float d = Vector3.Dot(rayDir, w0);
            float e = Vector3.Dot(axisDir, w0);
            float denom = 1f - b * b;

            if (MathF.Abs(denom) < 1e-5f)
            {
                return false;
            }

            param = (e - b * d) / denom;
            return true;
        }

        private static void CopyTransform(WorldScreenTransform target, WorldScreenTransform source)
        {
            target.Position = source.Position;
            target.RotationDegrees = source.RotationDegrees;
            target.Scale = source.Scale;
            target.Enabled = source.Enabled;
            target.Opacity = source.Opacity;
            target.IsProjectorMode = source.IsProjectorMode;
            target.ScreensaverColor = source.ScreensaverColor;
            target.ScreensaverStyle = source.ScreensaverStyle;
            target.ScaleAspectMode = source.ScaleAspectMode;
        }

        private bool TryPickFace(
            Vector3 cameraPos,
            Vector3 cameraForward,
            Vector3 cameraRight,
            Vector3 cameraUp,
            float fovY,
            float aspectRatio,
            Vector2 mousePos,
            IReadOnlyList<Pickable> pickables,
            out Pickable picked)
        {
            picked = default;
            if (pickables.Count == 0) return false;

            float bestDistance = float.MaxValue;
            Pickable best = default;
            bool found = false;

            foreach (var item in pickables)
            {
                if (!TryRayQuadHit(cameraPos, cameraForward, cameraRight, cameraUp, fovY, aspectRatio, mousePos,
                        item.Transform, out float distance, out _))
                {
                    continue;
                }

                if (distance >= bestDistance) continue;
                bestDistance = distance;
                best = item;
                found = true;
            }

            if (!found) return false;
            picked = best;
            return true;
        }

        private static bool TryRayQuadHit(
            Vector3 cameraPos,
            Vector3 cameraForward,
            Vector3 cameraRight,
            Vector3 cameraUp,
            float fovY,
            float aspectRatio,
            Vector2 mousePos,
            WorldScreenTransform transform,
            out float distance,
            out Vector2 uv)
        {
            distance = 0f;
            uv = new Vector2(-1, -1);

            var (tl, tr, br, bl) = transform.Corners;
            var quadRight = tr - tl;
            var quadDown = bl - tl;
            var quadNormal = Vector3.Normalize(Vector3.Cross(quadRight, quadDown));

            BuildRay(cameraPos, cameraForward, cameraRight, cameraUp, fovY, aspectRatio, mousePos,
                out var rayOrigin, out var rayDir);

            float denom = Vector3.Dot(quadNormal, rayDir);
            if (Math.Abs(denom) <= 1e-6f) return false;

            float t = Vector3.Dot(tl - rayOrigin, quadNormal) / denom;
            if (t <= 0f) return false;

            var hitPoint = rayOrigin + rayDir * t;
            var d = hitPoint - tl;
            float u = Vector3.Dot(d, quadRight) / quadRight.LengthSquared();
            float v = Vector3.Dot(d, quadDown) / quadDown.LengthSquared();
            if (u < 0f || u > 1f || v < 0f || v > 1f) return false;

            distance = t;
            uv = new Vector2(u, v);
            return true;
        }

        private static bool TryRayPlaneHit(
            Vector3 cameraPos,
            Vector3 cameraForward,
            Vector3 cameraRight,
            Vector3 cameraUp,
            float fovY,
            float aspectRatio,
            Vector2 mousePos,
            Vector3 planeOrigin,
            Vector3 planeNormal,
            out Vector3 hit)
        {
            hit = default;
            BuildRay(cameraPos, cameraForward, cameraRight, cameraUp, fovY, aspectRatio, mousePos,
                out var rayOrigin, out var rayDir);

            float denom = Vector3.Dot(planeNormal, rayDir);
            if (Math.Abs(denom) <= 1e-6f) return false;

            float t = Vector3.Dot(planeOrigin - rayOrigin, planeNormal) / denom;
            if (t < 0f) return false;

            hit = rayOrigin + rayDir * t;
            return true;
        }

        private static void BuildRay(
            Vector3 cameraPos,
            Vector3 cameraForward,
            Vector3 cameraRight,
            Vector3 cameraUp,
            float fovY,
            float aspectRatio,
            Vector2 mousePos,
            out Vector3 rayOrigin,
            out Vector3 rayDir)
        {
            var viewport = ImGui.GetMainViewport();
            float ndcX = ((mousePos.X - viewport.Pos.X) / viewport.Size.X) * 2f - 1f;
            float ndcY = -(((mousePos.Y - viewport.Pos.Y) / viewport.Size.Y) * 2f - 1f);
            float fovDist = 1.0f / MathF.Tan(fovY * 0.5f);
            rayOrigin = cameraPos;
            rayDir = Vector3.Normalize(ndcX * aspectRatio * cameraRight + ndcY * cameraUp - fovDist * cameraForward);
        }

        private static void DrawQuadOutline(IGameGui gameGui, ImDrawListPtr drawList, WorldScreenTransform transform, uint color, float thickness)
        {
            var (tl, tr, br, bl) = transform.Corners;
            if (!gameGui.WorldToScreen(tl, out var sTL)) return;
            if (!gameGui.WorldToScreen(tr, out var sTR)) return;
            if (!gameGui.WorldToScreen(br, out var sBR)) return;
            if (!gameGui.WorldToScreen(bl, out var sBL)) return;

            drawList.AddLine(sTL, sTR, color, thickness);
            drawList.AddLine(sTR, sBR, color, thickness);
            drawList.AddLine(sBR, sBL, color, thickness);
            drawList.AddLine(sBL, sTL, color, thickness);
        }
    }
}
