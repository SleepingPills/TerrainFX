using System.Collections.Generic;
using TerrainFX.Details;
using UnityEngine;
using UnityEngine.Rendering;

namespace TerrainFX.Editor;

/// <summary>
/// Draws the brush guide on the terrain under the mouse cursor: a ring at the brush radius, a second ring where the
/// brush starts to fade when hardness is below 1, and one small square per density cell the next click would change.
/// The guide is drawn on top of everything (no depth test) so grass and props cannot hide it.
///
/// The mesh is only rebuilt when the hover point, the brush, the selected detail or the painted cells changed.
/// </summary>
public sealed class BrushOverlay
{
    private const int RingSegments = 96;
    private const int MaxCellQuads = 4000;
    private const float LiftAboveSurface = 0.05f;
    private const float CellInset = 0.12f;

    private static readonly Color BrushRing = new(0.25f, 1f, 0.45f, 0.95f);
    private static readonly Color EraserRing = new(1f, 0.3f, 0.2f, 0.95f);
    private const float InnerRingAlpha = 0.6f;
    private const float CellAlpha = 0.35f;

    private readonly List<Vector3> _vertices = [];
    private readonly List<Color> _colors = [];
    private readonly List<int> _triangles = [];
    private readonly List<DetailCellEdit> _cellEdits = [];

    private Mesh _mesh;
    private Material _material;
    private bool _missingShaderLogged;

    // What the mesh was last built for
    private Vector3 _builtPoint;
    private Terrain _builtTerrain;
    private DetailBrush _builtBrush;
    private Detail _builtDetail;
    private int _builtVersion = -1;

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
                      || mode.EditVersion != _builtVersion
                      || !SameBrush(brush, _builtBrush);

        if (changed)
        {
            Rebuild(mode.HoverTerrain, mode.HoverPoint, mode.Selected, brush);

            _builtPoint = mode.HoverPoint;
            _builtTerrain = mode.HoverTerrain;
            _builtDetail = mode.Selected;
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

    private void Rebuild(Terrain terrain, Vector3 point, Detail detail, DetailBrush brush)
    {
        _vertices.Clear();
        _colors.Clear();
        _triangles.Clear();

        var ringColor = brush.Erase ? EraserRing : BrushRing;
        var ringWidth = 0.06f + brush.Radius * 0.008f;

        AddRing(terrain, point, brush.Radius, ringWidth, ringColor);

        if (brush.Hardness < 1f)
        {
            var inner = ringColor;
            inner.a *= InnerRingAlpha;
            AddRing(terrain, point, brush.Radius * brush.Hardness, ringWidth * 0.75f, inner);
        }

        AddCells(terrain, point, detail, brush, ringColor);

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
    /// Adds one inset square for every density cell of the hovered terrain that the next stamp would change. Draws
    /// none when the detail is not on this terrain yet or when there would be more than 4000 of them.
    /// </summary>
    private void AddCells(Terrain terrain, Vector3 point, Detail detail, DetailBrush brush, Color ringColor)
    {
        // Checked first because collecting on a terrain without the detail logs a warning every time
        if (!DetailPainter.TryGetResolution(terrain, detail.Name, out var resolution)) return;

        _cellEdits.Clear();
        if (!DetailPainter.CollectEdits(terrain, point, detail.Name, brush, _cellEdits)) return;
        if (_cellEdits.Count > MaxCellQuads) return;

        var size = terrain.terrainData.size;
        var stepX = size.x / resolution;
        var stepZ = size.z / resolution;
        var origin = terrain.transform.position;
        var insetX = stepX * CellInset;
        var insetZ = stepZ * CellInset;

        var color = ringColor;
        color.a = CellAlpha;

        for (var e = 0; e < _cellEdits.Count; e++)
        {
            var edit = _cellEdits[e];
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
