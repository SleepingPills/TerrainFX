using System.Collections.Generic;
using TerrainFX.Details;
using UnityEngine;

namespace TerrainFX.Editor;

/// <summary>The two tools of detail painting.</summary>
public enum DetailTool
{
    /// <summary>Adds instances of the selected detail.</summary>
    Brush,

    /// <summary>Removes instances of the selected detail.</summary>
    Eraser
}

/// <summary>
/// Everything one brush stroke changed: for each terrain it touched, the density cell edits in the order they were
/// made. Undoing a stroke writes the edits back in reverse order.
/// </summary>
public sealed class DetailStroke
{
    /// <summary>The detail that was painted. A stroke always paints a single detail.</summary>
    public readonly string DetailName;

    /// <summary>The terrains the stroke touched. Entry i belongs to entry i of <see cref="Edits"/>.</summary>
    public readonly List<Terrain> Terrains = [];

    /// <summary>The edits made on each terrain the stroke touched. Entry i holds the edits made on entry i of <see cref="Terrains"/>.</summary>
    public readonly List<List<DetailCellEdit>> Edits = [];

    /// <summary>Creates an empty stroke for the named detail.</summary>
    public DetailStroke(string detailName)
    {
        DetailName = detailName;
    }

    /// <summary>The total number of cell edits on all terrains.</summary>
    public int EditCount
    {
        get
        {
            var count = 0;
            for (var i = 0; i < Edits.Count; i++) count += Edits[i].Count;
            return count;
        }
    }

    /// <summary>Returns the list of edits of a terrain, creating it when the stroke has not touched that terrain yet.</summary>
    public List<DetailCellEdit> EditsOn(Terrain terrain)
    {
        // A stroke touches only a handful of terrains, so a linear search is enough
        for (var i = 0; i < Terrains.Count; i++)
        {
            if (Terrains[i] == terrain) return Edits[i];
        }

        var edits = new List<DetailCellEdit>();
        Terrains.Add(terrain);
        Edits.Add(edits);
        return edits;
    }

    /// <summary>True when at least one terrain the stroke touched still exists.</summary>
    public bool HasLivingTerrain()
    {
        for (var i = 0; i < Terrains.Count; i++)
        {
            var terrain = Terrains[i];
            if (terrain != null) return true;
        }

        return false;
    }
}

/// <summary>
/// The detail painting mode: the brush settings the window shows, what is under the mouse cursor, brush strokes,
/// undo and redo, and saving. A plain class that <see cref="TerrainEditor"/> calls every frame while the editor is open
/// and the user is not flying.
/// </summary>
public sealed class DetailPaintMode
{
    /// <summary>Smallest brush radius in metres.</summary>
    public const float MinRadius = 0.5f;

    /// <summary>Largest brush radius in metres.</summary>
    public const float MaxRadius = 30f;

    /// <summary>Smallest share of cells the brush fills.</summary>
    public const float MinCoverage = 0.01f;

    /// <summary>How far, in metres, the mouse ray looks for terrain.</summary>
    public const float MaxRayDistance = 600f;

    private const int MaxUndo = 100;
    private const float StatusSeconds = 4f;

    /// <summary>The detail that is painted. When null, the first registered detail is picked on the next update.</summary>
    public Detail Selected;

    /// <summary>Whether painting adds (brush) or removes (eraser) instances.</summary>
    public DetailTool Tool = DetailTool.Brush;

    /// <summary>Radius of the brush in metres, from <see cref="MinRadius"/> to <see cref="MaxRadius"/>.</summary>
    public float Radius = 3f;

    /// <summary>Instances put into each painted cell, from 1 to the selected detail's <see cref="Detail.MaxCount"/>.</summary>
    public int Count = 1;

    /// <summary>Share of the cells in the brush's solid middle that painting fills, from 0.01 to 1.</summary>
    public float Coverage = 0.25f;

    /// <summary>Share of the radius that has the full effect, from 0 (fades from the centre) to 1 (hard edge).</summary>
    public float Hardness = 1f;

    /// <summary>True when the mouse cursor is over terrain the brush can paint on this frame.</summary>
    public bool HasHover { get; private set; }

    /// <summary>The terrain under the mouse cursor, or null when <see cref="HasHover"/> is false.</summary>
    public Terrain HoverTerrain { get; private set; }

    /// <summary>The point on the terrain surface under the mouse cursor. Only meaningful when <see cref="HasHover"/> is true.</summary>
    public Vector3 HoverPoint { get; private set; }

