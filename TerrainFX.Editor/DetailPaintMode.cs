using System.Collections.Generic;
using TerrainFX.Details;
using UnityEngine;

namespace TerrainFX.Editor;

/// <summary>The three tools of detail painting.</summary>
public enum DetailTool
{
    /// <summary>Adds instances of the selected detail.</summary>
    Brush,

    /// <summary>
    /// Removes instances of the selected detail. For a map detail it removes both the painted additions and the
    /// game's own instances.
    /// </summary>
    Eraser,

    /// <summary>Map details only: brings back the game's own instances that the eraser removed.</summary>
    Restore
}

/// <summary>
/// Everything one brush stroke changed: for each terrain it touched, the density cell edits of the painted detail and
/// the removal mask edits of the game's own instances, each in the order they were made. Undoing a stroke writes the
/// edits back in reverse order.
/// </summary>
public sealed class DetailStroke
{
    /// <summary>The detail that was painted. A stroke always paints a single detail.</summary>
    public readonly Detail Detail;

    /// <summary>The map asset <see cref="Detail"/> describes, or null when a custom detail was painted.</summary>
    public readonly StockAsset Stock;

    /// <summary>The terrains the stroke touched. Entry i belongs to entry i of <see cref="Edits"/> and of <see cref="Removals"/>.</summary>
    public readonly List<Terrain> Terrains = [];

    /// <summary>The density edits made on each terrain the stroke touched. Entry i holds the edits made on entry i of <see cref="Terrains"/>.</summary>
    public readonly List<List<DetailCellEdit>> Edits = [];

    /// <summary>
    /// The removal mask edits made on each terrain the stroke touched. Entry i holds the edits made on entry i of
    /// <see cref="Terrains"/>. The lists stay empty when <see cref="Stock"/> is null.
    /// </summary>
    public readonly List<List<DetailCellEdit>> Removals = [];

    /// <summary>Creates an empty stroke for a detail and its map asset, which is null for a custom detail.</summary>
    public DetailStroke(Detail detail, StockAsset stock)
    {
        Detail = detail;
        Stock = stock;
    }

    /// <summary>The total number of density and removal edits on all terrains.</summary>
    public int EditCount
    {
        get
        {
            var count = 0;
            for (var i = 0; i < Edits.Count; i++) count += Edits[i].Count + Removals[i].Count;
            return count;
        }
    }

    /// <summary>Returns the list of density edits of a terrain, creating the terrain's lists when the stroke has not touched it yet.</summary>
    public List<DetailCellEdit> EditsOn(Terrain terrain)
    {
        return Edits[IndexOf(terrain)];
    }

    /// <summary>Returns the list of removal edits of a terrain, creating the terrain's lists when the stroke has not touched it yet.</summary>
    public List<DetailCellEdit> RemovalsOn(Terrain terrain)
    {
        return Removals[IndexOf(terrain)];
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

    /// <summary>The index of a terrain in <see cref="Terrains"/>, adding it with two empty edit lists when it is not there yet.</summary>
    private int IndexOf(Terrain terrain)
    {
        // A stroke touches only a handful of terrains, so a linear search is enough
        for (var i = 0; i < Terrains.Count; i++)
        {
            if (Terrains[i] == terrain) return i;
        }

        Terrains.Add(terrain);
        Edits.Add([]);
        Removals.Add([]);
        return Terrains.Count - 1;
    }
}

/// <summary>
/// The detail painting mode: the brush settings the window shows, what is under the mouse cursor, brush strokes,
/// undo and redo, and saving. A plain class that <see cref="TerrainEditor"/> calls every frame while the editor is open
/// and the user is not flying.
///
/// Two kinds of detail can be selected: custom details (<see cref="DetailRegistry.Details"/>) and map details, the
/// descriptions of the game's own assets (<see cref="StockAsset.Detail"/>). Painting a map detail adds to the game's
/// instances; erasing it also removes the game's own instances, which the restore tool brings back.
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
    private const string PreparingText = "Preparing terrain...";
    private const string UnavailableText = "Not available on this terrain";

