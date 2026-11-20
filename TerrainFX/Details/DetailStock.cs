using System.Collections.Generic;
using GPUInstancer;
using UnityEngine;
using Object = UnityEngine.Object;

namespace TerrainFX.Details;

/// <summary>One of the game's own detail assets (a grass texture or a mesh) found on the loaded map.</summary>
public sealed class StockAsset
{
    /// <summary>
    /// The name of the game's prototypes of this asset without their "Detail_{index}_" prefix. It is the same for the
    /// same asset on every detail manager and terrain of a map.
    /// </summary>
    public string Key;

    /// <summary>The key without its trailing "_{hash}", for showing in the editor.</summary>
    public string DisplayName;

    /// <summary>
    /// A description of the asset for the editor: its name is <see cref="Key"/>, and it gives the largest count a cell
    /// can hold (<see cref="Detail.MaxCount"/>), how counts are stored in the additions file
    /// (<see cref="Detail.DensityScale"/>), and the game's prefab or texture for previews. Its
    /// <see cref="Detail.IsStock"/> is true. It is never put on a detail manager: additions are painted into the game's
    /// own prototypes through <see cref="DetailStock.CollectAdditionEdits"/> and <see cref="DetailStock.ApplyAdditions"/>.
    /// </summary>
    public Detail Detail;
}

/// <summary>
/// The game's own detail assets of the loaded map, and painting more of them or removing them in the game's own
/// prototypes.
///
/// Two layers are kept per terrain and asset, both on the terrain's detail grid ([z * resolution + x], terrain detail
/// resolution cells along each side) and created on first use:
///  - additions: instance counts to add, stored in "&lt;terrain name&gt;.&lt;asset key&gt;.png"
///  - removal: 1 where the game's own instances are removed, stored in "&lt;terrain name&gt;.&lt;asset key&gt;.removed.png"
///
/// The game's density maps are never written to. Every prototype of an asset with a layer on its terrain is given a
/// working copy of the game's map (the game's original is kept here), derived as: 0 where the removal layer is set at
/// the cell's centre, else the game's count, plus, on the asset's primary prototype of the manager (the first one in
/// its prototype list), the added count of the cell. Each layer cell belongs to the prototype cell its centre falls
/// into, and a prototype cell's added count is the sum of the layer cells that belong to it, so additions keep the
/// painted number of instances on the main view. The scope view gets the same share of them that the game uses for its
/// own grass: the sum is multiplied by (scope resolution / main resolution)^2 and rounded.
///
/// The game's density mask hides a texture detail wherever its channel is 0, and instances are scattered anywhere in
/// their prototype cell, so for a primary prototype that uses the mask the channel is opened (255) over the whole of
/// every prototype cell that received additions. The game's mask array is compressed, so it is replaced on
/// the terrain settings by an uncompressed copy the first time a masked asset gets additions on the terrain; the
/// original is put back when the copy's managers are gone or the map changes. Mesh details have the mask off, their
/// counts alone decide.
/// </summary>
public static class DetailStock
{
    private const string StockPrefix = "Detail_";
    private const int HashLength = 6;

    /// <summary>One entry per game asset found on the loaded map, never duplicates, in the order they were found. Cleared when the map changes.</summary>
    public static readonly List<StockAsset> Assets = [];

    /// <summary>
    /// When true, the uncompressed mask copies stay readable and edits can write into them. When false (the default),
    /// a copy is uploaded with its CPU copy freed and no pixel arrays are kept, so painting additions during the raid
    /// leaves the mask unchanged with a warning. The editor sets it to true at startup.
    /// </summary>
    public static bool KeepMasksWritable;

    /// <summary>A prototype of the game's, with the manager it is on and the asset it shows.</summary>
    private sealed class StockPrototype
    {
        public GPUInstancerDetailManager Manager;
        public GPUInstancerDetailPrototype Prototype;
        public StockAsset Asset;

        // True for the first prototype of the asset in the manager's prototype list, the only one additions go into
        public bool Primary;

        // Cells along one side of the prototype's density map, and the factor its summed additions are multiplied by.
        // Both are set by PrepareAdditions before every derivation; Resolution is 0 when the map is missing or not square.
        public int Resolution;
        public float AddScale = 1f;
    }

    /// <summary>The additions and the removal of an asset on a terrain.</summary>
    private sealed class StockLayers
    {
        public Terrain Terrain;
        public StockAsset Asset;

        // Terrain detail resolution x resolution instance counts to add. Null while there is no file and nothing was painted.
        public int[] Additions;

        // Terrain detail resolution x resolution cells, 1 = removed. Null while there is no file and nothing was removed.
        public int[] Removed;
    }

    /// <summary>The uncompressed copy of a terrain settings object's density mask array, with the game's original.</summary>
    private sealed class MaskCopy
    {
        public GPUInstancerTerrainSettings Settings;
        public Texture2DArray Original;
        public Texture2DArray Copy;

        // The managers drawing with these settings. When none of them exists any more the original is put back.
        public readonly List<GPUInstancerDetailManager> Managers = [];

        // The asset, slice and channel of every primary prototype whose additions were written into the copy
        public readonly List<(StockAsset Asset, int Slice, int Channel)> Channels = [];

        // Per slice, the copy's pixels and the game's original pixels. An entry is null until that slice is written.
        public Color32[][] Pixels;
        public Color32[][] OriginalPixels;

        public int Uploads;
        public bool UnwritableLogged;
    }

    // Every game prototype of every loaded detail manager, so lookups during painting need no prototype names
    private static readonly List<StockPrototype> Prototypes = [];

    // Layers that were looked up, including the ones that have no files
    private static readonly List<StockLayers> Layers = [];

    // Mask copies of the loaded map's terrain settings
    private static readonly List<MaskCopy> Copies = [];

    // The game's own density map of every prototype that was given a working copy
    private static readonly Dictionary<GPUInstancerDetailPrototype, int[]> Originals = new();

    // Edits of one prototype's density map that are handed to the painter, reused so a stamp does not allocate
    private static readonly List<DetailCellEdit> PrototypeEdits = [];

    // The primary prototypes whose additions open one mask channel, reused so writing the mask does not allocate
    private static readonly List<StockPrototype> MaskEntries = [];

    // Per mask entry while a channel is written: the first cell row of the box, the cells per box row, the first flag,
    // and the first flag of the current pixel row. Grows when needed and is never shrunk.
    private const int EntryFields = 4;
    private static int[] EntryCells = new int[2 * EntryFields];

    // Per mask entry and prototype cell of the box, whether the cell has additions. Grows when needed and is never shrunk.
    private static bool[] CellFlags = new bool[1024];

    // Per mask entry and pixel column of the box, the column's cell counted from the box's first cell column. Grows
    // when needed and is never shrunk.
    private static int[] ColumnCells = new int[1024];

