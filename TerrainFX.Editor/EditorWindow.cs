using System;
using TerrainFX.Details;
using UnityEngine;
using Object = UnityEngine.Object;

namespace TerrainFX.Editor;

/// <summary>
/// Draws the editor window with IMGUI: the mode toolbar, then the panel of the current mode. It only draws and
/// writes what the user changed straight into the mode's public state; it keeps no editing state of its own apart
/// from its position and scroll offset.
///
/// The window is scaled by the UI Scale setting through GUI.matrix, so its rectangle is kept in unscaled units and
/// <see cref="ContainsMouse"/> divides the mouse position by the scale before testing it.
/// </summary>
public sealed class EditorWindow
{
    private const int WindowId = 0x54465845;
    private const float Width = 340f;
    private const float TitleHeight = 24f;
    private const float PreviewSize = 160f;
    private const float GridCell = 92f;
    private const float GridHeight = 250f;

    private static readonly Color WindowColor = new(0.11f, 0.115f, 0.125f, 0.97f);
    private static readonly Color BoxColor = new(0.16f, 0.165f, 0.18f, 1f);
    private static readonly Color ButtonColor = new(0.22f, 0.23f, 0.25f, 1f);
    private static readonly Color ButtonHoverColor = new(0.28f, 0.29f, 0.32f, 1f);
    private static readonly Color ButtonActiveColor = new(0.18f, 0.19f, 0.21f, 1f);
    private static readonly Color AccentColor = new(0.2f, 0.52f, 0.85f, 1f);
    private static readonly Color AccentHoverColor = new(0.26f, 0.58f, 0.92f, 1f);
    private static readonly Color SliderTrackColor = new(0.25f, 0.26f, 0.28f, 1f);
    private static readonly Color SliderThumbColor = new(0.75f, 0.77f, 0.8f, 1f);
    private static readonly Color TextColor = new(0.88f, 0.89f, 0.9f, 1f);
    private static readonly Color DimTextColor = new(0.6f, 0.62f, 0.65f, 1f);

    // Read once: Enum.GetValues allocates a new array on every call, and the toolbar is drawn on every OnGUI event
    private static readonly EditorMode[] Modes = (EditorMode[])Enum.GetValues(typeof(EditorMode));

    private Rect _rect = new(24f, 96f, Width, 10f);
    private Vector2 _gridScroll;
    private float _scale = 1f;

    // State handed to the window function. Unity does not call the window function inside GUILayout.Window but after
    // Draw has returned, so these fields must keep the values of the latest Draw call and are never cleared.
    private EditorMode _mode;
    private DetailPaintMode _paint;
    private DetailPreview _preview;

    // The mode the user clicked in the toolbar. The click is seen inside the deferred window function, after Draw has
    // already returned, so it is kept here and handed back by the next Draw call instead of the caller's mode.
    private EditorMode? _clickedMode;

    // Made once because passing the DrawContents method group to GUILayout.Window would create a new delegate on every OnGUI event
    private readonly GUI.WindowFunction _drawContents;

    // Reused for every detail tile, since IMGUI only reads the content during the Button call itself
    private readonly GUIContent _tileContent = new();

    private bool _stylesBuilt;
    private Texture2D[] _textures = [];
    private GUIStyle _windowStyle;
    private GUIStyle _boxStyle;
    private GUIStyle _buttonStyle;
    private GUIStyle _selectedButtonStyle;
    private GUIStyle _tileStyle;
    private GUIStyle _selectedTileStyle;
    private GUIStyle _labelStyle;
    private GUIStyle _dimLabelStyle;
    private GUIStyle _headingStyle;
    private GUIStyle _tileLabelStyle;
    private GUIStyle _sliderStyle;
    private GUIStyle _thumbStyle;
    private GUIStyle _previewStyle;

    /// <summary>Creates the window. Its styles are built the first time it is drawn.</summary>
    public EditorWindow()
    {
        _drawContents = DrawContents;
    }

    /// <summary>
    /// True when the mouse cursor is over the window, as it was last drawn. Painting must not start while this is
    /// true. False before the window has been drawn once.
    /// </summary>
    public bool ContainsMouse
    {
        get
        {
            var mouse = Input.mousePosition;
            var guiPoint = new Vector2(mouse.x, Screen.height - mouse.y) / _scale;
            return _rect.Contains(guiPoint);
        }
    }

