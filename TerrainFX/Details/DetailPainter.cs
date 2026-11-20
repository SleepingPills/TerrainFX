using System.Collections.Generic;
using System.Reflection;
using GPUInstancer;
using HarmonyLib;
using UnityEngine;

namespace TerrainFX.Details;

/// <summary>The shape and effect of one brush stamp, see <see cref="DetailPainter.CollectEdits"/>.</summary>
public struct DetailBrush
{
    /// <summary>Radius of the brush in metres. A radius of 0 or less changes nothing.</summary>
    public float Radius;

    /// <summary>
    /// How many instances painting puts into each density cell it picks. It is capped at the detail's
    /// <see cref="Detail.MaxCount"/>. Erasing ignores it.
    /// </summary>
    public int Count;

    /// <summary>
    /// The share of density cells, from 0 to 1, that painting fills in the solid middle of the brush. Which cells are
    /// picked is fixed per cell, so painting the same spot twice changes nothing. Erasing ignores it.
    /// </summary>
    public float Coverage;

    /// <summary>
    /// The share of the radius, from 0 to 1, that has the full effect. Beyond it the effect fades linearly to nothing
    /// at the rim: fewer cells are painted, or fewer are erased. 1 gives a hard edged circle, 0 fades all the way
    /// from the centre.
    /// </summary>
    public float Hardness;

    /// <summary>When true the brush removes instances instead of adding them.</summary>
    public bool Erase;
}

/// <summary>
/// One changed density cell, kept so the change can be undone. <see cref="Index"/> is z * resolution + x in the
/// detail's density map.
/// </summary>
public struct DetailCellEdit
{
    /// <summary>The cell's position in the density map, z * resolution + x.</summary>
    public int Index;

    /// <summary>The cell's instance count before the edit.</summary>
    public int Before;

    /// <summary>The cell's instance count after the edit.</summary>
    public int After;
}

/// <summary>
/// Walks the cells of a square grid laid over a terrain (resolution x resolution cells) whose centres lie inside a
/// brush circle, together with how strongly the brush acts on each of them. Create it, then call <see cref="Next"/>
/// until it returns false; after each true the fields describe the current cell. It does not allocate.
/// </summary>
internal struct DetailBrushCells
{
    private readonly int _resolution;
    private readonly int _minX;
    private readonly int _maxX;
    private readonly int _maxZ;
    private readonly float _stepX;
    private readonly float _stepZ;
    private readonly float _localX;
    private readonly float _localZ;
    private readonly float _radius;
    private readonly float _hardness;

    private int _x;
    private int _z;

    /// <summary>Column of the current cell.</summary>
    public int X;

    /// <summary>Row of the current cell.</summary>
    public int Z;

    /// <summary>Position of the current cell in the grid, Z * resolution + X.</summary>
    public int Index;

    /// <summary>
    /// How strongly the brush acts on the current cell, from 0 to 1: 1 up to the hardness share of the radius, then
    /// fading linearly to 0 at the rim.
    /// </summary>
    public float Weight;

    /// <summary>
    /// Prepares the walk over the cells under a brush of <paramref name="radius"/> metres centred on
    /// <paramref name="worldPoint"/>. A radius of 0 or less, or a resolution of 0 or less, visits no cells.
    /// </summary>
    public DetailBrushCells(Terrain terrain, Vector3 worldPoint, float radius, float hardness, int resolution)
    {
        _resolution = resolution;
        _radius = radius;
        _hardness = Mathf.Clamp01(hardness);

        var terrainSize = terrain.terrainData.size;
        var local = worldPoint - terrain.transform.position;

        _stepX = resolution > 0 ? terrainSize.x / resolution : 1f;
        _stepZ = resolution > 0 ? terrainSize.z / resolution : 1f;
        _localX = local.x;
        _localZ = local.z;

        X = 0;
        Z = 0;
        Index = 0;
        Weight = 0f;

        if (radius <= 0f || resolution <= 0)
        {
            _minX = 0;
            _maxX = -1;
            _maxZ = -1;
            _x = 0;
            _z = 0;
            return;
        }

        _minX = Mathf.Max(0, Mathf.FloorToInt((local.x - radius) / _stepX));
        _maxX = Mathf.Min(resolution - 1, Mathf.FloorToInt((local.x + radius) / _stepX));
        _maxZ = Mathf.Min(resolution - 1, Mathf.FloorToInt((local.z + radius) / _stepZ));
        _x = _minX - 1;
        _z = Mathf.Max(0, Mathf.FloorToInt((local.z - radius) / _stepZ));
    }

