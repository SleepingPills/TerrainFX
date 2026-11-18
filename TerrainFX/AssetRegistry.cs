using System.IO;
using System.Linq;
using System.Reflection;
using UnityEngine;

namespace TerrainFX;

internal static class AssetRegistry
{
    // ReSharper disable once NotAccessedField.Global
    public static AssetBundle AssetBundle;

    public static void LoadBundles()
    {
        var bundleDirectory = Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location);

        var assetBundlePath = $"{bundleDirectory}\\terrainfx";

        if (!File.Exists(assetBundlePath))
        {
            Plugin.Log.LogWarning($"No asset bundle at {assetBundlePath}");
            return;
        }

        Plugin.Log.LogInfo($"Loading TerrainFX Bundle: {assetBundlePath}");
        AssetBundle = AssetBundle.LoadFromFile(assetBundlePath);
    }

    /// <summary>Finds a shader in the bundle by its shader name ("TerrainFX/DetailLit"), null if it isn't there.</summary>
    public static Shader FindShader(string shaderName)
    {
        return AssetBundle == null
            ? null
            : AssetBundle.LoadAllAssets<Shader>().FirstOrDefault(shader => shader.name == shaderName);
    }
}
