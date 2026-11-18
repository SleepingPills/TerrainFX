using System.Collections.Generic;
using UnityEngine;

namespace TerrainFX.Details;

/// <summary>
/// Test details that are added to every map while the "Demo Details" setting is on:
///  - one mesh detail for each child of the "TFX Foliage" prefab in the asset bundle
///  - one texture detail, a magenta tuft generated in code
/// They are laid out in 16 m wide bands so that the direction of the density map can be checked in game: the mesh
/// details are in bands that alternate along world X, the tufts are in bands that alternate along world Z.
/// </summary>
internal static class DemoDetails
{
    private const string FoliagePrefabPath = "Assets/TerrainFX/TFX Foliage.prefab";
    private const float BandWidth = 16f;

    // Mesh details are placed in every 4th cell in both directions, which is roughly one per 2.7 x 2.7 m on a
    // 700 m terrain with a 1024 cell density map
    private const int MeshCellSpacing = 4;

    private static readonly List<GameObject> FoliageTemplates = [];
    private static Texture2D _tuftTexture;

    public static void Register()
    {
        _tuftTexture = CreateTuftTexture();

        LoadFoliageTemplates();

        DetailRegistry.AddProvider(Provide);
    }

    /// <summary>
    /// Creates one hidden copy of the bundle prefab that stays alive for as long as the plugin runs. Each of its
    /// direct children is one mesh detail.
    /// </summary>
    private static void LoadFoliageTemplates()
    {
        if (AssetRegistry.AssetBundle == null)
        {
            Plugin.Log.LogWarning("No asset bundle loaded, there will be no demo mesh details");
            return;
        }

        var prefab = AssetRegistry.AssetBundle.LoadAsset<GameObject>(FoliagePrefabPath);
        if (prefab == null)
        {
            Plugin.Log.LogWarning(
                $"{FoliagePrefabPath} is not in the asset bundle, there will be no demo mesh details. " +
                $"The bundle contains: {string.Join(", ", AssetRegistry.AssetBundle.GetAllAssetNames())}"
            );
            return;
        }

        var root = DetailTemplates.Adopt(Object.Instantiate(prefab));
        root.name = "TFX Foliage (detail templates)";

        foreach (Transform child in root.transform)
        {
            if (child.GetComponentInChildren<MeshRenderer>(true) == null)
            {
                Plugin.Log.LogWarning($"'{child.name}' in {FoliagePrefabPath} has no MeshRenderer and is ignored");
                continue;
            }

            FoliageTemplates.Add(child.gameObject);
        }

        Plugin.Log.LogInfo($"Loaded {FoliageTemplates.Count} demo mesh detail(s) from {FoliagePrefabPath}");
    }

    private static IEnumerable<DetailDefinition> Provide(DetailContext context)
    {
        if (!Plugin.DemoDetailsEnabled.Value) yield break;

        foreach (var template in FoliageTemplates)
        {
            yield return new DetailDefinition
            {
                Name = $"TFX_{template.name}",
                Prefab = template,
                WidthRange = new Vector2(0.8f, 1.2f),
                HeightRange = new Vector2(0.8f, 1.2f),
                Density = (map, _) => FillSparseBands(map)
            };
        }

        yield return new DetailDefinition
        {
            Name = "TFX_DemoTuft",
            Texture = _tuftTexture,
            WidthRange = new Vector2(0.4f, 0.7f),
            HeightRange = new Vector2(0.4f, 0.7f),
            Density = (map, _) => map.Fill((x, z) => (int)(z / BandWidth) % 2 == 0 ? 3 : 0)
        };
    }

    private static void FillSparseBands(DetailDensityMap map)
    {
        for (var z = 0; z < map.Resolution; z += MeshCellSpacing)
        {
            for (var x = 0; x < map.Resolution; x += MeshCellSpacing)
            {
                var worldX = (x + 0.5f) * map.CellSize.x;

                if ((int)(worldX / BandWidth) % 2 == 0) map[x, z] = 1;
            }
        }
    }

    private static Texture2D CreateTuftTexture()
    {
        const int size = 64;

        var texture = new Texture2D(size, size, TextureFormat.RGBA32, true)
        {
            name = "TFX_DemoTuft",
            wrapMode = TextureWrapMode.Clamp,
            hideFlags = HideFlags.HideAndDontSave
        };

        // Five blades fanning out from the bottom centre, tapering towards the top
        var pixels = new Color32[size * size];
        for (var y = 0; y < size; y++)
        {
            var height = y / (size - 1f);
            var halfWidth = 0.05f * (1f - height) + 0.01f;

            for (var x = 0; x < size; x++)
            {
                var u = x / (size - 1f);
                var covered = false;

                for (var blade = -2; blade <= 2 && !covered; blade++)
                {
                    covered = Mathf.Abs(u - (0.5f + blade * 0.2f * height)) < halfWidth;
                }

                pixels[y * size + x] = new Color32(255, 0, 200, covered ? (byte)255 : (byte)0);
            }
        }

        texture.SetPixels32(pixels);
        texture.Apply(true);

        return texture;
    }
}
