using System;
using System.Collections.Generic;
using Sandbox.Game.Entities;
using Sandbox.ModAPI;
using VRage.Game.ModAPI;
using VRageMath;

namespace PolarsGridMover;

internal static class GridOps
{
    private static readonly Vector3I[] Neighbours =
    {
        Vector3I.Up, Vector3I.Down, Vector3I.Left, Vector3I.Right, Vector3I.Forward, Vector3I.Backward
    };

    // Points tested inside each block cell for collisions: the centre and eight points
    // 80% of the way to the corners, in cell units. Two grids that only touch face to
    // face never collide, an overlap of more than a tenth of a block always does.
    private const double SampleReach = 0.4;

    private static readonly Vector3D[] SampleUnits =
    {
        Vector3D.Zero,
        new Vector3D(-SampleReach, -SampleReach, -SampleReach), new Vector3D(SampleReach, -SampleReach, -SampleReach),
        new Vector3D(-SampleReach, SampleReach, -SampleReach), new Vector3D(SampleReach, SampleReach, -SampleReach),
        new Vector3D(-SampleReach, -SampleReach, SampleReach), new Vector3D(SampleReach, -SampleReach, SampleReach),
        new Vector3D(-SampleReach, SampleReach, SampleReach), new Vector3D(SampleReach, SampleReach, SampleReach),
    };

    private static readonly Vector3D[] Samples = new Vector3D[SampleUnits.Length];
    private static readonly List<IMySlimBlock> Blocks = new List<IMySlimBlock>();
    private static readonly List<IMyCubeGrid> Others = new List<IMyCubeGrid>();

    // A real grid in the world: not a paste preview or projection, not being removed
    internal static bool IsMovable(IMyCubeGrid grid)
    {
        return grid != null && !grid.MarkedForClose && !grid.Closed && grid.Physics != null
               && !((MyCubeGrid)grid).IsPreview;
    }

    // The grid and everything joined to it by rotors, pistons and hinges
    internal static void GetMechanicalGroup(IMyCubeGrid grid, List<IMyCubeGrid> group)
    {
        group.Clear();
        MyAPIGateway.GridGroups.GetGroup(grid, GridLinkTypeEnum.Mechanical, group);
        if (group.Count == 0)
            group.Add(grid);
    }

    internal static void Translate(List<IMyCubeGrid> group, Vector3D delta)
    {
        foreach (var grid in group)
        {
            var matrix = grid.WorldMatrix;
            matrix.Translation += delta;
            grid.SetWorldMatrix(matrix);
            if (grid.Physics != null && !grid.IsStatic)
            {
                grid.Physics.LinearVelocity = Vector3.Zero;
                grid.Physics.AngularVelocity = Vector3.Zero;
            }
        }
    }

    // The first grid outside the group that would overlap a block of the group after
    // moving by delta, or null. Dropping a ship into another grid makes the physics
    // engine push them apart violently. Both directions are tested, so a small block
    // sitting between the test points of a large one is still found.
    internal static IMyCubeGrid FindBlocker(List<IMyCubeGrid> group, Vector3D delta)
    {
        try
        {
            foreach (var grid in group)
            {
                if (!IsMovable(grid))
                    continue;

                var aabb = grid.WorldAABB;
                var moved = new BoundingBoxD(aabb.Min + delta, aabb.Max + delta);
                var nearby = MyAPIGateway.Entities.GetTopMostEntitiesInBox(ref moved);

                Others.Clear();
                foreach (var entity in nearby)
                {
                    if (entity is IMyCubeGrid other && !group.Contains(other) && IsMovable(other))
                        Others.Add(other);
                }
                if (Others.Count == 0)
                    continue;

                foreach (var other in Others)
                {
                    if (CellsHit(grid, delta, other) || OtherCellsHit(other, grid, delta, moved))
                        return other;
                }
            }
            return null;
        }
        finally
        {
            Blocks.Clear();
            Others.Clear();
        }
    }

