using BepInEx.Configuration;
using UnityEngine;

namespace TerrainFX.Editor;

/// <summary>Reads the editor's hotkeys from Unity's legacy input.</summary>
public static class Hotkey
{
    /// <summary>
    /// True on the frame the shortcut's main key goes down while exactly the Ctrl, Shift and Alt modifiers the
    /// shortcut lists are held (left or right both count). Any other modifier key the shortcut lists must be held as
    /// well. Unlike BepInEx's own KeyboardShortcut.IsDown, holding unrelated keys (W, a mouse button) does not stop
    /// the shortcut from firing. A shortcut without a main key never fires.
    /// </summary>
    public static bool Pressed(KeyboardShortcut shortcut)
    {
        if (shortcut.MainKey == KeyCode.None || !Input.GetKeyDown(shortcut.MainKey)) return false;

        var wantCtrl = false;
        var wantShift = false;
        var wantAlt = false;

        // Modifiers is an IEnumerable<KeyCode> (a LINQ Skip over BepInEx's private key array) with no indexer, so it is
        // walked with its enumerator. That allocates one small iterator, and only on the frame the main key goes down.
        using var modifiers = shortcut.Modifiers.GetEnumerator();
        while (modifiers.MoveNext())
        {
            var modifier = modifiers.Current;
            switch (modifier)
            {
                case KeyCode.LeftControl or KeyCode.RightControl:
                    wantCtrl = true;
                    break;
                case KeyCode.LeftShift or KeyCode.RightShift:
                    wantShift = true;
                    break;
                case KeyCode.LeftAlt or KeyCode.RightAlt:
                    wantAlt = true;
                    break;
                default:
                    if (!Input.GetKey(modifier)) return false;
                    break;
            }
        }

        var ctrl = Input.GetKey(KeyCode.LeftControl) || Input.GetKey(KeyCode.RightControl);
        var shift = Input.GetKey(KeyCode.LeftShift) || Input.GetKey(KeyCode.RightShift);
        var alt = Input.GetKey(KeyCode.LeftAlt) || Input.GetKey(KeyCode.RightAlt);

        return ctrl == wantCtrl && shift == wantShift && alt == wantAlt;
    }
}
