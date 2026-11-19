using BepInEx;
using GPUInstancer;
using UnityEngine;

namespace TerrainFX.Details;

/// <summary>
/// Test for painting details in a running raid. While the paint key is held, the detail named in the settings is
/// painted onto the terrain under the crosshair ten times a second; the erase key removes it again. Each change is
/// logged with the time it took.
/// </summary>
public class DetailPaintTest : MonoBehaviour
{
    private const float StampInterval = 0.1f;
    private const float MaxDistance = 300f;
    private const float RayStep = 0.5f;

    private float _nextStamp;

    private void Update()
    {
        // Only the main key is checked. BepInEx's own shortcut test fails as soon as any other key (W, mouse) is held.
        var erase = UnityInput.Current.GetKey(Plugin.PaintEraseKey.Value.MainKey);
        var paint = !erase && UnityInput.Current.GetKey(Plugin.PaintKey.Value.MainKey);

        if (!paint && !erase) return;
        if (Time.unscaledTime < _nextStamp) return;

        _nextStamp = Time.unscaledTime + StampInterval;

        if (!TryFindTerrainUnderCrosshair(out var terrain, out var point)) return;

        DetailPainter.Paint(
            terrain, point, Plugin.PaintDetail.Value, Plugin.PaintRadius.Value, Plugin.PaintCount.Value,
            Plugin.PaintCoverage.Value, erase
        );
    }

    /// <summary>
    /// Walks along the view direction until it dips below a terrain's surface. This uses the terrain heights rather
    /// than physics, so buildings and other objects do not block it.
    /// </summary>
    private static bool TryFindTerrainUnderCrosshair(out Terrain terrain, out Vector3 point)
    {
        terrain = null;
        point = default;

        if (GPUInstancerManager.activeManagerList == null) return false;

        Camera camera = null;
        foreach (var manager in GPUInstancerManager.activeManagerList)
        {
            if (manager is GPUInstancerDetailManager { IsOptic: false } && manager.Camera != null)
            {
                camera = manager.Camera;
                break;
            }
        }

        if (camera == null) return false;

        var origin = camera.transform.position;
        var direction = camera.transform.forward;
        var above = origin;

        for (var distance = 0f; distance <= MaxDistance; distance += RayStep)
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

    private static Terrain TerrainAt(Vector3 position)
    {
        foreach (var manager in GPUInstancerManager.activeManagerList)
        {
            if (manager is not GPUInstancerDetailManager { IsOptic: false } detailManager) continue;

            var terrain = detailManager.terrain;
            if (terrain == null || terrain.terrainData == null) continue;

            var local = position - terrain.transform.position;
            var size = terrain.terrainData.size;

            if (local.x >= 0f && local.x < size.x && local.z >= 0f && local.z < size.z) return terrain;
        }

        return null;
    }

    private static float SurfaceHeight(Terrain terrain, Vector3 position)
    {
        return terrain.SampleHeight(position) + terrain.transform.position.y;
    }
}
