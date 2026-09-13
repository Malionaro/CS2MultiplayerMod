using Colossal.Mathematics;
using CS2MultiplayerMod.Core.Protocol.Messages;
using Game.Rendering;
using Unity.Mathematics;
using UnityEngine;

namespace CS2MultiplayerMod.Game.Sync.Players
{
    public partial class RemotePlayerMarkerSystem
    {
        private const long HoverStaleAfterMs = 1500;

        /// <summary>
        /// Outline width, in metres, at the closest camera. A width fixed in world units is a slab
        /// from close up and a hairline from a zoomed-out camera - which is where a partner's
        /// outlines are most needed - so it grows with the distance the shape is seen from.
        /// </summary>
        private const float HoverLineWidth = 2f;
        private const float HoverWidthPerMetre = 0.005f;
        private const float HoverMaxLineWidth = 14f;

        /// <summary>Steps the curve outline is drawn in; a road edge is two lines of these.</summary>
        private const int CurveSteps = 16;

        /// <summary>Eases an outline onto a newly received shape rather than snapping it there.</summary>
        private const float HoverBlendSeconds = 0.07f;

        private static void AdvanceHover(Trail trail, RemotePlayer player, float seconds, long now)
        {
            var target = player.Hover;
            int count = now - player.LastUpdateMs <= HoverStaleAfterMs && target != null
                ? math.min(target.Length, PlayerHoverShape.MaxShapes) : 0;
            float blend = 1f - math.exp(-math.max(0f, seconds) / HoverBlendSeconds);
            for (int i = 0; i < count; i++)
            {
                PlayerHoverShape next = target[i], previous = trail.Hover[i];
                if (i < trail.HoverCount && previous.Placement && next.Placement &&
                    previous.Kind == next.Kind && previous.Key == next.Key &&
                    math.distancesq(Vector(previous.A), Vector(next.A)) < SnapDistance * SnapDistance)
                {
                    next.A = Blend(previous.A, next.A, blend);
                    next.B = Blend(previous.B, next.B, blend);
                    next.C = Blend(previous.C, next.C, blend);
                    next.D = Blend(previous.D, next.D, blend);
                    next.Width = math.lerp(previous.Width, next.Width, blend);
                    next.Height = math.lerp(previous.Height, next.Height, blend);
                }
                trail.Hover[i] = next;
            }
            trail.HoverCount = count;
        }

        private bool HoverVisible(Trail trail, bool culling)
        {
            for (int i = 0; i < trail.HoverCount; i++)
                if (!culling || ShapeVisible(trail.Hover[i])) return true;
            return false;
        }

        /// <summary>How thick a shape at <paramref name="point"/> has to be drawn to read on screen.</summary>
        private float HoverWidth(float3 point) => math.clamp(
            math.distance(_localEye, point) * HoverWidthPerMetre, HoverLineWidth, HoverMaxLineWidth);

        /// <summary>
        /// The screen-readable width, held down to a fraction of what it is outlining: a house is a
        /// few metres across, and a line scaled for a distant camera would swallow it whole.
        /// </summary>
        private float HoverWidth(float3 point, float extent) => math.max(HoverLineWidth,
            math.min(HoverWidth(point), extent * 0.25f));

        private bool ShapeVisible(PlayerHoverShape shape)
        {
            float3 a = Vector(shape.A);
            if (shape.Kind == PlayerHoverKind.Circle) return SphereVisible(a, shape.Width * 0.5f + HoverLineWidth);
            float3 min = math.min(math.min(a, Vector(shape.B)), math.min(Vector(shape.C), Vector(shape.D)));
            float3 max = math.max(math.max(a, Vector(shape.B)), math.max(Vector(shape.C), Vector(shape.D)));
            max.y += shape.Height;
            return SphereVisible((min + max) * 0.5f, math.length(max - min) * 0.5f + shape.Width * 0.5f + HoverLineWidth);
        }