    // Terrain and asset pairs a "no such asset on this terrain" warning was logged for
    private static readonly HashSet<(Terrain Terrain, StockAsset Asset)> MissingLogged = [];

    /// <summary>
    /// Forgets every asset, prototype, layer and mask copy. Prototypes that still exist get the game's density map
    /// back and terrain settings that still exist get the game's mask array back. Called when the map changes.
    /// </summary>
    internal static void Clear()
    {
        if (Originals.Count > 0)
        {
            var keys = new GPUInstancerDetailPrototype[Originals.Count];
            Originals.Keys.CopyTo(keys, 0);
            for (var i = 0; i < keys.Length; i++)
            {
                if (keys[i] != null) keys[i].cachedDensityMapForInstance = Originals[keys[i]];
            }
        }

        for (var i = 0; i < Copies.Count; i++)
        {
            Restore(Copies[i]);
        }

        Assets.Clear();
        Prototypes.Clear();
        Layers.Clear();
        Copies.Clear();
        Originals.Clear();
        MissingLogged.Clear();
    }

    /// <summary>
    /// Forgets the prototypes and layers of managers and terrains that no longer exist (a previous raid of the same
    /// map), and puts the game's mask array back on terrain settings none of whose managers exists any more.
    /// </summary>
    internal static void DropDead()
    {
        for (var i = Prototypes.Count - 1; i >= 0; i--)
        {
            if (Prototypes[i].Manager == null || Prototypes[i].Prototype == null) Prototypes.RemoveAt(i);
        }

        for (var i = Layers.Count - 1; i >= 0; i--)
        {
            if (Layers[i].Terrain == null) Layers.RemoveAt(i);
        }

        for (var i = Copies.Count - 1; i >= 0; i--)
        {
            var copy = Copies[i];
            for (var m = copy.Managers.Count - 1; m >= 0; m--)
            {
                if (copy.Managers[m] == null) copy.Managers.RemoveAt(m);
            }

            if (copy.Managers.Count > 0) continue;

            Restore(copy);
            Copies.RemoveAt(i);
        }

        if (Originals.Count == 0) return;

        var keys = new GPUInstancerDetailPrototype[Originals.Count];
        Originals.Keys.CopyTo(keys, 0);
        for (var i = 0; i < keys.Length; i++)
        {
            if (keys[i] == null) Originals.Remove(keys[i]);
        }
    }

    /// <summary>
    /// Looks at every detail manager in a loaded scene that has a terrain and records each of the game's prototypes,
    /// adding an asset for every key not seen yet. Prototypes TerrainFX added, empty entries and prototypes that are not
    /// detail prototypes are skipped. The first prototype of an asset in a manager's list is marked as the asset's
    /// primary prototype on that manager. An asset found again refreshes its preview prefab or texture when the old one
    /// is gone.
    /// </summary>
    internal static void Collect()
    {
        var managers = Resources.FindObjectsOfTypeAll<GPUInstancerDetailManager>();
        for (var m = 0; m < managers.Length; m++)
        {
            var manager = managers[m];
            if (!IsSceneManager(manager)) continue;

            var prototypes = manager.prototypeList;
            for (var p = 0; p < prototypes.Count; p++)
            {
                if (prototypes[p] is not GPUInstancerDetailPrototype prototype || prototype == null) continue;
                if (DetailInjector.IsInjected(prototype) || IsRecorded(manager, prototype)) continue;

                var asset = FindOrAddAsset(prototype);
                var primary = !HasEntry(manager, asset);
                Prototypes.Add(new StockPrototype { Manager = manager, Prototype = prototype, Asset = asset, Primary = primary });
            }
        }
    }

    /// <summary>Returns the asset with the given key, or null when the loaded map has none.</summary>
    public static StockAsset Find(string key)
    {
        for (var i = 0; i < Assets.Count; i++)
        {
            if (Assets[i].Key == key) return Assets[i];
        }

        return null;
    }

    /// <summary>Returns the asset whose description is <paramref name="detail"/>, or null when there is none.</summary>
    public static StockAsset FindByDetail(Detail detail)
    {
        for (var i = 0; i < Assets.Count; i++)
        {
            if (Assets[i].Detail == detail) return Assets[i];
        }

        return null;
    }

    /// <summary>
    /// The asset key of a game prototype name: the name without its "Detail_{digits}_" prefix. A name that does not
    /// start that way is used whole.
    /// </summary>
    public static string KeyOf(string prototypeName)
    {
        if (prototypeName == null) return "";

        var start = KeyStart(prototypeName);
        return start == 0 ? prototypeName : prototypeName.Substring(start);
    }

    /// <summary>True when the terrain has at least one of the game's prototypes of <paramref name="asset"/>. It does not allocate.</summary>
    public static bool HasStockOn(Terrain terrain, StockAsset asset)
    {
        for (var i = 0; i < Prototypes.Count; i++)
        {
            var entry = Prototypes[i];
            if (entry.Asset == asset && entry.Manager != null && entry.Manager.terrain == terrain) return true;
        }

        return false;
    }

    /// <summary>
    /// Appends the additions layer cells one stamp would change. Painting: cells the brush picks (by
    /// <see cref="DetailBrush.Coverage"/>, weighted by hardness, with the same per-cell hash as
    /// <see cref="DetailPainter.CollectEdits"/>) are raised to <see cref="DetailBrush.Count"/>, capped at the asset's
    /// <see cref="Detail.MaxCount"/>. Erasing (<see cref="DetailBrush.Erase"/>): cells the eraser picks (with its own
    /// hash) are set to 0. Changes nothing. Free of allocations after the first call for a terrain and asset (which
    /// reads the files), so it can be called every frame. Edits are layer cells laid out as [z * resolution + x] at the
    /// terrain's detail resolution, holding instance counts.
    /// </summary>
    /// <returns>
    /// False, with nothing appended, when the terrain has no stock prototype of this asset (warned about once per
    /// terrain and asset), or its managers are not ready. True otherwise, even when the stamp changes no cells.
    /// </returns>
    public static bool CollectAdditionEdits(Terrain terrain, Vector3 worldPoint, StockAsset asset, DetailBrush brush, List<DetailCellEdit> edits)
    {
        if (terrain == null || asset == null) return false;
        if (!IsPaintable(terrain, asset)) return false;
        if (brush.Radius <= 0f) return true;

        var cells = GetLayers(terrain, asset).Additions;
        var count = Mathf.Min(brush.Count, asset.Detail.MaxCount);
        var resolution = terrain.terrainData.detailResolution;
        var brushCells = new DetailBrushCells(terrain, worldPoint, brush.Radius, brush.Hardness, resolution);

        while (brushCells.Next())
        {
            var current = cells == null ? 0 : cells[brushCells.Index];
            int target;

            if (brush.Erase)
            {
                if (current == 0 || !DetailPainter.ErasePicks(brushCells.X, brushCells.Z, brushCells.Weight)) continue;
                target = 0;
            }
            else
            {
                if (!DetailPainter.PaintPicks(brushCells.X, brushCells.Z, brush.Coverage, brushCells.Weight)) continue;
                target = Mathf.Max(current, count);
            }

            if (target == current) continue;

            edits.Add(new DetailCellEdit { Index = brushCells.Index, Before = current, After = target });
        }

        return true;
    }