    /// <summary>Moves to the next cell under the brush. Returns false when every cell has been visited.</summary>
    public bool Next()
    {
        while (true)
        {
            _x++;
            if (_x > _maxX)
            {
                _x = _minX;
                _z++;
            }

            if (_z > _maxZ || _minX > _maxX) return false;

            var offsetX = (_x + 0.5f) * _stepX - _localX;
            var offsetZ = (_z + 0.5f) * _stepZ - _localZ;
            var t = Mathf.Sqrt(offsetX * offsetX + offsetZ * offsetZ) / _radius;
            if (t > 1f) continue;

            X = _x;
            Z = _z;
            Index = _z * _resolution + _x;
            // Full effect up to the hardness, then fading linearly to nothing at the rim
            Weight = t <= _hardness ? 1f : (1f - t) / (1f - _hardness);
            return true;
        }
    }
}

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
///
/// Every terrain has two detail managers, one for the main camera and one for the scope camera, each with its own
/// copy of the density map. Which cells a stamp changes is decided once on the main camera's manager
/// (<see cref="CollectEdits"/>), and the same changes are then written into both (<see cref="Apply"/>).
///
/// Custom details are painted this way. A detail is only found on a terrain once it has been put there (at load, or by
/// <see cref="DetailInjector.EnsureOnTerrain"/>). The game's own assets are painted through <see cref="DetailStock"/>,
/// which writes into the game's prototypes with <see cref="ApplyOnManager"/>.
/// </summary>
public static class DetailPainter
{
    internal static readonly FieldInfo SpatialDataField = AccessTools.Field(typeof(GPUInstancerManager), "spData");
    private static readonly AccessTools.FieldRef<GPUInstancerTerrainManager, bool> Initializing =
        AccessTools.FieldRefAccess<GPUInstancerTerrainManager, bool>("initalizingInstances");
    private static readonly AccessTools.FieldRef<GPUInstancerTerrainManager, bool> Replacing =
        AccessTools.FieldRefAccess<GPUInstancerTerrainManager, bool>("replacingInstances");
    private static readonly FieldInfo GrassComputeField = AccessTools.Field(typeof(GPUInstancerDetailManager), "_grassInstantiationComputeShader");

    // Salt of the second per-cell hash, so the cells an eraser thins out are not the same ones painting picks
    private const uint EraseSalt = 0x9e3779b9;

    // Edits of the stamp that Paint is applying
    private static readonly List<DetailCellEdit> StampEdits = [];

    // Terrain cells whose density copy ApplyOnManager changed, reused so that applying a stamp does not allocate
    private static readonly List<GPUInstancerDetailCell> TouchedCells = [];

    // Managers whose density map did not match the main camera's one, so the error is only logged once for each
    private static readonly HashSet<GPUInstancerDetailManager> MismatchLogged = [];

    // Terrain and detail pairs a "detail not found" warning was logged for, so each is only logged once
    private static readonly HashSet<(Terrain Terrain, Detail Detail)> MissingLogged = [];

