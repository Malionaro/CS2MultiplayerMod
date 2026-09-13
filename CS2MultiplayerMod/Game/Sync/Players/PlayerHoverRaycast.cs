using Game;
using Game.Common;
using Game.Input;
using Game.Net;
using Game.Rendering;
using Game.Tools;
using Unity.Collections;
using Unity.Entities;

namespace CS2MultiplayerMod.Game.Sync.Players
{
    /// <summary>
    /// Finds the net under the local cursor for <see cref="PlayerCursorSyncSystem"/> to publish.
    /// The shared tool raycast only looks for what the active tool needs, and with no tool selected
    /// that excludes every net, so a road, track or pipe being pointed at otherwise reaches no
    /// partner. Raycast phase: an input only joins the frame's raycast job from there. The answer to
    /// the previous frame's input is taken before this one is added, which leaves this system's
    /// order against the publisher irrelevant.
    /// </summary>
    public partial class PlayerHoverRaycastSystem : GameSystemBase
    {
        /// <summary>Faster than the publish cadence so the shape sent is the one under the cursor.</summary>
        private const long AskIntervalMs = 50;

        /// <summary>Editor-only and in-building marker nets are not what a partner is pointing at.</summary>
        private const Layer HoverLayers =
            (Layer)~(uint)(Layer.MarkerPathway | Layer.MarkerTaxiway | Layer.LaneEditor);

        private RaycastSystem _raycast;
        private ToolRaycastSystem _toolRaycast;
        private ToolSystem _tools;
        private CameraUpdateSystem _camera;
        private bool _pending;
        private long _lastAskMs;

        /// <summary>The net last found under the cursor, or <see cref="Entity.Null"/>.</summary>
        public Entity NetHit { get; private set; }

        protected override void OnCreate()
        {
            base.OnCreate();
            _raycast = World.GetOrCreateSystemManaged<RaycastSystem>();
            _toolRaycast = World.GetOrCreateSystemManaged<ToolRaycastSystem>();
            _tools = World.GetOrCreateSystemManaged<ToolSystem>();
            _camera = World.GetOrCreateSystemManaged<CameraUpdateSystem>();
        }

        protected override void OnUpdate()
        {
            using (Diagnostics.SyncProfiler.Measure("PartnerHover.Raycast"))
            {
                if (_pending) TakeResult();

                MultiplayerService service = Mod.Service;
                if (service == null || !service.GameplaySyncReady ||
                    _tools.fullUpdateRequired || !InputManager.instance.controlOverWorld ||
                    // The active tool already searches the net tree; its own result is what the
                    // capture reads, and a second input would search that tree twice per frame.
                    (_toolRaycast.typeMask & TypeMask.Net) != TypeMask.None)
                {
                    NetHit = Entity.Null;
                    return;
                }

                long now = service.NowMs;
                if (now - _lastAskMs < AskIntervalMs) return;

                Viewer viewer;
                if (!_camera.TryGetViewer(out viewer) || viewer.camera == null) return;

                _lastAskMs = now;
                _raycast.AddInput(this, new RaycastInput
                {
                    m_Line = ToolRaycastSystem.CalculateRaycastLine(viewer.camera),
                    m_TypeMask = TypeMask.Net,
                    m_NetLayerMask = HoverLayers,
                    // Follows the player's own underground toggle, so what they can see is what
                    // their partner is shown.
                    m_CollisionMask = _toolRaycast.collisionMask
                });
                _pending = true;
            }
        }

        private void TakeResult()
        {
            _pending = false;
            NativeArray<RaycastResult> results = _raycast.GetResult(this);
            NetHit = results.Length != 0 ? results[0].m_Owner : Entity.Null;
        }
    }
}
