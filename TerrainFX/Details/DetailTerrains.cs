using GPUInstancer;
using UnityEngine;

namespace TerrainFX.Details;

/// <summary>Finds the terrains that have detail managers, for picking the spot a brush is painting on.</summary>
public static class DetailTerrains
{
    private const float RayStep = 0.5f;

    /// <summary>
    /// Walks along <paramref name="ray"/> until it dips below the surface of a terrain that has a detail manager, and
    /// returns that terrain and the point where the ray meets its surface. This uses the terrain heights rather than
    /// physics, so buildings and other objects do not block it. Returns false when no terrain surface is crossed
    /// within <paramref name="maxDistance"/> metres or no detail manager is active.
    /// </summary>
    public static bool TryFindUnderRay(Ray ray, float maxDistance, out Terrain terrain, out Vector3 point)
    {
        terrain = null;
        point = default;

        if (GPUInstancerManager.activeManagerList == null) return false;

        var origin = ray.origin;
        var direction = ray.direction;
        var above = origin;

        for (var distance = 0f; distance <= maxDistance; distance += RayStep)
        {
            var sample = origin + direction * distance;
            var hit = TerrainAt(sample);

            if (hit == null || sample.y > SurfaceHeight(hit, sample))
            {
                above = sample;
                continue;
            }

            // Narrow the crossing down between the last sample above the surface and this one below it
            for (var i = 0; i < 6; i++)
            {
                var middle = (above + sample) * 0.5f;

                if (middle.y > SurfaceHeight(hit, middle)) above = middle;
                else sample = middle;
            }

            terrain = hit;
            point = sample;
            return true;
        }

        return false;
    }

    /// <summary>
    /// Returns the terrain of a main camera detail manager whose area contains <paramref name="position"/> along X
    /// and Z, ignoring height. Returns null when no such terrain is found.
    /// </summary>
    public static Terrain TerrainAt(Vector3 position)
    {
        var managers = GPUInstancerManager.activeManagerList;
        if (managers == null) return null;

        for (var i = 0; i < managers.Count; i++)
        {
            var manager = managers[i];
            if (manager is not GPUInstancerDetailManager { IsOptic: false } detailManager) continue;

            var terrain = detailManager.terrain;
            if (terrain == null || terrain.terrainData == null) continue;

            var local = position - terrain.transform.position;
            var size = terrain.terrainData.size;

            if (local.x >= 0f && local.x < size.x && local.z >= 0f && local.z < size.z) return terrain;
        }

        return null;
    }

    /// <summary>Returns the world height of the terrain's surface below or above <paramref name="position"/>.</summary>
    public static float SurfaceHeight(Terrain terrain, Vector3 position)
    {
        return terrain.SampleHeight(position) + terrain.transform.position.y;
    }
}
