using UnityEngine;

namespace TerrainFX.Details;

/// <summary>
/// Loads what details are made of: meshes and textures from the TerrainFX asset bundle, and the demo tuft texture
/// that is generated in code.
/// </summary>
public static class DetailAssets
{
    private const string FoliagePrefabPath = "Assets/TerrainFX/TFX Foliage.prefab";

    private static GameObject _foliage;
    private static Texture2D _demoTuft;

    /// <summary>
    /// Returns the direct child named <paramref name="childName"/> of the "TFX Foliage" prefab in the asset bundle,
    /// ready to be used as <see cref="Detail.Prefab"/>. On first use the prefab is copied once into a hidden object
    /// that survives scene loads, does not render and has no colliders; every child returned belongs to that copy.
    /// Logs a warning and returns null when the bundle is not loaded, the prefab is not in it, or the prefab has no
    /// child with that name.
    /// </summary>
    public static GameObject Mesh(string childName)
    {
        var foliage = LoadFoliage();
        if (foliage == null) return null;

        var child = foliage.transform.Find(childName);
        if (child == null)
        {
            Plugin.Log.LogWarning($"{FoliagePrefabPath} has no child named '{childName}'");
            return null;
        }

        return child.gameObject;
    }

    /// <summary>
    /// Loads a texture from the asset bundle by its full asset path, for example "Assets/TerrainFX/Leaves.png".
    /// Logs a warning and returns null when the bundle is not loaded or the texture is not in it.
    /// </summary>
    public static Texture2D Texture(string assetPath)
    {
        if (AssetRegistry.AssetBundle == null)
        {
            Plugin.Log.LogWarning($"No asset bundle loaded, cannot load the texture {assetPath}");
            return null;
        }

        var texture = AssetRegistry.AssetBundle.LoadAsset<Texture2D>(assetPath);
        if (texture == null) Plugin.Log.LogWarning($"{assetPath} is not a texture in the asset bundle");

        return texture;
    }

    /// <summary>Returns a magenta grass tuft texture generated in code, for testing texture details without the bundle.</summary>
    public static Texture2D DemoTuft()
    {
        if (_demoTuft == null) _demoTuft = CreateTuftTexture();

        return _demoTuft;
    }

    private static GameObject LoadFoliage()
    {
        if (_foliage != null) return _foliage;

        if (AssetRegistry.AssetBundle == null)
        {
            Plugin.Log.LogWarning($"No asset bundle loaded, cannot load {FoliagePrefabPath}");
            return null;
        }

        var prefab = AssetRegistry.AssetBundle.LoadAsset<GameObject>(FoliagePrefabPath);
        if (prefab == null)
        {
            Plugin.Log.LogWarning(
                $"{FoliagePrefabPath} is not in the asset bundle. " +
                $"The bundle contains: {string.Join(", ", AssetRegistry.AssetBundle.GetAllAssetNames())}"
            );
            return null;
        }

        _foliage = Object.Instantiate(prefab);
        _foliage.name = "TFX Foliage (detail templates)";
        MakeTemplate(_foliage);

        return _foliage;
    }

    /// <summary>
    /// GPUI copies the meshes, materials, child transforms and layer out of a detail's prefab again on every
    /// re-initialization, so the object has to stay alive for as long as the plugin runs. This makes it survive scene
    /// loads, stop rendering and lose its colliders. The object stays active because GPUI searches it with
    /// GetComponentsInChildren, which skips inactive objects.
    /// </summary>
    private static void MakeTemplate(GameObject template)
    {
        var colliders = template.GetComponentsInChildren<Collider>(true);
        for (var i = 0; i < colliders.Length; i++)
        {
            var collider = colliders[i];
            Object.DestroyImmediate(collider);
        }

        var renderers = template.GetComponentsInChildren<Renderer>(true);
        for (var i = 0; i < renderers.Length; i++)
        {
            var renderer = renderers[i];
            renderer.enabled = false;
        }

        Object.DontDestroyOnLoad(template);
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
