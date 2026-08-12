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
        public enum DragMode { None, Move, Rotate }

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

        private TargetType _selectedType = TargetType.None;
        private string _selectedId = string.Empty;
        private readonly WorldScreenTransform _workingTransform = new() { Enabled = true };

        private DragMode _dragMode = DragMode.None;
        private WorldScreenTransform _dragStartTransform = new();
        private Vector3 _dragPlaneOrigin;
        private Vector3 _dragPlaneNormal;
        private Vector3 _dragStartHitWorld;
        private Vector2 _dragStartMouse;

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
        }

        public void SetSelection(TargetType type, string id, WorldScreenTransform transform)
        {
            _selectedType = type;
            _selectedId = id;
            CopyTransform(_workingTransform, transform);
            _onSelectionChanged?.Invoke(type, id, _workingTransform);
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

            if (isMouseClicked && isLeftMouseDown)
            {
                if (HasSelection && TryHitRotateHandle(_workingTransform, mousePos, gameGui, out _))
                {
                    BeginRotateDrag(mousePos);
                    return true;
                }

                if (TryPickFace(cameraPos, cameraForward, cameraRight, cameraUp, fovY, aspectRatio, mousePos, pickables,
                        out var picked))
                {
                    if (!HasSelection || _selectedType != picked.Type || _selectedId != picked.Id)
                    {
                        SetSelection(picked.Type, picked.Id, picked.Transform);
                    }

                    BeginMoveDrag(cameraPos, cameraForward, cameraRight, cameraUp, fovY, aspectRatio, mousePos);
                    return true;
                }
            }

            return false;
        }

        public void DrawOverlay(IGameGui gameGui)
        {
            if (!HasSelection) return;

            var drawList = ImGui.GetBackgroundDrawList(ImGui.GetMainViewport());
            DrawQuadOutline(gameGui, drawList, _workingTransform, ImGui.ColorConvertFloat4ToU32(new Vector4(0.2f, 1f, 0.45f, 1f)), 3f);

            var handleWorld = GetRotateHandleWorld(_workingTransform);
            if (gameGui.WorldToScreen(handleWorld, out var handleScreen))
            {
                drawList.AddCircleFilled(handleScreen, 10f, ImGui.ColorConvertFloat4ToU32(new Vector4(1f, 0.85f, 0.2f, 1f)));
                drawList.AddCircle(handleScreen, 10f, ImGui.ColorConvertFloat4ToU32(new Vector4(0f, 0f, 0f, 1f)), 0, 2f);
            }

            var viewport = ImGui.GetMainViewport();
            var hintPos = viewport.Pos + new Vector2(viewport.Size.X * 0.5f - 180f, viewport.Pos.Y + 28f);
            drawList.AddText(hintPos, ImGui.ColorConvertFloat4ToU32(new Vector4(0.85f, 1f, 0.9f, 0.95f)),
                IsDragging
                    ? (_dragMode == DragMode.Rotate ? "Rotating..." : "Moving...")
                    : "Click screen/banner to select • Drag face to move • Drag gold handle to rotate");
        }

        private void BeginMoveDrag(
            Vector3 cameraPos,
            Vector3 cameraForward,
            Vector3 cameraRight,
            Vector3 cameraUp,
            float fovY,
            float aspectRatio,
            Vector2 mousePos)
        {
            _dragMode = DragMode.Move;
            _dragStartTransform = _workingTransform.Clone();
            _dragPlaneOrigin = _workingTransform.Position;
            _dragPlaneNormal = Vector3.Normalize(_workingTransform.Forward);

            if (!TryRayPlaneHit(cameraPos, cameraForward, cameraRight, cameraUp, fovY, aspectRatio, mousePos,
                    _dragPlaneOrigin, _dragPlaneNormal, out _dragStartHitWorld))
            {
                _dragStartHitWorld = _workingTransform.Position;
            }
        }

        private void BeginRotateDrag(Vector2 mousePos)
        {
            _dragMode = DragMode.Rotate;
            _dragStartTransform = _workingTransform.Clone();
            _dragStartMouse = mousePos;
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
            if (_dragMode == DragMode.Move)
            {
                if (TryRayPlaneHit(cameraPos, cameraForward, cameraRight, cameraUp, fovY, aspectRatio, mousePos,
                        _dragPlaneOrigin, _dragPlaneNormal, out var hit))
                {
                    var delta = hit - _dragStartHitWorld;
                    _workingTransform.Position = _dragStartTransform.Position + delta;
                    PreviewTransform();
                }
            }
            else if (_dragMode == DragMode.Rotate)
            {
                var delta = mousePos - _dragStartMouse;
                const float sensitivity = 0.35f;
                _workingTransform.RotationDegrees = new Vector3(
                    ClampPitch(_dragStartTransform.RotationDegrees.X - delta.Y * sensitivity),
                    _dragStartTransform.RotationDegrees.Y + delta.X * sensitivity,
                    _dragStartTransform.RotationDegrees.Z);
                PreviewTransform();
            }
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

        private static float ClampPitch(float pitch) => Math.Clamp(pitch, -89f, 89f);

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
        }

        private static Vector3 GetRotateHandleWorld(WorldScreenTransform transform)
        {
            var (tl, tr, _, bl) = transform.Corners;
            var right = tr - tl;
            var up = tl - bl;
            if (right.LengthSquared() < 1e-6f) right = Vector3.UnitX;
            if (up.LengthSquared() < 1e-6f) up = Vector3.UnitY;
            right = Vector3.Normalize(right);
            up = Vector3.Normalize(up);
            return tr + right * 0.25f + up * 0.25f;
        }

        private bool TryHitRotateHandle(WorldScreenTransform transform, Vector2 mousePos, IGameGui gameGui, out Vector2 handleScreen)
        {
            handleScreen = default;
            var handleWorld = GetRotateHandleWorld(transform);
            if (!gameGui.WorldToScreen(handleWorld, out handleScreen)) return false;
            return Vector2.Distance(mousePos, handleScreen) <= 18f;
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
