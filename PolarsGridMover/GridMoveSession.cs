using System;
using System.Collections.Generic;
using Sandbox.Game.Entities;
using Sandbox.Game.Gui;
using Sandbox.Game.World;
using Sandbox.Graphics.GUI;
using Sandbox.ModAPI;
using VRage.Game;
using VRage.Game.Components;
using VRage.Game.ModAPI;
using VRage.Input;
using VRage.ModAPI;
using VRage.Utils;
using VRageMath;

namespace PolarsGridMover;

// Ctrl + Numpad 8/2/4/6: move the grid forward / back / left / right by one block.
// Ctrl + Numpad 9/3: move it up / down.
// Ctrl + Numpad 5: lock / unlock the grid you are looking at; moves then apply to it.
// Ctrl + Numpad 1: carry the grid in the game's own paste preview; left click places it, Esc
//                  or Ctrl + Numpad 1 again puts it back. See CarryMode.
// Ctrl + Numpad 7: auto-merge on / off. A moved grid merges into an aligned grid it touches.
//
// Single player only, in creative mode or with creative tools on. Moving a static grid
// on a server is not replicated to clients, so the plugin does nothing in multiplayer.
[MySessionComponentDescriptor(MyUpdateOrder.NoUpdate)]
public class GridMoveSession : MySessionComponentBase
{
    private const double MaxRange = 200;
    private const double AxisMemorySeconds = 3;
    private const int NotifyMs = 2000;

    private enum Key
    {
        None,
        Forward,
        Back,
        Left,
        Right,
        Up,
        Down,
        Lock,
        Carry,
        ToggleMerge,
    }

    private static readonly MyStringId LineMaterial = MyStringId.GetOrCompute("Square");
    private static readonly Color LockColor = Color.Cyan;

    private readonly List<IMyCubeGrid> group = new List<IMyCubeGrid>();
    private readonly List<IHitInfo> hits = new List<IHitInfo>();
    private readonly List<MyCubeGrid> targets = new List<MyCubeGrid>();

    private volatile IMyCubeGrid locked;
    private volatile bool outlineFailed;
    private bool autoMerge = true;

    // Axis memory: the camera to grid direction mapping is kept while moves of the
    // same grid follow each other within AxisMemorySeconds.
    private long axisGridId;
    private DateTime axisUntilUtc;
    private Base6Directions.Direction axisForward, axisUp, axisLeft;

    public override void LoadData()
    {
        CarryMode.Notify = text => Notify(text, 3000);
        CarryMode.Placed = (grid, moved) =>
        {
            if (autoMerge)
                TryMergeTouching(grid, moved);
        };
    }

    protected override void UnloadData()
    {
        CarryMode.Cancel(false);
        CarryMode.Notify = null;
        CarryMode.Placed = null;
        Unlock(false);
        HudText.Clear();
        group.Clear();
        hits.Clear();
        targets.Clear();
        axisGridId = 0;
        outlineFailed = false;
    }

    // Called every frame on the update thread, also while menus are open. Only the Ctrl
    // check runs unless one of the plugin's keys was pressed in this frame, or a grid is
    // locked or carried.
    public override void HandleInput()
    {
        if (CarryMode.Active)
        {
            try
            {
                CarryMode.Update();
            }
            catch (Exception e)
            {
                Plugin.Log("Carry update failed: " + e);
                CarryMode.Cancel(true);
            }
        }
        if (locked != null && (locked.MarkedForClose || locked.Closed))
            Unlock(false);

        var input = MyInput.Static;
        if (input == null || !input.IsAnyCtrlKeyPressed())
            return;

        var key = PressedKey(input);
        if (key == Key.None)
            return;

        // Only from plain gameplay: no terminal, inventory, chat or menu in front
        if (!(MyScreenManager.GetScreenWithFocus() is MyGuiScreenGamePlay)
            || MyGuiScreenGamePlay.ActiveGameplayScreen != null)
            return;

        try
        {
            Handle(key);
        }
        catch (Exception e)
        {
            Plugin.Log("Key handling failed: " + e);
            Notify("Grid Mover failed, see the game log");
        }
    }

