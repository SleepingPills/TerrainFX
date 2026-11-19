using Comfort.Common;
using EFT;
using EFT.CameraControl;
using UnityEngine;

namespace TerrainFX.Editor;

/// <summary>
/// The editor's modes. A new mode adds a value here and a case to each switch in <see cref="TerrainEditor"/> and
/// <see cref="EditorWindow"/>.
/// </summary>
public enum EditorMode
{
    /// <summary>Paints and erases custom terrain details.</summary>
    DetailPainting
}

/// <summary>
/// The in-raid editor. This one MonoBehaviour drives everything in a fixed, visible order: closing when the raid
/// ends, the toggle hotkey, flying with the right mouse button, the other hotkeys, the current mode's update, the brush
/// guide, the previews and the window.
///
/// While the editor is open the mouse cursor is free: left click paints under the cursor or uses the window. Holding
/// the right mouse button (pressed outside the window) gives the game its input back so the user can fly with Fika's
/// free camera (or walk, when free camera is off). Nothing is painted while flying.
/// </summary>
public class TerrainEditor : MonoBehaviour
{
    private const float RadiusStep = 1.15f;

    private bool _open;
    private bool _inRaid;
    private bool _flying;
    private bool _flyPressStartedOverWindow;
    private EditorMode _mode = EditorMode.DetailPainting;

    private EditorInput _input;
    private readonly DetailPaintMode _detailPaint = new();
    private readonly BrushOverlay _overlay = new();
    private readonly DetailPreview _preview = new();
    private readonly EditorWindow _window = new();

    private void Awake()
    {
        _input = gameObject.AddComponent<EditorInput>();
    }

    private void Update()
    {
        // 1. Leaving the raid closes the editor and forgets the strokes, whose terrains are gone
        var camera = MainCamera();
        var inRaid = camera != null && Singleton<GameWorld>.Instantiated;

        if (!inRaid)
        {
            if (_open) Close();
            if (_inRaid) _detailPaint.Reset();

            _inRaid = false;
            return;
        }

        _inRaid = true;

        // 2. The toggle hotkey
        if (Hotkey.Pressed(Plugin.ToggleEditorKey.Value))
        {
            if (_open) Close();
            else Open();
        }

        if (!_open) return;

        // 3. Flying while the right mouse button is held. A press that starts over the window does not count, and
        // flying starts the frame after the press so Fika's free camera never sees the button go down.
        if (Input.GetMouseButtonDown(1)) _flyPressStartedOverWindow = _window.ContainsMouse;
        _flying = Input.GetMouseButton(1) && !Input.GetMouseButtonDown(1) && !_flyPressStartedOverWindow;

        _input.SetFlying(_flying);

        if (_flying)
        {
            _detailPaint.EndStroke();
            _detailPaint.ClearHover();
            return;
        }

        // 4. The other hotkeys
        HandleHotkeys();

        // 5. The mode
        var pointerBlocked = _window.ContainsMouse || GUIUtility.hotControl != 0;

        switch (_mode)
        {
            case EditorMode.DetailPainting:
                _detailPaint.Update(MouseRay(camera), pointerBlocked);
                break;
        }
    }

    private void LateUpdate()
    {
        if (!_open) return;

        var camera = MainCamera();
        if (camera == null) return;

        // The brush guide, only while not flying
        if (!_flying)
        {
            switch (_mode)
            {
                case EditorMode.DetailPainting:
                    _overlay.Draw(camera, _detailPaint);
                    break;
            }
        }

        // The previews the window asked for in its last OnGUI
        _preview.Render();
    }

    private void OnGUI()
    {
        if (!_open) return;

        _mode = _window.Draw(_mode, _detailPaint, _preview);
    }

    private void OnDestroy()
    {
        if (_open) Close();

        _window.Dispose();
    }

    private void Open()
    {
        _open = true;
        _flying = false;
        _flyPressStartedOverWindow = false;

        _input.Open();
        _detailPaint.EnsureSelection();
    }

    /// <summary>
    /// Closes the editor: gives the game back its input and Fika its free camera, ends the stroke in progress and
    /// releases the brush guide and preview resources. Unsaved painting stays unsaved and can still be saved after
    /// opening the editor again.
    /// </summary>
    private void Close()
    {
        _open = false;
        _flying = false;

        _input.Close();
        _detailPaint.EndStroke();
        _detailPaint.ClearHover();
        _overlay.Dispose();
        _preview.Dispose();
    }

    private void HandleHotkeys()
    {
        switch (_mode)
        {
            case EditorMode.DetailPainting:
                if (Hotkey.Pressed(Plugin.BrushKey.Value)) _detailPaint.Tool = DetailTool.Brush;
                if (Hotkey.Pressed(Plugin.EraserKey.Value)) _detailPaint.Tool = DetailTool.Eraser;
                if (Hotkey.Pressed(Plugin.BrushSmallerKey.Value)) _detailPaint.ScaleRadius(1f / RadiusStep);
                if (Hotkey.Pressed(Plugin.BrushLargerKey.Value)) _detailPaint.ScaleRadius(RadiusStep);
                if (Hotkey.Pressed(Plugin.UndoKey.Value)) _detailPaint.Undo();
                if (Hotkey.Pressed(Plugin.RedoKey.Value)) _detailPaint.Redo();
                if (Hotkey.Pressed(Plugin.SaveKey.Value)) _detailPaint.Save();
                break;
        }
    }

    /// <summary>The game's main camera, or null when there is none (not in a raid).</summary>
    private static Camera MainCamera()
    {
        return CameraManager.Exist ? CameraManager.Instance.Camera : null;
    }

    /// <summary>
    /// The ray through the mouse cursor. Built from viewport coordinates because the main camera may render at a
    /// different pixel size than the screen (supersampling, upscalers), which would make ScreenPointToRay miss.
    /// </summary>
    private static Ray MouseRay(Camera camera)
    {
        var mouse = Input.mousePosition;
        return camera.ViewportPointToRay(new Vector3(mouse.x / Screen.width, mouse.y / Screen.height, 0f));
    }
}
