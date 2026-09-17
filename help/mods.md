---
title: Mods and compatibility
description: "Which mods are officially supported, how unsupported mods affect multiplayer, and how the compatibility check works."
---

# Mods and compatibility

## Official mod support

The following mods are officially supported, verified and maintained by the CS2 Multiplayer
Mod developers. They do not show the other-mod warning and do not block hosting or joining,
even when **Ignore Mod Compatibility Checks** is off.

| Mod | Status |
| --- | --- |
| [Traffic](https://mods.paradoxplaza.com/mods/80095/) | Fully supported |
| [Road Speed Adjuster](https://mods.paradoxplaza.com/mods/125866/) | Fully supported |
| [Anarchy](https://mods.paradoxplaza.com/mods/74604/) | Fully supported |
| [Find It](https://mods.paradoxplaza.com/mods/77240/) | Fully supported |
| [Asset Icon Library](https://mods.paradoxplaza.com/mods/79634/) | Fully supported |

---

## Other mods are blocked

Hosting and joining are blocked while any unsupported mod is active, and a host rejects
players running a different CS2 Multiplayer Mod build. Nothing in the synchronization layer
accounts for an unverified third party changing prefabs, tools or the simulation, so one
unsupported mod on one machine is enough to desync the session or crash the other player.

The check reads your active Paradox Mods playset. That includes asset-only mods such as
maps, prop packs and prefab packs, which load no code at all. Mods in your other playsets
are not enabled for this run and are ignored.

| Banner | Meaning |
| --- | --- |
| Other Mods Enabled | Host and Join are blocked; the listed mods have to be disabled |
| Other Mods Enabled, still loaded | Already disabled in the playset, but still in memory - restart the game once |
| Compatibility Check Ignored | Other mods are active and the own-risk override is on |

To clear the block:

1. Disable every unsupported mod in your active playset. A playset that contains only CS2
   Multiplayer Mod and the officially supported mods above is allowed.
2. Go back to the game and wait a few seconds for the banner to clear.
3. If the banner says the mods are still loaded, restart the game.

---

## Turning the check off

Options ▸ CS2 Multiplayer Mod ▸ General ▸ Ignore Mod Compatibility Checks (Own Risk).
Change it while offline, before hosting or joining.

![](assets/img/ui-options-general.png)

With it on, other active mods no longer block hosting or joining on your machine, and a
host also admits players on a different CS2 Multiplayer Mod build as long as the network
protocol matches.

It does not bypass:

- the network protocol check, because different builds can encode network data differently,
- the Cities: Skylines II version check, or
- the DLC check.

It also does not make another mod multiplayer-aware. Back up the city, use the same playset
on every computer where possible, and expect desyncs, missing prefabs, broken cities or
crashes. The host decides whether different multiplayer-mod builds are admitted; each
player decides whether their own extra mods are allowed locally.

---

## Other mod compatibility

Mods not listed under official support are unverified and remain blocked by default. This
includes mods that may appear to be display-only or UI-only: without verification, the
multiplayer developers cannot guarantee that they will not change synchronized state.

---

[Back to troubleshooting.](troubleshooting.md)
