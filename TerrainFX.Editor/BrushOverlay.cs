using System.Collections.Generic;
using TerrainFX.Details;
using UnityEngine;
using UnityEngine.Rendering;

namespace TerrainFX.Editor;

/// <summary>
/// Draws the brush guide on the terrain under the mouse cursor: a ring at the brush radius, a second ring where the
/// brush starts to fade when hardness is below 1, and one small square per cell the next click would change on the
/// hovered terrain: density cells of a custom detail, or additions and removal mask cells of the game's own instances
/// for a map detail. The guide is drawn on top of everything (no depth test) so grass and props cannot hide it.
///
/// The mesh is only rebuilt when the hover point, the brush, the tool, the selected detail or asset, or the painted
/// cells changed, or when a custom detail has just arrived on a terrain it was missing from.
/// </summary>
public sealed class BrushOverlay
{
    private const int RingSegments = 96;
    private const int MaxCellQuads = 4000;
    private const float LiftAboveSurface = 0.05f;
    private const float CellInset = 0.12f;

    private static readonly Color BrushRing = new(0.25f, 1f, 0.45f, 0.95f);
    private static readonly Color EraserRing = new(1f, 0.3f, 0.2f, 0.95f);
    private static readonly Color RestoreRing = new(0.3f, 0.75f, 1f, 0.95f);
    private const float InnerRingAlpha = 0.6f;
    private const float CellAlpha = 0.35f;

    private readonly List<Vector3> _vertices = [];
    private readonly List<Color> _colors = [];
    private readonly List<int> _triangles = [];
    private readonly List<DetailCellEdit> _cellEdits = [];
    private readonly List<DetailCellEdit> _removalEdits = [];

    private Mesh _mesh;
    private Material _material;
    private bool _missingShaderLogged;

    // What the mesh was last built for
    private Vector3 _builtPoint;
    private Terrain _builtTerrain;
    private DetailBrush _builtBrush;
    private DetailTool _builtTool;
    private Detail _builtDetail;
    private StockAsset _builtStock;
    private int _builtVersion = -1;

    // True when the last build wanted the density cells of a custom detail but it was not on the terrain yet
    private bool _builtWithoutDetail;

    /// <summary>
    /// Draws the guide for the brush of <paramref name="mode"/> into <paramref name="camera"/> for this frame. Does
    /// nothing when the mode has no hover or no selected detail. Call it once per frame from LateUpdate.
    /// </summary>
    public void Draw(Camera camera, DetailPaintMode mode)
    {
        if (camera == null || !mode.HasHover || mode.HoverTerrain == null || mode.Selected == null) return;
        if (!EnsureResources()) return;

        var brush = mode.Brush;
        var changed = mode.HoverPoint != _builtPoint
                      || mode.HoverTerrain != _builtTerrain
                      || mode.Selected != _builtDetail
                      || mode.SelectedStock != _builtStock
                      || mode.Tool != _builtTool
                      || mode.EditVersion != _builtVersion
                      || !SameBrush(brush, _builtBrush);

        // The brush puts the detail on a terrain a few frames after the click, which changes no cells by itself
        if (!changed && _builtWithoutDetail) changed = DetailPainter.TryGetResolution(mode.HoverTerrain, mode.Selected, out _);

        if (changed)
        {
            Rebuild(mode.HoverTerrain, mode.HoverPoint, mode.Selected, mode.SelectedStock, mode.Tool, brush);

            _builtPoint = mode.HoverPoint;
            _builtTerrain = mode.HoverTerrain;
            _builtDetail = mode.Selected;
            _builtStock = mode.SelectedStock;
            _builtTool = mode.Tool;
            _builtBrush = brush;
            _builtVersion = mode.EditVersion;
        }

        Graphics.DrawMesh(_mesh, Matrix4x4.identity, _material, 0, camera, 0, null, false, false);
    }

    /// <summary>
    /// Destroys the mesh and material. The overlay can still be used afterwards: they are created again the next
    /// time it is drawn.
    /// </summary>
    public void Dispose()
    {
        if (_mesh != null) Object.Destroy(_mesh);
        if (_material != null) Object.Destroy(_material);

        _mesh = null;
        _material = null;
        _builtTerrain = null;
        _builtDetail = null;
        _builtStock = null;
        _builtVersion = -1;
    }