    /// <summary>
    /// Writes After (or Before, last to first, when <paramref name="undo"/> is true) into the terrain's additions layer
    /// of the asset, then brings every one of the game's prototypes of this asset on both of the terrain's managers in
    /// line with the layers for the touched area (see the class summary), and writes the mask channel of the primary
    /// prototypes that use the mask, creating the terrain's mask copy when this is its first addition. Marks the layer
    /// as unsaved in <see cref="DetailDensityStore"/>. A manager that is still loading or rebuilding keeps its counts.
    /// </summary>
    public static void ApplyAdditions(Terrain terrain, StockAsset asset, List<DetailCellEdit> edits, bool undo)
    {
        if (terrain == null || asset == null || edits.Count == 0) return;

        var resolution = terrain.terrainData.detailResolution;
        var layers = GetLayers(terrain, asset);
        layers.Additions ??= new int[resolution * resolution];

        var touched = WriteEdits(layers.Additions, resolution, edits, undo, out var minX, out var minZ, out var maxX, out var maxZ);

        DetailDensityStore.MarkAdditionsDirty(terrain, asset);

        if (touched) Rederive(terrain, asset, layers, minX, minZ, maxX, maxZ, true);
    }

    /// <summary>
    /// Appends the removal layer cells one stamp would change. erase: unset cells inside the brush become removed.
    /// restore: removed cells become unset. Uses the brush radius and hardness like DetailPainter's eraser
    /// (hardness-weighted per-cell hash); <see cref="DetailBrush.Count"/>, <see cref="DetailBrush.Coverage"/> and
    /// <see cref="DetailBrush.Erase"/> are ignored. Changes nothing. Free of allocations after the first call for a
    /// terrain and asset (which reads the files), so it can be called every frame. Edits are layer cells laid out as
    /// [z * resolution + x] at the terrain's detail resolution, with 1 = removed.
    /// </summary>
    /// <returns>
    /// False, with nothing appended, when the terrain has no stock prototype of this asset (warned about once per
    /// terrain and asset), or its managers are not ready. True otherwise, even when the stamp changes no cells.
    /// </returns>
    public static bool CollectRemovalEdits(
        Terrain terrain, Vector3 worldPoint, StockAsset asset, DetailBrush brush, bool restore, List<DetailCellEdit> edits
    )
    {
        if (terrain == null || asset == null) return false;
        if (!IsPaintable(terrain, asset)) return false;

        var cells = GetLayers(terrain, asset).Removed;

        // Nothing is removed yet, so there is nothing to restore
        if (cells == null && restore) return true;

        var resolution = terrain.terrainData.detailResolution;
        var brushCells = new DetailBrushCells(terrain, worldPoint, brush.Radius, brush.Hardness, resolution);

        while (brushCells.Next())
        {
            var current = cells == null ? 0 : cells[brushCells.Index];
            if (restore ? current == 0 : current != 0) continue;
            if (!DetailPainter.ErasePicks(brushCells.X, brushCells.Z, brushCells.Weight)) continue;

            edits.Add(new DetailCellEdit { Index = brushCells.Index, Before = current, After = restore ? 0 : 1 });
        }

        return true;
    }

    /// <summary>
    /// Writes After (or Before, last to first, when <paramref name="undo"/> is true) into the terrain's removal layer
    /// of the asset, then brings every one of the game's prototypes of this asset on both of the terrain's managers in
    /// line with the layers for the touched area (see the class summary), so additions painted there stay. The mask is
    /// left as it is. Marks the layer as unsaved in <see cref="DetailDensityStore"/>. A manager that is still loading or
    /// rebuilding keeps its counts.
    /// </summary>
    public static void ApplyRemoval(Terrain terrain, StockAsset asset, List<DetailCellEdit> edits, bool undo)
    {
        if (terrain == null || asset == null || edits.Count == 0) return;

        var resolution = terrain.terrainData.detailResolution;
        var layers = GetLayers(terrain, asset);
        layers.Removed ??= new int[resolution * resolution];

        var touched = WriteEdits(layers.Removed, resolution, edits, undo, out var minX, out var minZ, out var maxX, out var maxZ);

        DetailDensityStore.MarkRemovalDirty(terrain, asset);

        if (touched) Rederive(terrain, asset, layers, minX, minZ, maxX, maxZ, false);
    }

    /// <summary>
    /// The removal layer of <paramref name="asset"/> on <paramref name="terrain"/> (terrain detail resolution x
    /// resolution cells, 1 = removed) and its resolution. Returns null when nothing is removed there and there is no
    /// file.
    /// </summary>
    internal static int[] FindRemovalMask(Terrain terrain, StockAsset asset, out int resolution)
    {
        resolution = terrain.terrainData.detailResolution;
        return GetLayers(terrain, asset).Removed;
    }

    /// <summary>
    /// The additions layer of <paramref name="asset"/> on <paramref name="terrain"/> (terrain detail resolution x
    /// resolution instance counts) and its resolution. Returns null when nothing was painted there and there is no
    /// file.
    /// </summary>
    internal static int[] FindAdditions(Terrain terrain, StockAsset asset, out int resolution)
    {
        resolution = terrain.terrainData.detailResolution;
        return GetLayers(terrain, asset).Additions;
    }