    /// <summary>
    /// Draws the window for the current mode and returns the mode, which is different from <paramref name="mode"/>
    /// when the user clicked another mode in the toolbar. Unity runs the window's contents after this method has
    /// returned, so a toolbar click is returned by the next call, one GUI event later. Call it from OnGUI.
    /// </summary>
    public EditorMode Draw(EditorMode mode, DetailPaintMode paint, DetailPreview preview)
    {
        BuildStyles();

        if (_clickedMode.HasValue)
        {
            mode = _clickedMode.Value;
            _clickedMode = null;
        }

        _mode = mode;
        _paint = paint;
        _preview = preview;
        _scale = Mathf.Clamp(Plugin.UiScale.Value, 0.75f, 2.5f);

        // The scale is set only around the GUILayout.Window call. The window's contents run later, so this relies on
        // Unity keeping the matrix that was active at that call for the window, which the managed code does not show.
        var previousMatrix = GUI.matrix;
        GUI.matrix = Matrix4x4.TRS(Vector3.zero, Quaternion.identity, new Vector3(_scale, _scale, 1f));

        // The returned rectangle is the window's position from earlier events, including a drag made in the contents
        _rect = GUILayout.Window(WindowId, _rect, _drawContents, "TerrainFX Editor", _windowStyle, GUILayout.Width(Width));

        // Keep the title bar reachable on screen
        var screenWidth = Screen.width / _scale;
        var screenHeight = Screen.height / _scale;
        _rect.x = Mathf.Clamp(_rect.x, 0f, Mathf.Max(0f, screenWidth - _rect.width));
        _rect.y = Mathf.Clamp(_rect.y, 0f, Mathf.Max(0f, screenHeight - TitleHeight));

        GUI.matrix = previousMatrix;

        return _mode;
    }

    /// <summary>Destroys the textures the styles are built from. They are built again the next time the window is drawn.</summary>
    public void Dispose()
    {
        for (var i = 0; i < _textures.Length; i++)
        {
            var texture = _textures[i];
            if (texture != null) Object.Destroy(texture);
        }

        _textures = [];
        _stylesBuilt = false;
    }

    private void DrawContents(int id)
    {
        // An exception inside a window function leaves IMGUI's layout broken for the frame, so draw nothing without a mode to draw
        if (_paint == null || _preview == null)
        {
            GUI.DragWindow(new Rect(0f, 0f, Width, TitleHeight));
            return;
        }

        DrawModeToolbar();
        GUILayout.Space(6f);

        switch (_mode)
        {
            case EditorMode.DetailPainting:
                DrawDetailPainting();
                break;
        }

        GUI.DragWindow(new Rect(0f, 0f, Width, TitleHeight));
    }

    private void DrawModeToolbar()
    {
        GUILayout.BeginHorizontal();

        for (var i = 0; i < Modes.Length; i++)
        {
            var mode = Modes[i];
            if (GUILayout.Button(ModeName(mode), mode == _mode ? _selectedButtonStyle : _buttonStyle))
            {
                _mode = mode;
                _clickedMode = mode;
            }
        }

        GUILayout.EndHorizontal();
    }

    private static string ModeName(EditorMode mode)
    {
        return mode switch
        {
            EditorMode.DetailPainting => "Detail Painting",
            _ => mode.ToString()
        };
    }

    private void DrawDetailPainting()
    {
        var paint = _paint;
        paint.EnsureSelection();

        if (paint.Selected == null)
        {
            // Without a selection there are only custom details left, all hidden this season
            if (DetailRegistry.Details.Count == 0 && DetailStock.Assets.Count == 0)
            {
                GUILayout.Label("No details are registered and the map has none.", _dimLabelStyle);
            }
            else
            {
                GUILayout.Label("Every detail is hidden in the current season.", _dimLabelStyle);
                DrawDetailGrid(paint);
            }

            DrawStatus();
            return;
        }

        DrawBrushBox(paint);
        GUILayout.Space(6f);
        DrawToolButtons(paint);
        GUILayout.Space(4f);
        DrawSliders(paint);
        GUILayout.Space(8f);
        DrawDetailGrid(paint);
        GUILayout.Space(6f);
        DrawHistoryButtons(paint);
        GUILayout.Space(4f);
        DrawStatus();
    }