    /// <summary>
    /// Goes up by one every time painting, undo or redo changes density cells. The brush overlay uses it to know that
    /// the cells the next click would change have to be worked out again.
    /// </summary>
    public int EditVersion { get; private set; }

    /// <summary>True while the left mouse button is held down for a stroke.</summary>
    public bool IsStroking => _stroke != null;

    /// <summary>Number of finished strokes that can be undone.</summary>
    public int UndoCount => _undo.Count;

    /// <summary>Number of undone strokes that can be redone.</summary>
    public int RedoCount => _redo.Count;

    /// <summary>The last status message (for example the result of saving) while it is recent, otherwise null.</summary>
    public string Status => Time.unscaledTime < _statusUntil ? _status : null;

    private readonly List<DetailStroke> _undo = [];
    private readonly List<DetailStroke> _redo = [];
    private readonly List<Terrain> _stampTerrains = [];

    private DetailStroke _stroke;
    private Vector3 _lastStampPoint;
    private string _mapId;
    private string _status;
    private float _statusUntil;

    /// <summary>The brush the current settings describe.</summary>
    public DetailBrush Brush => new()
    {
        Radius = Radius,
        Count = Count,
        Coverage = Coverage,
        Hardness = Hardness,
        Erase = Tool == DetailTool.Eraser
    };

    /// <summary>
    /// Runs one frame of painting. Finds the terrain under <paramref name="mouseRay"/>, starts a stroke when the left
    /// mouse button goes down over terrain, stamps while it is held and the cursor moves, and ends the stroke when the
    /// button is released. When <paramref name="pointerBlocked"/> is true (the cursor is over the editor window or a
    /// window control is being dragged) there is no hover and no new stroke starts.
    /// </summary>
    public void Update(Ray mouseRay, bool pointerBlocked)
    {
        CheckMap();
        EnsureSelection();

        HasHover = false;
        HoverTerrain = null;

        if (!pointerBlocked && Selected != null && DetailTerrains.TryFindUnderRay(mouseRay, MaxRayDistance, out var terrain, out var point))
        {
            HasHover = true;
            HoverTerrain = terrain;
            HoverPoint = point;
        }

        if (_stroke == null)
        {
            if (Input.GetMouseButtonDown(0) && HasHover)
            {
                _stroke = new DetailStroke(Selected.Name);
                Stamp(HoverPoint);
            }

            return;
        }

        if (!Input.GetMouseButton(0))
        {
            EndStroke();
            return;
        }

        if (HasHover && Vector3.Distance(HoverPoint, _lastStampPoint) > Radius * 0.25f) Stamp(HoverPoint);
    }

    /// <summary>Forgets what is under the cursor, so the brush overlay is not drawn. Used while flying.</summary>
    public void ClearHover()
    {
        HasHover = false;
        HoverTerrain = null;
    }

    /// <summary>
    /// Finishes the current stroke, if any. A stroke that changed at least one cell goes on the undo stack (which keeps
    /// the last 100) and clears the redo stack; a stroke that changed nothing is dropped.
    /// </summary>
    public void EndStroke()
    {
        if (_stroke == null) return;

        if (_stroke.EditCount > 0)
        {
            _undo.Add(_stroke);
            if (_undo.Count > MaxUndo) _undo.RemoveAt(0);
            _redo.Clear();
        }

        _stroke = null;
    }

    /// <summary>
    /// Undoes the last finished stroke on every terrain it touched that still exists and moves it to the redo stack.
    /// Ends the current stroke first. Strokes whose terrains are all gone are dropped and the next one is tried.
    /// </summary>
    public void Undo()
    {
        EndStroke();

        while (_undo.Count > 0)
        {
            var stroke = _undo[_undo.Count - 1];
            _undo.RemoveAt(_undo.Count - 1);

            if (!stroke.HasLivingTerrain()) continue;

            ApplyStroke(stroke, true);
            _redo.Add(stroke);
            return;
        }
    }

    /// <summary>
    /// Applies the last undone stroke again and moves it back to the undo stack. Strokes whose terrains are all gone are
    /// dropped and the next one is tried.
    /// </summary>
    public void Redo()
    {
        EndStroke();

        while (_redo.Count > 0)
        {
            var stroke = _redo[_redo.Count - 1];
            _redo.RemoveAt(_redo.Count - 1);

            if (!stroke.HasLivingTerrain()) continue;

            ApplyStroke(stroke, false);
            _undo.Add(stroke);
            return;
        }
    }