    /// <summary>
    /// The detail that is painted, a custom detail or the description of a map asset. When null, the first
    /// selectable detail is picked on the next update.
    /// </summary>
    public Detail Selected { get; private set; }

    /// <summary>The map asset of <see cref="Selected"/> when a map detail is selected, otherwise null. Resolved when the selection changes.</summary>
    public StockAsset SelectedStock { get; private set; }

    /// <summary>Whether painting adds (brush), removes (eraser) or brings back the game's instances (restore). Changed with <see cref="SetTool"/>.</summary>
    public DetailTool Tool { get; private set; } = DetailTool.Brush;

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
    /// Goes up by one every time painting, undo or redo changes density or removal cells. The brush overlay uses it to
    /// know that the cells the next click would change have to be worked out again.
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

    /// <summary>"Preparing terrain..." while a terrain of the current stroke is still getting the custom detail put on it, otherwise null.</summary>
    public string PaintStatus => _stroke != null && _preparing ? PreparingText : null;

    private readonly List<DetailStroke> _undo = [];
    private readonly List<DetailStroke> _redo = [];
    private readonly List<Terrain> _stampTerrains = [];

    // The additions or removal edits of one map asset stamp on one terrain, applied and then appended to the stroke
    private readonly List<DetailCellEdit> _stockScratch = [];

    private DetailStroke _stroke;
    private Vector3 _lastStampPoint;
    private string _mapId;
    private string _status;
    private float _statusUntil;