        private void DrawHover(OverlayRenderSystem.Buffer buffer, Trail trail, Color color, bool culling)
        {
            using (Diagnostics.SyncProfiler.Measure("PartnerHover.Draw"))
            {
                for (int i = 0; i < trail.HoverCount; i++)
                {
                    PlayerHoverShape shape = trail.Hover[i];
                    if (culling && !ShapeVisible(shape)) continue;
                    float3 a = Vector(shape.A), b = Vector(shape.B), c = Vector(shape.C), d = Vector(shape.D);
                    float line = HoverWidth(a);
                    switch (shape.Kind)
                    {
                        case PlayerHoverKind.Circle:
                            line = HoverWidth(a, math.max(1f, shape.Width));
                            buffer.DrawCircle(color, new Color(color.r, color.g, color.b, 0f), line,
                                default, new float2(0f, 1f), a, math.max(1f, shape.Width));
                            break;
                        case PlayerHoverKind.Box:
                            line = HoverWidth(a, math.min(math.distance(a, b), math.distance(b, c)));
                            DrawQuad(buffer, color, line, a, b, c, d);
                            if (!shape.Placement && shape.Height > 1f)
                            {
                                float3 up = new float3(0f, shape.Height, 0f);
                                float3 centre = (a + b + c + d) * 0.25f;
                                DrawQuad(buffer, color, line, a + up, b + up, c + up, d + up);
                                DrawPost(buffer, color, line, centre, a, up);
                                DrawPost(buffer, color, line, centre, b, up);
                                DrawPost(buffer, color, line, centre, c, up);
                                DrawPost(buffer, color, line, centre, d, up);
                            }
                            break;
                        case PlayerHoverKind.Curve:
                            // A pipe or a power line is narrower than the outline that would trace
                            // it: one line down the middle, where two edges would merge into a blur.
                            if (shape.Width <= line * 2f)
                            {
                                buffer.DrawCurve(color, new Bezier4x3(a, b, c, d),
                                    math.max(shape.Width, line));
                                break;
                            }
                            // Two thin edges show the road width without a large translucent fill.
                            float3 lastLeft = default, lastRight = default;
                            for (int step = 0; step <= CurveSteps; step++)
                            {
                                float t = (float)step / CurveSteps, u = 1f - t;
                                float3 point = u * u * u * a + 3f * u * u * t * b +
                                    3f * u * t * t * c + t * t * t * d;
                                float3 tangent = u * u * (b - a) + 2f * u * t * (c - b) + t * t * (d - c);
                                float3 side = math.normalizesafe(new float3(-tangent.z, 0f, tangent.x),
                                    new float3(1f, 0f, 0f)) * (shape.Width * 0.5f);
                                float3 left = point + side, right = point - side;
                                if (step == 0 || step == CurveSteps)
                                    DrawHoverLine(buffer, color, line, left, right);
                                if (step != 0)
                                {
                                    DrawHoverLine(buffer, color, line, lastLeft, left);
                                    DrawHoverLine(buffer, color, line, lastRight, right);
                                }
                                lastLeft = left; lastRight = right;
                            }
                            break;
                    }
                }
            }
        }

        /// <summary>
        /// One upright of the outline, stood just outside the corner it belongs to: the overlay is
        /// depth-tested, so an upright on the corner itself is swallowed by the building's own mesh
        /// and the outline reads as two loose rectangles.
        /// </summary>
        private static void DrawPost(OverlayRenderSystem.Buffer buffer, Color color, float width,
            float3 centre, float3 corner, float3 up)
        {
            float3 outward = math.normalizesafe(new float3(corner.x - centre.x, 0f, corner.z - centre.z))
                * (width * 0.75f);
            DrawHoverLine(buffer, color, width, corner + outward, corner + outward + up);
        }

        private static void DrawQuad(OverlayRenderSystem.Buffer buffer, Color color, float width,
            float3 a, float3 b, float3 c, float3 d)
        {
            DrawHoverLine(buffer, color, width, a, b); DrawHoverLine(buffer, color, width, b, c);
            DrawHoverLine(buffer, color, width, c, d); DrawHoverLine(buffer, color, width, d, a);
        }

        private static void DrawHoverLine(OverlayRenderSystem.Buffer buffer, Color color, float width,
            float3 a, float3 b)
        {
            if (math.distancesq(a, b) > 0.0001f)
                buffer.DrawLine(color, new Line3.Segment(a, b), width, true);
        }

        private static float3 Vector(HoverPoint p) => new float3(p.X, p.Y, p.Z);
        private static HoverPoint Blend(HoverPoint a, HoverPoint b, float blend)
        {
            float3 value = math.lerp(Vector(a), Vector(b), blend);
            return new HoverPoint(value.x, value.y, value.z);
        }
    }
}
