/*
Copyright 2025 Aron Gere
Licensed under the Apache License, Version 2.0
*/

using System.Numerics;

namespace Altruist.Gaming.ThreeD;

/// <summary>Auto-generates a polygon nav-mesh from an
/// <see cref="IWalkabilityGrid"/> + <see cref="ITerrainProvider"/>. The grid
/// provides walkable/blocked per cell; the terrain provider gives Y per
/// (X,Z). Walkable cells are <em>greedy-meshed</em> into rectangles to keep
/// the polygon count low (a 512×512 cell map typically compresses to ~1–10k
/// polys instead of ~262k tris), then adjacency is computed by scanning each
/// rectangle's boundary cells.
///
/// This is intentionally NOT a Recast clone — it can't represent overhangs
/// or bridges that have walkable surfaces at the same XZ but different Y.
/// For heightfield maps (Metin2-style) that's fine.</summary>
/// <remarks>
/// Build once per zone (cost is O(cells)) and register the result with <see cref="INavMeshService.RegisterMesh"/>.
/// Cell (x, y) of the grid maps to world X/Z; only <see cref="WalkabilityGrid"/> contributes a world origin
/// (<c>BaseX</c>/<c>BaseY</c>), other <see cref="IWalkabilityGrid"/> implementations are placed at origin 0.
/// Output is deterministic for the same inputs.
/// </remarks>
public static class NavMeshBuilder
{
    /// <summary>Tuning knobs for <see cref="Build"/>.</summary>
    public sealed class Options
    {
        /// <summary>Mask of <see cref="CellAttribute"/> bits that count as
        /// non-walkable. Default = Blocked + Water.</summary>
        public byte BlockedMask { get; set; } = (byte)(CellAttribute.Blocked | CellAttribute.Water);

        /// <summary>Maximum height delta (world units) between adjacent
        /// cells inside a single rectangle. Cells with too steep a slope
        /// can't merge — keeps rectangles roughly planar so the funnel
        /// algorithm gets sensible results.</summary>
        public float MaxIntraRectHeightDelta { get; set; } = 1.5f;
    }