    private bool EnsureResources()
    {
        if (_material == null)
        {
            var shader = Shader.Find("Hidden/Internal-Colored");
            if (shader == null)
            {
                if (!_missingShaderLogged) Plugin.Log.LogError("Shader Hidden/Internal-Colored not found, the brush guide is not drawn");
                _missingShaderLogged = true;
                return false;
            }

            _material = new Material(shader) { name = "TerrainFX Brush Overlay", hideFlags = HideFlags.HideAndDontSave, renderQueue = 4000 };
            _material.SetInt("_SrcBlend", (int)BlendMode.SrcAlpha);
            _material.SetInt("_DstBlend", (int)BlendMode.OneMinusSrcAlpha);
            _material.SetInt("_Cull", (int)CullMode.Off);
            _material.SetInt("_ZWrite", 0);
            _material.SetInt("_ZTest", (int)CompareFunction.Always);
            _material.SetColor("_Color", Color.white);
        }

        if (_mesh == null)
        {
            _mesh = new Mesh { name = "TerrainFX Brush Overlay", hideFlags = HideFlags.HideAndDontSave };
            _mesh.MarkDynamic();
            _builtVersion = -1;
        }

        return true;
    }

    private void Rebuild(Terrain terrain, Vector3 point, Detail detail, StockAsset stock, DetailTool tool, DetailBrush brush)
    {
        _vertices.Clear();
        _colors.Clear();
        _triangles.Clear();

        var ringColor = tool switch
        {
            DetailTool.Eraser => EraserRing,
            DetailTool.Restore => RestoreRing,
            _ => BrushRing
        };
        var ringWidth = 0.06f + brush.Radius * 0.008f;

        AddRing(terrain, point, brush.Radius, ringWidth, ringColor);

        if (brush.Hardness < 1f)
        {
            var inner = ringColor;
            inner.a *= InnerRingAlpha;
            AddRing(terrain, point, brush.Radius * brush.Hardness, ringWidth * 0.75f, inner);
        }

        var densityResolution = CollectCells(terrain, point, detail, stock, tool, brush);

        // Too many squares to draw: only the rings are shown
        if (_cellEdits.Count + _removalEdits.Count <= MaxCellQuads)
        {
            var cellColor = ringColor;
            cellColor.a = CellAlpha;

            AddCells(terrain, _cellEdits, densityResolution, cellColor);
            AddCells(terrain, _removalEdits, terrain.terrainData.detailResolution, cellColor);
        }

        _mesh.Clear();
        _mesh.SetVertices(_vertices);
        _mesh.SetColors(_colors);
        _mesh.SetTriangles(_triangles, 0);
        _mesh.RecalculateBounds();
    }

    /// <summary>Adds a flat ring of quads that follows the terrain surface. A ring with a radius below 1 cm is skipped.</summary>
    private void AddRing(Terrain hovered, Vector3 centre, float radius, float width, Color color)
    {
        if (radius < 0.01f) return;

        var inner = Mathf.Max(0f, radius - width * 0.5f);
        var outer = radius + width * 0.5f;
        var first = _vertices.Count;

        for (var i = 0; i <= RingSegments; i++)
        {
            var angle = i * Mathf.PI * 2f / RingSegments;
            var direction = new Vector3(Mathf.Cos(angle), 0f, Mathf.Sin(angle));

            _vertices.Add(OnSurface(hovered, centre + direction * inner));
            _vertices.Add(OnSurface(hovered, centre + direction * outer));
            _colors.Add(color);
            _colors.Add(color);
        }

        for (var i = 0; i < RingSegments; i++)
        {
            var a = first + i * 2;
            AddQuad(a, a + 1, a + 3, a + 2);
        }
    }