    /// <summary>
    /// Works out which density cells one brush stamp at <paramref name="worldPoint"/> would change, reading the
    /// current counts from the terrain's main camera detail manager, and appends them to <paramref name="edits"/>.
    /// Nothing is changed on the terrain. It only visits the cells under the brush, so it is cheap enough to call every
    /// frame (for example to draw a brush overlay). A detail that is not on the terrain is warned about once per
    /// terrain and detail; later calls for the same pair stay quiet.
    /// </summary>
    /// <returns>
    /// False, with nothing appended, when the detail is null, is not on this terrain, or the terrain's detail manager
    /// is not ready to be painted on (still loading or rebuilding). True otherwise, even when the stamp changes no
    /// cells.
    /// </returns>
    public static bool CollectEdits(Terrain terrain, Vector3 worldPoint, Detail detail, DetailBrush brush, List<DetailCellEdit> edits)
    {
        if (detail == null) return false;

        var prototype = DetailInjector.FindPrototype(terrain, false, detail, out var manager, out _);
        if (prototype == null)
        {
            WarnNotOnTerrain(terrain, detail);
            return false;
        }

        if (!IsReady(manager)) return false;

        var densityMap = prototype.cachedDensityMapForInstance;
        var resolution = prototype.detailResolution;

        if (densityMap == null || resolution <= 0 || densityMap.Length != resolution * resolution) return false;
        if (brush.Radius <= 0f) return true;

        var count = Mathf.Min(brush.Count, detail.MaxCount);
        var cells = new DetailBrushCells(terrain, worldPoint, brush.Radius, brush.Hardness, resolution);

        while (cells.Next())
        {
            var current = densityMap[cells.Index];
            int target;

            if (brush.Erase)
            {
                if (current == 0 || !ErasePicks(cells.X, cells.Z, cells.Weight)) continue;
                target = 0;
            }
            else
            {
                if (!PaintPicks(cells.X, cells.Z, brush.Coverage, cells.Weight)) continue;
                target = Mathf.Max(current, count);
            }

            if (target == current) continue;

            edits.Add(new DetailCellEdit { Index = cells.Index, Before = current, After = target });
        }

        return true;
    }

    /// <summary>
    /// Writes edits into both detail managers of <paramref name="terrain"/>: each cell gets its After count, or its
    /// Before count when <paramref name="undo"/> is true (the edits are then gone through from last to first, so a
    /// list holding several edits of the same cell ends at the first edit's Before). The changed parts of the detail
    /// are rebuilt on the spot and the terrain and detail are marked as unsaved in <see cref="DetailDensityStore"/>.
    /// A manager that is still loading or rebuilding is left unchanged.
    /// </summary>
    public static void Apply(Terrain terrain, Detail detail, List<DetailCellEdit> edits, bool undo)
    {
        if (edits.Count == 0 || detail == null) return;

        var mainPrototype = DetailInjector.FindPrototype(terrain, false, detail, out var main, out var mainIndex);
        if (mainPrototype == null)
        {
            WarnNotOnTerrain(terrain, detail);
            return;
        }

        var mainLength = mainPrototype.cachedDensityMapForInstance?.Length ?? 0;

        ApplyOnManager(main, mainIndex, edits, undo);

        var opticPrototype = DetailInjector.FindPrototype(terrain, true, detail, out var optic, out var opticIndex);
        if (opticPrototype != null)
        {
            var opticLength = opticPrototype.cachedDensityMapForInstance?.Length ?? 0;

            if (opticLength == mainLength)
            {
                ApplyOnManager(optic, opticIndex, edits, undo);
            }
            else if (MismatchLogged.Add(optic))
            {
                Plugin.Log.LogError(
                    $"'{detail.Name}' has a density map of {opticLength} cells on {optic.name} but {mainLength} on {main.name}, " +
                    "painting is not shown through scopes on this terrain"
                );
            }
        }

        DetailDensityStore.MarkDirty(terrain, detail);
    }

    /// <summary>
    /// Works out the cells one brush stamp changes (<see cref="CollectEdits"/>) and writes them into the terrain
    /// (<see cref="Apply"/>). The edits are appended to <paramref name="edits"/> so the stamp can be undone later;
    /// pass null when there is nothing to undo.
    /// </summary>
    /// <returns>The number of density cells that changed.</returns>
    public static int Paint(Terrain terrain, Vector3 worldPoint, Detail detail, DetailBrush brush, List<DetailCellEdit> edits)
    {
        StampEdits.Clear();

        if (!CollectEdits(terrain, worldPoint, detail, brush, StampEdits)) return 0;

        Apply(terrain, detail, StampEdits, false);
        edits?.AddRange(StampEdits);

        return StampEdits.Count;
    }

