using System.Collections.Generic;
using GPUInstancer;
using HarmonyLib;
using UnityEngine;

namespace TerrainFX.Details;

/// <summary>
/// Registers shaders with Tarkov's GPU Instancer so that materials using them can be put on detail prefabs.
///
/// GPUI never draws a prefab with its own material. For every material it calls
/// <see cref="GPUInstancerShaderBindings.GetInstancedMaterial"/>, which looks the shader up BY NAME and creates a copy
/// of the material on the "instanced" shader it finds:
///   1. an entry in <see cref="GPUInstancerShaderBindings.shaderInstances"/> (serialized by BSG, extended by us here)
///   2. Unity built-ins ("Standard", "VertexLit", ...) map to "GPUInstancer/Standard" etc. via Shader.Find
///   3. anything else falls back to "GPUInstancer/Standard"
/// BSG stripped the GPUInstancer/* variants of the built-in shaders from the build, so 2 and 3 return null and
/// "new Material(null)" throws inside the manager's OnEnable, which kills detail rendering for the whole terrain.
///
/// Registering only stops the exception. For instances to actually land on the terrain the shader itself has to read
/// GPUI's buffers ("#pragma instancing_options procedural:setupGPUI", see Shaders/TerrainFXGPUI.cginc). A stock
/// shader such as "Standard" has no such setup function and would draw every instance at the world origin.
/// </summary>
public static class DetailShaders
{
    private static readonly List<(string Name, Shader Shader)> Registered = new();

    private static readonly List<string> UnityShaders = ReadNameList("_standardUnityShaders");
    private static readonly List<string> UnityShadersGpui = ReadNameList("_standardUnityShadersGPUI");
    private static readonly List<string> ExtraGpuiShaders = ReadNameList("_extraGPUIShaders");

    private static GPUInstancerShaderBindings Bindings
    {
        get
        {
            var settings = GPUInstancerConstants.gpuiSettings;
            return settings == null ? null : settings.shaderBindings;
        }
    }

    /// <summary>Registers a shader that implements the GPUI procedural setup itself.</summary>
    public static void Register(Shader gpuiReadyShader)
    {
        if (gpuiReadyShader != null) RegisterAlias(gpuiReadyShader.name, gpuiReadyShader);
    }

    /// <summary>Makes materials using <paramref name="sourceShaderName"/> render with <paramref name="gpuiReadyShader"/>.</summary>
    public static void RegisterAlias(string sourceShaderName, Shader gpuiReadyShader)
    {
        if (string.IsNullOrEmpty(sourceShaderName) || gpuiReadyShader == null) return;

        var index = FindRegistered(sourceShaderName);
        if (index >= 0) Registered[index] = (sourceShaderName, gpuiReadyShader);
        else Registered.Add((sourceShaderName, gpuiReadyShader));

        Apply();
    }

    /// <summary>Returns the index of the registration for <paramref name="name"/>, or -1 when there is none.</summary>
    private static int FindRegistered(string name)
    {
        for (var i = 0; i < Registered.Count; i++)
        {
            if (Registered[i].Name == name) return i;
        }

        return -1;
    }

    /// <summary>
    /// Pushes the registrations into the game's bindings. The bindings asset only exists once the first GPUI manager
    /// has woken up and can be reloaded between raids, so this is re-run for every detail manager.
    /// </summary>
    internal static void Apply()
    {
        var bindings = Bindings;
        if (bindings == null) return;

        bindings.shaderInstances ??= new List<ShaderInstance>();

        for (var r = 0; r < Registered.Count; r++)
        {
            var (name, shader) = Registered[r];
            if (shader == null) continue;

            var isOriginal = name == shader.name;
            ShaderInstance existing = null;
            var instances = bindings.shaderInstances;
            for (var i = 0; i < instances.Count; i++)
            {
                if (instances[i].name != name || !string.IsNullOrEmpty(instances[i].extensionCode)) continue;

                existing = instances[i];
                break;
            }

            if (existing == null)
            {
                bindings.AddShaderInstance(name, shader, isOriginal);
            }
            else if (existing.instancedShader != shader)
            {
                existing.instancedShader = shader;
                existing.isOriginalInstanced = isOriginal;
            }
        }
    }

    /// <summary>
    /// The shader GPUI would instance <paramref name="shaderName"/> with, or null where the game would throw.
    /// Mirrors <see cref="GPUInstancerShaderBindings.GetInstancedShader"/> without its warnings.
    /// </summary>
    public static Shader Resolve(string shaderName)
    {
        var bindings = Bindings;
        if (bindings == null || string.IsNullOrEmpty(shaderName)) return null;

        if (bindings.shaderInstances != null)
        {
            var instances = bindings.shaderInstances;
            for (var i = 0; i < instances.Count; i++)
            {
                var instance = instances[i];
                if (instance.name == shaderName && string.IsNullOrEmpty(instance.extensionCode))
                {
                    return instance.instancedShader;
                }
            }
        }

        var builtinIndex = UnityShaders.IndexOf(shaderName);
        if (builtinIndex >= 0 && builtinIndex < UnityShadersGpui.Count)
        {
            return Shader.Find(UnityShadersGpui[builtinIndex]);
        }

        if (UnityShadersGpui.Contains(shaderName) || ExtraGpuiShaders.Contains(shaderName))
        {
            return Shader.Find(shaderName);
        }

        return Shader.Find(GPUInstancerConstants.SHADER_GPUI_STANDARD);
    }

    public static bool CanInstance(Material material)
    {
        return material != null && material.shader != null && Resolve(material.shader.name) != null;
    }

    private static List<string> ReadNameList(string fieldName)
    {
        return AccessTools.Field(typeof(GPUInstancerShaderBindings), fieldName)?.GetValue(null) as List<string> ?? [];
    }
}
