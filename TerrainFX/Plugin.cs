using System.Collections;
using System.Diagnostics.CodeAnalysis;
using System.IO;
using BepInEx;
using BepInEx.Configuration;
using BepInEx.Logging;
using TerrainFX.Details;
using TerrainFX.Patches;
using UnityEngine;

namespace TerrainFX;

[BepInPlugin("com.janky.terrainfx", "Janky-TerrainFX", TerrainFXVersion)]
[SuppressMessage("ReSharper", "HeapView.ObjectAllocation.Evident")]
[SuppressMessage("ReSharper", "MemberCanBePrivate.Global")]
public class Plugin : BaseUnityPlugin
{
    public const string MajorMinorVersion = "1.0";
    public const string TerrainFXVersion = $"{MajorMinorVersion}.0";

    public static ManualLogSource Log;

    private static ConfigEntry<bool> _loggingEnabled;

    internal static ConfigEntry<bool> DumpDetails;
    internal static ConfigEntry<bool> IsolateCustomDetails;

    private void Awake()
    {
        Log = Logger;

        AssetRegistry.LoadBundles();

        StartCoroutine(DelayedLoad());
    }

    private IEnumerator DelayedLoad()
    {
        // We wait for 5 seconds to allow all the 500 shonky mods (incl. this one) an average user installs to load 
        yield return new WaitForSeconds(5);

        SetupConfig();

        // Versioning
        var key = new ConfigDefinition("99. Internal", "ConfigVersion");
        var description = new ConfigDescription(
            "Do not modify.",
            null,
            new ConfigurationManagerAttributes { Order = 0, IsAdvanced = true }
        );
        
        // Stub entry that will get whatever value is there without accidentally hiding it by a default
        var versionConfig = Config.Bind(key, "", description);

        if (versionConfig is not { Value: MajorMinorVersion })
        {
            Config.Clear();
            File.WriteAllText(Config.ConfigFilePath, "");
            Config.Reload();

            SetupConfig();
        
            Log.LogInfo($"Configuration reset for version {MajorMinorVersion}.");
        }
        else
        {
            // We have to remove the stub entry so that we can replace it with the real one below
            Config.Remove(key);
        }
        
        versionConfig = Config.Bind(key, MajorMinorVersion, description);
        versionConfig.Value = MajorMinorVersion;

        DetailDefinitions.DefineDetails();
        DetailSeasons.Initialize();

        // Patches
        new TerrainDetailOverridePatch().Enable();
        new TerrainDetailShadowPatch().Enable();
        
        Log.LogInfo("Initialization finished");

        if (_loggingEnabled.Value)
        {
            Log.LogInfo("Logging enabled");
        }
        else
        {
            Log.LogInfo("Logging disabled");
            BepInEx.Logging.Logger.Sources.Remove(Log);
        }
    }

    private static void LoadTemplateDrawer(ConfigEntryBase entry)
    {
        if (GUILayout.Button("Janky's Special"))
        {
            ConfigurationTemplates.SetJanky(entry.ConfigFile);
        }

        if (GUILayout.Button("Potato"))
        {
            ConfigurationTemplates.SetPotato(entry.ConfigFile);
        }

        if (GUILayout.Button("Defaults"))
        {
            ConfigurationTemplates.SetDefaults(entry.ConfigFile);
        }
    }
    
    private void SetupConfig()
    {
        const string general = "00. General";
        const string debug = "01. Debug";
        
        /*
         * General
         */
        Config.Bind(general, "Load Template (RESTART)", "", new ConfigDescription(
            "Use a preset template for the TFX settings. Requires restarting the game to ensure all the settings take effect.",
            null,
            new ConfigurationManagerAttributes { Order = 1, CustomDrawer = LoadTemplateDrawer }
        ));

        /*
         * Deboog
         */
        _loggingEnabled = Config.Bind(debug, "Enable Debug Logging (RESTART)", true, new ConfigDescription(
            "Duh. Requires restarting the game to take effect.",
            null,
            new ConfigurationManagerAttributes { Order = 4 }
        ));

        DumpDetails = Config.Bind(debug, "Dump Detail Managers", true, new ConfigDescription(
            "Logs every terrain detail prototype and the GPU Instancer shader bindings when a map loads.",
            null,
            new ConfigurationManagerAttributes { Order = 2 }
        ));

        IsolateCustomDetails = Config.Bind(debug, "Isolate Custom Details", false, new ConfigDescription(
            "Removes the map's own details so that only custom ones are drawn. Applies on the next raid.",
            null,
            new ConfigurationManagerAttributes { Order = 1 }
        ));
    }
}