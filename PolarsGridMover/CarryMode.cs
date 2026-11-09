using System;
using System.Collections;
using System.Collections.Generic;
using System.Reflection;
using HarmonyLib;
using Sandbox.Game.Components;
using Sandbox.Game.Entities;
using Sandbox.Game.Entities.Character;
using Sandbox.Game.Entities.Cube;
using Sandbox.Game.GUI;
using Sandbox.Game.SessionComponents;
using Sandbox.Game.SessionComponents.Clipboard;
using Sandbox.Game.World;
using VRage.Audio;
using VRage.Game;
using VRage.Game.ModAPI;
using VRage.Utils;
using VRageMath;
using VRageRender;

namespace PolarsGridMover;

// Carry mode runs on the game's own paste preview: the grid's real blocks, the block
// rotation keys, Ctrl + mouse wheel for distance, snapping, red where it can't go, left
// click to place and Esc to cancel, all exactly as with copy and paste.
//
// The difference is what happens at the end. The original grids are hidden and taken
// out of the physics world while they are carried, at the moment Cut would delete them,
// so the preview never collides with them. A Harmony prefix on MyGridClipboard.PasteGrid
// then moves the originals to where the preview grids are, instead of pasting copies.
// Cancelling puts them back as they were. The clipboard is cleared afterwards, so
// Ctrl+V can't paste a duplicate.
internal static class CarryMode
{
    private const int ActivationTimeoutFrames = 300;

    private static MethodInfo canBePlaced;
    private static MethodInfo componentActivate;
    private static MethodInfo updatePasteNotification;
    private static FieldInfo cellObjects;
    private static FieldInfo cellsLock;

    private static MyGridClipboard clipboard;
    private static readonly List<MyCubeGrid> Originals = new List<MyCubeGrid>();
    private static string carriedName;
    private static bool placing;
    private static bool wasActive;
    private static bool cancelPending;
    private static int idleFrames;

    internal static bool Available { get; private set; }
    internal static bool Active => clipboard != null;

    // Set by the session component: messages for the player, and the merge after a placement
    internal static Action<string> Notify;
    internal static Action<IMyCubeGrid, List<IMyCubeGrid>> Placed;

    internal static void Init(Harmony harmony)
    {
        try
        {
            var paste = AccessTools.Method(typeof(MyGridClipboard), nameof(MyGridClipboard.PasteGrid), new[] { typeof(bool), typeof(bool) });
            canBePlaced = AccessTools.PropertyGetter(typeof(MyGridClipboard), "CanBePlaced");
            componentActivate = AccessTools.Method(typeof(MyClipboardComponent), "Activate", Type.EmptyTypes);
            updatePasteNotification = AccessTools.Method(typeof(MyClipboardComponent), "UpdatePasteNotification", new[] { typeof(MyStringId) });
            cellObjects = AccessTools.Field(typeof(MyCubeGridRenderCell), "m_instanceGroupRenderObjects");
            cellsLock = AccessTools.Field(typeof(MyCubeGridRenderData), "m_cellsUpdateLock");
            if (cellObjects == null)
                Plugin.Log("Armor can't be hidden while carrying: this game version's grid render is different");

            if (paste == null || canBePlaced == null || componentActivate == null)
            {
                Plugin.Log("Carry mode is off: this game version's clipboard is different");
                return;
            }

            harmony.Patch(paste, prefix: new HarmonyMethod(typeof(CarryMode), nameof(PastePrefix)));
            Available = true;
        }
        catch (Exception e)
        {
            Plugin.Log("Carry mode is off, patching failed: " + e);
            Available = false;
        }
    }

    internal static void Dispose()
    {
        Cancel(false);
        Available = false;
    }

    // Null when the preview is up, otherwise the reason it can't start
    internal static string Start(MyCubeGrid grid)
    {
        if (!Available)
            return "carrying is off, see the game log";

        try
        {
            return StartInternal(grid);
        }
        catch (Exception e)
        {
            Plugin.Log("Starting to carry failed: " + e);
            Cancel(false);
            return "it failed, see the game log";
        }
    }