    // A test point of a block of grid, moved by delta, lies in a cell of other
    private static bool CellsHit(IMyCubeGrid grid, Vector3D delta, IMyCubeGrid other)
    {
        PrepareSamples(grid);
        var otherBox = other.WorldAABB.Inflate(grid.GridSize);

        Blocks.Clear();
        grid.GetBlocks(Blocks);
        foreach (var block in Blocks)
        {
            for (var x = block.Min.X; x <= block.Max.X; x++)
            for (var y = block.Min.Y; y <= block.Max.Y; y++)
            for (var z = block.Min.Z; z <= block.Max.Z; z++)
            {
                var centre = grid.GridIntegerToWorld(new Vector3I(x, y, z)) + delta;
                if (otherBox.Contains(centre) == ContainmentType.Disjoint)
                    continue;
                foreach (var sample in Samples)
                {
                    if (other.CubeExists(other.WorldToGridInteger(centre + sample)))
                        return true;
                }
            }
        }
        return false;
    }

    // A test point of a block of other lies in a cell of grid moved by delta.
    // Only other's cells inside the moved box are visited, found from whichever is
    // smaller: the cell range the box covers, or other's block list.
    private static bool OtherCellsHit(IMyCubeGrid other, IMyCubeGrid grid, Vector3D delta, BoundingBoxD moved)
    {
        PrepareSamples(other);
        var box = moved.Inflate(other.GridSize);

        var local = box.TransformFast(other.WorldMatrixNormalizedInv);
        var min = Vector3I.Floor(local.Min / other.GridSize);
        var max = Vector3I.Ceiling(local.Max / other.GridSize);
        var size = max - min + Vector3I.One;
        var volume = (long)size.X * size.Y * size.Z;

        if (volume <= ((MyCubeGrid)other).BlocksCount)
        {
            for (var x = min.X; x <= max.X; x++)
            for (var y = min.Y; y <= max.Y; y++)
            for (var z = min.Z; z <= max.Z; z++)
            {
                var cell = new Vector3I(x, y, z);
                if (other.CubeExists(cell) && SamplesHit(other.GridIntegerToWorld(cell) - delta, grid))
                    return true;
            }
            return false;
        }

        Blocks.Clear();
        other.GetBlocks(Blocks);
        foreach (var block in Blocks)
        {
            for (var x = block.Min.X; x <= block.Max.X; x++)
            for (var y = block.Min.Y; y <= block.Max.Y; y++)
            for (var z = block.Min.Z; z <= block.Max.Z; z++)
            {
                var centre = other.GridIntegerToWorld(new Vector3I(x, y, z));
                if (box.Contains(centre) != ContainmentType.Disjoint && SamplesHit(centre - delta, grid))
                    return true;
            }
        }
        return false;
    }

    private static bool SamplesHit(Vector3D centre, IMyCubeGrid grid)
    {
        foreach (var sample in Samples)
        {
            if (grid.CubeExists(grid.WorldToGridInteger(centre + sample)))
                return true;
        }
        return false;
    }

    private static void PrepareSamples(IMyCubeGrid grid)
    {
        var matrix = grid.WorldMatrix;
        for (var i = 0; i < SampleUnits.Length; i++)
            Samples[i] = Vector3D.TransformNormal(SampleUnits[i] * grid.GridSize, matrix);
    }

    // Same grid size and rotated in 90 degree steps relative to each other
    internal static bool IsSquareTo(IMyCubeGrid target, IMyCubeGrid other)
    {
        if (target.GridSizeEnum != other.GridSizeEnum)
            return false;
        var inv = target.WorldMatrixNormalizedInv;
        return IsAxis(Vector3D.TransformNormal(other.WorldMatrix.Forward, inv))
               && IsAxis(Vector3D.TransformNormal(other.WorldMatrix.Up, inv));
    }

