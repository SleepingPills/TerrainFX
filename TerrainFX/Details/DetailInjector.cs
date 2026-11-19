using System;
using System.Collections.Generic;
using EFT;
using GPUInstancer;
using UnityEngine;
using Object = UnityEngine.Object;

namespace TerrainFX.Details;

/// <summary>
/// Appends custom prototypes to a detail manager. Has to run between the manager's Awake and OnEnable: OnEnable
/// snapshots the prototype list into per-cell density data and per-prototype runtime data, all matched up by index.
///
/// Everything a definition could get wrong is checked here first, because the game does not tolerate it:
///  - a material whose shader GPUI cannot resolve throws and aborts the whole manager
///  - a prototype GPUI fails to build renderers for is dropped from the runtime list but not from the per-cell
///    density lists, which shifts every later prototype onto the wrong density map
///  - a prototype without a cached density map makes the manager read a Unity detail layer that does not exist
/// </summary>
public static class DetailInjector
{
    private static readonly List<(GPUInstancerDetailManager Manager, GPUInstancerDetailPrototype Prototype, Detail Definition)> Injected = [];

    /// <summary>
    /// Every custom prototype that has been added, with the manager it was added to and the definition it was built
    /// from. Entries of managers from an earlier raid stay in the list until the next map loads; their manager is
    /// null by then.
    /// </summary>
    internal static IReadOnlyList<(GPUInstancerDetailManager Manager, GPUInstancerDetailPrototype Prototype, Detail Definition)> InjectedDetails => Injected;

    public static int Inject(GPUInstancerDetailManager manager)
    {
        DestroyOrphans();

        if (manager.terrain == null || manager.terrain.terrainData == null || manager.terrainSettings == null)
        {
            return 0;
        }

        DetailShaders.Apply();

        manager.prototypeList ??= [];

        DetailDensityStore.SetMap(ResolveMapId(manager));

        var added = 0;

        var definitions = DetailRegistry.Details;
        for (var i = 0; i < definitions.Count; i++)
        {
            var definition = definitions[i];
            try
            {
                var problem = Validate(definition, manager);
                if (problem != null)
                {
                    Plugin.Log.LogError($"Detail '{definition?.Name}' skipped on {manager.name}: {problem}");
                    continue;
                }

                var prototype = Build(definition, manager, manager.prototypeList.Count);

                manager.prototypeList.Add(prototype);
                Injected.Add((manager, prototype, definition));
                added++;
            }
            catch (Exception e)
            {
                Plugin.Log.LogError($"Detail '{definition?.Name}' failed on {manager.name}: {e}");
            }
        }

        return added;
    }

    /// <summary>
    /// The game only gives a mesh detail a shadow pass when its material uses the GPUInstancer/Foliage shader. This
    /// flags our shadow casting mesh details as well, and has to run right after the manager created its runtime data
    /// (before the GPU buffers are allocated).
    /// </summary>
    public static void EnableMeshShadows(List<GPUInstancerRuntimeData> runtimeDataList)
    {
        for (var i = 0; i < runtimeDataList.Count; i++)
        {
            var runtimeData = runtimeDataList[i];
            if (runtimeData.prototype is not GPUInstancerDetailPrototype { usePrototypeMesh: true, isShadowCasting: true } prototype)
            {
                continue;
            }

            for (var e = 0; e < Injected.Count; e++)
            {
                if (Injected[e].Prototype != prototype) continue;

                runtimeData.hasShadowCasterBuffer = true;
                break;
            }
        }
    }

    /// <summary>
    /// Returns the runtime data of <paramref name="prototype"/> in <paramref name="runtimeDataList"/>, or null when
    /// the list is null or holds no runtime data for it.
    /// </summary>
    internal static GPUInstancerRuntimeData FindRuntimeData(List<GPUInstancerRuntimeData> runtimeDataList, GPUInstancerPrototype prototype)
    {
        if (runtimeDataList == null) return null;

        for (var i = 0; i < runtimeDataList.Count; i++)
        {
            if (runtimeDataList[i].prototype == prototype) return runtimeDataList[i];
        }

        return null;
    }

    /// <summary>
    /// The id of the map being loaded, which names the folder its density maps are stored in. The raid settings know
    /// the map before its scenes load; the scene name of the manager is used when they don't, and "unknown" when
    /// neither is available.
    /// </summary>
    private static string ResolveMapId(GPUInstancerDetailManager manager)
    {
        var mapId = TarkovApplication.Exist(out var app) ? app.CurrentRaidSettings?.SelectedLocation?.Id : null;
        if (string.IsNullOrEmpty(mapId)) mapId = manager.gameObject.scene.name;

        return string.IsNullOrEmpty(mapId) ? "unknown" : mapId;
    }