    /// <summary>Build a nav-mesh from the supplied grid + terrain. Returns
    /// null if the grid is empty / has no walkable cells.</summary>
    /// <param name="grid">Per-cell attributes; cells matching <see cref="Options.BlockedMask"/> are excluded.</param>
    /// <param name="terrain">Height source; Y is sampled at cell centers and polygon corners.</param>
    /// <param name="options">Optional tuning; defaults when <c>null</c>.</param>
    /// <exception cref="ArgumentNullException"><paramref name="grid"/> or <paramref name="terrain"/> is <c>null</c>.</exception>
    public static NavMeshGraph? Build(
        IWalkabilityGrid grid,
        ITerrainProvider terrain,
        Options? options = null)
    {
        if (grid == null) throw new ArgumentNullException(nameof(grid));
        if (terrain == null) throw new ArgumentNullException(nameof(terrain));

        options ??= new Options();
        int w = grid.Width;
        int h = grid.Height;
        int scale = grid.CellScale;

        // 1) Mark walkability + sample heights per cell corner. Heights are
        //    sampled at the SHARED corner between (cx,cy) and (cx+1,cy+1)
        //    so adjacent rectangles agree on edge Y by construction.
        var walkable = new bool[w * h];
        for (int y = 0; y < h; y++)
        for (int x = 0; x < w; x++)
        {
            byte cell = grid.GetCell(x, y);
            walkable[y * w + x] = (cell & options.BlockedMask) == 0;
        }

        // 2) Greedy mesh. visited[] marks cells already absorbed into a
        //    larger rect — we only seed new rects from un-visited cells.
        var visited = new bool[w * h];
        var rects = new List<Rect>(capacity: 1024);

        for (int y = 0; y < h; y++)
        {
            for (int x = 0; x < w; x++)
            {
                if (visited[y * w + x] || !walkable[y * w + x])
                    continue;

                // Extend right while cells stay walkable + un-visited + same
                // height as the seed (within tolerance).
                float seedY = SampleCellY(terrain, x, y, scale, grid);
                int rectW = 1;
                while (x + rectW < w
                    && walkable[y * w + x + rectW]
                    && !visited[y * w + x + rectW]
                    && CellsCoplanar(terrain, x + rectW, y, scale, grid, seedY, options.MaxIntraRectHeightDelta))
                {
                    rectW++;
                }

                // Extend down while EVERY cell in the candidate row is OK.
                int rectH = 1;
                while (y + rectH < h)
                {
                    bool rowOk = true;
                    for (int dx = 0; dx < rectW; dx++)
                    {
                        int idx = (y + rectH) * w + x + dx;
                        if (!walkable[idx] || visited[idx]
                            || !CellsCoplanar(terrain, x + dx, y + rectH, scale, grid, seedY, options.MaxIntraRectHeightDelta))
                        {
                            rowOk = false;
                            break;
                        }
                    }
                    if (!rowOk) break;
                    rectH++;
                }

                for (int dy = 0; dy < rectH; dy++)
                for (int dx = 0; dx < rectW; dx++)
                    visited[(y + dy) * w + x + dx] = true;

                rects.Add(new Rect(x, y, rectW, rectH));
            }
        }

        if (rects.Count == 0)
            return null;

        // 3) Materialize polygons (4 vertices CCW from above) with Y from
        //    the heightmap. We sample at the four world-space corners; the
        //    polygon's SampleYAt then bilerps for queries.
        var polys = new NavPoly[rects.Count];
        var min = new Vector3(float.PositiveInfinity, float.PositiveInfinity, float.PositiveInfinity);
        var max = new Vector3(float.NegativeInfinity, float.NegativeInfinity, float.NegativeInfinity);

        for (int i = 0; i < rects.Count; i++)
        {
            var r = rects[i];
            float wx0 = grid.WorldX(r.X) ;
            float wz0 = grid.WorldZ(r.Y);
            float wx1 = grid.WorldX(r.X + r.W);
            float wz1 = grid.WorldZ(r.Y + r.H);

            // CCW from above: (x0,z0) → (x1,z0) → (x1,z1) → (x0,z1)
            var v0 = new Vector3(wx0, terrain.GetHeight(wx0, wz0), wz0);
            var v1 = new Vector3(wx1, terrain.GetHeight(wx1, wz0), wz0);
            var v2 = new Vector3(wx1, terrain.GetHeight(wx1, wz1), wz1);
            var v3 = new Vector3(wx0, terrain.GetHeight(wx0, wz1), wz1);

            polys[i] = new NavPoly(i, new[] { v0, v1, v2, v3 });

            min = Vector3.Min(min, polys[i].MinXZ.ToVec3WithY(MathF.Min(MathF.Min(v0.Y, v1.Y), MathF.Min(v2.Y, v3.Y))));
            max = Vector3.Max(max, polys[i].MaxXZ.ToVec3WithY(MathF.Max(MathF.Max(v0.Y, v1.Y), MathF.Max(v2.Y, v3.Y))));
        }

        // 4) Build adjacency. For each rectangle, walk its 4 boundary edges
        //    one cell at a time and look up the rect on the other side via
        //    the cell→rect map. Coalesce contiguous "same neighbor" runs
        //    into a single portal edge.
        var cellToRect = new int[w * h];
        Array.Fill(cellToRect, -1); // -1 = unwalkable / not in any rect
        for (int i = 0; i < rects.Count; i++)
        {
            var r = rects[i];
            for (int dy = 0; dy < r.H; dy++)
            for (int dx = 0; dx < r.W; dx++)
                cellToRect[(r.Y + dy) * w + r.X + dx] = i;
        }

        var edgesByPoly = new List<NavEdge>[rects.Count];
        for (int i = 0; i < rects.Count; i++)
            edgesByPoly[i] = new List<NavEdge>(4);

        for (int i = 0; i < rects.Count; i++)
        {
            var r = rects[i];
            ScanEdge(edgesByPoly, cellToRect, polys, grid, w, h, i,
                cellsAlongX: true, fixedCell: r.Y - 1,
                varStart: r.X, varEnd: r.X + r.W - 1,
                portalOnPlusSide: false, axisFixed: 'Z',
                portalCoord: grid.WorldZ(r.Y));

            ScanEdge(edgesByPoly, cellToRect, polys, grid, w, h, i,
                cellsAlongX: true, fixedCell: r.Y + r.H,
                varStart: r.X, varEnd: r.X + r.W - 1,
                portalOnPlusSide: true, axisFixed: 'Z',
                portalCoord: grid.WorldZ(r.Y + r.H));

            ScanEdge(edgesByPoly, cellToRect, polys, grid, w, h, i,
                cellsAlongX: false, fixedCell: r.X - 1,
                varStart: r.Y, varEnd: r.Y + r.H - 1,
                portalOnPlusSide: false, axisFixed: 'X',
                portalCoord: grid.WorldX(r.X));

            ScanEdge(edgesByPoly, cellToRect, polys, grid, w, h, i,
                cellsAlongX: false, fixedCell: r.X + r.W,
                varStart: r.Y, varEnd: r.Y + r.H - 1,
                portalOnPlusSide: true, axisFixed: 'X',
                portalCoord: grid.WorldX(r.X + r.W));
        }

        // 5) Flatten edges per-poly into the CSR-style array NavMeshGraph wants.
        var offsets = new int[rects.Count + 1];
        int totalEdges = 0;
        for (int i = 0; i < rects.Count; i++)
        {
            offsets[i] = totalEdges;
            totalEdges += edgesByPoly[i].Count;
        }
        offsets[rects.Count] = totalEdges;

        var edges = new NavEdge[totalEdges];
        for (int i = 0; i < rects.Count; i++)
            edgesByPoly[i].CopyTo(edges, offsets[i]);

        return new NavMeshGraph(polys, edges, offsets, min, max);
    }

