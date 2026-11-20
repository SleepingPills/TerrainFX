using System.Collections.Generic;
using EFT;
using GPUInstancer;
using UnityEngine;

namespace TerrainFX.Details;

/// <summary>
/// Knows the season of the raid, and gives custom mesh details their seasonal look (<see cref="Detail.SeasonLooks"/>).
///
/// The season is first read while the map loads, from the game's session (with the game's rule that Labs is always
/// summer), because details have to be put on the terrains before the game announces the season. The game announces
/// the authoritative season once per raid, after the map has loaded and after it has rebuilt the detail managers for
/// its own seasonal grass.
///
/// The game's own seasonal changes only reach the materials its map data points at, so custom details are left out.
/// At the announcement the look is written to two places:
///  - the detail's own materials, which a detail manager copies whenever it is (re)built
///  - the copies the detail managers are drawing with right now
/// The materials are kept between raids, so their original values are remembered and every season is applied on top
/// of those, including summer, which puts the originals back.
/// </summary>
public static class DetailSeasons
{
    private const string LabsMapId = "laboratory";

    private static readonly int MainTexId = Shader.PropertyToID("_MainTex");
    private static readonly int BumpMapId = Shader.PropertyToID("_BumpMap");
    private static readonly int ColorId = Shader.PropertyToID("_Color");
    private static readonly int BaseTintColorId = Shader.PropertyToID("_BaseTintColor");

    // What each material looked like before any season was applied to it
    private static readonly Dictionary<Material, DetailSeasonLook> Originals = new();

    // The season read while the map loaded, and the controller that announced the authoritative season of this raid
    private static ESeason? _loadSeason;
    private static ISeasonsController _announcedBy;

    /// <summary>
    /// The season of the current raid: read from the game's session while the map loads, then replaced by the season
    /// the game announces. Null while it is unknown.
    /// </summary>
    public static ESeason? RaidSeason { get; private set; }

    /// <summary>Subscribes to the game's season announcement, so the look is applied once per raid.</summary>
    public static void Initialize()
    {
        Seasons.OnInitialized += OnSeasonsInitialized;
    }

    /// <summary>
    /// True when <paramref name="detail"/> lists the season of this raid in <see cref="Detail.HiddenSeasons"/>. False
    /// while the season is unknown. It does not allocate.
    /// </summary>
    public static bool IsHidden(Detail detail)
    {
        if (!RaidSeason.HasValue) return false;

        var season = RaidSeason.Value;
        var hidden = detail.HiddenSeasons;
        for (var i = 0; i < hidden.Count; i++)
        {
            if (hidden[i] == season) return true;
        }

        return false;
    }

    /// <summary>
    /// Reads the season of the raid that is loading from the game's session, with Labs forced to summer like the game
    /// does. Called for every detail manager that wakes up. Once the game has announced the season of a raid, that
    /// season is kept for as long as the raid lasts.
    /// </summary>
    internal static void ReadAtLoad(string mapId)
    {
        if (_announcedBy != null && Seasons.Controller == _announcedBy) return;

        _announcedBy = null;

        ESeason? season = TarkovApplication.Exist(out var app) ? app.Session?.Season : null;
        if (season.HasValue && mapId == LabsMapId) season = ESeason.Summer;

        _loadSeason = season;
        RaidSeason = season;
    }

    private static void OnSeasonsInitialized(ISeasonsController controller)
    {
        var season = controller.Season;

        if (_loadSeason.HasValue && _loadSeason.Value != season)
        {
            Plugin.Log.LogWarning(
                $"The game announced {season} but {_loadSeason.Value} was read while the map loaded, details hidden or shown " +
                "for the season may be wrong for this raid"
            );
        }

        RaidSeason = season;
        _announcedBy = controller;

        Apply(season);
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

            var prefabRenderers = definition.Prefab.GetComponentsInChildren<MeshRenderer>(true);
            for (var r = 0; r < prefabRenderers.Length; r++)
            {
                var sharedMaterials = prefabRenderers[r].sharedMaterials;
                for (var m = 0; m < sharedMaterials.Length; m++)
                {
                    if (sharedMaterials[m] != null) ApplyLook(sharedMaterials[m], look);
                }
            }

            var runtimeData = DetailInjector.FindRuntimeData(manager.runtimeDataList, prototype);
            if (runtimeData?.instanceLODs == null) continue;

            CopyLooksToRuntime(definition.Prefab, runtimeData);
            applied++;
        }

        if (applied > 0) Plugin.Log.LogInfo($"Applied the {season} look to {applied} custom detail(s)");
    }

    /// <summary>
    /// Copies the look of the prefab's materials onto the copies a manager draws with. The copies are named
    /// "&lt;material name&gt;_GPUI".
    /// </summary>
    private static void CopyLooksToRuntime(GameObject prefab, GPUInstancerRuntimeData runtimeData)
    {
        var materials = new Dictionary<string, Material>();
        var prefabRenderers = prefab.GetComponentsInChildren<MeshRenderer>(true);
        for (var r = 0; r < prefabRenderers.Length; r++)
        {
            var sharedMaterials = prefabRenderers[r].sharedMaterials;
            for (var m = 0; m < sharedMaterials.Length; m++)
            {
                var material = sharedMaterials[m];
                if (material != null) materials[material.name + "_GPUI"] = material;
            }
        }

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
