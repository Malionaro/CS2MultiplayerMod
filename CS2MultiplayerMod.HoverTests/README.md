# Hover regression checks

Run `dotnet run --project CS2MultiplayerMod.HoverTests -c Release` from the repository root. This compiles the actual game-independent Core sources and opens three loopback sessions with and without TLS.

Checks cover shape round trips, maximum packet size, every truncated prefix, invalid kinds/counts/flags, nonfinite and oversized geometry, randomized payload mutations, host relay and identity stamping, explicit clear messages, send/relay backpressure, and world-sync suspension/resumption.

A normal one-shape presence packet is 96 bytes; an empty packet is 34 bytes; the eight-shape maximum is 530 bytes, excluding transport overhead. At 10 Hz this is 0.96 KB/s normally and 5.3 KB/s at the maximum per destination. Hover capture uses existing tool results, reads at most 128 existing local road definitions per batch, and keeps at most eight visible curves. Rendering uses the existing overlay pass, culls offscreen shapes, smooths placement movement, and expires hover after 1.5 seconds without presence updates.

The separate legacy `tools/CoreTests` harness currently fails to compile against unrelated removed APIs. This project does not depend on that harness.

Live game validation is still required:
- Two players hover the same building/road while keeping independent local selection.
- Move and rotate a building footprint; draw/cancel straight and curved roads; change road prefab/mode.
- Move a terrain or vegetation brush while paused.
- Move the cursor over UI, switch tools, delete the hovered object, disconnect, and resync the world.
- Toggle Show Partner Markers and check large-city frame timings with overlays on/off.

These are display-only geometric outlines, not native ghost meshes or validation/error colours. Large grids show a bounded subset of road curves; specialized tools without a supported outline fall back to the hovered object's outline or a cursor ring.