    /// <summary>Scan a single boundary side of <paramref name="rectIdx"/>'s
    /// rectangle, find runs where every adjacent cell belongs to the SAME
    /// neighbor rectangle, and emit one portal edge per run. Skipped cells
    /// (off-grid / unwalkable / same rect) end the current run.</summary>
    private static void ScanEdge(
        List<NavEdge>[] edgesByPoly, int[] cellToRect,
        NavPoly[] polys, IWalkabilityGrid grid, int gridW, int gridH,
        int rectIdx,
        bool cellsAlongX, int fixedCell, int varStart, int varEnd,
        bool portalOnPlusSide, char axisFixed, float portalCoord)
    {
        int currentNeighbor = -2; // -2 = no run, -1 = border (no rect)
        int runStart = 0;

        for (int v = varStart; v <= varEnd + 1; v++)
        {
            int neighbor;
            if (v > varEnd)
            {
                neighbor = -2; // sentinel — flush
            }
            else
            {
                int cellX = cellsAlongX ? v : fixedCell;
                int cellY = cellsAlongX ? fixedCell : v;
                neighbor = (cellX < 0 || cellX >= gridW || cellY < 0 || cellY >= gridH)
                    ? -1
                    : cellToRect[cellY * gridW + cellX];
                if (neighbor == rectIdx) neighbor = -1;
            }

            if (neighbor != currentNeighbor)
            {
                if (currentNeighbor >= 0)
                {
                    // Emit the edge for the run [runStart .. v-1] going to currentNeighbor.
                    EmitPortal(edgesByPoly, polys, grid, rectIdx, currentNeighbor,
                        cellsAlongX, runStart, v - 1, portalOnPlusSide, axisFixed, portalCoord);
                }
                currentNeighbor = neighbor;
                runStart = v;
            }
        }
    }

