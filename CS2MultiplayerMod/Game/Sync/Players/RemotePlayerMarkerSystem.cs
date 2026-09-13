using System.Collections.Generic;
using Colossal.Mathematics;
using CS2MultiplayerMod.Core.Diagnostics;
using CS2MultiplayerMod.Game.Diagnostics;
using Game;
using Game.Rendering;
using Unity.Jobs;
using Unity.Mathematics;
using UnityEngine;

namespace CS2MultiplayerMod.Game.Sync.Players
{
    /// <summary>
    /// Draws a coloured ground ring at every other player's camera focus - the point on
    /// the map they are looking at - so partners can see where each other is working.
    /// The positions themselves are published by <see cref="PlayerCursorSyncSystem"/>
    /// (the gameplay camera pivot) and kept fresh in
    /// <see cref="MultiplayerService.RemotePlayers"/>; this system renders them, easing
    /// between the received positions and trailing a short motion smear behind them.
    /// </summary>
    public partial class RemotePlayerMarkerSystem : GameSystemBase
    {
        /// <summary>A position older than this (no fresh update) stops being drawn.</summary>
        private const long StaleAfterMs = 5000;

        /// <summary>Ring size on the ground, in metres.</summary>
        private const float RingDiameter = 30f;
        private const float RingOutlineWidth = 4f;
        /// <summary>Width of the line drawn from the ground focus up to the camera.</summary>
        private const float BeamWidth = 3f;

        /// <summary>
        /// Beam length cap. The overlay quad is camera-facing, so its screen area grows with the
        /// partner's altitude even though the line itself stays 3 m wide - and a zoomed-out camera
        /// sits kilometres up. The first stretch above the ground already reads as height.
        /// </summary>
        private const float MaxBeamLength = 400f;

        /// <summary>
        /// Keep-out radius around the local camera. The beam's far end is the partner's eye, which
        /// in a shared view lands on top of the local camera; a camera-facing quad ending there is
        /// clipped against the near plane and covers most of the screen in translucent fill.
        /// </summary>
        private const float BeamCameraClearance = 80f;

        /// <summary>
        /// How far the drawn marker lags the newest received position. Positions arrive about ten
        /// times a second, so without this the ring holds still for several frames and then jumps.
        /// The lag has to be close to that gap to cover it, and all of it is marker latency.
        /// </summary>
        private const float MarkerLagSeconds = 0.07f;

        /// <summary>
        /// Lag of each smear copy behind the one in front of it. Times the partner's pan speed
        /// this is also their spacing on the ground: short enough that they overlap into one
        /// smear instead of reading as a row of separate rings.
        /// </summary>
        private const float SmearLagSeconds = 0.05f;

        /// <summary>
        /// Strength of each smear copy relative to the ring, nearest first. The length decides how
        /// many there are, and each one costs a single overlay entry - no extra pass, no shader.
        /// </summary>
        private static readonly float[] SmearStrength = { 0.45f, 0.28f, 0.16f };

        /// <summary>
        /// How far the back of the smear has to fall behind the ring for the smear to reach full
        /// strength. The whole smear fades on this one distance rather than each copy fading on its
        /// own: a marker standing still has them all sitting on top of it, where they have to come
        /// out to nothing rather than thicken the ring.
        /// </summary>
        private const float SmearFullLagDistance = 12f;

        /// <summary>Weaker than this is invisible anyway, so it does not get an overlay entry.</summary>
        private const float SmearMinStrength = 0.02f;

        /// <summary>
        /// A step larger than this is a jump rather than a pan - a minimap click, or a snap to a
        /// notification - and easing into it would drag the marker across half the map.
        /// </summary>
        private const float SnapDistance = 350f;

        // Distinct, readable colours cycled by player id so each partner is recognisable.
        private static readonly Color[] Palette =
        {
            new Color(0.36f, 0.78f, 1.00f), // blue
            new Color(1.00f, 0.69f, 0.26f), // orange
            new Color(0.56f, 0.88f, 0.55f), // green
            new Color(1.00f, 0.45f, 0.45f), // red
            new Color(0.80f, 0.60f, 1.00f), // purple
            new Color(1.00f, 0.85f, 0.40f), // yellow
        };

        private OverlayRenderSystem _overlay;
        private CameraUpdateSystem _camera;
        private readonly Plane[] _frustum = new Plane[6];

