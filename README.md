# Polar's Grid Mover

Space Engineers client plugin for [Pulsar](https://github.com/SpaceGT/Pulsar): move a whole grid without copying it. Nudge it one block at a time, or carry it in the game's own paste preview, then merge it into the grid it touches.

It is the same grid the whole time: same entity ids, block settings, inventories and subgrids. Nothing is copied or pasted.

## Controls

| Keys | Action |
|---|---|
| Ctrl + Numpad 8 / 2 | Forward / back |
| Ctrl + Numpad 4 / 6 | Left / right |
| Ctrl + Numpad 9 / 3 | Up / down |
| Ctrl + Numpad 5 | Lock onto the grid you are looking at, or unlock |
| Ctrl + Numpad 1 | Carry the grid, or put the carried grid back |
| Ctrl + Numpad 7 | Auto-merge on / off (on by default) |

NumLock must be on. With NumLock off the numpad sends arrow, PageUp, PageDown, Insert, Delete, Home and End, which the game uses for looking around and rotating the block in hand. The keys do nothing while Alt or Shift is held, or while a terminal, inventory, chat or menu is open.

## Behaviour

- **Target.** The grid under the crosshair, up to 200 m, or the locked grid. The first thing the view ray hits decides, so a grid behind terrain is not picked. A locked grid shows a cyan outline and moves wherever you look.
- **Nudging.** Directions are worked out from the camera and snapped to the grid's own axes. The mapping stays fixed while you keep moving the same grid, until 3 seconds pass without a move, so turning around mid-sequence does not swap axes.
- **Carrying.** Uses the game's own paste preview, so everything works as with Ctrl+C and Ctrl+V: the real blocks follow your view, the block rotation keys rotate it, Ctrl + mouse wheel changes the distance, it snaps to other grids, it turns red where it can't go, left click places it and Esc cancels. Ctrl + Numpad 1 again also cancels. While carried, the original grid, armor included, is hidden and taken out of the physics world, as if Cut had removed it. Placing moves the original grid to the preview's spot and rotation instead of pasting a copy. Cancelling leaves it where it was. The clipboard is cleared afterwards, so Ctrl+V can't paste a duplicate, and anything you had copied before is replaced, as with Ctrl+C. Carrying takes the same grids as Ctrl+C: everything joined by rotors, pistons, hinges and connectors. It works on foot or in spectator.
- **Subgrids.** Nudging moves everything joined by rotors, pistons and hinges with the grid.
- **Collisions.** A nudge that would put a block inside another grid is refused with "Blocked by ...". Each block is tested at its centre and near its eight corners, in both directions, so any overlap of more than a tenth of a block is caught. Terrain is not checked. Carrying uses the game's own placement check instead.
- **Auto-merge.** A locked grid being nudged is lined up with the block lattice of a nearby grid of the same size that is square to it. Two grids built apart rarely share a lattice, and without this they could never meet face to face. When it touches that grid it merges into it, but only if at least one pair of touching block faces actually connects, using the same mount point test vanilla uses when a pasted grid joins another. A placed grid merges into the grid it was snapped to in the same way. A station absorbs a ship, as with merge blocks, so a station never turns into a ship. Blocks keep their entity ids. A locked grid unlocks after a merge. When a nudge is blocked and a merge isn't possible, "Blocked by ..." says why: other grid size, or not square to it.

## Requirements

- Single player, in creative mode or with creative tools enabled.
- Multiplayer is not supported, as a host or as a client. Moving a grid needs server authority, and a static grid's new position is not sent to other players, so the keys only show "Grid Mover works in single player only".

## Compatibility

- Two Harmony patches, both removed when the game closes:
  - A prefix on `MyGridClipboard.PasteGrid(bool, bool)`. It only acts while this plugin is carrying a grid; every other paste, blueprint and projection runs unchanged. If it can't be applied, carrying is turned off and nudging still works.
  - A postfix on `MyHudNotifications.Draw()` that draws this plugin's messages just above the game's notifications, in the same style. Vanilla notifications share a small pool of text buffers with every mod and plugin, and a notification that finds the pool empty is never drawn. If the patch can't be applied, messages use a vanilla notification instead.
- Messages hide with the HUD, like vanilla notifications.
- No settings file, no file or network access.
- Vanilla binds none of the numpad keys. Ctrl + Numpad 0 is left free because [PCU Display](https://github.com/StarCpt/PluginHub/blob/main/Plugins/PCUDisplay.xml) uses it by default.
- Moves and merges are not recorded by Viktor Ferenczi's [Undo](https://github.com/viktor-ferenczi/se-undo) plugin, so Ctrl+Z does not take them back.

## How it works

- A session component handles input and draws the lock outline. Messages are drawn by the HUD patch while one is showing. In a frame without Ctrl held it does one key check and nothing else. While a grid is locked it draws the outline each frame. While a grid is carried it checks each frame that the preview is still up and the grids still exist.
- Nudges set each grid's world matrix and clear the velocity of dynamic grids. Placing sets each original grid to its preview grid's world matrix.
- Merges use the game's own `MyCubeGrid.MergeGrid_MergeBlock`, the same call merge blocks use.

## Building

Requires the [.NET Framework 4.8.1 Developer Pack](https://dotnet.microsoft.com/en-us/download/dotnet-framework/net481) and the [.NET 10 SDK](https://dotnet.microsoft.com/en-us/download/dotnet/10.0). The Space Engineers `Bin64` folder is found through Steam, or set it in `Directory.Build.props.user`.

```
dotnet build -c Release
```

Set `Pulsar` in `Directory.Build.props.user` to deploy each build into Pulsar's `Local` plugin folder.

## Support

Report problems on the [issue tracker](https://github.com/Po11arr/PolarsGridMover/issues).

## Licence

MIT, see [LICENSE](LICENSE).