    private static void EmitPortal(
        List<NavEdge>[] edgesByPoly, NavPoly[] polys, IWalkabilityGrid grid,
        int fromIdx, int toIdx,
        bool cellsAlongX, int varStart, int varEnd,
        bool portalOnPlusSide, char axisFixed, float portalCoord)
    {
        // Don't emit duplicates — outgoing edges from `from` are scanned
        // four times (once per side of `from`'s rect), but each adjacent
        // pair shows up exactly once on each side, so no dedup needed.

        // Compute portal endpoints in world space.
        float vStartWorld = cellsAlongX ? grid.WorldX(varStart) : grid.WorldZ(varStart);
        float vEndWorld = cellsAlongX ? grid.WorldX(varEnd + 1) : grid.WorldZ(varEnd + 1);

        // Portal endpoints in world space. Orient V0 → V1 so that V0 is on
        // the LEFT when crossing from `from` to `to`. Convention (XZ plane
        // viewed from above, X right, Z up):
        //   walking +Z  → LEFT = -X (low X)
        //   walking -Z  → LEFT = +X (high X)
        //   walking +X  → LEFT = +Z (high Z)
        //   walking -X  → LEFT = -Z (low Z)
        // The funnel's TriArea2 sign relies on this; getting it backwards
        // makes Stupid Funnel emit corners on the wrong side of the path.
        Vector3 v0, v1;
        if (axisFixed == 'Z')
        {
            // Portal lies along X at fixed Z = portalCoord.
            if (portalOnPlusSide)
            {
                // crossing toward +Z (higher z) — LEFT is -X (low X)
                v0 = new Vector3(vStartWorld, polys[fromIdx].SampleYAt(vStartWorld, portalCoord), portalCoord);
                v1 = new Vector3(vEndWorld, polys[fromIdx].SampleYAt(vEndWorld, portalCoord), portalCoord);
            }
            else
            {
                // crossing toward -Z (lower z) — LEFT is +X (high X)
                v0 = new Vector3(vEndWorld, polys[fromIdx].SampleYAt(vEndWorld, portalCoord), portalCoord);
                v1 = new Vector3(vStartWorld, polys[fromIdx].SampleYAt(vStartWorld, portalCoord), portalCoord);
            }
        }
        else // axisFixed == 'X'
        {
            // Portal along Z at fixed X = portalCoord.
            if (portalOnPlusSide)
            {
                // crossing toward +X — LEFT is +Z (high Z)
                v0 = new Vector3(portalCoord, polys[fromIdx].SampleYAt(portalCoord, vEndWorld), vEndWorld);
                v1 = new Vector3(portalCoord, polys[fromIdx].SampleYAt(portalCoord, vStartWorld), vStartWorld);
            }
            else
            {
                // crossing toward -X — LEFT is -Z (low Z)
                v0 = new Vector3(portalCoord, polys[fromIdx].SampleYAt(portalCoord, vStartWorld), vStartWorld);
                v1 = new Vector3(portalCoord, polys[fromIdx].SampleYAt(portalCoord, vEndWorld), vEndWorld);
            }
        }

        float cost = Vector3.Distance(polys[fromIdx].Centroid, polys[toIdx].Centroid);
        edgesByPoly[fromIdx].Add(new NavEdge(fromIdx, toIdx, v0, v1, cost));
    }

    private static float SampleCellY(ITerrainProvider terrain, int cx, int cy, int scale, IWalkabilityGrid grid)
    {
        float wx = grid.WorldX(cx) + scale * 0.5f;
        float wz = grid.WorldZ(cy) + scale * 0.5f;
        return terrain.GetHeight(wx, wz);
    }

    private static bool CellsCoplanar(ITerrainProvider terrain, int cx, int cy, int scale, IWalkabilityGrid grid, float seedY, float maxDelta)
    {
        float y = SampleCellY(terrain, cx, cy, scale, grid);
        return MathF.Abs(y - seedY) <= maxDelta;
    }

    private readonly record struct Rect(int X, int Y, int W, int H);
}

/// <summary>Convenience helpers for converting cell indices to world coords.
/// Walkability grids store BaseX/BaseY as their world-space origin and
/// CellScale as the meters-per-cell ratio.</summary>
internal static class WalkabilityGridExtensions
{
    public static float WorldX(this IWalkabilityGrid grid, int cellX)
    {
        if (grid is WalkabilityGrid wg) return wg.BaseX + cellX * grid.CellScale;
        return cellX * grid.CellScale;
    }

    public static float WorldZ(this IWalkabilityGrid grid, int cellY)
    {
        if (grid is WalkabilityGrid wg) return wg.BaseY + cellY * grid.CellScale;
        return cellY * grid.CellScale;
    }
}

internal static class Vec2Extensions
{
    public static Vector3 ToVec3WithY(this Vector2 xz, float y) => new(xz.X, y, xz.Y);
}