    /// <summary>The live preview beside the detail's name, kind and the current brush numbers.</summary>
    private void DrawBrushBox(DetailPaintMode paint)
    {
        var detail = paint.Selected;
        var stock = paint.SelectedStock;

        GUILayout.BeginHorizontal(_boxStyle);

        var previewRect = GUILayoutUtility.GetRect(PreviewSize, PreviewSize, _previewStyle, GUILayout.Width(PreviewSize), GUILayout.Height(PreviewSize));
        GUI.Box(previewRect, GUIContent.none, _previewStyle);

        var live = _preview.GetLive(detail);
        if (live != null) GUI.DrawTexture(previewRect, live, ScaleMode.ScaleToFit);

        GUILayout.Space(8f);
        GUILayout.BeginVertical();
        GUILayout.Label(stock != null ? stock.DisplayName : detail.Name, _headingStyle);
        GUILayout.Label(DetailKind(detail, stock), _dimLabelStyle);
        GUILayout.Space(6f);
        GUILayout.Label(ToolName(paint.Tool), _labelStyle);
        GUILayout.Label($"Size  {paint.Radius:0.0} m", _labelStyle);
        GUILayout.Label($"Density  {paint.Count} / cell", _labelStyle);
        GUILayout.Label($"Coverage  {paint.Coverage * 100f:0}%", _labelStyle);
        GUILayout.Label($"Hardness  {paint.Hardness * 100f:0}%", _labelStyle);
        GUILayout.EndVertical();

        GUILayout.EndHorizontal();
    }

    private static string DetailKind(Detail detail, StockAsset stock)
    {
        if (stock != null) return detail.IsMesh ? "Map detail, mesh" : "Map detail, texture";

        return detail.IsMesh ? "Mesh detail" : "Texture detail";
    }

    private static string ToolName(DetailTool tool)
    {
        return tool switch
        {
            DetailTool.Brush => "Brush",
            DetailTool.Eraser => "Eraser",
            DetailTool.Restore => "Restore",
            _ => tool.ToString()
        };
    }

    /// <summary>The brush, eraser and restore buttons. Restore is only enabled while a map detail is selected.</summary>
    private void DrawToolButtons(DetailPaintMode paint)
    {
        GUILayout.BeginHorizontal();

        if (GUILayout.Button($"Brush ({Plugin.BrushKey.Value})", paint.Tool == DetailTool.Brush ? _selectedButtonStyle : _buttonStyle))
        {
            paint.SetTool(DetailTool.Brush);
        }

        if (GUILayout.Button($"Eraser ({Plugin.EraserKey.Value})", paint.Tool == DetailTool.Eraser ? _selectedButtonStyle : _buttonStyle))
        {
            paint.SetTool(DetailTool.Eraser);
        }

        GUI.enabled = paint.SelectedStock != null;
        if (GUILayout.Button($"Restore ({Plugin.RestoreKey.Value})", paint.Tool == DetailTool.Restore ? _selectedButtonStyle : _buttonStyle))
        {
            paint.SetTool(DetailTool.Restore);
        }

        GUI.enabled = true;

        GUILayout.EndHorizontal();
    }

    private void DrawSliders(DetailPaintMode paint)
    {
        paint.Radius = Slider("Size", paint.Radius, DetailPaintMode.MinRadius, DetailPaintMode.MaxRadius, $"{paint.Radius:0.0} m");
        paint.Count = Mathf.RoundToInt(Slider("Density", paint.Count, 1f, paint.MaxCount, $"{paint.Count} / cell"));
        paint.Coverage = Slider("Coverage", paint.Coverage, DetailPaintMode.MinCoverage, 1f, $"{paint.Coverage * 100f:0}%");
        paint.Hardness = Slider("Hardness", paint.Hardness, 0f, 1f, $"{paint.Hardness * 100f:0}%");
        paint.ClampSettings();
    }

    /// <summary>One row of a label, a slider and a numeric read-out. Returns the slider's new value.</summary>
    private float Slider(string label, float value, float min, float max, string readout)
    {
        GUILayout.BeginHorizontal();
        GUILayout.Label(label, _labelStyle, GUILayout.Width(70f));

        // Without this the slider sits higher than the labels next to it
        GUILayout.BeginVertical();
        GUILayout.Space(7f);
        value = GUILayout.HorizontalSlider(value, min, max, _sliderStyle, _thumbStyle, GUILayout.ExpandWidth(true));
        GUILayout.EndVertical();

        GUILayout.Label(readout, _labelStyle, GUILayout.Width(66f));
        GUILayout.EndHorizontal();

        return value;
    }

