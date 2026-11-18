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
        foreach (var pair in mainConfig)
        {
            pair.Value.BoxedValue = pair.Value.DefaultValue;
        }
    }
}