    /// <summary>
    /// Gives every game prototype of the manager whose asset has additions or removals on the manager's terrain a
    /// working copy of its density map derived from the layers (see the class summary), and, when a primary prototype
    /// that uses the mask has additions, replaces the terrain settings' mask array by an uncompressed copy with those
    /// additions written into it. Has to run before the manager's OnEnable reads the maps and builds its materials, so
    /// the manager itself needs no re-pointing. A prototype that kept a working copy from earlier but has no layers any
    /// more gets the game's map back. Logs one line for the manager when anything was applied.
    /// </summary>
    internal static void ApplyLayersAtLoad(GPUInstancerDetailManager manager)
    {
        var start = System.Diagnostics.Stopwatch.GetTimestamp();
        var terrain = manager.terrain;
        var layerResolution = terrain.terrainData.detailResolution;
        var derived = 0;
        var withAdditions = 0;
        var withRemovals = 0;
        var restored = 0;
        var channels = 0;

        MaskCopy copy = null;
        var copyFailed = false;

        for (var i = 0; i < Prototypes.Count; i++)
        {
            var entry = Prototypes[i];
            if (entry.Manager != manager) continue;

            var prototype = entry.Prototype;
            var hasOriginal = Originals.TryGetValue(prototype, out var original);
            if (!hasOriginal) original = prototype.cachedDensityMapForInstance;
            if (original == null || original.Length == 0) continue;

            var layers = GetLayers(terrain, entry.Asset);
            var adds = entry.Primary && AnySet(layers.Additions);
            var removes = AnySet(layers.Removed);

            if (!adds && !removes)
            {
                if (!hasOriginal) continue;

                prototype.cachedDensityMapForInstance = original;
                Originals.Remove(prototype);
                restored++;
                continue;
            }

            PrepareAdditions(entry);
            var resolution = entry.Resolution;
            if (resolution == 0) continue;

            var working = new int[original.Length];
            for (var z = 0; z < resolution; z++)
            {
                for (var x = 0; x < resolution; x++)
                {
                    working[z * resolution + x] = TargetCount(entry, original, layers, layerResolution, x, z);
                }
            }

            Originals[prototype] = original;
            prototype.cachedDensityMapForInstance = working;
            derived++;
            if (adds) withAdditions++;
            if (removes) withRemovals++;

            if (!adds || !IsMasked(prototype)) continue;

            if (copy == null && !copyFailed)
            {
                copy = EnsureCopy(manager);
                copyFailed = copy == null;
            }

            // A manager sharing these settings wrote this channel already, for every manager drawing with it
            if (copy == null || copy.Channels.Contains((entry.Asset, prototype.densityMapIndex, ChannelOf(prototype.densityChanelMask)))) continue;

            // Outside the additions' box the fresh channel already holds the game's original values
            if (!AdditionsPixelBox(terrain, entry.Asset, layers.Additions, copy.Copy.width, copy.Copy.height, out var minX, out var minZ, out var maxX, out var maxZ))
            {
                continue;
            }

            if (WriteMaskChannel(copy, entry, layers, minX, minZ, maxX, maxZ)) channels++;
        }

        if (channels > 0) Upload(copy);

        if (derived + restored == 0) return;

        Plugin.Log.LogInfo(
            $"Game details on {manager.name} (terrain '{terrain.name}'): {derived} prototype(s) given working counts " +
            $"({withAdditions} with additions, {withRemovals} with removals), {restored} given the game's counts back, " +
            $"{channels} mask channel(s) written, {ElapsedMs(start):F1} ms"
        );
    }

    private static StockAsset FindOrAddAsset(GPUInstancerDetailPrototype prototype)
    {
        var key = KeyOf(prototype.name);
        var asset = Find(key);

        if (asset == null)
        {
            asset = new StockAsset { Key = key, DisplayName = DisplayNameOf(key), Detail = new Detail(key) { IsStock = true } };
            Assets.Add(asset);
        }

        // For the editor's previews. Refreshed when the objects of an earlier load are gone.
        if (prototype.usePrototypeMesh)
        {
            if (asset.Detail.Prefab == null) asset.Detail.Prefab = prototype.prefabObject;
        }
        else if (asset.Detail.Texture == null)
        {
            asset.Detail.Texture = prototype.prototypeTexture;
        }

        return asset;
    }

    /// <summary>The key without a trailing "_" and six characters. A key that does not end that way is used whole.</summary>
    private static string DisplayNameOf(string key)
    {
        var underscore = key.LastIndexOf('_');
        if (underscore <= 0 || key.Length - underscore - 1 != HashLength) return key;

        return key.Substring(0, underscore);
    }

    /// <summary>Index at which the key starts in a game prototype name ("Detail_{digits}_..."), or 0 when the name does not start that way.</summary>
    private static int KeyStart(string name)
    {
        if (!name.StartsWith(StockPrefix, System.StringComparison.Ordinal)) return 0;

        var i = StockPrefix.Length;
        while (i < name.Length && char.IsDigit(name[i])) i++;

        if (i == StockPrefix.Length || i >= name.Length - 1 || name[i] != '_') return 0;

        return i + 1;
    }

    private static bool IsSceneManager(GPUInstancerDetailManager manager)
    {
        return manager != null && manager.gameObject.scene.IsValid() && manager.terrain != null && manager.prototypeList != null;
    }

    private static bool IsRecorded(GPUInstancerDetailManager manager, GPUInstancerDetailPrototype prototype)
    {
        for (var i = 0; i < Prototypes.Count; i++)
        {
            if (Prototypes[i].Manager == manager && Prototypes[i].Prototype == prototype) return true;
        }

        return false;
    }

    /// <summary>True when a prototype of <paramref name="asset"/> on <paramref name="manager"/> has been recorded.</summary>
    private static bool HasEntry(GPUInstancerDetailManager manager, StockAsset asset)
    {
        for (var i = 0; i < Prototypes.Count; i++)
        {
            if (Prototypes[i].Manager == manager && Prototypes[i].Asset == asset) return true;
        }

        return false;
    }

    /// <summary>
    /// True when the terrain has a stock prototype of the asset and every manager holding one is ready. Warns once per
    /// terrain and asset when it has none.
    /// </summary>
    private static bool IsPaintable(Terrain terrain, StockAsset asset)
    {
        var found = false;
        for (var i = 0; i < Prototypes.Count; i++)
        {
            var entry = Prototypes[i];
            if (entry.Asset != asset || entry.Manager == null || entry.Manager.terrain != terrain) continue;

            if (!DetailPainter.IsReady(entry.Manager)) return false;
            found = true;
        }

        if (!found && MissingLogged.Add((terrain, asset))) Plugin.Log.LogWarning($"Terrain '{terrain.name}' has none of the game's '{asset.Key}'");

        return found;
    }

    /// <summary>The terrain's layers of the asset, created (and read from their files) on first use.</summary>
    private static StockLayers GetLayers(Terrain terrain, StockAsset asset)
    {
        for (var i = 0; i < Layers.Count; i++)
        {
            var layers = Layers[i];
            if (layers.Terrain == terrain && layers.Asset == asset) return layers;
        }

        var resolution = terrain.terrainData.detailResolution;
        var loaded = new StockLayers
        {
            Terrain = terrain,
            Asset = asset,
            Additions = DetailDensityStore.HasFile(terrain, asset.Detail) ? DetailDensityStore.Load(terrain, asset.Detail, resolution) : null,
            Removed = DetailDensityStore.LoadRemovalMask(terrain, asset.Key, resolution)
        };
        Layers.Add(loaded);

        return loaded;
    }