    /// <summary>
    /// Gets the number of density cells along one side of the terrain for a detail, as used by the main camera's
    /// detail manager. Each cell covers terrain size / resolution metres. Returns false when the detail is null, is not
    /// on this terrain, or its manager has not finished loading.
    /// </summary>
    public static bool TryGetResolution(Terrain terrain, Detail detail, out int resolution)
    {
        resolution = 0;
        if (detail == null) return false;

        var prototype = DetailInjector.FindPrototype(terrain, false, detail, out _, out _);
        if (prototype == null) return false;

        var densityMap = prototype.cachedDensityMapForInstance;
        if (densityMap == null || prototype.detailResolution <= 0 || densityMap.Length != prototype.detailResolution * prototype.detailResolution)
        {
            return false;
        }

        resolution = prototype.detailResolution;
        return true;
    }

    /// <summary>Logs that the detail is not on the terrain, but only the first time for each terrain and detail.</summary>
    private static void WarnNotOnTerrain(Terrain terrain, Detail detail)
    {
        if (MissingLogged.Add((terrain, detail))) Plugin.Log.LogWarning($"Detail '{detail.Name}' is not on terrain {terrain.name}");
    }

    /// <summary>
    /// True when the manager has finished loading its cells and is not in the middle of setting them up again. The
    /// game keeps the per-cell density copies in its spatial data, which only exists from then on. It does not
    /// allocate.
    /// </summary>
    internal static bool IsReady(GPUInstancerDetailManager manager)
    {
        return SpatialDataField.GetValue(manager) is GPUInstancerSpatialPartitioningData<GPUInstancerCell>
               && manager.isInitialized
               && !Initializing(manager);
    }

    /// <summary>
    /// True when the manager is ready (<see cref="IsReady"/>) and is not swapping the buffers of its active cells
    /// either, so it can be re-initialized without cutting the game's own work short.
    /// </summary>
    internal static bool IsIdle(GPUInstancerDetailManager manager)
    {
        return IsReady(manager) && !Replacing(manager);
    }

    /// <summary>
    /// True when the eraser picks the density cell at column <paramref name="x"/> and row <paramref name="z"/>,
    /// given the brush <paramref name="weight"/> there (see <see cref="DetailBrushCells.Weight"/>). The choice is fixed
    /// per cell, so the same cells are picked every time at the same weight.
    /// </summary>
    internal static bool ErasePicks(int x, int z, float weight)
    {
        return CellHash(x, z, EraseSalt) < weight;
    }

    /// <summary>
    /// True when painting with <paramref name="coverage"/> picks the density cell at column <paramref name="x"/> and
    /// row <paramref name="z"/>, given the brush <paramref name="weight"/> there (see
    /// <see cref="DetailBrushCells.Weight"/>). The choice is fixed per cell, so painting the same spot twice picks the
    /// same cells.
    /// </summary>
    internal static bool PaintPicks(int x, int z, float coverage, float weight)
    {
        return CellHash(x, z, 0) < coverage * weight;
    }

