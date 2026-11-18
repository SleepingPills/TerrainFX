using System;
using System.Collections.Generic;

namespace TerrainFX.Details;

/// <summary>
/// Where custom details are declared. Providers run once per detail manager (so twice per terrain: main camera and
/// scope camera) and can tailor what they return to the map being loaded.
/// </summary>
public static class DetailRegistry
{
    private static readonly List<Func<DetailContext, IEnumerable<DetailDefinition>>> Providers = [];

    /// <summary>Adds a detail to every terrain.</summary>
    public static void Add(DetailDefinition definition)
    {
        Providers.Add(_ => [definition]);
    }

    public static void AddProvider(Func<DetailContext, IEnumerable<DetailDefinition>> provider)
    {
        Providers.Add(provider);
    }

    internal static List<DetailDefinition> Collect(DetailContext context)
    {
        var definitions = new List<DetailDefinition>();

        foreach (var provider in Providers)
        {
            try
            {
                var provided = provider(context);
                if (provided != null) definitions.AddRange(provided);
            }
            catch (Exception e)
            {
                Plugin.Log.LogError($"Detail provider failed on {context.Manager.name}: {e}");
            }
        }

        return definitions;
    }
}
