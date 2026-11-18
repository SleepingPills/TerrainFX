using System;
using UnityEngine;

namespace TerrainFX.Details;

/// <summary>
/// Square grid of instance counts covering the whole terrain. Each cell holds the number of instances GPUI scatters
/// (at random positions and rotations) inside that cell. Handed to the game as
/// <see cref="GPUInstancer.GPUInstancerDetailPrototype.cachedDensityMapForInstance"/>.
/// </summary>
public sealed class DetailDensityMap
{
    public int Resolution { get; }

    /// <summary>World size of one cell in metres, along terrain X and Z.</summary>
    public Vector2 CellSize { get; }

    /// <summary>Row-major counts, same layout as Unity detail layers: Cells[z * Resolution + x].</summary>
    public int[] Cells { get; }

    public DetailDensityMap(int resolution, Vector3 terrainSize)
    {
        Resolution = resolution;
        CellSize = new Vector2(terrainSize.x / resolution, terrainSize.z / resolution);
        Cells = new int[resolution * resolution];
    }

    public int this[int x, int z]
    {
        get => Cells[z * Resolution + x];
        set => Cells[z * Resolution + x] = value;
    }

    public void Fill(int count)
    {
        Array.Fill(Cells, count);
    }

    /// <summary>Fills every cell from a function of the cell centre, in metres from the terrain's origin corner.</summary>
    public void Fill(Func<float, float, int> countAt)
    {
        for (var z = 0; z < Resolution; z++)
        {
            var worldZ = (z + 0.5f) * CellSize.y;

            for (var x = 0; x < Resolution; x++)
            {
                Cells[z * Resolution + x] = Mathf.Max(0, countAt((x + 0.5f) * CellSize.x, worldZ));
            }
        }
    }

    public long TotalInstances()
    {
        long total = 0;
        foreach (var count in Cells) total += count;
        return total;
    }
}