    /// <summary>
    /// Writes the edits into a layer and returns the bounds of the cells written. Returns false, with the bounds
    /// meaningless, when no edit was inside the layer.
    /// </summary>
    private static bool WriteEdits(
        int[] cells, int resolution, List<DetailCellEdit> edits, bool undo, out int minX, out int minZ, out int maxX, out int maxZ
    )
    {
        minX = resolution;
        minZ = resolution;
        maxX = -1;
        maxZ = -1;

        for (var i = 0; i < edits.Count; i++)
        {
            var edit = undo ? edits[edits.Count - 1 - i] : edits[i];
            if (edit.Index < 0 || edit.Index >= cells.Length) continue;

            cells[edit.Index] = undo ? edit.Before : edit.After;

            var x = edit.Index % resolution;
            var z = edit.Index / resolution;
            if (x < minX) minX = x;
            if (x > maxX) maxX = x;
            if (z < minZ) minZ = z;
            if (z > maxZ) maxZ = z;
        }

        return maxX >= 0;
    }

    /// <summary>
    /// Brings every game prototype of the asset on the terrain's managers in line with the layers around the touched
    /// layer area, through <see cref="DetailPainter.ApplyOnManager"/>. The prototype cells re-derived are the ones the
    /// touched layer cells belong to, with one more on each side. With <paramref name="masks"/> true, a primary
    /// prototype that uses the mask and had a cell's added count change between 0 and more also gets its mask channel
    /// rewritten over the whole footprint of the re-derived cells, creating the mask copy when there is none yet, and
    /// the copy is uploaded. A manager whose counts end up the same is left alone.
    /// </summary>
    private static void Rederive(
        Terrain terrain, StockAsset asset, StockLayers layers, int minX, int minZ, int maxX, int maxZ, bool masks
    )
    {
        var layerResolution = terrain.terrainData.detailResolution;

        for (var i = 0; i < Prototypes.Count; i++)
        {
            var entry = Prototypes[i];
            if (entry.Asset != asset || entry.Manager == null || entry.Manager.terrain != terrain) continue;

            var manager = entry.Manager;
            var prototype = entry.Prototype;

            var index = DetailInjector.IndexOf(manager, prototype);
            if (index < 0) continue;

            var original = EnsureWorkingCopy(prototype);
            if (original == null) continue;

            PrepareAdditions(entry);
            var resolution = entry.Resolution;
            if (resolution == 0 || original.Length != resolution * resolution) continue;

            // The prototype cells the touched layer cells belong to, one extra on each side for rounding
            var fromX = Mathf.Max(0, CentreCell(minX, layerResolution, resolution) - 1);
            var toX = Mathf.Min(resolution - 1, CentreCell(maxX, layerResolution, resolution) + 1);
            var fromZ = Mathf.Max(0, CentreCell(minZ, layerResolution, resolution) - 1);
            var toZ = Mathf.Min(resolution - 1, CentreCell(maxZ, layerResolution, resolution) + 1);

            var flipped = CollectPrototypeEdits(entry, original, layers, layerResolution, fromX, fromZ, toX, toZ);
            if (PrototypeEdits.Count > 0) DetailPainter.ApplyOnManager(manager, index, PrototypeEdits, false);

            if (!masks || !flipped || !entry.Primary || layers.Additions == null || !IsMasked(prototype)) continue;

            var copy = FindCopy(manager.terrainSettings) ?? EnsureCopy(manager);
            if (copy == null) continue;

            // The mask pixels whose centre falls into the re-derived prototype cells
            var width = copy.Copy.width;
            var height = copy.Copy.height;
            var pixelFromX = FirstFineCell(fromX, resolution, width);
            var pixelToX = FirstFineCell(toX + 1, resolution, width) - 1;
            var pixelFromZ = FirstFineCell(fromZ, resolution, height);
            var pixelToZ = FirstFineCell(toZ + 1, resolution, height) - 1;

            if (WriteMaskChannel(copy, entry, layers, pixelFromX, pixelFromZ, pixelToX, pixelToZ)) Upload(copy);
        }
    }

    /// <summary>
    /// The count a prototype's cell should hold: 0 where the removal layer is set at the cell's centre, else the game's
    /// count, plus the cell's added count (<see cref="AddedCount"/>) when the prototype is the asset's primary one on
    /// its manager. <see cref="PrepareAdditions"/> has to have run for the entry.
    /// </summary>
    private static int TargetCount(StockPrototype entry, int[] original, StockLayers layers, int layerResolution, int x, int z)
    {
        return BaseCount(entry, original, layers, layerResolution, x, z) + EntryAdded(entry, layers, layerResolution, x, z);
    }

    /// <summary>The game's count of a prototype's cell, or 0 where the removal layer is set at the cell's centre.</summary>
    private static int BaseCount(StockPrototype entry, int[] original, StockLayers layers, int layerResolution, int x, int z)
    {
        var resolution = entry.Resolution;
        var removed = layers.Removed;

        if (removed != null)
        {
            var layerX = CentreCell(x, resolution, layerResolution);
            var layerZ = CentreCell(z, resolution, layerResolution);
            if (removed[layerZ * layerResolution + layerX] != 0) return 0;
        }

        return original[z * resolution + x];
    }

    /// <summary>The added count of a prototype's cell, or 0 when the prototype is not the asset's primary one or nothing was painted.</summary>
    private static int EntryAdded(StockPrototype entry, StockLayers layers, int layerResolution, int x, int z)
    {
        if (!entry.Primary || layers.Additions == null) return 0;

        return AddedCount(layers.Additions, layerResolution, entry.Resolution, entry.AddScale, x, z);
    }

    /// <summary>
    /// The added count of cell (x, z) of a prototype grid with <paramref name="resolution"/> cells along each side: the
    /// sum of the layer cells whose centre falls into it, multiplied by <paramref name="scale"/> and rounded when the
    /// scale is not 1. The sum is not capped.
    /// </summary>
    private static int AddedCount(int[] additions, int layerResolution, int resolution, float scale, int x, int z)
    {
        var fromX = FirstFineCell(x, resolution, layerResolution);
        var toX = FirstFineCell(x + 1, resolution, layerResolution);
        var fromZ = FirstFineCell(z, resolution, layerResolution);
        var toZ = FirstFineCell(z + 1, resolution, layerResolution);

        var sum = 0;
        for (var layerZ = fromZ; layerZ < toZ; layerZ++)
        {
            var row = layerZ * layerResolution;
            for (var layerX = fromX; layerX < toX; layerX++)
            {
                sum += additions[row + layerX];
            }
        }

        return scale == 1f ? sum : Mathf.RoundToInt(sum * scale);
    }

