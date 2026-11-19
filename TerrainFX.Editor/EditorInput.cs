using Comfort.Common;
using EFT;
using EFT.CameraControl;
using EFT.InputSystem;
using FikaFreeCamera = Fika.Core.Main.FreeCamera.FreeCamera;

namespace TerrainFX.Editor;

/// <summary>
/// Takes the game's input away while the editor is open, and hands it back while the user holds the right mouse
/// button to fly.
///
/// The game recomputes the cursor every frame from its input tree, so writing Cursor.visible directly would be undone
/// on the next frame. Instead this node joins the tree: every node votes on the cursor (Ignore &lt; LockCursor &lt;
/// ShowCursor, the highest vote wins) and can remove commands and axes before the nodes after it see them.
///
/// The tree visits its children from the last one in its list to the first. The player owner puts itself at the start
/// of the list so it runs last; this node goes right after it in the list, so it runs after the game's UI (inventory,
/// console, menus keep working) and just before the player owner.
///
/// Fika's free camera reads Unity's input directly instead of going through the tree, so it is paused by disabling its
/// component while the user is not flying. The component has no OnEnable or OnDisable, so this has no other effect.
/// </summary>
public class EditorInput : InputNode
{
    private InputTree _tree;
    private FikaFreeCamera _freeCamera;
    private bool _open;
    private bool _flying;

    /// <summary>
    /// Adds this node to the input tree of the local player, which shows the cursor and blocks the game's input, and
    /// pauses Fika's free camera when it is active. Does nothing to the tree when there is no local player (not in a
    /// raid). Opening again re-inserts the node at the right place in case the tree changed since.
    /// </summary>
    public void Open()
    {
        _open = true;
        _flying = false;

        if (_tree != null) _tree.Remove(this);
        _tree = null;

        var gameWorld = Singleton<GameWorld>.Instantiated ? Singleton<GameWorld>.Instance : null;
        var player = gameWorld != null ? gameWorld.MainPlayer : null;
        var owner = player != null ? player.GetComponentInChildren<PlayerOwner>() : null;

        if (owner == null || owner.InputTree is not InputTree tree || tree == null)
        {
            Plugin.Log.LogWarning("No input tree found for the local player, the game keeps its input while the editor is open");
        }
        else
        {
            // Right after the player owner in the list, so that we run just before it
            tree._children.Remove(this);
            var ownerIndex = tree._children.IndexOf(owner);
            tree._children.Insert(ownerIndex < 0 ? 0 : ownerIndex + 1, this);
            _tree = tree;
        }

        ApplyFreeCamera();
    }

    /// <summary>
    /// Removes this node from the input tree, which gives the game back its cursor and input, and always leaves Fika's
    /// free camera component enabled. Safe to call when the tree or camera were already destroyed with the raid.
    /// </summary>
    public void Close()
    {
        _open = false;
        _flying = false;

        if (_tree != null) _tree.Remove(this);
        _tree = null;

        var freeCamera = FindFreeCamera();
        if (freeCamera != null) freeCamera.enabled = true;
    }

    /// <summary>
    /// Tells the node whether the user is flying. While flying the node lets the game lock the cursor and passes all
    /// commands and axes through (so a player on foot can walk and look when free camera is off), and Fika's free
    /// camera runs. While not flying the cursor is shown, look and movement are stopped, game commands are blocked and
    /// Fika's free camera is paused. Called every frame while the editor is open, which also catches free camera being
    /// switched on or off with Fika's own hotkey.
    /// </summary>
    public void SetFlying(bool flying)
    {
        _flying = flying;
        ApplyFreeCamera();
    }

    public override ETranslateResult TranslateCommand(ECommand command)
    {
        if (!_open || _flying) return ETranslateResult.Ignore;

        return command switch
        {
            ECommand.Escape or ECommand.ToggleInventory or ECommand.ShowConsole or ECommand.MakeScreenshot => ETranslateResult.Ignore,
            _ => ETranslateResult.Block
        };
    }

    public override void TranslateAxes(ref float[] axes)
    {
        if (_open && !_flying) axes = null;
    }

    public override ECursorResult ShouldLockCursor()
    {
        return _open && !_flying ? ECursorResult.ShowCursor : ECursorResult.Ignore;
    }

    /// <summary>Runs Fika's free camera while flying or while it is switched off (it then does nothing), pauses it otherwise.</summary>
    private void ApplyFreeCamera()
    {
        var freeCamera = FindFreeCamera();
        if (freeCamera == null) return;

        var run = !_open || _flying || !freeCamera.IsActive;
        if (freeCamera.enabled != run) freeCamera.enabled = run;
    }

    /// <summary>Fika's free camera component on the main camera, or null when there is none (not in a raid yet).</summary>
    private FikaFreeCamera FindFreeCamera()
    {
        if (_freeCamera != null) return _freeCamera;
        if (!CameraManager.Exist) return null;

        var camera = CameraManager.Instance.Camera;
        _freeCamera = camera != null ? camera.GetComponent<FikaFreeCamera>() : null;

        return _freeCamera;
    }
}
