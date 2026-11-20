using System.Collections.Generic;
using System.Text;
using GPUInstancer;
using UnityEngine;

namespace TerrainFX.Details;

/// <summary>Logs what a map's detail managers and the game's shader bindings actually contain.</summary>
public static class DetailDiagnostics
{
    private const string CrossFadeKeyword = "LOD_FADE_CROSSFADE";

    private static bool _bindingsDumped;

    // Instanced shaders whose cross-fade support was already logged
    private static readonly HashSet<Shader> CrossFadeLogged = [];

    /// <summary>
    /// Logs, once per shader, whether the instanced shader of each material of <paramref name="prefab"/> declares the
    /// LOD_FADE_CROSSFADE keyword. Without it GPUI's cross-fade has nothing to blend with.
    /// </summary>
    public static void LogCrossFadeSupport(GameObject prefab)
    {
        var renderers = prefab.GetComponentsInChildren<MeshRenderer>(true);
        for (var r = 0; r < renderers.Length; r++)
        {
            var materials = renderers[r].sharedMaterials;
            for (var m = 0; m < materials.Length; m++)
            {
                var material = materials[m];
                if (material == null || material.shader == null) continue;

                var instanced = DetailShaders.Resolve(material.shader.name);
                if (instanced == null || !CrossFadeLogged.Add(instanced)) continue;

                if (instanced.keywordSpace.FindKeyword(CrossFadeKeyword).isValid)
                {
                    Plugin.Log.LogInfo($"Shader '{instanced.name}' declares {CrossFadeKeyword}, LOD cross-fade should work");
                }
                else
                {
                    Plugin.Log.LogWarning(
                        $"Shader '{instanced.name}' has no cross-fade variant ({CrossFadeKeyword} is not declared), LOD " +
                        "switches will stay abrupt or both LOD levels will overlap during the switch"
                    );
                }
            }
        }
    }

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
            var mask = settings.densityMapArray;
            var maskInfo = mask == null
                ? "none"
                : $"{mask.depth} slices of {mask.width}x{mask.height} {mask.format} mips={mask.mipmapCount} " +
                  $"filter={mask.filterMode} wrap={mask.wrapMode} readable={mask.isReadable}";

            text.AppendLine(
                $"  settings maxDetailDistance={settings.maxDetailDistance} detailDensity={settings.detailDensity} " +
                $"cellSize={(settings.autoSPCellSize ? "auto" : settings.preferedSPCellSize.ToString())}"
            );
            text.AppendLine($"  densityMapArray: {maskInfo}");
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

        long instances = 0;
        for (var i = 0; cells != null && i < cells.Length; i++) instances += cells[i];

        text.AppendLine(
            $"  [{index}] '{prototype.name}' {(prototype.usePrototypeMesh ? "mesh" : "texture")} " +
            $"densityRes={resolution} instances={instances} scale={prototype.detailScale} " +
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

        var renderers = prefab.GetComponentsInChildren<MeshRenderer>(true);
        for (var i = 0; i < renderers.Length; i++)
        {
            var renderer = renderers[i];
            var sharedMaterials = renderer.sharedMaterials;
            var shaderInfos = new string[sharedMaterials.Length];
            for (var m = 0; m < sharedMaterials.Length; m++) shaderInfos[m] = ShaderInfo(sharedMaterials[m]);

            var materials = string.Join(", ", shaderInfos);
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

        var instances = settings.shaderBindings.shaderInstances;
        if (instances != null)
        {
            for (var i = 0; i < instances.Count; i++)
            {
                var instance = instances[i];
                var instanced = instance.instancedShader != null ? instance.instancedShader.name : "NULL";
                text.AppendLine($"  '{instance.name}' -> '{instanced}' original={instance.isOriginalInstanced} ext='{instance.extensionCode}'");
            }
        }

        string[] wellKnown =
        [
            GPUInstancerConstants.SHADER_GPUI_FOLIAGE,
            GPUInstancerConstants.SHADER_GPUI_STANDARD,
            GPUInstancerConstants.SHADER_GPUI_STANDARD_SPECULAR,
            GPUInstancerConstants.SHADER_GPUI_VERTEXLIT,
            GPUInstancerConstants.SHADER_GPUI_SHADOWS_ONLY
        ];

        for (var i = 0; i < wellKnown.Length; i++)
        {
            var name = wellKnown[i];
            text.AppendLine($"  Shader.Find('{name}') = {(Shader.Find(name) != null ? "present" : "MISSING")}");
        }

        Plugin.Log.LogInfo(text.ToString());
    }
}