    /// <summary>
    /// Fills <see cref="_cellEdits"/> with the density (or additions) cells and <see cref="_removalEdits"/> with the
    /// removal mask cells of the hovered terrain that the next stamp of <paramref name="tool"/> would change, and
    /// returns the resolution of the grid the density cells are on (0 when there are none to show). For a custom
    /// detail the brush and eraser show density cells only where the detail is already on the terrain, and records in
    /// <see cref="_builtWithoutDetail"/> when it is not there yet. For a map detail the brush and eraser show additions
    /// cells, the eraser and restore show removal cells, and nothing is shown on a terrain without that asset.
    /// </summary>
    private int CollectCells(Terrain terrain, Vector3 point, Detail detail, StockAsset stock, DetailTool tool, DetailBrush brush)
    {
        _cellEdits.Clear();
        _removalEdits.Clear();
        _builtWithoutDetail = false;

        if (stock == null)
        {
            if (tool == DetailTool.Restore) return 0;

            // Checked first because collecting on a terrain without the detail logs a warning
            if (!DetailPainter.TryGetResolution(terrain, detail, out var resolution))
            {
                _builtWithoutDetail = true;
                return 0;
            }

            DetailPainter.CollectEdits(terrain, point, detail, brush, _cellEdits);
            return resolution;
        }

        // Checked first because collecting on a terrain without the asset logs a warning
        if (!DetailStock.HasStockOn(terrain, stock)) return 0;

        if (tool != DetailTool.Restore) DetailStock.CollectAdditionEdits(terrain, point, stock, brush, _cellEdits);
        if (tool != DetailTool.Brush) DetailStock.CollectRemovalEdits(terrain, point, stock, brush, tool == DetailTool.Restore, _removalEdits);

        return terrain.terrainData.detailResolution;
    }

    /// <summary>
    /// Adds one inset square for every cell in <paramref name="edits"/>, on a grid of <paramref name="resolution"/>
    /// cells per side of the terrain (cell index z * resolution + x).
    /// </summary>
    private void AddCells(Terrain terrain, List<DetailCellEdit> edits, int resolution, Color color)
    {
        if (edits.Count == 0 || resolution <= 0) return;

        var size = terrain.terrainData.size;
        var stepX = size.x / resolution;
        var stepZ = size.z / resolution;
        var origin = terrain.transform.position;
        var insetX = stepX * CellInset;
        var insetZ = stepZ * CellInset;

        for (var e = 0; e < edits.Count; e++)
        {
            var edit = edits[e];
            var x = edit.Index % resolution;
            var z = edit.Index / resolution;

            var minX = origin.x + x * stepX + insetX;
            var maxX = origin.x + (x + 1) * stepX - insetX;
            var minZ = origin.z + z * stepZ + insetZ;
            var maxZ = origin.z + (z + 1) * stepZ - insetZ;

            var first = _vertices.Count;
            _vertices.Add(OnTerrain(terrain, minX, minZ));
            _vertices.Add(OnTerrain(terrain, maxX, minZ));
            _vertices.Add(OnTerrain(terrain, maxX, maxZ));
            _vertices.Add(OnTerrain(terrain, minX, maxZ));

            for (var i = 0; i < 4; i++) _colors.Add(color);

            AddQuad(first, first + 1, first + 2, first + 3);
        }
    }

    /// <summary>The point lifted onto the surface of whichever terrain is under it, falling back to the hovered terrain.</summary>
    private static Vector3 OnSurface(Terrain hovered, Vector3 position)
    {
        var terrain = DetailTerrains.TerrainAt(position);
        if (terrain == null) terrain = hovered;

        position.y = DetailTerrains.SurfaceHeight(terrain, position) + LiftAboveSurface;
        return position;
    }

    private static Vector3 OnTerrain(Terrain terrain, float x, float z)
    {
        var position = new Vector3(x, 0f, z);
        position.y = DetailTerrains.SurfaceHeight(terrain, position) + LiftAboveSurface;
        return position;
    }

    private void AddQuad(int a, int b, int c, int d)
    {
        _triangles.Add(a);
        _triangles.Add(b);
        _triangles.Add(c);
        _triangles.Add(a);
        _triangles.Add(c);
        _triangles.Add(d);
    }

    private static bool SameBrush(DetailBrush a, DetailBrush b)
    {
        return a.Radius == b.Radius && a.Count == b.Count && a.Coverage == b.Coverage && a.Hardness == b.Hardness && a.Erase == b.Erase;
    }
}