    private static string StartInternal(MyCubeGrid grid)
    {
        var session = MySession.Static;
        var component = MyClipboardComponent.Static;
        var clip = component?.Clipboard;
        if (session == null || clip == null)
            return "the clipboard isn't ready";
        if (!session.CreativeMode && !session.IsCopyPastingEnabled)
            return "carrying needs copy and paste enabled for you";
        if (clip.IsActive || clip.IsBeingAdded)
            return "the clipboard is in use, press Esc first";
        if (!(session.CameraController is MyCharacter) && session.GetCameraControllerEnum() != MyCameraControllerEnum.Spectator)
            return "pick it up on foot or in spectator";

        if (MySessionComponentVoxelHand.Static != null)
            MySessionComponentVoxelHand.Static.Enabled = false;
        component.DeactivateCopyPasteFloatingObject(clear: true);

        // Same group as vanilla Ctrl+C: everything joined by rotors, pistons, hinges and connectors
        clip.CopyGroup(grid, GridLinkTypeEnum.Logical);

        // Read the ids now: activating the preview renumbers the copied builders
        Originals.Clear();
        foreach (var builder in clip.CopiedGrids)
        {
            if (!(MyEntities.GetEntityById(builder.EntityId) is MyCubeGrid original) || original.MarkedForClose)
            {
                Originals.Clear();
                clip.ClearClipboard();
                return "couldn't find all of its grids";
            }
            Originals.Add(original);
        }
        if (Originals.Count == 0)
        {
            clip.ClearClipboard();
            return "nothing to carry";
        }

        clipboard = clip;
        carriedName = grid.DisplayName;
        placing = false;
        wasActive = false;
        cancelPending = false;
        idleFrames = 0;
        clip.Deactivated += OnDeactivated;
        SetCarried(true);

        clip.Activate();
        componentActivate.Invoke(component, null);
        updatePasteNotification?.Invoke(component, new object[] { MyCommonTexts.CubeBuilderPasteNotification });
        MyGuiAudio.PlaySound(MyGuiSounds.HudClick);
        return null;
    }

    // Same as pressing Esc while pasting. While the preview is still being built it can't
    // be cancelled yet: the game adds it when building ends, cancelled or not, so the
    // cancel waits for that and then runs.
    internal static void CancelByKey()
    {
        if (!Active)
            return;
        if (clipboard.IsBeingAdded)
        {
            cancelPending = true;
            return;
        }
        var component = MyClipboardComponent.Static;
        if (component == null || !component.HandleEscapeInternal())
            Cancel(true);
    }

    // Each frame on the update thread while carrying: runs a cancel that waited for the
    // preview, and puts the grids back if the preview never appears or the grids were
    // removed in the meantime.
    internal static void Update()
    {
        if (!Active || placing || clipboard.IsBeingAdded)
            return;

        if (cancelPending)
        {
            CancelByKey();
            return;
        }

        foreach (var original in Originals)
        {
            if (original.MarkedForClose || original.Closed)
            {
                CancelByKey();
                return;
            }
        }

        if (clipboard.IsActive)
        {
            wasActive = true;
            idleFrames = 0;
            return;
        }
        if (wasActive || ++idleFrames > ActivationTimeoutFrames)
            Cancel(true);
    }

    // Puts the carried grids back where they were, visible and solid
    internal static void Cancel(bool announce)
    {
        if (!Active)
            return;

        var clip = clipboard;
        clipboard = null;
        clip.Deactivated -= OnDeactivated;

        try
        {
            SetCarried(false);
        }
        catch (Exception e)
        {
            Plugin.Log("Restoring the carried grids failed: " + e);
        }

        try
        {
            clip.ClearClipboard();
        }
        catch (Exception e)
        {
            Plugin.Log("Clearing the clipboard failed: " + e);
        }

        Originals.Clear();
        placing = false;
        cancelPending = false;
        if (announce)
            Notify?.Invoke($"Put {carriedName} back");
    }

    private static void OnDeactivated(MyGridClipboard clip, bool afterPaste)
    {
        if (!placing)
            Cancel(true);
    }

