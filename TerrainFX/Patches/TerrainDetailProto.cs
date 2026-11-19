using System;
using System.Collections.Generic;
using System.Reflection;
using GPUInstancer;
using SPT.Reflection.Patching;
using TerrainFX.Details;
using UnityEngine;
using Random = UnityEngine.Random;

namespace TerrainFX.Patches;

/// <summary>Lets shadow casting custom mesh details into the shadow pass once their runtime data exists.</summary>
public class TerrainDetailShadowPatch : ModulePatch
{
    protected override MethodBase GetTargetMethod()
    {
        return typeof(GPUInstancerUtility).GetMethod(nameof(GPUInstancerUtility.AddDetailInstanceRuntimeDataToList));
    }

    [PatchPostfix]
    public static void Postfix(List<GPUInstancerRuntimeData> runtimeDataList)
    {
        DetailInjector.EnableMeshShadows(runtimeDataList);
    }
}

public class TerrainDetailOverridePatch : ModulePatch
{
    protected override MethodBase GetTargetMethod()
    {
        return typeof(GPUInstancerDetailManager).GetMethod(nameof(GPUInstancerDetailManager.Awake));
    }

    private const int HeightmapUpscaleRes = 4096;

    private static void UpscaleAndPerturbHeightmap(TerrainData terrainData)
    {
        var oldRes = terrainData.heightmapResolution;
        const int newRes = HeightmapUpscaleRes + 1;

        if (oldRes >= newRes)
        {
            Plugin.Log.LogInfo($"Heightmap already at {oldRes}, skipping upscale");
            return;
        }

        var sizeScale = 1 / terrainData.size.y;
        var noiseAmplitude = 0.05f * sizeScale; // 5 cm

        // GetHeights is indexed [y, x] and normalized to 0..1 over terrainData.size.y.
        var source = terrainData.GetHeights(0, 0, oldRes, oldRes);
        var upscaled = new float[newRes, newRes];

        // Corner-pinned mapping: source sample (oldRes - 1) lands exactly on destination sample (newRes - 1),
        // so the resampled grid stays aligned with everything already placed on the terrain.
        var step = (oldRes - 1) / (float)(newRes - 1);

        for (var y = 0; y < newRes; y++)
        {
            var sourceY = y * step;
            var y0 = Mathf.Min((int)sourceY, oldRes - 1);
            var y1 = Mathf.Min(y0 + 1, oldRes - 1);
            var fy = sourceY - y0;

            for (var x = 0; x < newRes; x++)
            {
                var sourceX = x * step;
                var x0 = Mathf.Min((int)sourceX, oldRes - 1);
                var x1 = Mathf.Min(x0 + 1, oldRes - 1);
                var fx = sourceX - x0;

                var top = Mathf.Lerp(source[y0, x0], source[y0, x1], fx);
                var bottom = Mathf.Lerp(source[y1, x0], source[y1, x1], fx);
                var height = Mathf.Lerp(top, bottom, fy);

                upscaled[y, x] = Mathf.Clamp01(height + Random.Range(-noiseAmplitude, noiseAmplitude));
            }
        }

        // Changing the resolution zeroes the heightmap and rescales terrainData.size, so both are restored after.
        var oldSize = terrainData.size;
        terrainData.heightmapResolution = newRes;
        terrainData.size = oldSize;

        terrainData.SetHeights(0, 0, upscaled);

        Plugin.Log.LogInfo($"Heightmap upscaled {oldRes} -> {newRes}");
    }

    [PatchPostfix]
    // ReSharper disable once InconsistentNaming
    public static void Postfix(GPUInstancerDetailManager __instance)
    {
        // Awake has finished and OnEnable (which snapshots the prototype list into runtime data) has not run yet.
        // An exception escaping from here would take the manager's Awake down with it.
        try
        {
            if (Plugin.DumpDetails.Value) DetailDiagnostics.Dump(__instance);

            var stock = new List<GPUInstancerPrototype>(__instance.prototypeList ?? []);
            var added = DetailInjector.Inject(__instance);

            if (added > 0 && Plugin.IsolateCustomDetails.Value)
            {
                var prototypes = __instance.prototypeList;
                for (var i = prototypes.Count - 1; i >= 0; i--)
                {
                    if (stock.Contains(prototypes[i])) prototypes.RemoveAt(i);
                }
            }
        }
        catch (Exception e)
        {
            Plugin.Log.LogError($"Detail injection failed on {__instance.name}: {e}");
        }

        // UpscaleAndPerturbHeightmap(__instance.terrain.terrainData);

        // if (!overrides.LodEnabled.Value)
        //     return;
        //
        // var terrainDetailDistance = overrides.DetailDistance.Value;
        //
        // __instance.terrainSettings.maxDetailDistance *= terrainDetailDistance;
        // __instance.terrainSettings.maxDetailDistanceLegacy *= terrainDetailDistance;
        //
        // foreach (var prototype in __instance.prototypeList)
        // {
        //     var detailPrototype = prototype as GPUInstancerDetailPrototype;
        //
        //     if (detailPrototype != null)
        //     {
        //         Plugin.Log.LogInfo($"DetailProto: {prototype.name} {prototype.maxDistance} {detailPrototype.densityFadeFactor}");
        //         detailPrototype.maxDistance *= terrainDetailDistance;
        //         detailPrototype.lodBiasAdjustment = terrainDetailDistance;
        //         detailPrototype.densityFadeFactor /= overrides.DetailDensity.Value;
        //     }
        //     else
        //     {
        //         Plugin.Log.LogInfo($"Proto: {prototype.name} {prototype.maxDistance}");
        //         prototype.maxDistance *= terrainDetailDistance;
        //         prototype.lodBiasAdjustment *= terrainDetailDistance;
        //     }
        // }
    }
}