    /// <summary>Writes every painted but unsaved density map to disk and shows how many files were written.</summary>
    public void Save()
    {
        EndStroke();

        var written = DetailDensityStore.SaveDirty();
        SetStatus(written > 0 ? $"Saved {written} file(s)" : "Nothing to save");
    }

    /// <summary>Selects a detail and clamps the instance count to what its cells can hold.</summary>
    public void Select(Detail detail)
    {
        if (detail == Selected) return;

        EndStroke();
        Selected = detail;
        ClampSettings();
    }

    /// <summary>Changes the brush radius by a factor (above 1 grows it), keeping it within its limits.</summary>
    public void ScaleRadius(float factor)
    {
        Radius = Mathf.Clamp(Radius * factor, MinRadius, MaxRadius);
    }

    /// <summary>Keeps every brush setting within its limits. The window calls it after the user moved a slider.</summary>
    public void ClampSettings()
    {
        Radius = Mathf.Clamp(Radius, MinRadius, MaxRadius);
        Count = Mathf.Clamp(Count, 1, MaxCount);
        Coverage = Mathf.Clamp(Coverage, MinCoverage, 1f);
        Hardness = Mathf.Clamp01(Hardness);
    }

    /// <summary>The largest count a cell of the selected detail can hold, or 1 when nothing is selected.</summary>
    public int MaxCount => Selected != null ? Mathf.Max(1, Selected.MaxCount) : 1;

    /// <summary>Picks the first registered detail when none is selected, or when the selected one is no longer registered.</summary>
    public void EnsureSelection()
    {
        if (Selected != null && DetailRegistry.Details.Contains(Selected)) return;

        Selected = DetailRegistry.Details.Count > 0 ? DetailRegistry.Details[0] : null;
        ClampSettings();
    }

    /// <summary>Ends the current stroke, forgets the hover and empties the undo and redo stacks. Called when the raid ends.</summary>
    public void Reset()
    {
        _stroke = null;
        _undo.Clear();
        _redo.Clear();
        ClearHover();
    }

    /// <summary>Empties the undo and redo stacks when the map whose density is stored has changed.</summary>
    private void CheckMap()
    {
        if (DetailDensityStore.MapId == _mapId) return;

        _mapId = DetailDensityStore.MapId;
        Reset();
    }

    /// <summary>
    /// Paints one stamp of the current brush at <paramref name="point"/> onto the terrain under it and onto every
    /// neighbouring terrain the brush circle reaches (checked one radius away along X and Z), recording the edits in
    /// the current stroke. Each terrain is painted once, with the same world point. Terrains that do not carry the
    /// detail, or are still loading, are skipped.
    /// </summary>
    private void Stamp(Vector3 point)
    {
        _stampTerrains.Clear();
        AddStampTerrain(HoverTerrain);
        AddStampTerrain(DetailTerrains.TerrainAt(point + new Vector3(Radius, 0f, 0f)));
        AddStampTerrain(DetailTerrains.TerrainAt(point - new Vector3(Radius, 0f, 0f)));
        AddStampTerrain(DetailTerrains.TerrainAt(point + new Vector3(0f, 0f, Radius)));
        AddStampTerrain(DetailTerrains.TerrainAt(point - new Vector3(0f, 0f, Radius)));

        var brush = Brush;
        var changed = 0;

        for (var i = 0; i < _stampTerrains.Count; i++)
        {
            var terrain = _stampTerrains[i];
            // Checked first because painting a terrain without the detail logs a warning every time
            if (!DetailPainter.TryGetResolution(terrain, _stroke.DetailName, out _)) continue;

            changed += DetailPainter.Paint(terrain, point, _stroke.DetailName, brush, _stroke.EditsOn(terrain));
        }

        _lastStampPoint = point;
        if (changed > 0) EditVersion++;
    }

    private void AddStampTerrain(Terrain terrain)
    {
        if (terrain != null && !_stampTerrains.Contains(terrain)) _stampTerrains.Add(terrain);
    }

    private void ApplyStroke(DetailStroke stroke, bool undo)
    {
        for (var i = 0; i < stroke.Terrains.Count; i++)
        {
            var terrain = stroke.Terrains[i];
            var edits = stroke.Edits[i];
            if (terrain == null) continue;

            DetailPainter.Apply(terrain, stroke.DetailName, edits, undo);
        }

        EditVersion++;
    }

    private void SetStatus(string message)
    {
        _status = message;
        _statusUntil = Time.unscaledTime + StatusSeconds;
    }
}
