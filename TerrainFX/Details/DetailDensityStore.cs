using System.Collections.Generic;
using System.IO;
using System.Reflection;
using GPUInstancer;
using UnityEngine;

namespace TerrainFX.Details;

/// <summary>
/// Reads and writes the painted density of each custom detail as one grayscale PNG per terrain and detail, so the
/// maps can also be opened in an image editor. The files of a map live in a folder named after the map, next to
/// TerrainFX.dll: "&lt;plugin folder&gt;\&lt;map id&gt;\&lt;terrain name&gt;.&lt;detail name&gt;.png".
///
/// Each pixel is the instance count of one density cell multiplied by <see cref="Detail.DensityScale"/>. Image row 0
/// (the bottom row of the picture) is terrain z = 0 and image column 0 is terrain x = 0, so the picture reads as a
/// top-down map with +Z up and +X to the right.
/// </summary>
public static class DetailDensityStore
{
    private static readonly string BaseFolder = Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location);

    // Terrain and detail name of every density map that was painted since it was last saved
    private static readonly List<(Terrain Terrain, string DetailName)> Dirty = [];

    // Which terrain each file path was handed out for during the current map, to catch two terrains sharing a file
    private static readonly Dictionary<string, Terrain> PathOwners = new();

    /// <summary>
    /// Id of the map whose files are read and written, for example "bigmap". It is null until the first detail
    /// manager of a raid has been set up.
    /// </summary>
    public static string MapId { get; private set; }

    /// <summary>The number of terrain and detail pairs that were painted since they were last saved.</summary>
    public static int DirtyCount => Dirty.Count;

    /// <summary>
    /// Sets the map whose files are read and written. Unsaved changes whose terrain no longer exists (because it was
    /// destroyed with the previous raid) are always dropped, which also covers playing the same map twice in a row.
    /// When the id differs from the current one, the whole list of unsaved changes is cleared, because the terrains
    /// it refers to belong to the previous map.
    /// </summary>
    public static void SetMap(string mapId)
    {
        for (var i = Dirty.Count - 1; i >= 0; i--)
        {
            if (Dirty[i].Terrain == null) Dirty.RemoveAt(i);
        }

        if (mapId == MapId) return;

        MapId = mapId;
        Dirty.Clear();
        PathOwners.Clear();

        Plugin.Log.LogInfo($"Detail density maps are stored in {Path.Combine(BaseFolder, MapId)}");
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

        if (!File.Exists(path)) return cells;

        var texture = new Texture2D(2, 2, TextureFormat.R8, false);

        try
        {
            if (!texture.LoadImage(File.ReadAllBytes(path)))
            {
                Plugin.Log.LogError($"{path} is not a readable PNG, '{detail.Name}' starts out empty on terrain '{terrain.name}'");
                return cells;
            }

            var width = texture.width;
            var height = texture.height;
            var pixels = texture.GetPixels32();
            var scale = Mathf.Clamp(detail.DensityScale, 1, 255);

            // Unity may decode an 8-bit grayscale PNG into Alpha8, which keeps the value in alpha instead of red
            var useAlpha = texture.format == TextureFormat.Alpha8;

            if (width != resolution || height != resolution)
            {
                Plugin.Log.LogWarning($"{path} is {width}x{height} but the terrain's density is {resolution}x{resolution}, the image is resampled");
            }

            long total = 0;

            for (var z = 0; z < resolution; z++)
            {
                var row = z * height / resolution;

                for (var x = 0; x < resolution; x++)
                {
                    var column = x * width / resolution;
                    var pixel = pixels[row * width + column];
                    var count = Mathf.RoundToInt((useAlpha ? pixel.a : pixel.r) / (float)scale);

                    cells[z * resolution + x] = count;
                    total += count;
                }
            }

            Plugin.Log.LogInfo($"Loaded the density of '{detail.Name}' on terrain '{terrain.name}' from {path} (decoded as {texture.format}): {total} instances");
        }
        catch (IOException e)
        {
            Plugin.Log.LogError($"Could not read {path}: {e.Message}");
        }
        finally
        {
            Object.Destroy(texture);
        }

        return cells;
    }

    /// <summary>
    /// Writes the density of <paramref name="detail"/> on <paramref name="terrain"/> to its PNG file as 8-bit
    /// grayscale, creating the map folder when it is missing. <paramref name="cells"/> are instance counts laid out
    /// as [z * resolution + x]; a count too large for <see cref="Detail.DensityScale"/> is stored as white (255).
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

        var texture = new Texture2D(resolution, resolution, TextureFormat.R8, false);

        try
        {
            texture.LoadRawTextureData(bytes);

            Directory.CreateDirectory(Path.GetDirectoryName(path));
            File.WriteAllBytes(path, texture.EncodeToPNG());
        }
        finally
        {
            Object.Destroy(texture);
        }
    }

    /// <summary>Records that the density of a detail on a terrain was changed and has not been saved yet.</summary>
    public static void MarkDirty(Terrain terrain, string detailName)
    {
        if (!Dirty.Contains((terrain, detailName))) Dirty.Add((terrain, detailName));
    }

    /// <summary>
    /// Saves every density map that was painted since it was last saved, taking the counts from the terrain's main
    /// camera detail manager, and clears the list of unsaved changes. A pair whose terrain, manager or detail no
    /// longer exists is skipped with a warning.
    /// </summary>
    /// <returns>The number of files written.</returns>
    public static int SaveDirty()
    {
        var written = 0;

        for (var i = 0; i < Dirty.Count; i++)
        {
            var (terrain, detailName) = Dirty[i];
            var detail = DetailRegistry.Find(detailName);
            var prototype = terrain == null ? null : FindMainPrototype(terrain, detailName);
            var cells = prototype == null ? null : prototype.cachedDensityMapForInstance;

            if (detail == null || cells == null)
            {
                Plugin.Log.LogWarning($"Cannot save '{detailName}': its terrain, detail manager or detail no longer exists");
                continue;
            }

            var resolution = Mathf.RoundToInt(Mathf.Sqrt(cells.Length));
            if (resolution * resolution != cells.Length)
            {
                Plugin.Log.LogError($"Cannot save '{detailName}' on terrain '{terrain.name}': its density map is not square");
                continue;
            }

            try
            {
                Save(terrain, detail, cells, resolution);
                written++;
            }
            catch (IOException e)
            {
                Plugin.Log.LogError($"Could not save '{detailName}' on terrain '{terrain.name}': {e.Message}");
            }
        }

        Dirty.Clear();

        if (written > 0) Plugin.Log.LogInfo($"Saved {written} detail density map(s) to {Path.Combine(BaseFolder, MapId ?? "unknown")}");

        return written;
    }

    private static GPUInstancerDetailPrototype FindMainPrototype(Terrain terrain, string detailName)
    {
        var managers = GPUInstancerManager.activeManagerList;
        if (managers == null) return null;

        for (var i = 0; i < managers.Count; i++)
        {
            var manager = managers[i];
            if (manager is not GPUInstancerDetailManager { IsOptic: false } detailManager || detailManager.terrain != terrain) continue;

            var prototypes = detailManager.prototypeList;
            for (var p = 0; p < prototypes.Count; p++)
            {
                var prototype = prototypes[p];
                if (prototype is GPUInstancerDetailPrototype detailPrototype && detailPrototype.name == detailName) return detailPrototype;
            }
        }

        return null;
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