    // The grid a moved grid could merge with: square to it and within one block of where
    // it is going. The nearest one wins.
    internal static IMyCubeGrid FindMergeCandidate(IMyCubeGrid grid, List<IMyCubeGrid> group, Vector3D delta)
    {
        var aabb = grid.WorldAABB;
        var moved = new BoundingBoxD(aabb.Min + delta, aabb.Max + delta).Inflate(grid.GridSize);
        var nearby = MyAPIGateway.Entities.GetTopMostEntitiesInBox(ref moved);
        var centre = moved.Center;

        IMyCubeGrid best = null;
        var bestDistance = double.MaxValue;
        foreach (var entity in nearby)
        {
            if (!(entity is IMyCubeGrid other) || group.Contains(other) || !IsMovable(other) || !IsSquareTo(other, grid))
                continue;
            var distance = other.WorldAABB.DistanceSquared(centre);
            if (distance < bestDistance)
            {
                bestDistance = distance;
                best = other;
            }
        }
        return best;
    }

    // Offset of at most half a block per axis that puts the grid, moved by delta,
    // on the target's block lattice. Two grids built apart are rarely on the same
    // lattice, and without this they could never touch face to face to merge.
    internal static Vector3D LatticeCorrection(IMyCubeGrid target, IMyCubeGrid grid, Vector3D delta)
    {
        var local = Vector3D.Transform(grid.WorldMatrix.Translation + delta, target.WorldMatrixNormalizedInv) / target.GridSize;
        var rounded = new Vector3D(Math.Round(local.X), Math.Round(local.Y), Math.Round(local.Z));
        return Vector3D.TransformNormal((rounded - local) * target.GridSize, target.WorldMatrix);
    }

    // The other grid must share the target's grid size, be rotated in 90 degree steps
    // relative to it, and sit on the target's block lattice.
    internal static bool TryGetMergeOffset(IMyCubeGrid target, IMyCubeGrid other, out Vector3I offset)
    {
        offset = Vector3I.Zero;
        if (!IsSquareTo(target, other))
            return false;

        var local = Vector3D.Transform(other.WorldMatrix.Translation, target.WorldMatrixNormalizedInv) / target.GridSize;
        var rounded = new Vector3I((int)Math.Round(local.X), (int)Math.Round(local.Y), (int)Math.Round(local.Z));
        if ((local - (Vector3D)rounded).LengthSquared() > 0.01)
            return false;

        offset = rounded;
        return true;
    }

    private static bool IsAxis(Vector3D v)
    {
        return Math.Max(Math.Abs(v.X), Math.Max(Math.Abs(v.Y), Math.Abs(v.Z))) > 0.999;
    }

    // True when a cell of the other grid is face adjacent to a target cell and none overlap
    internal static bool Touches(IMyCubeGrid target, IMyCubeGrid other)
    {
        try
        {
            Blocks.Clear();
            other.GetBlocks(Blocks);
            var touching = false;

            foreach (var block in Blocks)
            {
                for (var x = block.Min.X; x <= block.Max.X; x++)
                for (var y = block.Min.Y; y <= block.Max.Y; y++)
                for (var z = block.Min.Z; z <= block.Max.Z; z++)
                {
                    var cell = target.WorldToGridInteger(other.GridIntegerToWorld(new Vector3I(x, y, z)));
                    if (target.CubeExists(cell))
                        return false;
                    if (touching)
                        continue;
                    foreach (var n in Neighbours)
                    {
                        if (target.CubeExists(cell + n))
                        {
                            touching = true;
                            break;
                        }
                    }
                }
            }
            return touching;
        }
        finally
        {
            Blocks.Clear();
        }
    }

    // Merges other into absorber through the server entry point merge blocks use.
    // Block entities keep their ids. Returns null when merged, otherwise the reason.
    internal static string Merge(MyCubeGrid absorber, MyCubeGrid other)
    {
        if (!TryGetMergeOffset(absorber, other, out var offset))
            return "not aligned to its blocks";
        if (!absorber.CanMergeCubes(other, offset))
            return "blocks overlap";
        // The connectivity test vanilla uses when a pasted grid joins another:
        // at least one pair of touching block faces whose mount points meet
        if (!MyCubeGrid.CheckMergeConnectivity(absorber, other, offset))
            return "no block faces connect";
        if (absorber.MergeGrid_MergeBlock(other, offset, false) == null)
            return "the game refused the merge";
        return null;
    }
}