        /// <summary>Where a partner's marker is drawn, as opposed to where they last reported being.</summary>
        private sealed class Trail
        {
            public float3 Focus;
            public float3 Eye;
            /// <summary>The smear, nearest first; each copy chases the one ahead of it.</summary>
            public readonly float3[] Smear = new float3[SmearStrength.Length];
            public long TouchedMs;
        }

        private readonly Dictionary<int, Trail> _trails = new Dictionary<int, Trail>();
        private readonly List<int> _departed = new List<int>();
        private long _lastFrameMs;

        protected override void OnCreate()
        {
            base.OnCreate();
            _overlay = World.GetOrCreateSystemManaged<OverlayRenderSystem>();
            _camera = World.GetExistingSystemManaged<CameraUpdateSystem>();
        }

        protected override void OnUpdate()
        {
            MultiplayerService service = Mod.Service;
            if (service == null || _overlay == null || !service.GameplaySyncReady) return;
            if (Mod.Setting != null && !Mod.Setting.ShowPartnerMarkers)
            {
                if (_trails.Count != 0) _trails.Clear();
                return;
            }

            long now = service.NowMs;
            float frameSeconds = (now - _lastFrameMs) * 0.001f;
            _lastFrameMs = now;

            // Don't touch the overlay buffer at all unless there's a fresh position to draw.
            int fresh = 0;
            foreach (RemotePlayer p in service.RemotePlayers)
                if (now - p.LastUpdateMs <= StaleAfterMs) fresh++;
            if (fresh == 0)
            {
                if (_trails.Count != 0) _trails.Clear();
                return;
            }

            if (_camera == null) _camera = World.GetExistingSystemManaged<CameraUpdateSystem>();
            Camera view = _camera != null ? _camera.activeCamera : null;
            bool culling = view != null;
            if (culling) GeometryUtility.CalculateFrustumPlanes(view, _frustum);
            float3 localEye = _camera != null ? _camera.position : default(float3);

            // Writing the buffer forces the game's overlay pass on for the frame and completes its
            // writers on this thread, so decide there is something visible to draw before taking it.
            bool haveBuffer = false;
            OverlayRenderSystem.Buffer buffer = default(OverlayRenderSystem.Buffer);

            foreach (RemotePlayer p in service.RemotePlayers)
            {
                if (now - p.LastUpdateMs > StaleAfterMs)
                {
                    _trails.Remove(p.PlayerId);
                    continue;
                }

                // Eased before culling: the marker has to keep moving while it is off screen, or it
                // lurches on the frame it comes back into view.
                Trail trail = Advance(p, frameSeconds, now);
                float3 focus = trail.Focus;
                float smearLength = math.distance(trail.Smear[trail.Smear.Length - 1], focus);

                bool ringVisible = !culling || SphereVisible(focus, RingDiameter + smearLength);
                Line3.Segment beam;
                bool beamVisible = TryBuildBeam(focus, trail.Eye, localEye, out beam) &&
                                   (!culling || SegmentVisible(beam));
                if (!ringVisible && !beamVisible) continue;

                if (!haveBuffer)
                {
                    // Taking the buffer turns the game's overlay pass on for this frame and blocks
                    // here until everything the overlay system depends on has finished. Both halves
                    // are paid per frame and both get more expensive the more the frame is already
                    // doing, so this is measured separately from the cheap culling above.
                    using (Diagnostics.SyncProfiler.Measure("PartnerMarkers.Overlay"))
                    {
                        JobHandle dependencies;
                        buffer = _overlay.GetBuffer(out dependencies);
                        dependencies.Complete();
                    }
                    haveBuffer = true;
                }

                Color color = Palette[((p.PlayerId % Palette.Length) + Palette.Length) % Palette.Length];
                Color fill = new Color(color.r, color.g, color.b, 0.12f);
                color.a = 0.9f;

                // Ground ring where the partner is looking, over fading copies of itself along the
                // ground it just covered - those first, so the ring stays on top of its own smear.
                if (ringVisible)
                {
                    float moving = math.saturate(smearLength / SmearFullLagDistance);
                    // Outline only - copies of the ring, not more translucent discs over the
                    // ground. The fill keeps the colour so it tints rather than darkens.
                    var noFill = new Color(color.r, color.g, color.b, 0f);
                    for (int i = 0; i < trail.Smear.Length; i++)
                    {
                        float strength = moving * SmearStrength[i];
                        if (strength < SmearMinStrength) continue;

                        var echo = new Color(color.r, color.g, color.b, color.a * strength);
                        buffer.DrawCircle(echo, noFill, RingOutlineWidth, default,
                            new float2(0f, 1f), trail.Smear[i], RingDiameter);
                    }

                    buffer.DrawCircle(color, fill, RingOutlineWidth, default,
                        new float2(0f, 1f), focus, RingDiameter);
                }

                // A line from that point up towards their camera, so you can see how high they
                // are "flying" (and roughly where they are when zoomed out).
                if (beamVisible) buffer.DrawLine(color, beam, BeamWidth, true);
            }

            // A player who left the session stops turning up in the loop above, so their trail is
            // only reachable from here.
            if (_trails.Count > fresh) DropDepartedTrails(now);
        }