    private static GPUInstancerDetailPrototype Build(Detail definition, GPUInstancerDetailManager manager, int index)
    {
        var settings = manager.terrainSettings;
        var isMesh = definition.IsMesh;

        // Field for field what GPUInstancerUtility.AddDetailInstancePrototypeFromTerrainPrototype does for a Unity
        // detail prototype. That editor-time path is how BSG's own prototypes were made.
        var prototype = ScriptableObject.CreateInstance<GPUInstancerDetailPrototype>();
        prototype.name = definition.Name;
        prototype.prototypeIndex = index;
        prototype.usePrototypeMesh = isMesh;
        // The scope view is a separate manager with its own prototypes. The game never reads maxDistanceOptic on a
        // detail prototype, so the scope distance goes into maxDistance of the scope manager's prototype.
        prototype.maxDistance = (manager.IsOptic ? definition.MaxDistanceOptic : definition.MaxDistance)
                                ?? settings.maxDetailDistance;
        prototype.lodBiasAdjustment = definition.LodBiasAdjustment;
        prototype.densityFadeFactor = definition.DensityFadeFactor;
        prototype.detailDensity = settings.detailDensity;
        prototype.detailScale = new Vector4(
            definition.WidthRange.x, definition.WidthRange.y, definition.HeightRange.x, definition.HeightRange.y
        );
        prototype.detailHealthyColor = definition.HealthyColor;
        prototype.detailDryColor = definition.DryColor;
        prototype.noiseSpread = definition.NoiseSpread;
        prototype.windWaveTintColor = settings.wavingGrassTint;

        // The serialized defaults suit grass. A mesh must not be bent onto the heightmap or swayed by the wind.
        prototype.useVertexFit = !isMesh;
        prototype.useTerrainNormal = !isMesh;
        prototype.windWavesOn = !isMesh;
        prototype.isBillboardDisabled = !isMesh;

        if (isMesh)
        {
            prototype.detailRenderMode = DetailRenderMode.VertexLit;
            prototype.prefabObject = definition.Prefab;
            prototype.useCrossQuads = false;

            SetLayer(definition.Prefab.transform, definition.Layer ?? manager.detailLayer);
        }
        else
        {
            prototype.detailRenderMode = definition.Billboard ? DetailRenderMode.GrassBillboard : DetailRenderMode.Grass;
            prototype.prototypeTexture = definition.Texture;
            prototype.quadCount = Mathf.Clamp(definition.QuadCount, 1, 4);
            prototype.isBillboard = definition.Billboard;
            prototype.useCrossQuads = !definition.Billboard && prototype.quadCount > 1;
            prototype.useCustomMaterialForTextureDetail = definition.TextureMaterial != null;
            prototype.textureDetailCustomMaterial = definition.TextureMaterial;
        }

        var shadowDistance = GPUInstancerConstants.gpuiSettings.DEFAULT_GRASS_SHADOW_DISTANCE;
        prototype.isShadowCasting = (definition.CastShadows ?? !isMesh) && shadowDistance > 0f && !manager.IsOptic;
        prototype.useCustomShadowDistance = prototype.isShadowCasting;
        prototype.shadowDistance = shadowDistance;
        prototype.cullShadows = true;
        // Mesh details draw their shadows with each renderer's own instanced material, see EnableMeshShadows
        prototype.useOriginalShaderForShadow = isMesh;

        // A detail that has never been painted on this terrain gets a map of zeros, so it can be painted later
        var resolution = manager.terrain.terrainData.detailResolution;
        var density = DetailDensityStore.Load(manager.terrain, definition, resolution);
        prototype.cachedDensityMapForInstance = density;

        long instances = 0;
        for (var i = 0; i < density.Length; i++) instances += density[i];

        Plugin.Log.LogInfo(
            $"Detail '{prototype.name}' -> {manager.name} [{index}] {(isMesh ? "mesh" : "texture")}, " +
            $"density {resolution}x{resolution}, {instances} instances over the terrain"
        );

        return prototype;
    }

    private static string Validate(Detail definition, GPUInstancerDetailManager manager)
    {
        if (definition == null) return "definition is null";
        if (string.IsNullOrEmpty(definition.Name)) return "no name";

        var prototypes = manager.prototypeList;
        for (var i = 0; i < prototypes.Count; i++)
        {
            var existing = prototypes[i];
            if (existing != null && existing.name == definition.Name) return "a prototype with this name already exists";
        }

        if (definition.Prefab != null && definition.Texture != null) return "has both a prefab and a texture";

        if (definition.Prefab != null) return ValidatePrefab(definition.Prefab);

        if (definition.Texture == null) return "has neither a prefab nor a texture";

        if (definition.TextureMaterial != null)
        {
            return DetailShaders.CanInstance(definition.TextureMaterial)
                ? null
                : $"shader of the texture material is not registered with {nameof(DetailShaders)}";
        }

        return Shader.Find(GPUInstancerConstants.SHADER_GPUI_FOLIAGE) != null
            ? null
            : $"the game has no {GPUInstancerConstants.SHADER_GPUI_FOLIAGE} shader";
    }

    private static string ValidatePrefab(GameObject prefab)
    {
        var renderers = prefab.GetComponentsInChildren<MeshRenderer>(true);
        if (renderers.Length == 0) return "prefab has no MeshRenderer";

        for (var r = 0; r < renderers.Length; r++)
        {
            var renderer = renderers[r];
            var filter = renderer.GetComponent<MeshFilter>();
            if (filter == null || filter.sharedMesh == null) return $"renderer '{renderer.name}' has no mesh";

            var materials = renderer.sharedMaterials;
            if (materials.Length == 0) return $"renderer '{renderer.name}' has no materials";

            for (var m = 0; m < materials.Length; m++)
            {
                var material = materials[m];
                if (material == null || material.shader == null) return $"renderer '{renderer.name}' has a missing material";

                if (!DetailShaders.CanInstance(material))
                {
                    return $"shader '{material.shader.name}' has no GPU Instancer counterpart, register one with {nameof(DetailShaders)}";
                }
            }
        }

        return null;
    }

    private static void SetLayer(Transform transform, int layer)
    {
        transform.gameObject.layer = layer;

        for (var i = 0; i < transform.childCount; i++)
        {
            SetLayer(transform.GetChild(i), layer);
        }
    }

    private static void DestroyOrphans()
    {
        for (var i = Injected.Count - 1; i >= 0; i--)
        {
            if (Injected[i].Manager != null) continue;

            if (Injected[i].Prototype != null) Object.Destroy(Injected[i].Prototype);
            Injected.RemoveAt(i);
        }
    }
}