    // The lock outline, while a grid is locked. The game runs this on a parallel draw
    // task, so it only reads: all state changes happen in HandleInput.
    public override void Draw()
    {
        var grid = locked;
        // Not while carried: the grid is hidden and the preview shows where it goes
        if (grid == null || outlineFailed || CarryMode.Active || grid.MarkedForClose || grid.Closed)
            return;

        try
        {
            DrawBox(grid, LockColor);
        }
        catch (Exception e)
        {
            // Stop drawing so a failing draw does not log every frame
            outlineFailed = true;
            Plugin.Log("Drawing the lock outline failed, the outline is off: " + e);
        }
    }

    private void Handle(Key key)
    {
        var session = MySession.Static;
        var camera = MyAPIGateway.Session?.Camera;
        if (session == null || camera == null || MyAPIGateway.Physics == null)
            return;

        var multiplayer = MyAPIGateway.Multiplayer;
        if (multiplayer == null || multiplayer.MultiplayerActive)
        {
            Notify("Grid Mover works in single player only");
            return;
        }
        if (!session.CreativeMode && !session.CreativeToolsEnabled(multiplayer.MyId))
        {
            Notify("Grid Mover needs creative mode or creative tools");
            return;
        }

        if (key == Key.ToggleMerge)
        {
            autoMerge = !autoMerge;
            Notify(autoMerge ? "Auto-merge ON" : "Auto-merge OFF", 3000);
            return;
        }

        if (CarryMode.Active)
        {
            if (key == Key.Carry)
                CarryMode.CancelByKey();
            else
                Notify("Left click places the carried grid, Esc puts it back");
            return;
        }

        switch (key)
        {
            case Key.Lock:
                if (locked != null)
                {
                    Unlock(true);
                    return;
                }
                var aimed = AimedGrid(camera.WorldMatrix);
                if (aimed == null)
                {
                    Notify("No grid in sight");
                    return;
                }
                Lock(aimed);
                return;

            case Key.Carry:
                var toCarry = locked ?? AimedGrid(camera.WorldMatrix);
                if (toCarry == null)
                {
                    Notify("No grid in sight");
                    return;
                }
                var refusal = CarryMode.Start((MyCubeGrid)toCarry);
                Notify(refusal == null
                    ? $"Carrying {toCarry.DisplayName}: left click places it, Esc puts it back"
                    : "Can't carry: " + refusal, 4000);
                return;
        }

        var grid = locked ?? AimedGrid(camera.WorldMatrix);
        if (grid == null)
        {
            Notify("No grid in sight");
            return;
        }

        var delta = WorldStep(grid, key, camera.WorldMatrix);
        GridOps.GetMechanicalGroup(grid, group);

        // A locked grid heading for a grid it could merge with is put on that grid's
        // block lattice, so the two can meet face to face.
        string lined = null;
        if (locked != null && autoMerge)
        {
            var target = GridOps.FindMergeCandidate(grid, group, delta);
            if (target != null)
            {
                var correction = GridOps.LatticeCorrection(target, grid, delta);
                if (correction.LengthSquared() > 1e-6 && GridOps.FindBlocker(group, delta + correction) == null)
                {
                    delta += correction;
                    lined = target.DisplayName;
                }
            }
        }

        var blocker = GridOps.FindBlocker(group, delta);
        if (blocker != null)
        {
            group.Clear();
            Notify("Blocked by " + blocker.DisplayName + WhyNoMerge(blocker, grid));
            return;
        }

        GridOps.Translate(group, delta);
        var text = $"Moved {grid.DisplayName} 1 block";
        if (group.Count > 1)
            text += $" ({group.Count} grids)";
        if (lined != null)
            text += $", lined up with {lined}";
        Notify(text);

        if (locked != null && autoMerge)
            TryMergeTouching(locked, group);
        group.Clear();
    }

