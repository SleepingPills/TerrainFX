using System.Collections.Generic;
using System.Diagnostics;
using System.Reflection;
using GPUInstancer;
using HarmonyLib;
using UnityEngine;

namespace TerrainFX.Details;

/// <summary>
/// Changes the density of one detail around a point on a terrain while the raid is running.
///
/// The game has no call for this, so the steps of its own rebuild are repeated here for a single detail:
///  1. Change the instance counts in the detail's full density map (kept so that a later re-initialization of the
///     manager still has the painted state) and in the per-cell copies the game made of it at load.
///  2. Release the instance buffer of every changed cell. The game treats a missing buffer as "generate me".
///  3. Generate the missing buffers with the game's compute shader, then join the buffers of all active cells into
///     the one buffer the detail is drawn from.
/// The game's own routine does step 3 for every detail of the manager. Only the painted one is redone here.
/// </summary>
public static class DetailPainter
{
    private static readonly FieldInfo SpatialDataField = AccessTools.Field(typeof(GPUInstancerManager), "spData");
    private static readonly FieldInfo InitializingField = AccessTools.Field(typeof(GPUInstancerTerrainManager), "initalizingInstances");
    private static readonly FieldInfo ReplacingField = AccessTools.Field(typeof(GPUInstancerTerrainManager), "replacingInstances");
    private static readonly FieldInfo GrassComputeField = AccessTools.Field(typeof(GPUInstancerDetailManager), "_grassInstantiationComputeShader");

    /// <summary>
    /// Paints or erases a circle on every detail manager of <paramref name="terrain"/> (main camera and scope).
    /// Painting raises a share of the density cells in the circle to <paramref name="count"/> instances; the share
    /// is <paramref name="coverage"/> (0 to 1) and which cells are picked is fixed per cell, so painting the same
    /// spot twice changes nothing. Erasing sets every cell in the circle to 0.
    /// </summary>
    /// <returns>The number of density cells that changed on the main camera's manager.</returns>
    public static int Paint(Terrain terrain, Vector3 worldPoint, string detailName, float radius, int count, float coverage, bool erase)
    {
        var changedOnMain = 0;
        var found = false;

        foreach (var manager in GPUInstancerManager.activeManagerList ?? [])
        {
            if (manager is not GPUInstancerDetailManager detailManager || detailManager.terrain != terrain) continue;

            var index = detailManager.prototypeList.FindIndex(p => p != null && p.name == detailName);
            if (index < 0) continue;

            found = true;

            var timer = Stopwatch.StartNew();
            var changed = PaintOnManager(detailManager, index, worldPoint, radius, count, coverage, erase, out var cells, out var instances);
            timer.Stop();

            if (!detailManager.IsOptic) changedOnMain = changed;

            if (changed > 0)
            {
                Plugin.Log.LogInfo(
                    $"{(erase ? "Erased" : "Painted")} '{detailName}' on {detailManager.name}: {changed} density cells in " +
                    $"{cells} terrain cell(s), {instances} instances now active, {timer.Elapsed.TotalMilliseconds:F2} ms on the CPU"
                );
            }
        }

        if (!found) Plugin.Log.LogWarning($"No detail named '{detailName}' on terrain {terrain.name}");

        return changedOnMain;
    }

    private static int PaintOnManager(
        GPUInstancerDetailManager manager, int index, Vector3 worldPoint, float radius, int count, float coverage, bool erase,
        out int touchedCellCount, out int activeInstances
    )
    {
        touchedCellCount = 0;
        activeInstances = 0;

        var spatialData = SpatialDataField.GetValue(manager) as GPUInstancerSpatialPartitioningData<GPUInstancerCell>;
        if (spatialData == null || !manager.isInitialized || (bool)InitializingField.GetValue(manager))
        {
            return 0;
        }

        var prototype = (GPUInstancerDetailPrototype)manager.prototypeList[index];
        var densityMap = prototype.cachedDensityMapForInstance;
        var resolution = prototype.detailResolution;
        var cellsPerSide = spatialData.cellRowAndCollumnCountPerTerrain;

        if (densityMap == null || resolution <= 0 || densityMap.Length != resolution * resolution) return 0;

        // Number of density cells along one side of a terrain cell
        var perCell = resolution / cellsPerSide;

        var terrainSize = manager.terrain.terrainData.size;
        var stepX = terrainSize.x / resolution;
        var stepZ = terrainSize.z / resolution;
        var local = worldPoint - manager.terrain.transform.position;

        var minX = Mathf.Max(0, Mathf.FloorToInt((local.x - radius) / stepX));
        var maxX = Mathf.Min(resolution - 1, Mathf.FloorToInt((local.x + radius) / stepX));
        var minZ = Mathf.Max(0, Mathf.FloorToInt((local.z - radius) / stepZ));
        var maxZ = Mathf.Min(resolution - 1, Mathf.FloorToInt((local.z + radius) / stepZ));

        var touchedCells = new HashSet<GPUInstancerDetailCell>();
        var changed = 0;

        for (var z = minZ; z <= maxZ; z++)
        {
            for (var x = minX; x <= maxX; x++)
            {
                var offsetX = (x + 0.5f) * stepX - local.x;
                var offsetZ = (z + 0.5f) * stepZ - local.z;
                if (offsetX * offsetX + offsetZ * offsetZ > radius * radius) continue;

                var current = densityMap[z * resolution + x];
                int target;

                if (erase) target = 0;
                else if (IsPicked(x, z, coverage)) target = Mathf.Max(current, count);
                else continue;

                if (target == current) continue;

                densityMap[z * resolution + x] = target;
                changed++;

                // The same value in the copy held by the terrain cell this density cell falls into
                var cellX = x / perCell;
                var cellZ = z / perCell;
                if (cellX >= cellsPerSide || cellZ >= cellsPerSide) continue;

                spatialData.GetCell(GPUInstancerCell.CalculateHash(cellX, 0, cellZ), out var cell);
                if (cell is not GPUInstancerDetailCell detailCell) continue;
                if (detailCell.detailMapData == null || index >= detailCell.detailMapData.Count) continue;

                var cellMap = detailCell.detailMapData[index];
                var cellIndex = x % perCell + z % perCell * perCell;

                detailCell.totalDetailCounts[index] += target - cellMap[cellIndex];
                cellMap[cellIndex] = target;
                touchedCells.Add(detailCell);
            }
        }

        if (touchedCells.Count == 0) return changed;

        foreach (var cell in touchedCells)
        {
            if (cell.detailInstanceBuffers == null || !cell.detailInstanceBuffers.TryGetValue(index, out var stale)) continue;

            stale?.Release();
            cell.detailInstanceBuffers.Remove(index);
        }

        touchedCellCount = touchedCells.Count;
        activeInstances = Rebuild(manager, index, prototype, spatialData, perCell);

        return changed;
    }

