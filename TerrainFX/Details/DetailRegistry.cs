using System.Collections.Generic;

namespace TerrainFX.Details;

/// <summary>
/// The list of custom details that are added to every terrain of every map. It is filled once at startup by
/// <see cref="DetailDefinitions.DefineDetails"/> and read by <see cref="DetailInjector"/> whenever a map loads.
/// </summary>
public static class DetailRegistry
{
    /// <summary>Every detail that was added, in the order it was added.</summary>
    public static readonly List<Detail> Details = [];

    /// <summary>
    /// Adds a detail to every terrain. The detail is skipped with an error in the log when it is null, has no name,
    /// has the name of a detail that is already registered, or has neither a prefab nor a texture (which is what a
    /// missing asset bundle asset produces).
    /// </summary>
    public static void Add(Detail detail)
    {
        if (detail == null)
        {
            Plugin.Log.LogError("A null detail was not registered");
            return;
        }

        if (string.IsNullOrEmpty(detail.Name))
        {
            Plugin.Log.LogError("A detail without a name was not registered");
            return;
        }

        if (Find(detail.Name) != null)
        {
            Plugin.Log.LogError($"Detail '{detail.Name}' was not registered: a detail with this name already exists");
            return;
        }

        if (detail.Prefab == null && detail.Texture == null)
        {
            Plugin.Log.LogError($"Detail '{detail.Name}' was not registered: it has neither a prefab nor a texture");
            return;
        }

        Details.Add(detail);
    }

    /// <summary>Returns the registered detail with the given name, or null when there is none.</summary>
    public static Detail Find(string name)
    {
        for (var i = 0; i < Details.Count; i++)
        {
            var detail = Details[i];
            if (detail.Name == name) return detail;
        }

        return null;
    }
}