    // Why a grid in the way can't take the locked grid, when auto-merge is on
    private string WhyNoMerge(IMyCubeGrid blocker, IMyCubeGrid grid)
    {
        if (!autoMerge || locked == null)
            return "";
        if (blocker.GridSizeEnum != grid.GridSizeEnum)
            return " (other grid size, can't merge)";
        if (!GridOps.IsSquareTo(blocker, grid))
            return " (not square to it, can't merge)";
        return "";
    }

    // Camera relative direction snapped to the grid's axes. The mapping is worked out
    // once and reused for every move of the same grid within AxisMemorySeconds of the
    // last one, so turning the camera during a sequence of moves does not swap axes.
    private Vector3D WorldStep(IMyCubeGrid grid, Key key, MatrixD cam)
    {
        var now = DateTime.UtcNow;
        var gm = grid.WorldMatrix;

        if (grid.EntityId != axisGridId || now > axisUntilUtc)
        {
            axisForward = Closest(gm, cam.Forward);
            axisUp = Closest(gm, cam.Up, axisForward);
            axisLeft = Closest(gm, cam.Left, axisForward, axisUp);
            axisGridId = grid.EntityId;
        }
        axisUntilUtc = now.AddSeconds(AxisMemorySeconds);

        Base6Directions.Direction dir;
        switch (key)
        {
            case Key.Forward: dir = axisForward; break;
            case Key.Back: dir = Base6Directions.GetFlippedDirection(axisForward); break;
            case Key.Up: dir = axisUp; break;
            case Key.Down: dir = Base6Directions.GetFlippedDirection(axisUp); break;
            case Key.Left: dir = axisLeft; break;
            default: dir = Base6Directions.GetFlippedDirection(axisLeft); break;
        }
        return gm.GetDirectionVector(dir) * grid.GridSize;
    }

    // Grid direction closest to v, skipping the axes already taken
    internal static Base6Directions.Direction Closest(MatrixD gm, Vector3D v, params Base6Directions.Direction[] taken)
    {
        var best = Base6Directions.Direction.Forward;
        var bestDot = double.MinValue;
        foreach (var d in Base6Directions.EnumDirections)
        {
            var axis = Base6Directions.GetAxis(d);
            var skip = false;
            foreach (var t in taken)
                skip |= Base6Directions.GetAxis(t) == axis;
            if (skip)
                continue;

            var dot = Vector3D.Dot(gm.GetDirectionVector(d), v);
            if (dot > bestDot)
            {
                bestDot = dot;
                best = d;
            }
        }
        return best;
    }

    // The first thing the camera ray hits decides, skipping the player's own character.
    // A grid behind terrain or another object is not picked.
    private IMyCubeGrid AimedGrid(MatrixD cam)
    {
        hits.Clear();
        MyAPIGateway.Physics.CastRay(cam.Translation, cam.Translation + cam.Forward * MaxRange, hits);
        hits.Sort((a, b) => a.Fraction.CompareTo(b.Fraction));

        IMyCubeGrid grid = null;
        foreach (var hit in hits)
        {
            var top = hit.HitEntity?.GetTopMostParent();
            if (top == null || top is IMyCharacter)
                continue;
            grid = top as IMyCubeGrid;
            break;
        }
        hits.Clear();

        return grid != null && GridOps.IsMovable(grid) ? grid : null;
    }

