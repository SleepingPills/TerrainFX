using UnityEngine;

namespace TerrainFX.Details;

/// <summary>
/// Builds the GameObjects GPUI reads a mesh detail from. GPUI only copies the meshes, materials, child transforms and
/// layer out of the object, but it does so again on every re-initialization, so the object has to stay alive and must
/// never be destroyed while a manager still lists its prototype.
/// </summary>
public static class DetailTemplates
{
    public static GameObject Create(string name, Mesh mesh, params Material[] materials)
    {
        var template = new GameObject(name);
        template.AddComponent<MeshFilter>().sharedMesh = mesh;
        template.AddComponent<MeshRenderer>().sharedMaterials = materials;

        return Adopt(template);
    }

    /// <summary>
    /// Turns a scene object into a template: it survives scene loads, stops rendering and loses its colliders. The
    /// object stays active because GPUI searches it with GetComponentsInChildren, which skips inactive objects.
    /// </summary>
    public static GameObject Adopt(GameObject template)
    {
        foreach (var collider in template.GetComponentsInChildren<Collider>(true))
        {
            Object.DestroyImmediate(collider);
        }

        foreach (var renderer in template.GetComponentsInChildren<Renderer>(true))
        {
            renderer.enabled = false;
        }

        Object.DontDestroyOnLoad(template);

        return template;
    }
}
