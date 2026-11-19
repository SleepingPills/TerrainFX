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

    internal static ConfigEntry<bool> DemoDetailsEnabled;
    internal static ConfigEntry<bool> DumpDetails;
    internal static ConfigEntry<bool> IsolateCustomDetails;

    internal static ConfigEntry<KeyboardShortcut> PaintKey;
    internal static ConfigEntry<KeyboardShortcut> PaintEraseKey;
    internal static ConfigEntry<string> PaintDetail;
    internal static ConfigEntry<float> PaintRadius;
    internal static ConfigEntry<int> PaintCount;
    internal static ConfigEntry<float> PaintCoverage;

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

        DemoDetails.Register();
        DetailSeasons.Initialize();
        gameObject.AddComponent<DetailPaintTest>();

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
        const string paintTest = "02. Paint Test";
        
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

        DemoDetailsEnabled = Config.Bind(debug, "Demo Details", true, new ConfigDescription(
            "Scatters the TFX Foliage prefab from the asset bundle and magenta tufts over every terrain in 16 m bands. Applies on the next raid.",
            null,
            new ConfigurationManagerAttributes { Order = 3 }
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

        /*
         * Paint test
         */
        PaintKey = Config.Bind(paintTest, "Paint Key", new KeyboardShortcut(KeyCode.KeypadPlus), new ConfigDescription(
            "Hold to paint the detail below onto the terrain under the crosshair.",
            null,
            new ConfigurationManagerAttributes { Order = 6 }
        ));

        PaintEraseKey = Config.Bind(paintTest, "Erase Key", new KeyboardShortcut(KeyCode.KeypadMinus), new ConfigDescription(
            "Hold to remove the detail below from the terrain under the crosshair.",
            null,
            new ConfigurationManagerAttributes { Order = 5 }
        ));

        PaintDetail = Config.Bind(paintTest, "Detail Name", "TFX_Chunk_4", new ConfigDescription(
            "Name of the detail to paint, as it appears in the log.",
            null,
            new ConfigurationManagerAttributes { Order = 4 }
        ));

        PaintRadius = Config.Bind(paintTest, "Brush Radius", 3f, new ConfigDescription(
            "Radius of the brush in metres.",
            new AcceptableValueRange<float>(0.5f, 30f),
            new ConfigurationManagerAttributes { Order = 3 }
        ));

        PaintCount = Config.Bind(paintTest, "Instances Per Cell", 1, new ConfigDescription(
            "Number of instances painting puts into each density cell it picks.",
            new AcceptableValueRange<int>(1, 16),
            new ConfigurationManagerAttributes { Order = 2 }
        ));

        PaintCoverage = Config.Bind(paintTest, "Coverage", 0.25f, new ConfigDescription(
            "Share of the density cells inside the brush that painting fills. 1 fills every cell.",
            new AcceptableValueRange<float>(0.01f, 1f),
            new ConfigurationManagerAttributes { Order = 1 }
        ));
    }
}