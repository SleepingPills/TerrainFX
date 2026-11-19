using System.Collections;
using System.Diagnostics.CodeAnalysis;
using System.IO;
using BepInEx;
using BepInEx.Configuration;
using BepInEx.Logging;
using UnityEngine;

namespace TerrainFX.Editor;

[BepInPlugin("com.janky.terrainfx.editor", "Janky-TerrainFX", TerrainFXEditorVersion)]
[SuppressMessage("ReSharper", "HeapView.ObjectAllocation.Evident")]
[SuppressMessage("ReSharper", "MemberCanBePrivate.Global")]
public class Plugin : BaseUnityPlugin
{
    public const string MajorMinorVersion = "1.0";
    public const string TerrainFXEditorVersion = $"{MajorMinorVersion}.0";

    public static ManualLogSource Log;

    private static ConfigEntry<bool> _loggingEnabled;

    private void Awake()
    {
        Log = Logger;

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

        // Patches
        
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
    
    private void SetupConfig()
    {
        const string debug = "01. Debug";
        
        /*
         * General
         */
    
        /*
         * Deboog
         */
        _loggingEnabled = Config.Bind(debug, "Enable Debug Logging (RESTART)", true, new ConfigDescription(
            "Duh. Requires restarting the game to take effect.",
            null,
            new ConfigurationManagerAttributes { Order = 4 }
        ));

        /*
         * Paint test
         */
    }
}