    // Harmony prefix on MyGridClipboard.PasteGrid(bool deactivate, bool showWarning)
    private static bool PastePrefix(MyGridClipboard __instance, ref bool __result)
    {
        if (!Active || placing || __instance != clipboard)
            return true;

        var movedAny = false;
        try
        {
            // Still activating, or the preview is red: vanilla refuses with its own sound
            if (!__instance.IsActive || !(bool)canBePlaced.Invoke(__instance, null))
                return true;

            var previews = __instance.PreviewGrids;
            if (previews.Count != Originals.Count)
            {
                Cancel(false);
                Notify?.Invoke("Couldn't match the preview to the grid, put it back");
                __result = false;
                return false;
            }

            placing = true;
            var matrices = new MatrixD[previews.Count];
            for (var i = 0; i < previews.Count; i++)
            {
                var m = previews[i].WorldMatrix;
                matrices[i] = MatrixD.CreateWorld(m.Translation, Vector3D.Normalize(m.Forward), Vector3D.Normalize(m.Up));
            }

            __instance.Deactivated -= OnDeactivated;
            __instance.Deactivate(afterPaste: true);

            // Shown and solid again first, then moved with the same call nudging uses, so
            // doors, turrets, armor and other subparts follow their blocks
            SetCarried(false);
            var moved = new List<IMyCubeGrid>(Originals.Count);
            for (var i = 0; i < Originals.Count; i++)
            {
                IMyCubeGrid original = Originals[i];
                original.SetWorldMatrix(matrices[i]);
                movedAny = true;
                if (original.Physics != null && !original.IsStatic)
                {
                    original.Physics.LinearVelocity = Vector3.Zero;
                    original.Physics.AngularVelocity = Vector3.Zero;
                }
                moved.Add(original);
            }
            __instance.ClearClipboard();

            var main = Originals[0];
            var name = carriedName;
            Originals.Clear();
            clipboard = null;
            placing = false;

            MyGuiAudio.PlaySound(MyGuiSounds.HudPlaceBlock);
            Notify?.Invoke($"Placed {name}");
            try
            {
                Placed?.Invoke(main, moved);
            }
            catch (Exception e)
            {
                // The grid is placed; only the merge after it failed
                Plugin.Log("Merging the placed grid failed: " + e);
                Notify?.Invoke($"Placed {name}, merging failed. See the game log");
            }

            __result = true;
            return false;
        }
        catch (Exception e)
        {
            // Never fall through to vanilla here: that would paste a copy
            Plugin.Log("Placing the carried grid failed, carrying is off: " + e);
            placing = false;
            Cancel(false);
            Available = false;
            Notify?.Invoke(movedAny
                ? "Placing failed part way, see the game log"
                : "Placing failed, the grid is back where it was. See the game log");
            __result = false;
            return false;
        }
    }

    // Out of sight and out of the physics world while carried, as if Cut had removed them.
    //
    // Blocks with their own entity (thrusters, doors, turrets, lights and so on) are taken
    // out of the render world with UpdateRenderObject. Hiding them is not enough: thruster
    // flames and glares are drawn even for a hidden thruster. Put back later, they are
    // rebuilt at the block's current position, so they follow a placed grid.
    //
    // The grid itself is only hidden, together with its armor. Armor is drawn by the grid's
    // render cells, which are not among the grid's render objects, so their objects are
    // hidden one by one. Removing the grid's render objects instead would leave the armor
    // behind: the cells are reused when the grid comes back, still tied to the old position.
    private static void SetCarried(bool carried)
    {
        var show = !carried;
        foreach (var grid in Originals)
        {
            if (grid.MarkedForClose || grid.Closed)
                continue;

            // Children need a visible parent to come back, and go first when hiding
            if (show)
            {
                grid.Render.Visible = true;
                SetArmorVisible(grid, true);
                SetBlocksInRender(grid, true);
            }
            else
            {
                SetBlocksInRender(grid, false);
                grid.Render.Visible = false;
                SetArmorVisible(grid, false);
            }

            if (grid.Physics == null)
                continue;
            grid.Physics.Enabled = show;
            if (show && !grid.IsStatic)
            {
                grid.Physics.LinearVelocity = Vector3.Zero;
                grid.Physics.AngularVelocity = Vector3.Zero;
            }
        }
    }

    // Each block entity of the grid, with its subparts
    private static void SetBlocksInRender(MyCubeGrid grid, bool inRender)
    {
        var hierarchy = grid.Hierarchy;
        if (hierarchy == null)
            return;
        foreach (var child in hierarchy.Children)
        {
            var entity = child?.Container?.Entity;
            if (entity == null || entity.MarkedForClose || entity.Closed)
                continue;
            entity.Render?.UpdateRenderObject(inRender);
        }
    }

    private static void SetArmorVisible(MyCubeGrid grid, bool visible)
    {
        if (cellObjects == null || !(grid.Render is MyRenderComponentCubeGrid render))
            return;

        var data = render.RenderData;
        var gate = cellsLock?.GetValue(data) ?? data;
        lock (gate)
        {
            foreach (var pair in data.Cells)
            {
                if (!(cellObjects.GetValue(pair.Value) is IDictionary objects))
                    continue;
                foreach (var id in objects.Values)
                    MyRenderProxy.UpdateRenderObjectVisibility((uint)id, visible, false);
            }
        }
    }
}
