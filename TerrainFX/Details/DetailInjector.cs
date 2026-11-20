using System;
using System.Collections.Generic;
using EFT;
using GPUInstancer;
using UnityEngine;
using Object = UnityEngine.Object;

namespace TerrainFX.Details;

/// <summary>What <see cref="DetailInjector.EnsureOnTerrain"/> found, or set in motion, for a detail on a terrain.</summary>
public enum DetailTerrainState
{
    /// <summary>The detail is on every detail manager of the terrain and they are ready to be painted.</summary>
    Ready,

    /// <summary>The detail is being put on the terrain, or a detail manager of the terrain is still loading or rebuilding. Ask again later.</summary>
    Preparing,

    /// <summary>The detail cannot be put on this terrain during this raid. The reason was logged.</summary>
    Unavailable
}

/// <summary>
/// Appends prototypes for custom details to a detail manager, and has <see cref="DetailStock"/> apply the saved edits
/// of the game's own details (which are painted into the game's own prototypes, never added). At load this has to run between the manager's Awake and OnEnable: OnEnable snapshots
/// the prototype list into per-cell density data and per-prototype runtime data, all matched up by index. During the
/// raid a detail is added by appending its prototype and re-initializing the manager (<see cref="EnsureOnTerrain"/>).
///
/// A detail only goes on a terrain when it has saved density there or the editor asks for it, because every
/// prototype costs memory on the manager even when none of its cells hold an instance.
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

    // Terrain and detail pairs EnsureOnTerrain reported as unavailable, so the reason is only logged once and a detail
    // that failed validation is not checked again every frame
    private static readonly HashSet<(Terrain Terrain, Detail Detail)> UnavailableLogged = [];
    private static readonly HashSet<(Terrain Terrain, Detail Detail)> Rejected = [];

    /// <summary>
    /// Every prototype that has been added, with the manager it was added to and the definition it was built from.
    /// Entries of managers from an earlier raid stay in the list until the next map loads; their manager is null by
    /// then.
    /// </summary>
    internal static IReadOnlyList<(GPUInstancerDetailManager Manager, GPUInstancerDetailPrototype Prototype, Detail Definition)> InjectedDetails => Injected;

    /// <summary>
    /// Puts every custom detail that has saved density on this manager's terrain, and is not hidden this season, on
    /// the manager. Also collects the game's own detail assets (<see cref="DetailStock"/>) and applies their saved
    /// additions and removals to this manager. Logs one summary line for the manager.
    /// </summary>
    /// <returns>The number of prototypes added.</returns>
    public static int Inject(GPUInstancerDetailManager manager)
    {
        DestroyOrphans();

        if (manager.terrain == null || manager.terrain.terrainData == null || manager.terrainSettings == null)
        {
            return 0;
        }

        DetailShaders.Apply();

        manager.prototypeList ??= [];

        var mapId = ResolveMapId(manager);
        if (DetailDensityStore.SetMap(mapId)) DetailStock.Clear();

        DetailSeasons.ReadAtLoad(mapId);
        DetailStock.DropDead();
        DetailStock.Collect();
        DetailStock.ApplyLayersAtLoad(manager);

        var added = 0;
        var noData = 0;
        var hidden = 0;

        var definitions = DetailRegistry.Details;
        for (var i = 0; i < definitions.Count; i++)
        {
            InjectAtLoad(definitions[i], manager, ref added, ref noData, ref hidden);
        }

        var season = DetailSeasons.RaidSeason?.ToString() ?? "unknown season";
        Plugin.Log.LogInfo(
            $"Details on {manager.name} (terrain '{manager.terrain.name}'): {added} added, {noData} without saved density, " +
            $"{hidden} hidden ({season})"
        );

        return added;
    }

    /// <summary>
    /// Makes sure the detail is on both detail managers of the terrain (those that exist) so it can be painted. Cheap
    /// and free of allocations once the detail is there, so it can be called every frame.
    ///
    /// When a manager lacks the detail and is ready, the detail's prototype is built exactly as at load (with the saved
    /// density, or all zero without a file), appended to the manager and the manager is re-initialized. Both managers
    /// are handled in the same call. Re-initializing takes a handful of frames, during which the state is
    /// <see cref="DetailTerrainState.Preparing"/>.
    /// </summary>
    /// <returns>
    /// Ready when the detail is on every manager of the terrain and they are ready to be painted. Preparing while the
    /// detail is being added or a manager is still loading or rebuilding. Unavailable, with the reason logged once per
    /// terrain and detail, when the detail is hidden this season, fails validation, or the terrain has no detail
    /// manager. Always Unavailable for the description of a map asset (<see cref="Detail.IsStock"/>): map assets are
    /// painted into the game's own prototypes through <see cref="DetailStock.CollectAdditionEdits"/> and
    /// <see cref="DetailStock.ApplyAdditions"/> instead.
    /// </returns>
    public static DetailTerrainState EnsureOnTerrain(Terrain terrain, Detail detail)
    {
        if (terrain == null || detail == null || detail.IsStock) return DetailTerrainState.Unavailable;

        FindManagers(terrain, out var main, out var optic);
        if (main == null && optic == null)
        {
            LogUnavailable(terrain, detail, "the terrain has no detail manager");
            return DetailTerrainState.Unavailable;
        }

        if (DetailSeasons.IsHidden(detail))
        {
            // Checked here so the message is not built again on every later call
            if (!UnavailableLogged.Contains((terrain, detail))) LogUnavailable(terrain, detail, $"it is hidden in {DetailSeasons.RaidSeason}");
            return DetailTerrainState.Unavailable;
        }

        if (Rejected.Contains((terrain, detail))) return DetailTerrainState.Unavailable;

        var mainMissing = main != null && FindInjected(main, detail) < 0;
        var opticMissing = optic != null && FindInjected(optic, detail) < 0;

        if (!mainMissing && !opticMissing)
        {
            var ready = (main == null || DetailPainter.IsReady(main)) && (optic == null || DetailPainter.IsReady(optic));
            return ready ? DetailTerrainState.Ready : DetailTerrainState.Preparing;
        }

        // Re-initializing a manager that is still filling its cells or swapping buffers would cut that work short
        if (main != null && !DetailPainter.IsIdle(main)) return DetailTerrainState.Preparing;
        if (optic != null && !DetailPainter.IsIdle(optic)) return DetailTerrainState.Preparing;

        var problem = mainMissing ? Validate(detail, main) : null;
        if (problem == null && opticMissing) problem = Validate(detail, optic);

        if (problem != null)
        {
            Rejected.Add((terrain, detail));
            LogUnavailable(terrain, detail, problem);
            return DetailTerrainState.Unavailable;
        }

        try
        {
            if (mainMissing) AddAndRebuild(detail, main);
            if (opticMissing) AddAndRebuild(detail, optic);
        }
        catch (Exception e)
        {
            Rejected.Add((terrain, detail));
            Plugin.Log.LogError($"Detail '{detail.Name}' could not be added to terrain '{terrain.name}': {e}");
            return DetailTerrainState.Unavailable;
        }

        return DetailTerrainState.Preparing;
    }

    /// <summary>
    /// The game only gives a mesh detail a shadow pass when its material uses the GPUInstancer/Foliage shader. This
    /// flags our shadow casting mesh details as well, and has to run right after the manager created its runtime data
    /// (before the GPU buffers are allocated). It runs after every (re)initialization of a manager, including the ones
    /// <see cref="EnsureOnTerrain"/> and the game's seasonal fix trigger.
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

            if (IsInjected(prototype)) runtimeData.hasShadowCasterBuffer = true;
        }
    }

    /// <summary>True when <paramref name="prototype"/> is one TerrainFX added to a detail manager.</summary>
    internal static bool IsInjected(GPUInstancerPrototype prototype)
    {
        for (var i = 0; i < Injected.Count; i++)
        {
            if (Injected[i].Prototype == prototype) return true;
        }

        return false;
    }

    /// <summary>
    /// Finds the prototype of <paramref name="detail"/> on the terrain's main camera manager (or scope manager when
    /// <paramref name="optic"/> is true), with that manager and the prototype's index in its prototype list. Returns
    /// null, with the manager null and the index -1, when the detail is not on that manager. It does not allocate.
    /// </summary>
    internal static GPUInstancerDetailPrototype FindPrototype(
        Terrain terrain, bool optic, Detail detail, out GPUInstancerDetailManager manager, out int index
    )
    {
        for (var i = 0; i < Injected.Count; i++)
        {
            var (candidate, prototype, definition) = Injected[i];
            if (definition != detail || candidate == null || prototype == null) continue;
            if (candidate.terrain != terrain || candidate.IsOptic != optic) continue;

            var found = IndexOf(candidate, prototype);
            if (found < 0) continue;

            manager = candidate;
            index = found;
            return prototype;
        }

        manager = null;
        index = -1;
        return null;
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

    /// <summary>Returns the index of <paramref name="prototype"/> in the manager's prototype list, or -1 when it is not in it.</summary>
    internal static int IndexOf(GPUInstancerDetailManager manager, GPUInstancerPrototype prototype)
    {
        var prototypes = manager.prototypeList;
        if (prototypes == null) return -1;

        for (var i = 0; i < prototypes.Count; i++)
        {
            if (prototypes[i] == prototype) return i;
        }

        return -1;
    }

    /// <summary>The terrain's active main camera and scope detail managers. Either is null when the terrain has none.</summary>
    internal static void FindManagers(Terrain terrain, out GPUInstancerDetailManager main, out GPUInstancerDetailManager optic)
    {
        main = null;
        optic = null;

        var managers = GPUInstancerManager.activeManagerList;
        if (managers == null) return;

        for (var i = 0; i < managers.Count; i++)
        {
            if (managers[i] is not GPUInstancerDetailManager detailManager || detailManager.terrain != terrain) continue;

            if (detailManager.IsOptic)
            {
                if (optic == null) optic = detailManager;
            }
            else if (main == null)
            {
                main = detailManager;
            }
        }
    }

    /// <summary>Index in <see cref="Injected"/> of the detail's entry on <paramref name="manager"/>, or -1 when there is none.</summary>
    private static int FindInjected(GPUInstancerDetailManager manager, Detail detail)
    {
        for (var i = 0; i < Injected.Count; i++)
        {
            if (Injected[i].Definition == detail && Injected[i].Manager == manager) return i;
        }

        return -1;
    }

    private static void InjectAtLoad(Detail definition, GPUInstancerDetailManager manager, ref int added, ref int noData, ref int hidden)
    {
        try
        {
            if (definition == null) return;

            if (DetailSeasons.IsHidden(definition))
            {
                hidden++;
                return;
            }

            if (!DetailDensityStore.HasFile(manager.terrain, definition))
            {
                noData++;
                return;
            }

            var problem = Validate(definition, manager);
            if (problem != null)
            {
                Plugin.Log.LogError($"Detail '{definition.Name}' skipped on {manager.name}: {problem}");
                return;
            }

            Add(definition, manager);
            added++;
        }
        catch (Exception e)
        {
            Plugin.Log.LogError($"Detail '{definition?.Name}' failed on {manager.name}: {e}");
        }
    }

    /// <summary>Builds the detail's prototype for the manager and appends it to the manager's prototype list. The detail has to be validated first.</summary>
    private static void Add(Detail definition, GPUInstancerDetailManager manager)
    {
        var index = manager.prototypeList.Count;
        var prototype = Build(definition, manager, index);

        manager.prototypeList.Add(prototype);
        Injected.Add((manager, prototype, definition));
    }

    /// <summary>
    /// Adds the detail to a manager during the raid and re-initializes the manager. The runtime data of every
    /// prototype is rebuilt on the spot (which runs <see cref="EnableMeshShadows"/> through the shadow patch, and
    /// picks up the seasonal textures and colours already written onto the prototypes); the cells fill over the next
    /// frames.
    /// </summary>
    private static void AddAndRebuild(Detail definition, GPUInstancerDetailManager manager)
    {
        Add(definition, manager);
        manager.InitializeRuntimeDataAndBuffers(true);

        Plugin.Log.LogInfo($"Detail '{definition.Name}' added to {manager.name} during the raid, the manager is re-initializing");
    }

    private static void LogUnavailable(Terrain terrain, Detail detail, string reason)
    {
        if (UnavailableLogged.Add((terrain, detail)))
        {
            Plugin.Log.LogWarning($"Detail '{detail.Name}' cannot be put on terrain '{terrain.name}': {reason}");
        }
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

            // Distance based cross-fade between LOD levels, which only has an effect for a prefab with a LODGroup
            prototype.isLODCrossFade = definition.LodCrossFade;
            prototype.isLODCrossFadeAnimate = false;
            prototype.lodFadeTransitionWidth = Mathf.Clamp01(definition.LodFadeWidth);

            if (definition.LodCrossFade && definition.Prefab.GetComponent<LODGroup>() != null)
            {
                DetailDiagnostics.LogCrossFadeSupport(definition.Prefab);
            }
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

        SetDensity(prototype, definition, manager, index);

        return prototype;
    }

    /// <summary>
    /// Gives the prototype the saved density of the detail on the manager's terrain, at the terrain's detail
    /// resolution. A detail that has never been painted on this terrain gets a map of zeros, so it can be painted later.
    /// </summary>
    private static void SetDensity(GPUInstancerDetailPrototype prototype, Detail definition, GPUInstancerDetailManager manager, int index)
    {
        var resolution = manager.terrain.terrainData.detailResolution;
        var density = DetailDensityStore.Load(manager.terrain, definition, resolution);
        prototype.cachedDensityMapForInstance = density;
        prototype.detailResolution = resolution;

        long instances = 0;
        for (var i = 0; i < density.Length; i++) instances += density[i];

        Plugin.Log.LogInfo(
            $"Detail '{prototype.name}' -> {manager.name} [{index}] {(prototype.usePrototypeMesh ? "mesh" : "texture")}" +
            $", density {resolution}x{resolution}, {instances} instances over the terrain"
        );
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
        var removed = false;

        for (var i = Injected.Count - 1; i >= 0; i--)
        {
            if (Injected[i].Manager != null) continue;

            if (Injected[i].Prototype != null) Object.Destroy(Injected[i].Prototype);
            Injected.RemoveAt(i);
            removed = true;
        }

        // Managers of the previous raid are gone, so a new raid is loading and the terrains recorded below are gone too
        if (removed)
        {
            UnavailableLogged.Clear();
            Rejected.Clear();
        }
    }
}
