using System.Collections.Generic;
using GPUInstancer;
using UnityEngine;

namespace TerrainFX.Details;

/// <summary>
/// Two throwaway details that exercise both prototype kinds on every map: spheres (mesh) and magenta tufts (texture).
/// They are laid out in 16 m bands so that the density map's orientation can be read off in game: sphere bands
/// alternate along world X, tuft bands alternate along world Z.
/// </summary>
internal static class DemoDetails
{
    private const string LitShaderName = "TerrainFX/DetailLit";
    private const float BandWidth = 16f;

    private static Mesh _sphereMesh;
    private static GameObject _litSphere;
    private static Texture2D _tuftTexture;

    // Used while the bundle has no shader yet: sphere templates that use a material copied from one of the map's
    // own mesh details, remembered together with that material
    private static readonly List<(Material Source, GameObject Template)> BorrowedSpheres = [];

    public static void Register()
    {
        _sphereMesh = DetailTemplates.PrimitiveMesh(PrimitiveType.Sphere);
        _tuftTexture = CreateTuftTexture();

        var litShader = AssetRegistry.FindShader(LitShaderName);
        if (litShader != null)
        {
            DetailShaders.Register(litShader);

            var material = new Material(litShader)
            {
                name = "TFX_DemoSphere",
                color = new Color(1f, 0.45f, 0f),
                hideFlags = HideFlags.HideAndDontSave
            };
            _litSphere = DetailTemplates.Create("TFX_DemoSphere", _sphereMesh, material);
        }
        else
        {
            Plugin.Log.LogWarning($"Shader {LitShaderName} is not in the bundle, demo spheres will borrow a material from the map");
        }

        DetailRegistry.AddProvider(Provide);
    }

    private static IEnumerable<DetailDefinition> Provide(DetailContext context)
    {
        if (!Plugin.DemoDetailsEnabled.Value) yield break;

        var sphere = _litSphere != null ? _litSphere : BorrowedSphere(context.Manager);
        if (sphere != null)
        {
            yield return new DetailDefinition
            {
                Name = "TFX_DemoSphere",
                Prefab = sphere,
                WidthRange = new Vector2(0.3f, 0.6f),
                HeightRange = new Vector2(0.3f, 0.6f),
                Density = (map, _) => map.Fill((x, z) => (int)(x / BandWidth) % 2 == 0 ? 1 : 0)
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

    private static GameObject BorrowedSphere(GPUInstancerDetailManager manager)
    {
        // The game destroys a map's materials when the raid ends. Templates that used one are useless after that.
        for (var i = BorrowedSpheres.Count - 1; i >= 0; i--)
        {
            if (BorrowedSpheres[i].Source != null) continue;

            Object.Destroy(BorrowedSpheres[i].Template);
            BorrowedSpheres.RemoveAt(i);
        }

        var source = FindInstancedMaterial(manager);
        if (source == null)
        {
            Plugin.Log.LogWarning($"No mesh detail on {manager.name} to borrow a material from, skipping demo spheres");
            return null;
        }

        foreach (var borrowed in BorrowedSpheres)
        {
            if (borrowed.Source == source) return borrowed.Template;
        }

        var template = DetailTemplates.Create("TFX_DemoSphere", _sphereMesh, source);
        BorrowedSpheres.Add((source, template));

        return template;
    }

    private static Material FindInstancedMaterial(GPUInstancerDetailManager manager)
    {
        Material fallback = null;

        foreach (var prototype in manager.prototypeList)
        {
            if (prototype is not GPUInstancerDetailPrototype { usePrototypeMesh: true } || prototype.prefabObject == null)
            {
                continue;
            }

            var renderer = prototype.prefabObject.GetComponentInChildren<MeshRenderer>(true);
            if (renderer == null || !DetailShaders.CanInstance(renderer.sharedMaterial)) continue;

            if (prototype.name.ToLower().Contains("rock")) return renderer.sharedMaterial;

            fallback ??= renderer.sharedMaterial;
        }

        return fallback;
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