    /// <summary>
    /// A scrollable grid of the custom details and then the map's own details, three to a row under a heading per
    /// group, with the selected one outlined. A group without details has no heading. Custom details hidden this
    /// season are dimmed and cannot be selected.
    /// </summary>
    private void DrawDetailGrid(DetailPaintMode paint)
    {
        GUILayout.Label("Details", _headingStyle);

        _gridScroll = GUILayout.BeginScrollView(_gridScroll, false, true, GUIStyle.none, GUI.skin.verticalScrollbar, _boxStyle, GUILayout.Height(GridHeight));

        var details = DetailRegistry.Details;
        if (details.Count > 0)
        {
            GUILayout.Label("Custom details", _dimLabelStyle);

            for (var row = 0; row * 3 < details.Count; row++)
            {
                GUILayout.BeginHorizontal();

                for (var column = 0; column < 3; column++)
                {
                    var index = row * 3 + column;
                    if (index >= details.Count) break;

                    var detail = details[index];
                    DrawTile(paint, detail, detail.Name, DetailSeasons.IsHidden(detail));
                }

                GUILayout.EndHorizontal();
            }
        }

        var assets = DetailStock.Assets;
        if (assets.Count > 0)
        {
            if (details.Count > 0) GUILayout.Space(6f);
            GUILayout.Label("Map details", _dimLabelStyle);

            for (var row = 0; row * 3 < assets.Count; row++)
            {
                GUILayout.BeginHorizontal();

                for (var column = 0; column < 3; column++)
                {
                    var index = row * 3 + column;
                    if (index >= assets.Count) break;

                    var asset = assets[index];
                    DrawTile(paint, asset.Detail, asset.DisplayName, false);
                }

                GUILayout.EndHorizontal();
            }
        }

        GUILayout.EndScrollView();
    }

    /// <summary>
    /// One tile of the detail grid: the thumbnail as a button that selects the detail, and its caption below. A hidden
    /// detail's tile is dimmed, cannot be clicked and has a second caption line saying it is hidden.
    /// </summary>
    private void DrawTile(DetailPaintMode paint, Detail detail, string caption, bool hidden)
    {
        var selected = detail == paint.Selected;

        GUILayout.BeginVertical(GUILayout.Width(GridCell));
        GUI.enabled = !hidden;

        var thumbnail = _preview.GetThumbnail(detail);
        // The same fields the two GUIContent constructors set: image and tooltip, or text alone
        _tileContent.image = thumbnail;
        _tileContent.text = thumbnail != null ? string.Empty : caption;
        _tileContent.tooltip = thumbnail != null ? caption : string.Empty;
        if (GUILayout.Button(_tileContent, selected ? _selectedTileStyle : _tileStyle, GUILayout.Width(GridCell), GUILayout.Height(GridCell)))
        {
            paint.Select(detail);
        }

        GUILayout.Label(caption, _tileLabelStyle, GUILayout.Width(GridCell));
        if (hidden) GUILayout.Label("Hidden this season", _tileLabelStyle, GUILayout.Width(GridCell));

        GUI.enabled = true;
        GUILayout.EndVertical();
    }

    private void DrawHistoryButtons(DetailPaintMode paint)
    {
        GUILayout.BeginHorizontal();

        GUI.enabled = paint.UndoCount > 0;
        if (GUILayout.Button($"Undo ({paint.UndoCount})", _buttonStyle)) paint.Undo();

        GUI.enabled = paint.RedoCount > 0;
        if (GUILayout.Button($"Redo ({paint.RedoCount})", _buttonStyle)) paint.Redo();

        GUI.enabled = true;
        var dirty = DetailDensityStore.DirtyCount;
        if (GUILayout.Button(dirty > 0 ? $"Save ({dirty} unsaved)" : "Save", dirty > 0 ? _selectedButtonStyle : _buttonStyle)) paint.Save();

        GUILayout.EndHorizontal();
    }

    private void DrawStatus()
    {
        var paint = _paint;
        var terrain = paint.HoverTerrain != null ? paint.HoverTerrain.name : "-";

        GUILayout.Label($"Map: {DetailDensityStore.MapId ?? "-"}    Terrain: {terrain}", _dimLabelStyle);

        var paintStatus = paint.PaintStatus;
        if (paintStatus != null) GUILayout.Label(paintStatus, _labelStyle);

        var status = paint.Status;
        if (status != null) GUILayout.Label(status, _labelStyle);

        GUILayout.Label($"Hold right mouse button to fly. {Plugin.ToggleEditorKey.Value} closes the editor.", _dimLabelStyle);
    }

