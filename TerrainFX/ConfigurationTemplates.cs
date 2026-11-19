using BepInEx.Configuration;

namespace TerrainFX;

public static class ConfigurationTemplates
{
    public static void SetJanky(ConfigFile mainConfig)
    {
        // First reset to the defaults
        SetDefaults(mainConfig);
    }

    public static void SetPotato(ConfigFile mainConfig)
    {
        // First reset to the defaults
        SetDefaults(mainConfig);
    }
    
    public static void SetDefaults(ConfigFile mainConfig)
    {
        var keys = new ConfigDefinition[mainConfig.Count];
        mainConfig.Keys.CopyTo(keys, 0);

        for (var i = 0; i < keys.Length; i++)
        {
            var entry = mainConfig[keys[i]];
            entry.BoxedValue = entry.DefaultValue;
        }
    }
}