    /// <summary>
    /// Generates the instance buffers that are missing for one detail and rebuilds the buffer it is drawn from.
    /// Follows GPUInstancerDetailManager.MergeVisibilityBufferFromActiveCellsCoroutine.
    /// </summary>
    private static int Rebuild(
        GPUInstancerDetailManager manager, int index, GPUInstancerDetailPrototype prototype,
        GPUInstancerSpatialPartitioningData<GPUInstancerCell> spatialData, int perCell
    )
    {
        // The game is in the middle of its own rebuild. It regenerates the buffers released above by itself.
        if ((bool)ReplacingField.GetValue(manager)) return 0;

        var runtimeData = manager.runtimeDataList?.Find(data => data.prototype == prototype);
        var grassCompute = GrassComputeField.GetValue(null) as ComputeShader;
        if (runtimeData == null || grassCompute == null) return 0;

        var terrainData = manager.terrain.terrainData;
        var heightResolution = terrainData.heightmapResolution;
        var heightMapSize = (heightResolution - 1) / spatialData.cellRowAndCollumnCountPerTerrain + 1;

        var total = 0;

        foreach (var activeCell in spatialData.activeCellList)
        {
            if (activeCell is not GPUInstancerDetailCell cell) continue;
            if (cell.totalDetailCounts == null || index >= cell.totalDetailCounts.Count) continue;

            var instanceCount = cell.totalDetailCounts[index];
            total += instanceCount;

            cell.detailInstanceBuffers ??= new Dictionary<int, ComputeBuffer>();

            if (instanceCount <= 0) continue;
            if (cell.detailInstanceBuffers.TryGetValue(index, out var existing) && existing != null) continue;

            var heightBuffer = new ComputeBuffer(cell.heightMapData.Length, GPUInstancerConstants.STRIDE_SIZE_INT);
            heightBuffer.SetData(cell.heightMapData);

            var densityBuffer = new ComputeBuffer(perCell * perCell, GPUInstancerConstants.STRIDE_SIZE_INT);
            densityBuffer.SetData(cell.detailMapData[index]);

            cell.detailInstanceBuffers[index] = manager.GetComputeBufferForDetailPrototypeWithComputeShader(
                prototype, heightMapSize, heightResolution, cell.instanceStartPosition, terrainData.size, instanceCount,
                grassCompute, manager.terrainSettings, heightBuffer, densityBuffer
            );

            heightBuffer.Release();
            densityBuffer.Release();
        }

        runtimeData.bufferSize = total;
        runtimeData.instanceCount = total;

        if (total == 0)
        {
            runtimeData.transformationMatrixVisibilityBuffer?.Release();
            runtimeData.transformationMatrixVisibilityBuffer = null;
            return 0;
        }

        var merged = new ComputeBuffer(total, GPUInstancerConstants.STRIDE_SIZE_MATRIX4X4);
        var offset = 0;

        foreach (var activeCell in spatialData.activeCellList)
        {
            if (activeCell is not GPUInstancerDetailCell { detailInstanceBuffers: not null } cell) continue;
            if (!cell.detailInstanceBuffers.TryGetValue(index, out var cellBuffer) || cellBuffer == null) continue;

            merged.CopyComputeBuffer(offset, cellBuffer.count, cellBuffer);
            offset += cellBuffer.count;
        }

        runtimeData.transformationMatrixVisibilityBuffer?.Release();
        runtimeData.transformationMatrixVisibilityBuffer = merged;

        GPUInstancerUtility.InitializeGPUBuffer(runtimeData);

        return total;
    }

    /// <summary>Decides from the cell's coordinates alone whether painting puts instances into it.</summary>
    private static bool IsPicked(int x, int z, float coverage)
    {
        unchecked
        {
            var hash = (uint)(x * 73856093) ^ (uint)(z * 19349663);
            hash ^= hash >> 13;
            hash *= 0x5bd1e995;
            hash ^= hash >> 15;

            return (hash & 0xFFFF) < coverage * 65536f;
        }
    }
}
