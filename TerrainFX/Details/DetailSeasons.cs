using System.Collections.Generic;
using UnityEngine;

namespace TerrainFX.Details;

/// <summary>
/// Gives custom mesh details their seasonal look (<see cref="Detail.SeasonLooks"/>).
///
/// The game's own seasonal changes only reach the materials its map data points at, so custom details are left out.
/// The game announces the season once per raid, after the map has loaded and after it has rebuilt the detail
/// managers for its own seasonal grass. At that moment the look is written to two places:
///  - the detail's own materials, which a detail manager copies whenever it is (re)built
///  - the copies the detail managers are drawing with right now
/// The materials are kept between raids, so their original values are remembered and every season is applied on top
/// of those, including summer, which puts the originals back.
/// </summary>
public static class DetailSeasons
{
    private static readonly int MainTexId = Shader.PropertyToID("_MainTex");
    private static readonly int BumpMapId = Shader.PropertyToID("_BumpMap");
    private static readonly int ColorId = Shader.PropertyToID("_Color");
    private static readonly int BaseTintColorId = Shader.PropertyToID("_BaseTintColor");

    // What each material looked like before any season was applied to it
    private static readonly Dictionary<Material, DetailSeasonLook> Originals = new();

    /// <summary>Subscribes to the game's season announcement, so the look is applied once per raid.</summary>
    public static void Initialize()
    {
        Seasons.OnInitialized += OnSeasonsInitialized;
    }

    private static void OnSeasonsInitialized(ISeasonsController controller)
    {
        Apply(controller.Season);
    }

    public static void Apply(ESeason season)
    {
        var applied = 0;

        var injected = DetailInjector.InjectedDetails;
        for (var d = 0; d < injected.Count; d++)
        {
            var (manager, prototype, definition) = injected[d];
            if (manager == null || prototype == null || !definition.IsMesh || definition.SeasonLooks.Count == 0) continue;

            var look = FindLook(definition, season);

            // Materials of the detail by name. The copies a manager draws with are named "<material name>_GPUI".
            var materials = new Dictionary<string, Material>();
            var prefabRenderers = definition.Prefab.GetComponentsInChildren<MeshRenderer>(true);
            for (var r = 0; r < prefabRenderers.Length; r++)
            {
                var renderer = prefabRenderers[r];
                var sharedMaterials = renderer.sharedMaterials;
                for (var m = 0; m < sharedMaterials.Length; m++)
                {
                    var material = sharedMaterials[m];
                    if (material == null) continue;

                    ApplyLook(material, look);
                    materials[material.name + "_GPUI"] = material;
                }
            }

            var runtimeData = DetailInjector.FindRuntimeData(manager.runtimeDataList, prototype);
            if (runtimeData?.instanceLODs == null) continue;

            var lods = runtimeData.instanceLODs;
            for (var l = 0; l < lods.Count; l++)
            {
                var lod = lods[l];
                for (var r = 0; r < lod.renderers.Count; r++)
                {
                    var renderer = lod.renderers[r];
                    for (var c = 0; c < renderer.materials.Count; c++)
                    {
                        var copy = renderer.materials[c];
                        if (copy != null && materials.TryGetValue(copy.name, out var source)) CopyLook(source, copy);
                    }
                }
            }

            applied++;
        }

        if (applied > 0) Plugin.Log.LogInfo($"Applied the {season} look to {applied} custom detail(s)");
    }

    private static DetailSeasonLook FindLook(Detail definition, ESeason season)
    {
        if (definition.SeasonLooks.TryGetValue(season, out var look)) return look;

        if (season == ESeason.AutumnLate && definition.SeasonLooks.TryGetValue(ESeason.Autumn, out look)) return look;
        if (season == ESeason.SpringEarly && definition.SeasonLooks.TryGetValue(ESeason.Spring, out look)) return look;

        return null;
    }

    /// <summary>Sets each of the four properties to the look's value, or to the material's original value where the look has none.</summary>
    private static void ApplyLook(Material material, DetailSeasonLook look)
    {
        if (!Originals.TryGetValue(material, out var original))
        {
            original = new DetailSeasonLook
            {
                MainTexture = material.HasProperty(MainTexId) ? material.GetTexture(MainTexId) : null,
                NormalMap = material.HasProperty(BumpMapId) ? material.GetTexture(BumpMapId) : null,
                Color = material.HasProperty(ColorId) ? material.GetColor(ColorId) : null,
                BaseTintColor = material.HasProperty(BaseTintColorId) ? material.GetColor(BaseTintColorId) : null
            };
            Originals[material] = original;
        }

        if (material.HasProperty(MainTexId))
        {
            material.SetTexture(MainTexId, look != null && look.MainTexture != null ? look.MainTexture : original.MainTexture);
        }

        if (material.HasProperty(BumpMapId))
        {
            material.SetTexture(BumpMapId, look != null && look.NormalMap != null ? look.NormalMap : original.NormalMap);
        }

        if (original.Color.HasValue) material.SetColor(ColorId, look?.Color ?? original.Color.Value);
        if (original.BaseTintColor.HasValue) material.SetColor(BaseTintColorId, look?.BaseTintColor ?? original.BaseTintColor.Value);
    }

    private static void CopyLook(Material source, Material copy)
    {
        if (source.HasProperty(MainTexId) && copy.HasProperty(MainTexId)) copy.SetTexture(MainTexId, source.GetTexture(MainTexId));
        if (source.HasProperty(BumpMapId) && copy.HasProperty(BumpMapId)) copy.SetTexture(BumpMapId, source.GetTexture(BumpMapId));
        if (source.HasProperty(ColorId) && copy.HasProperty(ColorId)) copy.SetColor(ColorId, source.GetColor(ColorId));

        if (source.HasProperty(BaseTintColorId) && copy.HasProperty(BaseTintColorId))
        {
            copy.SetColor(BaseTintColorId, source.GetColor(BaseTintColorId));
        }
    }
}