    /// <summary>
    /// Writes edits into the density map of the prototype at <paramref name="index"/> of <paramref name="manager"/>
    /// and into the per-cell copies, then rebuilds the touched parts. Does nothing while the manager is not ready.
    /// </summary>
    internal static void ApplyOnManager(GPUInstancerDetailManager manager, int index, List<DetailCellEdit> edits, bool undo)
    {
        if (!IsReady(manager)) return;

        var spatialData = (GPUInstancerSpatialPartitioningData<GPUInstancerCell>)SpatialDataField.GetValue(manager);
        var prototype = (GPUInstancerDetailPrototype)manager.prototypeList[index];
        var densityMap = prototype.cachedDensityMapForInstance;
        var resolution = prototype.detailResolution;
        var cellsPerSide = spatialData.cellRowAndCollumnCountPerTerrain;

        if (densityMap == null || resolution <= 0 || densityMap.Length != resolution * resolution) return;

        // Number of density cells along one side of a terrain cell
        var perCell = resolution / cellsPerSide;

        var touchedCells = TouchedCells;
        touchedCells.Clear();

        for (var i = 0; i < edits.Count; i++)
        {
            var edit = undo ? edits[edits.Count - 1 - i] : edits[i];
            if (edit.Index < 0 || edit.Index >= densityMap.Length) continue;

            var target = undo ? edit.Before : edit.After;
            var x = edit.Index % resolution;
            var z = edit.Index / resolution;

            densityMap[edit.Index] = target;

            // The same value in the copy held by the terrain cell this density cell falls into
            var cellX = x / perCell;
            var cellZ = z / perCell;
            if (cellX >= cellsPerSide || cellZ >= cellsPerSide) continue;

            spatialData.GetCell(GPUInstancerCell.CalculateHash(cellX, 0, cellZ), out var cell);
            if (cell is not GPUInstancerDetailCell detailCell) continue;
            if (detailCell.detailMapData == null || index >= detailCell.detailMapData.Count) continue;

            var cellMap = detailCell.detailMapData[index];
            var cellIndex = x % perCell + z % perCell * perCell;
            if (cellMap[cellIndex] == target) continue;

            detailCell.totalDetailCounts[index] += target - cellMap[cellIndex];
            cellMap[cellIndex] = target;
            if (!touchedCells.Contains(detailCell)) touchedCells.Add(detailCell);
        }

        if (touchedCells.Count == 0) return;

        for (var i = 0; i < touchedCells.Count; i++)
        {
            var cell = touchedCells[i];
            if (cell.detailInstanceBuffers == null || !cell.detailInstanceBuffers.TryGetValue(index, out var stale)) continue;

            stale?.Release();
            cell.detailInstanceBuffers.Remove(index);
        }

        Rebuild(manager, index, prototype, spatialData, perCell);
    }

    /// <summary>
    /// Generates the instance buffers that are missing for one detail and rebuilds the buffer it is drawn from.
    /// Follows GPUInstancerDetailManager.MergeVisibilityBufferFromActiveCellsCoroutine.
    /// </summary>
    private static void Rebuild(
        GPUInstancerDetailManager manager, int index, GPUInstancerDetailPrototype prototype,
        GPUInstancerSpatialPartitioningData<GPUInstancerCell> spatialData, int perCell
    )
    {
        // The game is in the middle of its own rebuild. It regenerates the buffers released above by itself.
        if (Replacing(manager)) return;

        var runtimeData = DetailInjector.FindRuntimeData(manager.runtimeDataList, prototype);
        var grassCompute = GrassComputeField.GetValue(null) as ComputeShader;
        if (runtimeData == null || grassCompute == null) return;

        var terrainData = manager.terrain.terrainData;
        var heightResolution = terrainData.heightmapResolution;
        var heightMapSize = (heightResolution - 1) / spatialData.cellRowAndCollumnCountPerTerrain + 1;

        var total = 0;

        var activeCells = spatialData.activeCellList;
        for (var i = 0; i < activeCells.Count; i++)
        {
            var activeCell = activeCells[i];
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
            return;
        }

        var merged = new ComputeBuffer(total, GPUInstancerConstants.STRIDE_SIZE_MATRIX4X4);
        var offset = 0;

        for (var i = 0; i < activeCells.Count; i++)
        {
            var activeCell = activeCells[i];
            if (activeCell is not GPUInstancerDetailCell { detailInstanceBuffers: not null } cell) continue;
            if (!cell.detailInstanceBuffers.TryGetValue(index, out var cellBuffer) || cellBuffer == null) continue;

            merged.CopyComputeBuffer(offset, cellBuffer.count, cellBuffer);
            offset += cellBuffer.count;
        }

        runtimeData.transformationMatrixVisibilityBuffer?.Release();
        runtimeData.transformationMatrixVisibilityBuffer = merged;

        GPUInstancerUtility.InitializeGPUBuffer(runtimeData);
    }

    /// <summary>
    /// A fixed number from 0 to just below 1 for a density cell, worked out from its coordinates alone. With a salt
    /// of 0 it decides which cells painting picks; erasing uses a different salt.
    /// </summary>
    private static float CellHash(int x, int z, uint salt)
    {
        unchecked
        {
            var hash = (uint)(x * 73856093) ^ (uint)(z * 19349663) ^ salt;
            hash ^= hash >> 13;
            hash *= 0x5bd1e995;
            hash ^= hash >> 15;

            return (hash & 0xFFFF) / 65536f;
        }
    }
}