    /// <summary>
    /// Sets the entry's <see cref="StockPrototype.Resolution"/> and <see cref="StockPrototype.AddScale"/>. The scale is 1
    /// on a main camera manager. On a scope manager it is (its resolution / the resolution of the asset's primary
    /// prototype on the terrain's main manager)^2, so the scope view keeps the share of instances the game gives it.
    /// When the main manager has no prototype of the asset, the ratio of the two managers' detail densities is used,
    /// and 1 when the terrain has no main manager.
    /// </summary>
    private static void PrepareAdditions(StockPrototype entry)
    {
        entry.Resolution = ResolutionOf(entry.Prototype);
        entry.AddScale = 1f;

        var manager = entry.Manager;
        if (!manager.IsOptic || entry.Resolution == 0) return;

        GPUInstancerDetailManager main = null;
        for (var i = 0; i < Prototypes.Count; i++)
        {
            var other = Prototypes[i];
            if (other.Manager == null || other.Manager.IsOptic || other.Manager.terrain != manager.terrain) continue;

            if (main == null) main = other.Manager;
            if (other.Asset != entry.Asset || !other.Primary) continue;

            var mainResolution = ResolutionOf(other.Prototype);
            if (mainResolution == 0) continue;

            var ratio = entry.Resolution / (float)mainResolution;
            entry.AddScale = ratio * ratio;
            return;
        }

        if (main == null || main.terrainSettings == null || manager.terrainSettings == null) return;
        if (main.terrainSettings.detailDensity <= 0f) return;

        entry.AddScale = manager.terrainSettings.detailDensity / main.terrainSettings.detailDensity;
    }

    /// <summary>Cells along one side of the game's density map of the prototype, or 0 when it has none or it is not square.</summary>
    private static int ResolutionOf(GPUInstancerDetailPrototype prototype)
    {
        if (!Originals.TryGetValue(prototype, out var map)) map = prototype.cachedDensityMapForInstance;
        if (map == null || map.Length == 0) return 0;

        var resolution = Mathf.RoundToInt(Mathf.Sqrt(map.Length));
        return resolution * resolution == map.Length ? resolution : 0;
    }

    /// <summary>
    /// Returns the game's original density map of the prototype, giving the prototype a working copy of it first when
    /// it does not have one yet. Returns null when the prototype has no density map.
    /// </summary>
    private static int[] EnsureWorkingCopy(GPUInstancerDetailPrototype prototype)
    {
        if (Originals.TryGetValue(prototype, out var original)) return original;

        original = prototype.cachedDensityMapForInstance;
        if (original == null || original.Length == 0) return null;

        Originals[prototype] = original;
        prototype.cachedDensityMapForInstance = (int[])original.Clone();

        return original;
    }

    /// <summary>
    /// Fills <see cref="PrototypeEdits"/> with the cells of the given box of a prototype's working density map whose
    /// count does not match <see cref="TargetCount"/>. Returns true when a cell's added count changed between 0 and
    /// more, which opens or closes its mask footprint. The added count a cell held before is its working count minus
    /// its base count, which holds while the removal layer is unchanged.
    /// </summary>
    private static bool CollectPrototypeEdits(
        StockPrototype entry, int[] original, StockLayers layers, int layerResolution, int fromX, int fromZ, int toX, int toZ
    )
    {
        PrototypeEdits.Clear();

        var working = entry.Prototype.cachedDensityMapForInstance;
        if (working == null || working.Length != original.Length) return false;

        var resolution = entry.Resolution;
        var flipped = false;

        for (var z = fromZ; z <= toZ; z++)
        {
            for (var x = fromX; x <= toX; x++)
            {
                var index = z * resolution + x;
                var baseCount = BaseCount(entry, original, layers, layerResolution, x, z);
                var added = EntryAdded(entry, layers, layerResolution, x, z);
                var target = baseCount + added;
                var current = working[index];
                if (target == current) continue;

                if (current - baseCount > 0 != added > 0) flipped = true;
                PrototypeEdits.Add(new DetailCellEdit { Index = index, Before = current, After = target });
            }
        }

        return flipped;
    }

    /// <summary>True for a texture detail that uses the game's density mask.</summary>
    private static bool IsMasked(GPUInstancerDetailPrototype prototype)
    {
        return !prototype.usePrototypeMesh && prototype.useDensityMask && prototype.densityMapIndex >= 0;
    }

    /// <summary>The mask copy made for <paramref name="settings"/>, or null when there is none.</summary>
    private static MaskCopy FindCopy(GPUInstancerTerrainSettings settings)
    {
        for (var i = 0; i < Copies.Count; i++)
        {
            if (Copies[i].Settings == settings) return Copies[i];
        }

        return null;
    }

    /// <summary>
    /// Returns the mask copy of the manager's terrain settings, making it on the first call: an uncompressed RGBA32
    /// copy of the game's array, uploaded and kept readable, replaces the game's array on the settings, and every
    /// recorded manager using the settings that has built its runtime data already is pointed at it. Returns null, with
    /// a warning, when the settings have no mask array or it is not readable.
    /// </summary>
    private static MaskCopy EnsureCopy(GPUInstancerDetailManager manager)
    {
        var settings = manager.terrainSettings;
        var existing = FindCopy(settings);
        if (existing != null)
        {
            if (!existing.Managers.Contains(manager)) existing.Managers.Add(manager);
            return existing;
        }

        var original = settings != null ? settings.densityMapArray : null;
        if (original == null || !original.isReadable)
        {
            Plugin.Log.LogWarning($"{manager.name} has no readable density mask array, additions of masked grass will not show");
            return null;
        }

        var start = System.Diagnostics.Stopwatch.GetTimestamp();

        var width = original.width;
        var height = original.height;
        var depth = original.depth;

        var copy = new Texture2DArray(width, height, depth, TextureFormat.RGBA32, false, true)
        {
            name = original.name + "_TerrainFX",
            filterMode = original.filterMode,
            wrapMode = original.wrapMode,
            anisoLevel = original.anisoLevel
        };

        for (var slice = 0; slice < depth; slice++)
        {
            copy.SetPixels32(original.GetPixels32(slice, 0), slice, 0);
        }

        copy.Apply(false, false);

        settings.densityMapArray = copy;

        var entry = new MaskCopy
        {
            Settings = settings,
            Original = original,
            Copy = copy,
            Pixels = new Color32[depth][],
            OriginalPixels = new Color32[depth][]
        };
        entry.Managers.Add(manager);

        for (var i = 0; i < Prototypes.Count; i++)
        {
            var other = Prototypes[i].Manager;
            if (other != null && other.terrainSettings == settings && !entry.Managers.Contains(other)) entry.Managers.Add(other);
        }

        Copies.Add(entry);

        var repointed = 0;
        for (var i = 0; i < entry.Managers.Count; i++)
        {
            if (PointAtArray(entry.Managers[i], copy)) repointed++;
        }

        Plugin.Log.LogInfo(
            $"Density mask of terrain '{manager.terrain.name}' replaced by an uncompressed copy ({width}x{height}x{depth}) in " +
            $"{ElapsedMs(start):F1} ms, {repointed} running manager(s) re-pointed"
        );

        return entry;
    }

