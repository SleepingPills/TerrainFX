using System.Linq;
using System.Text;
using GPUInstancer;
using UnityEngine;

namespace TerrainFX.Details;

/// <summary>Logs what a map's detail managers and the game's shader bindings actually contain.</summary>
public static class DetailDiagnostics
{
    private static bool _bindingsDumped;

    public static void Dump(GPUInstancerDetailManager manager)
    {
        DumpShaderBindings();

        var text = new StringBuilder();
        var settings = manager.terrainSettings;
        var terrainData = manager.terrain != null ? manager.terrain.terrainData : null;

        text.AppendLine($"Detail manager '{manager.name}' optic={manager.IsOptic} detailLayer={manager.detailLayer}");

        if (terrainData != null)
        {
            text.AppendLine(
                $"  terrain '{manager.terrain.name}' size={terrainData.size} detailRes={terrainData.detailResolution} " +
                $"heightRes={terrainData.heightmapResolution} unityDetailLayers={terrainData.detailPrototypes.Length}"
            );
        }

        if (settings != null)
        {
            text.AppendLine(
                $"  settings maxDetailDistance={settings.maxDetailDistance} detailDensity={settings.detailDensity} " +
                $"cellSize={(settings.autoSPCellSize ? "auto" : settings.preferedSPCellSize.ToString())} " +
                $"densityMapArray={(settings.densityMapArray != null ? settings.densityMapArray.depth.ToString() : "none")}"
            );
        }

        var prototypes = manager.prototypeList;
        for (var i = 0; prototypes != null && i < prototypes.Count; i++)
        {
            if (prototypes[i] is GPUInstancerDetailPrototype prototype)
            {
                Describe(text, i, prototype);
            }
            else
            {
                text.AppendLine($"  [{i}] {(prototypes[i] == null ? "NULL" : prototypes[i].GetType().Name)}");
            }
        }

        Plugin.Log.LogInfo(text.ToString());
    }

    private static void Describe(StringBuilder text, int index, GPUInstancerDetailPrototype prototype)
    {
        var cells = prototype.cachedDensityMapForInstance;
        var resolution = cells == null ? 0 : Mathf.FloorToInt(Mathf.Sqrt(cells.Length));

        text.AppendLine(
            $"  [{index}] '{prototype.name}' {(prototype.usePrototypeMesh ? "mesh" : "texture")} " +
            $"densityRes={resolution} instances={(cells == null ? 0 : cells.Sum(c => (long)c))} scale={prototype.detailScale} " +
            $"maxDist={prototype.maxDistance} density={prototype.detailDensity} shadows={prototype.isShadowCasting} " +
            $"densityMask={prototype.useDensityMask} alphaMask={prototype.useAlphaMask} vertexFit={prototype.useVertexFit}"
        );
        text.AppendLine(
            $"       lodBias={prototype.lodBiasAdjustment} minDist={prototype.minDistance} " +
            $"maxDistOptic={prototype.maxDistanceOptic} shadowDist={prototype.shadowDistance} " +
            $"densityFade={prototype.densityFadeFactor} densityMapIndex={prototype.densityMapIndex} " +
            $"densityChannel={prototype.densityChanelMask} densityMinMax={prototype.densityMinMax}"
        );

        if (!prototype.usePrototypeMesh)
        {
            var texture = prototype.prototypeTexture != null ? prototype.prototypeTexture.name : "NULL";
            var custom = prototype.useCustomMaterialForTextureDetail && prototype.textureDetailCustomMaterial != null
                ? $" customMaterial={ShaderInfo(prototype.textureDetailCustomMaterial)}"
                : "";

            text.AppendLine($"       texture={texture} quads={prototype.quadCount} billboard={prototype.isBillboard}{custom}");
            return;
        }

        if (prototype.prefabObject == null)
        {
            text.AppendLine("       prefab=NULL");
            return;
        }

        var prefab = prototype.prefabObject;
        text.AppendLine($"       prefab='{prefab.name}' lodGroup={prefab.GetComponent<LODGroup>() != null}");

        foreach (var renderer in prefab.GetComponentsInChildren<MeshRenderer>(true))
        {
            var materials = string.Join(", ", renderer.sharedMaterials.Select(ShaderInfo));
            text.AppendLine($"       renderer '{renderer.name}' layer={renderer.gameObject.layer} shadows={renderer.shadowCastingMode}: {materials}");
        }
    }

    private static string ShaderInfo(Material material)
    {
        if (material == null || material.shader == null) return "NULL";

        var instanced = DetailShaders.Resolve(material.shader.name);
        return $"'{material.name}' [{material.shader.name} -> {(instanced != null ? instanced.name : "UNRESOLVED")}]";
    }

    private static void DumpShaderBindings()
    {
        if (_bindingsDumped) return;

        var settings = GPUInstancerConstants.gpuiSettings;
        if (settings == null || settings.shaderBindings == null) return;

        _bindingsDumped = true;

        var text = new StringBuilder();
        text.AppendLine($"GPUI matrix handling: {GPUInstancerUtility.matrixHandlingType}, shader bindings:");

        foreach (var instance in settings.shaderBindings.shaderInstances ?? [])
        {
            var instanced = instance.instancedShader != null ? instance.instancedShader.name : "NULL";
            text.AppendLine($"  '{instance.name}' -> '{instanced}' original={instance.isOriginalInstanced} ext='{instance.extensionCode}'");
        }

        string[] wellKnown =
        [
            GPUInstancerConstants.SHADER_GPUI_FOLIAGE,
            GPUInstancerConstants.SHADER_GPUI_STANDARD,
            GPUInstancerConstants.SHADER_GPUI_STANDARD_SPECULAR,
            GPUInstancerConstants.SHADER_GPUI_VERTEXLIT,
            GPUInstancerConstants.SHADER_GPUI_SHADOWS_ONLY
        ];

        foreach (var name in wellKnown)
        {
            text.AppendLine($"  Shader.Find('{name}') = {(Shader.Find(name) != null ? "present" : "MISSING")}");
        }

        Plugin.Log.LogInfo(text.ToString());
    }
}