        /// <summary>
        /// Moves a partner's drawn marker and its smear towards the position they last reported.
        /// Each step closes the same fraction of the remaining distance per second, so the result
        /// does not change with frame rate.
        /// </summary>
        private Trail Advance(RemotePlayer player, float frameSeconds, long now)
        {
            var focus = new float3(player.X, player.Y, player.Z);
            var eye = new float3(player.EyeX, player.EyeY, player.EyeZ);

            Trail trail;
            if (!_trails.TryGetValue(player.PlayerId, out trail))
            {
                trail = new Trail();
                _trails.Add(player.PlayerId, trail);
                Land(trail, focus, eye);
            }
            else if (math.distancesq(trail.Focus, focus) > SnapDistance * SnapDistance)
            {
                Land(trail, focus, eye);
            }
            else
            {
                float toMarker = 1f - math.exp(-frameSeconds / MarkerLagSeconds);
                trail.Focus += (focus - trail.Focus) * toMarker;
                trail.Eye += (eye - trail.Eye) * toMarker;

                float toSmear = 1f - math.exp(-frameSeconds / SmearLagSeconds);
                float3 ahead = trail.Focus;
                for (int i = 0; i < trail.Smear.Length; i++)
                {
                    trail.Smear[i] += (ahead - trail.Smear[i]) * toSmear;
                    ahead = trail.Smear[i];
                }
            }

            trail.TouchedMs = now;
            return trail;
        }

        /// <summary>Puts the marker and its whole smear on one position, with nothing in between.</summary>
        private static void Land(Trail trail, float3 focus, float3 eye)
        {
            trail.Focus = focus;
            trail.Eye = eye;
            for (int i = 0; i < trail.Smear.Length; i++) trail.Smear[i] = focus;
        }

        private void DropDepartedTrails(long now)
        {
            foreach (KeyValuePair<int, Trail> entry in _trails)
                if (entry.Value.TouchedMs != now) _departed.Add(entry.Key);

            for (int i = 0; i < _departed.Count; i++) _trails.Remove(_departed[i]);
            _departed.Clear();
        }

        /// <summary>
        /// The drawable part of the focus-to-eye line: capped in length and cut short of the local
        /// camera's keep-out sphere. False when nothing worth drawing is left.
        /// </summary>
        private static bool TryBuildBeam(float3 focus, float3 eye, float3 localEye,
            out Line3.Segment beam)
        {
            beam = default(Line3.Segment);
            float3 delta = eye - focus;
            float length = math.length(delta);
            if (length <= 1f) return false;

            float3 direction = delta / length;
            length = math.min(length, MaxBeamLength);

            // Both ends inside the keep-out sphere: the partner is where the local camera is, so
            // the beam has nothing to say and everything to fill.
            float3 fromCamera = focus - localEye;
            float distanceToStart = math.length(fromCamera);
            if (distanceToStart < BeamCameraClearance) return false;

            // Otherwise clip at the sphere's first intersection along the line.
            float b = 2f * math.dot(fromCamera, direction);
            float c = distanceToStart * distanceToStart - BeamCameraClearance * BeamCameraClearance;
            float discriminant = b * b - 4f * c;
            if (discriminant > 0f)
            {
                float entry = 0.5f * (-b - math.sqrt(discriminant));
                if (entry > 0f) length = math.min(length, entry);
            }
            if (length <= 1f) return false;

            beam = new Line3.Segment(focus, focus + direction * length);
            return true;
        }

        private bool SphereVisible(float3 center, float radius)
        {
            var point = new Vector3(center.x, center.y, center.z);
            for (int i = 0; i < _frustum.Length; i++)
                if (_frustum[i].GetDistanceToPoint(point) < -radius) return false;
            return true;
        }

        private bool SegmentVisible(Line3.Segment segment)
        {
            float3 center = (segment.a + segment.b) * 0.5f;
            float radius = math.length(segment.b - segment.a) * 0.5f + BeamWidth;
            return SphereVisible(center, radius);
        }
    }
}