    // moverGroup holds the moved grid and its subgrids, which are never merge targets
    private void TryMergeTouching(IMyCubeGrid moverGrid, List<IMyCubeGrid> moverGroup)
    {
        if (!GridOps.IsMovable(moverGrid))
            return;

        var mover = (MyCubeGrid)moverGrid;
        var box = moverGrid.WorldAABB.Inflate(moverGrid.GridSize);
        var nearby = MyAPIGateway.Entities.GetTopMostEntitiesInBox(ref box);
        var merged = 0;
        string lastName = null;

        // Only grids the moved grid itself touches, found before any merge. Tested after a
        // merge, the grown grid could pull in a neighbour that only touched the other grid.
        targets.Clear();
        foreach (var entity in nearby)
        {
            var target = entity as MyCubeGrid;
            if (target == null || target == mover || !GridOps.IsMovable(target) || moverGroup.Contains(target))
                continue;
            if (GridOps.TryGetMergeOffset(target, mover, out _) && GridOps.Touches(target, mover))
                targets.Add(target);
        }

        foreach (var target in targets)
        {
            if (!GridOps.IsMovable(target) || !GridOps.IsMovable(mover))
                continue;

            // A static grid absorbs a dynamic one, as with merge blocks, so a station never
            // turns into a ship. Otherwise the grid that was moved into place is absorbed.
            var absorber = target;
            var other = mover;
            if (!target.IsStatic && mover.IsStatic)
            {
                absorber = mover;
                other = target;
            }

            var name = target.DisplayName;
            var refusal = GridOps.Merge(absorber, other);
            if (refusal != null)
            {
                Notify($"Can't merge with {name}: {refusal}", 3000);
                continue;
            }

            lastName = name;
            merged++;
            mover = absorber;
        }
        targets.Clear();

        if (merged > 0)
        {
            Unlock(false);
            Notify($"Merged into {lastName}", 3000);
        }
    }

    private void Lock(IMyCubeGrid grid)
    {
        locked = grid;
        locked.OnClose += OnLockedClosed;
        Notify($"Locked {grid.DisplayName}, auto-merge {(autoMerge ? "ON" : "OFF")}", 3000);
    }

    private void Unlock(bool announce)
    {
        if (locked == null)
            return;
        locked.OnClose -= OnLockedClosed;
        if (announce)
            Notify("Unlocked " + locked.DisplayName);
        locked = null;
    }

    private void OnLockedClosed(IMyEntity entity)
    {
        entity.OnClose -= OnLockedClosed;
        if (locked == entity)
            locked = null;
    }

    private static void DrawBox(IMyCubeGrid grid, Color color)
    {
        var matrix = grid.WorldMatrix;
        var local = (BoundingBoxD)grid.LocalAABB;
        MySimpleObjectDraw.DrawTransparentBox(ref matrix, ref local, ref color, MySimpleObjectRasterizer.Wireframe, 1,
            grid.GridSize * 0.04f, null, LineMaterial);
    }

    // Numpad keys only: vanilla binds none of them, while the arrow keys turn the camera
    // and PageUp, PageDown, Insert, Delete, Home and End rotate the block in hand.
    // NumLock has to be on, otherwise the numpad sends those keys instead.
    // Numpad 0 is left free: PCU Display uses Ctrl + Numpad 0 by default.
    private static Key PressedKey(VRage.Input.IMyInput input)
    {
        if (input.IsAnyAltKeyPressed() || input.IsAnyShiftKeyPressed())
            return Key.None;
        if (input.IsNewKeyPressed(MyKeys.NumPad8)) return Key.Forward;
        if (input.IsNewKeyPressed(MyKeys.NumPad2)) return Key.Back;
        if (input.IsNewKeyPressed(MyKeys.NumPad4)) return Key.Left;
        if (input.IsNewKeyPressed(MyKeys.NumPad6)) return Key.Right;
        if (input.IsNewKeyPressed(MyKeys.NumPad9)) return Key.Up;
        if (input.IsNewKeyPressed(MyKeys.NumPad3)) return Key.Down;
        if (input.IsNewKeyPressed(MyKeys.NumPad5)) return Key.Lock;
        if (input.IsNewKeyPressed(MyKeys.NumPad1)) return Key.Carry;
        if (input.IsNewKeyPressed(MyKeys.NumPad7)) return Key.ToggleMerge;
        return Key.None;
    }

    // One message at a time; a new one replaces the last, so fast key presses don't stack
    private static void Notify(string text, int ms = NotifyMs)
    {
        HudText.Show(text, ms);
    }
}
