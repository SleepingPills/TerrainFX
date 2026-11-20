using System.Collections.Generic;
using System.IO;
using System.Reflection;
using UnityEngine;

namespace TerrainFX.Details;

/// <summary>
/// Reads and writes the painted density of each detail as one grayscale PNG per terrain and detail, so the maps can
/// also be opened in an image editor. The files of a map live in a folder named after the map, next to
/// TerrainFX.dll: "&lt;plugin folder&gt;\&lt;map id&gt;\&lt;terrain name&gt;.&lt;detail name&gt;.png". The instances
/// added to the game's own assets (<see cref="DetailStock"/>) are stored the same way, named after the asset key and
/// scaled by the <see cref="Detail.DensityScale"/> of the asset's <see cref="StockAsset.Detail"/>.
///
/// Each pixel is the instance count of one density cell multiplied by <see cref="Detail.DensityScale"/>. Image row 0
/// (the bottom row of the picture) is terrain z = 0 and image column 0 is terrain x = 0, so the picture reads as a
/// top-down map with +Z up and +X to the right.
///
/// Where the game's own instances of an asset were removed is stored the same way in
/// "&lt;terrain name&gt;.&lt;asset key&gt;.removed.png": 255 where the game's instances are removed, 0 elsewhere.
///
/// A file only exists while it holds something: saving all zeros deletes it, so "has a file" means "has density"
/// (or "has removals").
/// </summary>
public static class DetailDensityStore
{
    private static readonly string BaseFolder = Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location);

    // Terrain and detail of every density map that was painted since it was last saved
    private static readonly List<(Terrain Terrain, Detail Detail)> Dirty = [];

    // Terrain and asset of every removal mask that was changed since it was last saved
    private static readonly List<(Terrain Terrain, StockAsset Asset)> DirtyRemovals = [];

    // Terrain and asset of every additions layer of a game asset that was changed since it was last saved
    private static readonly List<(Terrain Terrain, StockAsset Asset)> DirtyAdditions = [];

    // Which terrain each file path was handed out for during the current map, to catch two terrains sharing a file
    private static readonly Dictionary<string, Terrain> PathOwners = new();

    /// <summary>
    /// Id of the map whose files are read and written, for example "bigmap". It is null until the first detail
    /// manager of a raid has been set up.
    /// </summary>
    public static string MapId { get; private set; }

    /// <summary>
    /// The number of density maps, additions layers of game assets and removal masks that were changed since they
    /// were last saved.
    /// </summary>
    public static int DirtyCount => Dirty.Count + DirtyRemovals.Count + DirtyAdditions.Count;

    /// <summary>
    /// Sets the map whose files are read and written. Unsaved changes whose terrain no longer exists (because it was
    /// destroyed with the previous raid) are always dropped, which also covers playing the same map twice in a row.
    /// When the id differs from the current one, the whole list of unsaved changes is cleared, because the terrains
    /// it refers to belong to the previous map.
    /// </summary>
    /// <returns>True when the map id changed.</returns>
    public static bool SetMap(string mapId)
    {
        for (var i = Dirty.Count - 1; i >= 0; i--)
        {
            if (Dirty[i].Terrain == null) Dirty.RemoveAt(i);
        }

        for (var i = DirtyRemovals.Count - 1; i >= 0; i--)
        {
            if (DirtyRemovals[i].Terrain == null) DirtyRemovals.RemoveAt(i);
        }

        for (var i = DirtyAdditions.Count - 1; i >= 0; i--)
        {
            if (DirtyAdditions[i].Terrain == null) DirtyAdditions.RemoveAt(i);
        }

        if (mapId == MapId) return false;

        MapId = mapId;
        Dirty.Clear();
        DirtyRemovals.Clear();
        DirtyAdditions.Clear();
        PathOwners.Clear();

        Plugin.Log.LogInfo($"Detail density maps are stored in {Path.Combine(BaseFolder, MapId)}");
        return true;
    }

    /// <summary>True when a density file of <paramref name="detail"/> on <paramref name="terrain"/> exists, which means it has saved density there.</summary>
    public static bool HasFile(Terrain terrain, Detail detail)
    {
        return File.Exists(FilePath(terrain, detail.Name));
    }

    /// <summary>
    /// Reads the painted density of <paramref name="detail"/> on <paramref name="terrain"/>. Returns
    /// <paramref name="resolution"/> x <paramref name="resolution"/> instance counts laid out as [z * resolution + x].
    /// Every count is 0 when nothing has been saved for this terrain and detail yet, or when the file cannot be read.
    /// An image of a different size is resampled to the resolution with a warning.
    /// </summary>
    public static int[] Load(Terrain terrain, Detail detail, int resolution)
    {
        var cells = new int[resolution * resolution];
        var path = FilePath(terrain, detail.Name);

        var values = ReadGray(path, resolution);
        if (values == null) return cells;

        var scale = Mathf.Clamp(detail.DensityScale, 1, 255);
        long total = 0;

        for (var i = 0; i < cells.Length; i++)
        {
            var count = Mathf.RoundToInt(values[i] / (float)scale);
            cells[i] = count;
            total += count;
        }

        Plugin.Log.LogInfo($"Loaded the density of '{detail.Name}' on terrain '{terrain.name}' from {path}: {total} instances");

        return cells;
    }

    /// <summary>
    /// Writes the density of <paramref name="detail"/> on <paramref name="terrain"/> to its PNG file as 8-bit
    /// grayscale, creating the map folder when it is missing. <paramref name="cells"/> are instance counts laid out
    /// as [z * resolution + x]; a count too large for <see cref="Detail.DensityScale"/> is stored as white (255).
    /// When every count is 0 no picture is written: the file is deleted if it exists, so that the detail is not put on
    /// this terrain at the next load.
    /// </summary>
    public static void Save(Terrain terrain, Detail detail, int[] cells, int resolution)
    {
        var path = FilePath(terrain, detail.Name);
        var scale = Mathf.Clamp(detail.DensityScale, 1, 255);

        var bytes = new byte[resolution * resolution];
        for (var i = 0; i < bytes.Length; i++)
        {
            bytes[i] = (byte)Mathf.Clamp(cells[i] * scale, 0, 255);
        }

        WriteGray(path, bytes, resolution);
    }

    /// <summary>
    /// Reads where the game's own instances of <paramref name="assetKey"/> were removed on <paramref name="terrain"/>:
    /// <paramref name="resolution"/> x <paramref name="resolution"/> cells laid out as [z * resolution + x], 1 where
    /// removed (a pixel of 128 or more) and 0 elsewhere. Returns null when there is no file or it cannot be read.
    /// </summary>
    internal static int[] LoadRemovalMask(Terrain terrain, string assetKey, int resolution)
    {
        var path = RemovalPath(terrain, assetKey);

        var values = ReadGray(path, resolution);
        if (values == null) return null;

        var cells = new int[resolution * resolution];
        long removed = 0;

        for (var i = 0; i < cells.Length; i++)
        {
            if (values[i] < 128) continue;

            cells[i] = 1;
            removed++;
        }

        Plugin.Log.LogInfo($"Loaded the removals of '{assetKey}' on terrain '{terrain.name}' from {path}: {removed} cells");

        return cells;
    }

    /// <summary>
    /// Writes a removal mask (1 = removed) as 8-bit grayscale, 255 where removed. A mask without any removed cell
    /// deletes its file instead.
    /// </summary>
    internal static void SaveRemovalMask(Terrain terrain, string assetKey, int[] cells, int resolution)
    {
        var bytes = new byte[resolution * resolution];
        for (var i = 0; i < bytes.Length; i++)
        {
            bytes[i] = cells[i] != 0 ? (byte)255 : (byte)0;
        }

        WriteGray(RemovalPath(terrain, assetKey), bytes, resolution);
    }

    /// <summary>Records that the density of a detail on a terrain was changed and has not been saved yet.</summary>
    public static void MarkDirty(Terrain terrain, Detail detail)
    {
        if (!Dirty.Contains((terrain, detail))) Dirty.Add((terrain, detail));
    }

    /// <summary>Records that the additions layer of a game asset on a terrain was changed and has not been saved yet.</summary>
    internal static void MarkAdditionsDirty(Terrain terrain, StockAsset asset)
    {
        if (!DirtyAdditions.Contains((terrain, asset))) DirtyAdditions.Add((terrain, asset));
    }

    /// <summary>Records that the removal mask of a game asset on a terrain was changed and has not been saved yet.</summary>
    internal static void MarkRemovalDirty(Terrain terrain, StockAsset asset)
    {
        if (!DirtyRemovals.Contains((terrain, asset))) DirtyRemovals.Add((terrain, asset));
    }

    /// <summary>
    /// Saves every density map, additions layer of a game asset and removal mask that was changed since it was last
    /// saved and clears the list of unsaved changes. Density of custom details is taken from the terrain's main camera
    /// detail manager, additions and removals from <see cref="DetailStock"/>. A map whose terrain, manager or detail no
    /// longer exists is skipped with a warning. Maps that are all zero delete their file.
    /// </summary>
    /// <returns>The number of files written or deleted.</returns>
    public static int SaveDirty()
    {
        var written = 0;

        for (var i = 0; i < Dirty.Count; i++)
        {
            var (terrain, detail) = Dirty[i];
            var prototype = terrain == null ? null : DetailInjector.FindPrototype(terrain, false, detail, out _, out _);
            var cells = prototype == null ? null : prototype.cachedDensityMapForInstance;

            if (cells == null)
            {
                Plugin.Log.LogWarning($"Cannot save '{detail.Name}': its terrain or detail manager no longer exists");
                continue;
            }

            var resolution = Mathf.RoundToInt(Mathf.Sqrt(cells.Length));
            if (resolution * resolution != cells.Length)
            {
                Plugin.Log.LogError($"Cannot save '{detail.Name}' on terrain '{terrain.name}': its density map is not square");
                continue;
            }

            try
            {
                Save(terrain, detail, cells, resolution);
                written++;
            }
            catch (IOException e)
            {
                Plugin.Log.LogError($"Could not save '{detail.Name}' on terrain '{terrain.name}': {e.Message}");
            }
        }

        for (var i = 0; i < DirtyRemovals.Count; i++)
        {
            var (terrain, asset) = DirtyRemovals[i];
            var cells = terrain == null ? null : DetailStock.FindRemovalMask(terrain, asset, out _);

            if (cells == null)
            {
                Plugin.Log.LogWarning($"Cannot save the removals of '{asset.Key}': its terrain no longer exists");
                continue;
            }

            try
            {
                SaveRemovalMask(terrain, asset.Key, cells, terrain.terrainData.detailResolution);
                written++;
            }
            catch (IOException e)
            {
                Plugin.Log.LogError($"Could not save the removals of '{asset.Key}' on terrain '{terrain.name}': {e.Message}");
            }
        }

        for (var i = 0; i < DirtyAdditions.Count; i++)
        {
            var (terrain, asset) = DirtyAdditions[i];
            var cells = terrain == null ? null : DetailStock.FindAdditions(terrain, asset, out _);

            if (cells == null)
            {
                Plugin.Log.LogWarning($"Cannot save the additions of '{asset.Key}': its terrain no longer exists");
                continue;
            }

            try
            {
                Save(terrain, asset.Detail, cells, terrain.terrainData.detailResolution);
                written++;
            }
            catch (IOException e)
            {
                Plugin.Log.LogError($"Could not save the additions of '{asset.Key}' on terrain '{terrain.name}': {e.Message}");
            }
        }

        Dirty.Clear();
        DirtyRemovals.Clear();
        DirtyAdditions.Clear();

        if (written > 0) Plugin.Log.LogInfo($"Saved {written} detail file(s) to {Path.Combine(BaseFolder, MapId ?? "unknown")}");

        return written;
    }

    /// <summary>
    /// Reads an 8-bit grayscale PNG as <paramref name="resolution"/> x <paramref name="resolution"/> values from 0 to
    /// 255 laid out as [z * resolution + x]. An image of a different size is resampled with a warning. Returns null when
    /// the file does not exist or cannot be read.
    /// </summary>
    private static byte[] ReadGray(string path, int resolution)
    {
        if (!File.Exists(path)) return null;

        var texture = new Texture2D(2, 2, TextureFormat.R8, false);

        try
        {
            if (!texture.LoadImage(File.ReadAllBytes(path)))
            {
                Plugin.Log.LogError($"{path} is not a readable PNG and is ignored");
                return null;
            }

            var width = texture.width;
            var height = texture.height;
            var pixels = texture.GetPixels32();

            // Unity may decode an 8-bit grayscale PNG into Alpha8, which keeps the value in alpha instead of red
            var useAlpha = texture.format == TextureFormat.Alpha8;

            if (width != resolution || height != resolution)
            {
                Plugin.Log.LogWarning($"{path} is {width}x{height} but the terrain's density is {resolution}x{resolution}, the image is resampled");
            }

            var values = new byte[resolution * resolution];

            for (var z = 0; z < resolution; z++)
            {
                var row = z * height / resolution;

                for (var x = 0; x < resolution; x++)
                {
                    var column = x * width / resolution;
                    var pixel = pixels[row * width + column];
                    values[z * resolution + x] = useAlpha ? pixel.a : pixel.r;
                }
            }

            return values;
        }
        catch (IOException e)
        {
            Plugin.Log.LogError($"Could not read {path}: {e.Message}");
            return null;
        }
        finally
        {
            Object.Destroy(texture);
        }
    }

    /// <summary>
    /// Writes <paramref name="values"/> as an 8-bit grayscale PNG, creating the map folder when it is missing. When
    /// every value is 0 the file is deleted instead (if it exists).
    /// </summary>
    private static void WriteGray(string path, byte[] values, int resolution)
    {
        var empty = true;
        for (var i = 0; i < values.Length && empty; i++)
        {
            empty = values[i] == 0;
        }

        if (empty)
        {
            if (File.Exists(path)) File.Delete(path);
            return;
        }

        var texture = new Texture2D(resolution, resolution, TextureFormat.R8, false);

        try
        {
            texture.LoadRawTextureData(values);

            Directory.CreateDirectory(Path.GetDirectoryName(path));
            File.WriteAllBytes(path, texture.EncodeToPNG());
        }
        finally
        {
            Object.Destroy(texture);
        }
    }

    private static string RemovalPath(Terrain terrain, string assetKey)
    {
        return FilePath(terrain, assetKey + ".removed");
    }

    private static string FilePath(Terrain terrain, string detailName)
    {
        var fileName = $"{terrain.name}.{detailName}.png";
        var invalidChars = Path.GetInvalidFileNameChars();
        for (var i = 0; i < invalidChars.Length; i++)
        {
            var invalid = invalidChars[i];
            fileName = fileName.Replace(invalid, '_');
        }

        var path = Path.Combine(BaseFolder, MapId ?? "unknown", fileName);

        // Both detail managers of a terrain ask for the same file, a different terrain asking for it is a problem
        if (PathOwners.TryGetValue(path, out var owner) && owner != null && owner != terrain)
        {
            Plugin.Log.LogError($"Terrains '{owner.name}' and '{terrain.name}' both store '{detailName}' in {path}, their painting overwrites each other");
        }

        PathOwners[path] = terrain;

        return path;
    }
}