    // True when the last stamp met a terrain that is still preparing, so the stamp is repeated on the next frames
    private bool _preparing;

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
    /// mouse button goes down over terrain, stamps while it is held and the cursor moves (or a terrain of the stroke is
    /// still preparing), and ends the stroke when the button is released. When <paramref name="pointerBlocked"/> is
    /// true (the cursor is over the editor window or a window control is being dragged) there is no hover and no new
    /// stroke starts.
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
                _stroke = new DetailStroke(Selected, SelectedStock);
                Stamp(HoverPoint);
            }

            return;
        }

        if (!Input.GetMouseButton(0))
        {
            EndStroke();
            return;
        }

        // A terrain that was preparing is retried even when the cursor has not moved
        if (HasHover && (_preparing || Vector3.Distance(HoverPoint, _lastStampPoint) > Radius * 0.25f)) Stamp(HoverPoint);
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
        _preparing = false;
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

    /// <summary>Writes every painted but unsaved density map and removal mask to disk and shows how many files were written.</summary>
    public void Save()
    {
        EndStroke();

        var written = DetailDensityStore.SaveDirty();
        SetStatus(written > 0 ? $"Saved {written} file(s)" : "Nothing to save");
    }

    /// <summary>
    /// Selects a detail, a custom one or the description of a map asset, and clamps the instance count to what its
    /// cells can hold. A custom detail that is hidden this season, or a detail in neither list, is not selected.
    /// Selecting a custom detail while the restore tool is chosen switches to the brush.
    /// </summary>
    public void Select(Detail detail)
    {
        if (detail == Selected || !IsSelectable(detail)) return;

        SetSelected(detail);
    }

    /// <summary>Chooses a tool. Restore is only chosen while a map detail is selected; otherwise the call does nothing.</summary>
    public void SetTool(DetailTool tool)
    {
        if (tool == DetailTool.Restore && SelectedStock == null) return;

        Tool = tool;
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

    /// <summary>
    /// Keeps the selection valid. When nothing is selected, or the selected detail is in neither list any more (map
    /// details are replaced when the map changes) or is hidden this season, the first custom detail that is not hidden
    /// is selected, else the first map detail, else nothing.
    /// </summary>
    public void EnsureSelection()
    {
        if (Selected != null && IsSelectable(Selected)) return;

        Detail first = null;

        var details = DetailRegistry.Details;
        for (var i = 0; i < details.Count; i++)
        {
            if (DetailSeasons.IsHidden(details[i])) continue;

            first = details[i];
            break;
        }

        if (first == null && DetailStock.Assets.Count > 0) first = DetailStock.Assets[0].Detail;

        SetSelected(first);
    }

    /// <summary>Ends the current stroke, forgets the hover and empties the undo and redo stacks. Called when the raid ends.</summary>
    public void Reset()
    {
        _stroke = null;
        _preparing = false;
        _undo.Clear();
        _redo.Clear();
        ClearHover();
    }

    /// <summary>
    /// True when the detail can be selected: a custom detail that is registered and not hidden this season, or the
    /// description of a map asset of the loaded map.
    /// </summary>
    private static bool IsSelectable(Detail detail)
    {
        if (detail == null) return false;
        if (detail.IsStock) return DetailStock.FindByDetail(detail) != null;

        return DetailRegistry.Details.Contains(detail) && !DetailSeasons.IsHidden(detail);
    }

    /// <summary>Makes a detail (or nothing) the selection, resolves its map asset and keeps the tool and settings valid for it.</summary>
    private void SetSelected(Detail detail)
    {
        EndStroke();

        Selected = detail;
        SelectedStock = detail != null && detail.IsStock ? DetailStock.FindByDetail(detail) : null;

        if (Tool == DetailTool.Restore && SelectedStock == null) Tool = DetailTool.Brush;

        ClampSettings();
    }

    /// <summary>Empties the undo and redo stacks when the map whose density is stored has changed.</summary>
    private void CheckMap()
    {
        if (DetailDensityStore.MapId == _mapId) return;

        _mapId = DetailDensityStore.MapId;
        Reset();
    }

    /// <summary>
    /// Applies one stamp of the current tool at <paramref name="point"/> to the terrain under it and to every
    /// neighbouring terrain the brush circle reaches (checked one radius away along X and Z), recording the edits in
    /// the current stroke. Each terrain is stamped once, with the same world point.
    ///
    /// For a custom detail the brush first makes sure the detail is on the terrain: a terrain that is still preparing
    /// is skipped and the stamp is repeated on the next frames, and when the hovered terrain cannot get the detail a
    /// status message says so. The eraser only erases a custom detail where it already is.
    ///
    /// For a map detail the brush and eraser paint or erase additions in the game's own prototypes, on terrains that
    /// have a prototype of the asset (the hovered terrain reports when it has none); the eraser then also removes the
    /// game's own instances. Restore only brings back the game's own instances.
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
        var detail = _stroke.Detail;
        var stock = _stroke.Stock;
        var changed = 0;
        var preparing = false;

        for (var i = 0; i < _stampTerrains.Count; i++)
        {
            var terrain = _stampTerrains[i];

            switch (Tool)
            {
                case DetailTool.Brush when stock != null:
                    if (!DetailStock.HasStockOn(terrain, stock))
                    {
                        // Only the terrain under the cursor reports, so a neighbour without the asset does not
                        if (terrain == HoverTerrain) SetStatus(UnavailableText);
                        break;
                    }

                    changed += StampAdditions(terrain, point, stock, brush);
                    break;

                case DetailTool.Brush:
                    var state = DetailInjector.EnsureOnTerrain(terrain, detail);
                    if (state == DetailTerrainState.Preparing)
                    {
                        preparing = true;
                        break;
                    }

                    if (state == DetailTerrainState.Unavailable)
                    {
                        // Only the terrain under the cursor reports, so a neighbour without detail managers does not
                        if (terrain == HoverTerrain) SetStatus(UnavailableText);
                        break;
                    }

                    changed += DetailPainter.Paint(terrain, point, detail, brush, _stroke.EditsOn(terrain));
                    break;

                case DetailTool.Eraser when stock != null:
                    // The brush carries Erase, so this erases the additions
                    changed += StampAdditions(terrain, point, stock, brush);
                    changed += StampRemoval(terrain, point, stock, brush, false);
                    break;

                case DetailTool.Eraser:
                    // Checked first because erasing on a terrain without the detail logs a warning
                    if (DetailPainter.TryGetResolution(terrain, detail, out _))
                    {
                        changed += DetailPainter.Paint(terrain, point, detail, brush, _stroke.EditsOn(terrain));
                    }

                    break;

                case DetailTool.Restore:
                    if (stock != null) changed += StampRemoval(terrain, point, stock, brush, true);
                    break;
            }
        }

        _preparing = preparing;
        _lastStampPoint = point;
        if (changed > 0) EditVersion++;
    }

    /// <summary>
    /// Paints (or, when the brush erases, erases) additions of a map asset into the game's own prototypes under one
    /// stamp on one terrain and records the edits in the current stroke's density list. Terrains without that asset
    /// are skipped.
    /// </summary>
    /// <returns>The number of additions cells that changed.</returns>
    private int StampAdditions(Terrain terrain, Vector3 point, StockAsset stock, DetailBrush brush)
    {
        // Checked first because collecting on a terrain without the asset logs a warning
        if (!DetailStock.HasStockOn(terrain, stock)) return 0;

        _stockScratch.Clear();
        if (!DetailStock.CollectAdditionEdits(terrain, point, stock, brush, _stockScratch)) return 0;
        if (_stockScratch.Count == 0) return 0;

        DetailStock.ApplyAdditions(terrain, stock, _stockScratch, false);
        _stroke.EditsOn(terrain).AddRange(_stockScratch);

        return _stockScratch.Count;
    }

    /// <summary>
    /// Removes (or, with <paramref name="restore"/>, brings back) the game's own instances of a map asset under one
    /// stamp on one terrain and records the mask edits in the current stroke. Terrains without that asset are skipped.
    /// </summary>
    /// <returns>The number of mask cells that changed.</returns>
    private int StampRemoval(Terrain terrain, Vector3 point, StockAsset stock, DetailBrush brush, bool restore)
    {
        // Checked first because collecting on a terrain without the asset logs a warning
        if (!DetailStock.HasStockOn(terrain, stock)) return 0;

        _stockScratch.Clear();
        if (!DetailStock.CollectRemovalEdits(terrain, point, stock, brush, restore, _stockScratch)) return 0;
        if (_stockScratch.Count == 0) return 0;

        DetailStock.ApplyRemoval(terrain, stock, _stockScratch, false);
        _stroke.RemovalsOn(terrain).AddRange(_stockScratch);

        return _stockScratch.Count;
    }

    private void AddStampTerrain(Terrain terrain)
    {
        if (terrain != null && !_stampTerrains.Contains(terrain)) _stampTerrains.Add(terrain);
    }

    /// <summary>
    /// Writes a stroke's density and removal edits back (<paramref name="undo"/> true) or again. Terrains that no
    /// longer exist are skipped. A custom detail's density edits are skipped on a terrain the detail is no longer on,
    /// and a map asset's edits on a terrain that no longer has a prototype of it.
    /// </summary>
    private void ApplyStroke(DetailStroke stroke, bool undo)
    {
        for (var i = 0; i < stroke.Terrains.Count; i++)
        {
            var terrain = stroke.Terrains[i];
            if (terrain == null) continue;

            var edits = stroke.Edits[i];
            var removals = stroke.Removals[i];

            if (stroke.Stock == null)
            {
                if (edits.Count > 0 && DetailPainter.TryGetResolution(terrain, stroke.Detail, out _))
                {
                    DetailPainter.Apply(terrain, stroke.Detail, edits, undo);
                }

                continue;
            }

            if (!DetailStock.HasStockOn(terrain, stroke.Stock)) continue;

            if (edits.Count > 0) DetailStock.ApplyAdditions(terrain, stroke.Stock, edits, undo);
            if (removals.Count > 0) DetailStock.ApplyRemoval(terrain, stroke.Stock, removals, undo);
        }

        EditVersion++;
    }

    private void SetStatus(string message)
    {
        _status = message;
        _statusUntil = Time.unscaledTime + StatusSeconds;
    }
}