    /// <summary>
    /// Points a manager that has built its runtime data at <paramref name="array"/>: the game's refresh re-points the
    /// property block of the first renderer of each level of detail, and the mask is then set directly on every
    /// material, property block and shadow material of the prototypes that use the mask. Returns false, changing
    /// nothing, when the manager has no runtime data yet (it reads the array from the settings when it builds them).
    /// </summary>
    private static bool PointAtArray(GPUInstancerDetailManager manager, Texture2DArray array)
    {
        var runtimeDataList = manager.runtimeDataList;
        if (runtimeDataList == null || runtimeDataList.Count == 0) return false;

        GPUInstancerAPI.UpdateDetailInstances(manager, false);

        var property = GPUInstancerUtility._densityMapArray;

        for (var i = 0; i < runtimeDataList.Count; i++)
        {
            var runtimeData = runtimeDataList[i];
            if (runtimeData.prototype is not GPUInstancerDetailPrototype { useDensityMask: true }) continue;

            var lods = runtimeData.instanceLODs;
            for (var l = 0; lods != null && l < lods.Count; l++)
            {
                var renderers = lods[l].renderers;
                for (var r = 0; renderers != null && r < renderers.Count; r++)
                {
                    var renderer = renderers[r];

                    renderer.mpb?.SetTexture(property, array);
                    if (renderer.shadowMPB != null && renderer.shadowMPB.HasTexture(property)) renderer.shadowMPB.SetTexture(property, array);

                    var materials = renderer.materials;
                    for (var m = 0; materials != null && m < materials.Count; m++)
                    {
                        var material = materials[m];
                        if (material != null && material.HasProperty(property)) material.SetTexture(property, array);
                    }
                }
            }

            var shadow = runtimeData.shadowCasterMaterial;
            if (shadow != null && shadow.HasProperty(property)) shadow.SetTexture(property, array);
        }

        return true;
    }

    /// <summary>
    /// Writes the mask channel of the entry's prototype for every mask pixel in the given box: 255 when the prototype
    /// cell its centre falls into has an added count above 0, the game's original value elsewhere. When several
    /// managers of the terrain draw with this copy, a pixel is opened when any of their primary prototypes of the asset
    /// on the same slice and channel has additions there. The other channels are left as they are. The slice's pixels
    /// (and the game's original pixels) are fetched on first use. Nothing is uploaded, see <see cref="Upload"/>. Returns
    /// false, writing nothing, when the copy is no longer readable (warned about once per copy) or the prototype's slice
    /// is outside the array.
    /// </summary>
    private static bool WriteMaskChannel(MaskCopy copy, StockPrototype entry, StockLayers layers, int minX, int minZ, int maxX, int maxZ)
    {
        var texture = copy.Copy;
        var prototype = entry.Prototype;
        var slice = prototype.densityMapIndex;
        if (slice < 0 || slice >= texture.depth || layers.Additions == null) return false;

        if (!texture.isReadable || copy.Original == null)
        {
            if (!copy.UnwritableLogged)
            {
                copy.UnwritableLogged = true;
                Plugin.Log.LogWarning(
                    $"The density mask copy of {copy.Copy.name} cannot be written (set DetailStock.KeepMasksWritable before the " +
                    "map loads), additions of masked grass will not show until the next load"
                );
            }

            return false;
        }

        var channel = ChannelOf(prototype.densityChanelMask);
        var terrain = entry.Manager.terrain;

        // Every primary prototype of the asset on this terrain whose mask is this channel of this copy
        MaskEntries.Clear();
        for (var i = 0; i < Prototypes.Count; i++)
        {
            var other = Prototypes[i];
            if (other.Asset != entry.Asset || !other.Primary || other.Manager == null || other.Manager.terrain != terrain) continue;
            if (other.Manager.terrainSettings != copy.Settings || !IsMasked(other.Prototype)) continue;
            if (other.Prototype.densityMapIndex != slice || ChannelOf(other.Prototype.densityChanelMask) != channel) continue;

            PrepareAdditions(other);
            if (other.Resolution > 0) MaskEntries.Add(other);
        }

        var pixels = copy.Pixels[slice] ??= texture.GetPixels32(slice, 0);
        var originals = copy.OriginalPixels[slice] ??= copy.Original.GetPixels32(slice, 0);

        var width = texture.width;
        var height = texture.height;
        var additions = layers.Additions;
        var layerResolution = terrain.terrainData.detailResolution;

        minX = Mathf.Max(0, minX);
        minZ = Mathf.Max(0, minZ);
        maxX = Mathf.Min(width - 1, maxX);
        maxZ = Mathf.Min(height - 1, maxZ);
        if (maxX < minX || maxZ < minZ) return false;

        var entries = MaskEntries.Count;
        var columns = maxX - minX + 1;
        if (EntryCells.Length < entries * EntryFields) EntryCells = new int[entries * EntryFields];
        if (ColumnCells.Length < entries * columns) ColumnCells = new int[entries * columns];

        // Per entry: the prototype cells the box covers, and where their flags start
        var flagCount = 0;
        for (var e = 0; e < entries; e++)
        {
            var resolution = MaskEntries[e].Resolution;
            var cellFromX = CentreCell(minX, width, resolution);
            var cellFromZ = CentreCell(minZ, height, resolution);
            var cellWidth = CentreCell(maxX, width, resolution) - cellFromX + 1;
            var cellHeight = CentreCell(maxZ, height, resolution) - cellFromZ + 1;

            var data = e * EntryFields;
            EntryCells[data] = cellFromZ;
            EntryCells[data + 1] = cellWidth;
            EntryCells[data + 2] = flagCount;
            flagCount += cellWidth * cellHeight;
        }

        if (CellFlags.Length < flagCount) CellFlags = new bool[flagCount];

        // Each prototype cell's added count is summed once, and each pixel column is mapped to its cell once
        for (var e = 0; e < entries; e++)
        {
            var other = MaskEntries[e];
            var resolution = other.Resolution;
            var data = e * EntryFields;
            var cellFromX = CentreCell(minX, width, resolution);
            var cellFromZ = EntryCells[data];
            var cellWidth = EntryCells[data + 1];
            var flagStart = EntryCells[data + 2];
            var cellToZ = CentreCell(maxZ, height, resolution);

            for (var cellZ = cellFromZ; cellZ <= cellToZ; cellZ++)
            {
                var flagRow = flagStart + (cellZ - cellFromZ) * cellWidth;
                for (var cellX = 0; cellX < cellWidth; cellX++)
                {
                    var added = AddedCount(additions, layerResolution, resolution, other.AddScale, cellFromX + cellX, cellZ);
                    CellFlags[flagRow + cellX] = added > 0;
                }
            }

            var columnStart = e * columns;
            for (var x = minX; x <= maxX; x++)
            {
                ColumnCells[columnStart + x - minX] = CentreCell(x, width, resolution) - cellFromX;
            }
        }

        for (var z = minZ; z <= maxZ; z++)
        {
            // The first flag of the cell row this pixel row falls into, per entry
            for (var e = 0; e < entries; e++)
            {
                var data = e * EntryFields;
                var cellZ = CentreCell(z, height, MaskEntries[e].Resolution);
                EntryCells[data + 3] = EntryCells[data + 2] + (cellZ - EntryCells[data]) * EntryCells[data + 1];
            }

            for (var x = minX; x <= maxX; x++)
            {
                var open = false;
                for (var e = 0; e < entries && !open; e++)
                {
                    open = CellFlags[EntryCells[e * EntryFields + 3] + ColumnCells[e * columns + x - minX]];
                }

                var i = z * width + x;
                pixels[i] = WithChannel(pixels[i], channel, open ? (byte)255 : ChannelValue(originals[i], channel));
            }
        }

        texture.SetPixels32(pixels, slice, 0);

        if (!copy.Channels.Contains((entry.Asset, slice, channel))) copy.Channels.Add((entry.Asset, slice, channel));

        return true;
    }

