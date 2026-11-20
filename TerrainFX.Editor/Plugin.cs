using System.Collections;
using System.Diagnostics.CodeAnalysis;
using System.IO;
using BepInEx;
using BepInEx.Configuration;
using BepInEx.Logging;
using TerrainFX.Details;
using UnityEngine;

namespace TerrainFX.Editor;

[BepInPlugin("com.janky.terrainfx.editor", "Janky-TerrainFX-Editor", TerrainFXEditorVersion)]
[BepInDependency("com.janky.terrainfx")]
[BepInDependency("com.fika.core")]
[SuppressMessage("ReSharper", "HeapView.ObjectAllocation.Evident")]
[SuppressMessage("ReSharper", "MemberCanBePrivate.Global")]
public class Plugin : BaseUnityPlugin
{
    public const string MajorMinorVersion = "1.0";
    public const string TerrainFXEditorVersion = $"{MajorMinorVersion}.0";

    public static ManualLogSource Log;

    private static ConfigEntry<bool> _loggingEnabled;

    /// <summary>Opens the editor in a raid, or closes it when it is open.</summary>
    public static ConfigEntry<KeyboardShortcut> ToggleEditorKey;

    /// <summary>Switches the detail painting tool to the brush, which adds instances.</summary>
    public static ConfigEntry<KeyboardShortcut> BrushKey;

    /// <summary>Switches the detail painting tool to the eraser, which removes instances.</summary>
    public static ConfigEntry<KeyboardShortcut> EraserKey;

    /// <summary>Switches the detail painting tool to restore, which brings back the game's own instances of a map detail.</summary>
    public static ConfigEntry<KeyboardShortcut> RestoreKey;

    /// <summary>Makes the brush radius smaller by one step.</summary>
    public static ConfigEntry<KeyboardShortcut> BrushSmallerKey;

    /// <summary>Makes the brush radius larger by one step.</summary>
    public static ConfigEntry<KeyboardShortcut> BrushLargerKey;

    /// <summary>Undoes the last finished brush stroke.</summary>
    public static ConfigEntry<KeyboardShortcut> UndoKey;

    /// <summary>Redoes the last undone brush stroke.</summary>
    public static ConfigEntry<KeyboardShortcut> RedoKey;

    /// <summary>Saves every painted density map of the current map to disk.</summary>
    public static ConfigEntry<KeyboardShortcut> SaveKey;

    /// <summary>Scale of the editor window, 1 being its normal size.</summary>
    public static ConfigEntry<float> UiScale;

    private void Awake()
    {
        Log = Logger;

        // Set here rather than in the delayed load because the density mask copies made while a raid loads must stay editable for painting map details
        DetailStock.KeepMasksWritable = true;

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

        gameObject.AddComponent<TerrainEditor>();

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
        const string hotkeys = "00. Hotkeys";
        const string ui = "01. Interface";
        const string debug = "02. Debug";

        /*
         * Hotkeys
         */
        ToggleEditorKey = BindKey(hotkeys, "Toggle Editor", new KeyboardShortcut(KeyCode.F11), "Opens the editor in a raid, or closes it.", 9);
        BrushKey = BindKey(hotkeys, "Brush", new KeyboardShortcut(KeyCode.B), "Switches to the brush, which adds instances of the selected detail.", 8);
        EraserKey = BindKey(hotkeys, "Eraser", new KeyboardShortcut(KeyCode.X), "Switches to the eraser, which removes instances of the selected detail.", 7);
        RestoreKey = BindKey(hotkeys, "Restore", new KeyboardShortcut(KeyCode.R), "Switches to restore, which brings back the game's own instances of the selected map detail.", 6);
        BrushSmallerKey = BindKey(hotkeys, "Brush Smaller", new KeyboardShortcut(KeyCode.LeftBracket), "Makes the brush smaller.", 5);
        BrushLargerKey = BindKey(hotkeys, "Brush Larger", new KeyboardShortcut(KeyCode.RightBracket), "Makes the brush larger.", 4);
        UndoKey = BindKey(hotkeys, "Undo", new KeyboardShortcut(KeyCode.Z, KeyCode.LeftControl), "Undoes the last brush stroke.", 3);
        RedoKey = BindKey(hotkeys, "Redo", new KeyboardShortcut(KeyCode.Y, KeyCode.LeftControl), "Redoes the last undone brush stroke.", 2);
        SaveKey = BindKey(hotkeys, "Save", new KeyboardShortcut(KeyCode.S, KeyCode.LeftControl), "Saves the painted density maps of the current map.", 1);

        /*
         * Interface
         */
        UiScale = Config.Bind(ui, "UI Scale", 1f, new ConfigDescription(
            "Size of the editor window. 1 is the normal size.",
            new AcceptableValueRange<float>(0.75f, 2.5f),
            new ConfigurationManagerAttributes { Order = 1 }
        ));

        /*
         * Deboog
         */
        _loggingEnabled = Config.Bind(debug, "Enable Debug Logging (RESTART)", true, new ConfigDescription(
            "Duh. Requires restarting the game to take effect.",
            null,
            new ConfigurationManagerAttributes { Order = 4 }
        ));
    }

    private ConfigEntry<KeyboardShortcut> BindKey(string section, string name, KeyboardShortcut defaultKey, string description, int order)
    {
        return Config.Bind(section, name, defaultKey, new ConfigDescription(
            description + " Only works while the editor is open and you are not flying, except Toggle Editor which always works in a raid.",
            null,
            new ConfigurationManagerAttributes { Order = order }
        ));
    }
}