    /// <summary>Builds the dark flat styles once, from copies of the default skin's styles and 1x1 colour textures.</summary>
    private void BuildStyles()
    {
        if (_stylesBuilt) return;

        var window = Solid(WindowColor);
        var box = Solid(BoxColor);
        var button = Solid(ButtonColor);
        var buttonHover = Solid(ButtonHoverColor);
        var buttonActive = Solid(ButtonActiveColor);
        var accent = Solid(AccentColor);
        var accentHover = Solid(AccentHoverColor);
        var track = Solid(SliderTrackColor);
        var thumb = Solid(SliderThumbColor);
        var previewBackground = Solid(new Color(0.16f, 0.17f, 0.18f, 1f));
        _textures = [window, box, button, buttonHover, buttonActive, accent, accentHover, track, thumb, previewBackground];

        _windowStyle = new GUIStyle(GUI.skin.window)
        {
            padding = new RectOffset(10, 10, (int)TitleHeight + 4, 10),
            border = new RectOffset(0, 0, 0, 0),
            alignment = TextAnchor.UpperCenter,
            fontStyle = FontStyle.Bold,
            fontSize = 13,
            contentOffset = new Vector2(0f, -(TitleHeight - 4f))
        };
        SetBackground(_windowStyle, window, TextColor);

        _boxStyle = new GUIStyle(GUI.skin.box) { padding = new RectOffset(6, 6, 6, 6), margin = new RectOffset(0, 0, 2, 2), border = new RectOffset(0, 0, 0, 0) };
        SetBackground(_boxStyle, box, TextColor);

        _buttonStyle = new GUIStyle(GUI.skin.button) { border = new RectOffset(0, 0, 0, 0), padding = new RectOffset(6, 6, 5, 5), fontSize = 12 };
        SetBackground(_buttonStyle, button, TextColor);
        _buttonStyle.hover.background = buttonHover;
        _buttonStyle.active.background = buttonActive;

        _selectedButtonStyle = new GUIStyle(_buttonStyle);
        SetBackground(_selectedButtonStyle, accent, Color.white);
        _selectedButtonStyle.hover.background = accentHover;
        _selectedButtonStyle.active.background = accent;

        _tileStyle = new GUIStyle(_buttonStyle) { padding = new RectOffset(3, 3, 3, 3), margin = new RectOffset(2, 2, 2, 2), imagePosition = ImagePosition.ImageOnly };
        SetBackground(_tileStyle, button, TextColor);
        _tileStyle.hover.background = buttonHover;
        _tileStyle.active.background = buttonActive;

        // The accent background shows as a frame around the thumbnail, which covers the inside
        _selectedTileStyle = new GUIStyle(_tileStyle);
        SetBackground(_selectedTileStyle, accent, Color.white);
        _selectedTileStyle.hover.background = accentHover;
        _selectedTileStyle.active.background = accent;

        _labelStyle = new GUIStyle(GUI.skin.label) { fontSize = 12, wordWrap = false, padding = new RectOffset(0, 0, 1, 1) };
        _labelStyle.normal.textColor = TextColor;

        _dimLabelStyle = new GUIStyle(_labelStyle) { fontSize = 11, wordWrap = true };
        _dimLabelStyle.normal.textColor = DimTextColor;

        _headingStyle = new GUIStyle(_labelStyle) { fontSize = 13, fontStyle = FontStyle.Bold, wordWrap = true };

        _tileLabelStyle = new GUIStyle(_labelStyle) { fontSize = 10, alignment = TextAnchor.UpperCenter, clipping = TextClipping.Clip };
        _tileLabelStyle.normal.textColor = DimTextColor;

        _sliderStyle = new GUIStyle(GUI.skin.horizontalSlider) { border = new RectOffset(0, 0, 0, 0), fixedHeight = 4f, margin = new RectOffset(4, 4, 0, 0) };
        SetBackground(_sliderStyle, track, TextColor);

        _thumbStyle = new GUIStyle(GUI.skin.horizontalSliderThumb) { border = new RectOffset(0, 0, 0, 0), fixedWidth = 10f, fixedHeight = 14f, margin = new RectOffset(0, 0, -5, 0) };
        SetBackground(_thumbStyle, thumb, TextColor);
        _thumbStyle.hover.background = Solid(Color.white);
        _thumbStyle.active.background = _thumbStyle.hover.background;
        _textures = [.. _textures, _thumbStyle.hover.background];

        _previewStyle = new GUIStyle(GUIStyle.none);
        _previewStyle.normal.background = previewBackground;

        _stylesBuilt = true;
    }

    /// <summary>Gives every state of the style the same background and text colour, including the "on" states.</summary>
    private static void SetBackground(GUIStyle style, Texture2D background, Color textColor)
    {
        var states = new[] { style.normal, style.hover, style.active, style.focused, style.onNormal, style.onHover, style.onActive, style.onFocused };
        for (var i = 0; i < states.Length; i++)
        {
            var state = states[i];
            state.background = background;
            state.textColor = textColor;
        }
    }

    private static Texture2D Solid(Color color)
    {
        var texture = new Texture2D(1, 1, TextureFormat.RGBA32, false) { hideFlags = HideFlags.HideAndDontSave };
        texture.SetPixel(0, 0, color);
        texture.Apply();
        return texture;
    }
}