    /// <summary>
    /// Uploads the copy to the GPU. When <see cref="KeepMasksWritable"/> is false the CPU copy is freed and the pixel
    /// arrays are dropped, so it cannot be written again. The time of the first three uploads of each copy is logged.
    /// </summary>
    private static void Upload(MaskCopy copy)
    {
        var start = System.Diagnostics.Stopwatch.GetTimestamp();

        copy.Copy.Apply(false, !KeepMasksWritable);

        if (!KeepMasksWritable)
        {
            for (var i = 0; i < copy.Pixels.Length; i++)
            {
                copy.Pixels[i] = null;
                copy.OriginalPixels[i] = null;
            }
        }

        copy.Uploads++;
        if (copy.Uploads <= 3) Plugin.Log.LogInfo($"Upload {copy.Uploads} of density mask {copy.Copy.name} took {ElapsedMs(start):F1} ms");
    }

    /// <summary>Puts the game's array back on the settings when they still hold the copy, and destroys the copy.</summary>
    private static void Restore(MaskCopy copy)
    {
        if (copy.Settings != null && copy.Settings.densityMapArray == copy.Copy) copy.Settings.densityMapArray = copy.Original;
        if (copy.Copy != null) Object.Destroy(copy.Copy);
    }

    private static double ElapsedMs(long start)
    {
        return (System.Diagnostics.Stopwatch.GetTimestamp() - start) * 1000.0 / System.Diagnostics.Stopwatch.Frequency;
    }

    /// <summary>
    /// The mask pixel box outside which the additions of <paramref name="asset"/> on <paramref name="terrain"/> cannot
    /// open a pixel: the bounds of the non-zero layer cells, widened by one prototype cell of the asset's coarsest
    /// prototype on the terrain and one more layer cell, mapped to pixels. Returns false, with an empty box, when the
    /// layer holds no additions.
    /// </summary>
    private static bool AdditionsPixelBox(
        Terrain terrain, StockAsset asset, int[] additions, int width, int height, out int minX, out int minZ, out int maxX, out int maxZ
    )
    {
        var layerResolution = terrain.terrainData.detailResolution;
        var fromX = layerResolution;
        var fromZ = layerResolution;
        var toX = -1;
        var toZ = -1;

        for (var z = 0; z < layerResolution; z++)
        {
            var row = z * layerResolution;
            for (var x = 0; x < layerResolution; x++)
            {
                if (additions[row + x] == 0) continue;

                if (x < fromX) fromX = x;
                if (x > toX) toX = x;
                if (z < fromZ) fromZ = z;
                if (z > toZ) toZ = z;
            }
        }

        minX = 0;
        minZ = 0;
        maxX = -1;
        maxZ = -1;
        if (toX < 0) return false;

        var coarsest = layerResolution;
        for (var i = 0; i < Prototypes.Count; i++)
        {
            var entry = Prototypes[i];
            if (entry.Asset != asset || entry.Manager == null || entry.Manager.terrain != terrain) continue;

            var resolution = ResolutionOf(entry.Prototype);
            if (resolution > 0 && resolution < coarsest) coarsest = resolution;
        }

        var margin = (layerResolution + coarsest - 1) / coarsest + 1;
        fromX = Mathf.Max(0, fromX - margin);
        fromZ = Mathf.Max(0, fromZ - margin);
        toX = Mathf.Min(layerResolution - 1, toX + margin);
        toZ = Mathf.Min(layerResolution - 1, toZ + margin);

        minX = fromX * width / layerResolution;
        minZ = fromZ * height / layerResolution;
        maxX = Mathf.Min(width - 1, (toX + 1) * width / layerResolution);
        maxZ = Mathf.Min(height - 1, (toZ + 1) * height / layerResolution);

        return true;
    }

    /// <summary>The cell of a grid with <paramref name="toResolution"/> cells that the centre of cell <paramref name="cell"/> of a grid with <paramref name="resolution"/> cells falls into.</summary>
    private static int CentreCell(int cell, int resolution, int toResolution)
    {
        if (resolution == toResolution) return cell;

        return Mathf.Clamp(Mathf.FloorToInt((cell + 0.5f) / resolution * toResolution), 0, toResolution - 1);
    }

    /// <summary>
    /// The first cell of a fine grid (<paramref name="fineResolution"/> cells) whose centre falls into cell
    /// <paramref name="cell"/> of a coarse grid (<paramref name="resolution"/> cells) or a later one. For the cell after
    /// the last it returns <paramref name="fineResolution"/>, so the fine cells belonging to coarse cell c run from
    /// FirstFineCell(c) to FirstFineCell(c + 1) - 1.
    /// </summary>
    private static int FirstFineCell(int cell, int resolution, int fineResolution)
    {
        if (cell >= resolution) return fineResolution;

        var fine = Mathf.Max(0, cell * fineResolution / resolution - 1);
        while (fine < fineResolution && CentreCell(fine, fineResolution, resolution) < cell) fine++;

        return fine;
    }

    private static bool AnySet(int[] cells)
    {
        if (cells == null) return false;

        for (var i = 0; i < cells.Length; i++)
        {
            if (cells[i] != 0) return true;
        }

        return false;
    }

    /// <summary>The index (0 = R to 3 = A) of the largest component of a one-hot channel mask.</summary>
    private static int ChannelOf(Vector4 mask)
    {
        var channel = 0;
        for (var i = 1; i < 4; i++)
        {
            if (mask[i] > mask[channel]) channel = i;
        }

        return channel;
    }

    private static byte ChannelValue(Color32 pixel, int channel)
    {
        switch (channel)
        {
            case 0: return pixel.r;
            case 1: return pixel.g;
            case 2: return pixel.b;
            default: return pixel.a;
        }
    }

    private static Color32 WithChannel(Color32 pixel, int channel, byte value)
    {
        switch (channel)
        {
            case 0:
                pixel.r = value;
                break;
            case 1:
                pixel.g = value;
                break;
            case 2:
                pixel.b = value;
                break;
            default:
                pixel.a = value;
                break;
        }

        return pixel;
    }
}
