using System.Collections.Generic;
using TerrainFX.Details;
using UnityEngine;
using UnityEngine.Rendering;

namespace TerrainFX.Editor;

/// <summary>
/// Renders previews of details for the editor window: one large, slowly rotating live preview of the selected detail,
/// and one small still thumbnail per detail for the grid.
///
/// The previews are rendered on a hidden stage far below the map: a disabled camera that only sees the game's
/// "Weapon Preview" layer, two directional lights that only light that layer, and a pivot holding one model per
/// detail. Models and lights are only switched on for the moment a preview is rendered, so nothing of the stage can
/// show up in the game's own weapon previews.
///
/// The window asks for textures while it is drawn (<see cref="GetLive"/>, <see cref="GetThumbnail"/>), and the
/// rendering itself happens in <see cref="Render"/>, which <see cref="TerrainEditor"/> calls from LateUpdate. Rendering
/// a camera in the middle of OnGUI would switch the active render target under IMGUI and would run once per GUI event
/// instead of once per frame.
///
/// GPU-instanced details are never drawn by a second camera, so the models are plain copies with ordinary renderers.
/// </summary>
public sealed class DetailPreview
{
    private const int LiveSize = 256;
    private const int ThumbnailSize = 96;
    private const float LiveDegreesPerSecond = 25f;
    private const float ThumbnailYaw = 35f;
    private const float CameraPitch = 20f;
    private const float FieldOfView = 30f;
    private const int MaxThumbnailsPerFrame = 4;

    private static readonly Vector3 StagePosition = new(0f, -2000f, 0f);
    private static readonly Color Background = new(0.16f, 0.17f, 0.18f, 1f);
    private static readonly Color PreviewAmbient = new(0.45f, 0.45f, 0.47f, 1f);

    private GameObject _stage;
    private Camera _camera;
    private Transform _pivot;
    private Light[] _lights = [];
    private Shader _quadShader;
    private bool _quadShaderSearched;
    private bool _callbacksAdded;

    private RenderTexture _live;
    private Detail _liveDetail;
    private float _liveYaw;

    // Keyed by the detail itself rather than its name, so a custom detail and a map detail with the same name, or the
    // map details of two maps, never share a thumbnail or a model
    private readonly Dictionary<Detail, RenderTexture> _thumbnails = new();

    // The prefab or texture each thumbnail was last queued for, so a thumbnail is rendered again when it changes
    // (the texture of a map detail follows the season, and a map detail's prefab is replaced on a new load)
    private readonly Dictionary<Detail, Object> _thumbnailSources = new();

    // Every thumbnail texture ever made, so Dispose can destroy them with an indexed loop
    private readonly List<RenderTexture> _thumbnailTextures = [];
    private readonly List<Detail> _pendingThumbnails = [];

    // One model per detail, made on first use and made again when the detail's prefab or texture changed. The radius
    // is that of the model's bounding sphere, for framing.
    private readonly Dictionary<Detail, (GameObject Model, float Radius, Object Source)> _models = new();
    private readonly List<Object> _ownedAssets = [];

    // Render settings saved before our camera renders and put back after it
    private AmbientMode _savedAmbientMode;
    private Color _savedAmbientLight;
    private Color _savedAmbientSky;
    private Color _savedAmbientEquator;
    private Color _savedAmbientGround;
    private float _savedAmbientIntensity;
    private bool _savedFog;

    /// <summary>
    /// Returns the live preview texture and makes <paramref name="detail"/> the one shown in it. The texture is
    /// rendered by the next <see cref="Render"/> call, so the window has to ask again every frame for the preview to
    /// keep turning. Returns the detail's own texture when it is a texture detail and the preview shader is missing,
    /// and null when <paramref name="detail"/> is null.
    /// </summary>
    public Texture GetLive(Detail detail)
    {
        if (detail == null) return null;
        if (!detail.IsMesh && QuadShader() == null) return detail.Texture;

        _liveDetail = detail;

        if (_live == null || !_live.IsCreated())
        {
            if (_live == null) _live = NewTexture(LiveSize, "TerrainFX Live Preview");
            _live.Create();
        }

        return _live;
    }

    /// <summary>
    /// Returns the thumbnail texture of <paramref name="detail"/>, a still view from a three-quarter angle. A new
    /// thumbnail, one whose texture was lost (for example after a display change), or one whose detail got a different
    /// prefab or texture since it was rendered, is queued and rendered by the next <see cref="Render"/> call; until
    /// then it shows the background colour or the old picture. Returns the detail's own texture when it is a texture
    /// detail and the preview shader is missing.
    /// </summary>
    public Texture GetThumbnail(Detail detail)
    {
        if (detail == null) return null;
        if (!detail.IsMesh && QuadShader() == null) return detail.Texture;

        if (!_thumbnails.TryGetValue(detail, out var texture) || texture == null)
        {
            texture = NewTexture(ThumbnailSize, $"TerrainFX Thumbnail {detail.Name}");
            _thumbnails[detail] = texture;
            _thumbnailTextures.Add(texture);
        }

        var source = SourceOf(detail);
        var queue = !_thumbnailSources.TryGetValue(detail, out var renderedSource) || renderedSource != source;

        if (!texture.IsCreated())
        {
            texture.Create();
            queue = true;
        }

        if (queue)
        {
            _thumbnailSources[detail] = source;
            if (!_pendingThumbnails.Contains(detail)) _pendingThumbnails.Add(detail);
        }

        return texture;
    }

    /// <summary>
    /// Renders the live preview that was asked for since the last call (turning it a little further) and up to four
    /// queued thumbnails. Call once per frame while the window is open.
    /// </summary>
    public void Render()
    {
        if (_liveDetail == null && _pendingThumbnails.Count == 0) return;
        if (!EnsureStage()) return;

        if (_liveDetail != null && _live != null)
        {
            _liveYaw = Mathf.Repeat(_liveYaw + LiveDegreesPerSecond * Time.unscaledDeltaTime, 360f);
            RenderDetail(_liveDetail, _liveYaw, _live);
            _liveDetail = null;
        }

        for (var i = 0; i < MaxThumbnailsPerFrame && _pendingThumbnails.Count > 0; i++)
        {
            var detail = _pendingThumbnails[0];
            _pendingThumbnails.RemoveAt(0);

            if (_thumbnails.TryGetValue(detail, out var texture) && texture != null) RenderDetail(detail, ThumbnailYaw, texture);
        }
    }

    /// <summary>
    /// Destroys the stage, the preview models, render textures, meshes and materials, and stops listening to the
    /// camera callbacks. The preview can still be used afterwards: everything is made again on demand.
    /// </summary>
    public void Dispose()
    {
        if (_callbacksAdded)
        {
            Camera.onPreCull -= OnPreCull;
            Camera.onPreRender -= OnPreRender;
            Camera.onPostRender -= OnPostRender;
            _callbacksAdded = false;
        }

        if (_stage != null) Object.Destroy(_stage);
        _stage = null;
        _camera = null;
        _pivot = null;
        _lights = [];
        _models.Clear();

        DestroyTexture(_live);
        _live = null;
        _liveDetail = null;

        for (var i = 0; i < _thumbnailTextures.Count; i++) DestroyTexture(_thumbnailTextures[i]);
        _thumbnailTextures.Clear();
        _thumbnails.Clear();
        _thumbnailSources.Clear();
        _pendingThumbnails.Clear();

        for (var i = 0; i < _ownedAssets.Count; i++)
        {
            var asset = _ownedAssets[i];
            if (asset != null) Object.Destroy(asset);
        }

        _ownedAssets.Clear();
    }

    private bool EnsureStage()
    {
        if (_stage != null) return true;

        var layer = LayersMaskController.WeaponPreview;
        if (layer < 0)
        {
            Plugin.Log.LogError("The Weapon Preview layer does not exist, detail previews are not rendered");
            return false;
        }

        _stage = new GameObject("TerrainFX Editor Preview Stage");
        _stage.transform.position = StagePosition;
        Object.DontDestroyOnLoad(_stage);

        _pivot = new GameObject("Pivot").transform;
        _pivot.SetParent(_stage.transform, false);

        var cameraObject = new GameObject("Preview Camera");
        cameraObject.transform.SetParent(_stage.transform, false);
        _camera = cameraObject.AddComponent<Camera>();
        _camera.enabled = false;
        _camera.cullingMask = 1 << layer;
        _camera.clearFlags = CameraClearFlags.SolidColor;
        _camera.backgroundColor = Background;
        _camera.useOcclusionCulling = false;
        _camera.allowHDR = false;
        _camera.allowMSAA = false;
        _camera.renderingPath = RenderingPath.DeferredShading;
        _camera.fieldOfView = FieldOfView;

        _lights =
        [
            AddLight("Key Light", Quaternion.Euler(40f, -35f, 0f), 1.1f, layer),
            AddLight("Fill Light", Quaternion.Euler(15f, 150f, 0f), 0.45f, layer)
        ];

        _stage.SetActive(true);

        // The game's LevelSettings overwrites the ambient light for every camera in onPreCull, which runs inside
        // Render(). Setting the ambient around the Render() call would therefore be undone, so like the game's
        // CameraImage we set it in onPreRender (after culling) and put it back in onPostRender. Only our camera is acted on.
        Camera.onPreCull += OnPreCull;
        Camera.onPreRender += OnPreRender;
        Camera.onPostRender += OnPostRender;
        _callbacksAdded = true;

        return true;
    }

    private Light AddLight(string name, Quaternion rotation, float intensity, int layer)
    {
        var lightObject = new GameObject(name);
        lightObject.transform.SetParent(_stage.transform, false);
        lightObject.transform.rotation = rotation;

        var light = lightObject.AddComponent<Light>();
        light.type = LightType.Directional;
        light.intensity = intensity;
        light.color = Color.white;
        light.shadows = LightShadows.None;
        light.cullingMask = 1 << layer;
        light.enabled = false;

        return light;
    }

    /// <summary>
    /// Shows the detail's model alone on the turned pivot, frames the camera on it and renders into the texture. The
    /// model is made first when there is none yet, or made again when the detail's prefab or texture is not the one
    /// it was made from.
    /// </summary>
    private void RenderDetail(Detail detail, float yaw, RenderTexture target)
    {
        var source = SourceOf(detail);
        var found = _models.TryGetValue(detail, out var entry);

        if (!found || entry.Model == null || entry.Source != source)
        {
            if (found && entry.Model != null) Object.Destroy(entry.Model);
            _models.Remove(detail);

            var (model, radius) = CreateModel(detail);
            if (model == null) return;

            entry = (model, radius, source);
            _models[detail] = entry;
        }

        if (!target.IsCreated()) target.Create();

        _pivot.localRotation = Quaternion.Euler(0f, yaw, 0f);

        // Far enough for the bounding sphere to fill most of the view
        var distance = entry.Radius / Mathf.Sin(FieldOfView * 0.5f * Mathf.Deg2Rad) * 1.08f;
        var direction = Quaternion.Euler(CameraPitch, 0f, 0f) * Vector3.back;
        _camera.transform.position = _pivot.position + direction * distance;
        _camera.transform.rotation = Quaternion.LookRotation(_pivot.position - _camera.transform.position);
        _camera.nearClipPlane = Mathf.Max(0.01f, distance - entry.Radius * 2f);
        _camera.farClipPlane = distance + entry.Radius * 2f;

        entry.Model.SetActive(true);
        for (var i = 0; i < _lights.Length; i++) _lights[i].enabled = true;

        _camera.targetTexture = target;
        _camera.Render();
        _camera.targetTexture = null;

        for (var i = 0; i < _lights.Length; i++) _lights[i].enabled = false;
        entry.Model.SetActive(false);
    }

    /// <summary>
    /// Makes the preview model of a detail under the pivot, centred on it, on the preview layer and inactive. Mesh
    /// details get a copy of their prefab with its renderers switched on (the prefab itself is never changed); texture
    /// details get two crossed quads. Returns a null model when it cannot be made.
    /// </summary>
    private (GameObject Model, float Radius) CreateModel(Detail detail)
    {
        GameObject model;

        if (detail.IsMesh)
        {
            model = Object.Instantiate(detail.Prefab, _pivot, false);
            model.name = $"Preview {detail.Name}";

            var prefabRenderers = model.GetComponentsInChildren<Renderer>(true);
            for (var i = 0; i < prefabRenderers.Length; i++)
            {
                var renderer = prefabRenderers[i];
                renderer.enabled = true;
                renderer.shadowCastingMode = ShadowCastingMode.Off;
            }
        }
        else
        {
            var shader = QuadShader();
            if (shader == null || detail.Texture == null) return (null, 0f);

            model = new GameObject($"Preview {detail.Name}");
            model.transform.SetParent(_pivot, false);

            var mesh = CrossedQuads(detail.Texture);
            var material = new Material(shader) { name = $"Preview {detail.Name}", mainTexture = detail.Texture, hideFlags = HideFlags.HideAndDontSave };
            _ownedAssets.Add(mesh);
            _ownedAssets.Add(material);

            model.AddComponent<MeshFilter>().sharedMesh = mesh;
            var meshRenderer = model.AddComponent<MeshRenderer>();
            meshRenderer.sharedMaterial = material;
            meshRenderer.shadowCastingMode = ShadowCastingMode.Off;
        }

        SetLayer(model.transform, LayersMaskController.WeaponPreview);
        model.transform.localPosition = Vector3.zero;
        model.SetActive(true);

        // Centre the combined bounds on the pivot
        var renderers = model.GetComponentsInChildren<Renderer>(true);
        if (renderers.Length == 0)
        {
            Object.Destroy(model);
            return (null, 0f);
        }

        var bounds = renderers[0].bounds;
        for (var i = 1; i < renderers.Length; i++) bounds.Encapsulate(renderers[i].bounds);

        model.transform.position -= bounds.center - _pivot.position;
        model.SetActive(false);

        return (model, Mathf.Max(0.05f, bounds.extents.magnitude));
    }

    /// <summary>The object a detail's preview model is made from: its prefab for a mesh detail, its texture otherwise.</summary>
    private static Object SourceOf(Detail detail)
    {
        return detail.IsMesh ? detail.Prefab : detail.Texture;
    }

    /// <summary>Two vertical quads crossing at right angles, one metre tall and as wide as the texture's aspect, standing on y = 0.</summary>
    private static Mesh CrossedQuads(Texture2D texture)
    {
        var halfWidth = 0.5f * texture.width / Mathf.Max(1f, texture.height);
        const float height = 1f;

        var mesh = new Mesh { name = "TerrainFX Preview Quads", hideFlags = HideFlags.HideAndDontSave };
        mesh.SetVertices(new List<Vector3>
        {
            new(-halfWidth, 0f, 0f), new(halfWidth, 0f, 0f), new(halfWidth, height, 0f), new(-halfWidth, height, 0f),
            new(0f, 0f, -halfWidth), new(0f, 0f, halfWidth), new(0f, height, halfWidth), new(0f, height, -halfWidth)
        });
        mesh.SetUVs(0, new List<Vector2>
        {
            new(0f, 0f), new(1f, 0f), new(1f, 1f), new(0f, 1f),
            new(0f, 0f), new(1f, 0f), new(1f, 1f), new(0f, 1f)
        });

        var colors = new List<Color>();
        for (var i = 0; i < 8; i++) colors.Add(Color.white);
        mesh.SetColors(colors);

        mesh.SetTriangles(new List<int> { 0, 2, 1, 0, 3, 2, 4, 6, 5, 4, 7, 6 }, 0);
        mesh.RecalculateBounds();

        return mesh;
    }

    /// <summary>The shader for texture details' quads, or null when the game build does not have it (looked up once).</summary>
    private Shader QuadShader()
    {
        if (_quadShaderSearched) return _quadShader;

        _quadShaderSearched = true;
        _quadShader = Shader.Find("Particles/Alpha Blended");
        if (_quadShader == null) Plugin.Log.LogWarning("Shader Particles/Alpha Blended not found, texture details are previewed flat");

        return _quadShader;
    }

    private static void SetLayer(Transform transform, int layer)
    {
        transform.gameObject.layer = layer;
        for (var i = 0; i < transform.childCount; i++) SetLayer(transform.GetChild(i), layer);
    }

    private static RenderTexture NewTexture(int size, string name)
    {
        return new RenderTexture(size, size, 24, RenderTextureFormat.ARGB32)
        {
            name = name,
            hideFlags = HideFlags.HideAndDontSave,
            antiAliasing = 1
        };
    }

    private static void DestroyTexture(RenderTexture texture)
    {
        if (texture == null) return;

        texture.Release();
        Object.Destroy(texture);
    }

    /// <summary>Turns on the shader keyword the game's own previews render with, for our camera only (as EFT.UI.CameraImage does).</summary>
    private void OnPreCull(Camera camera)
    {
        if (camera != _camera || _camera == null) return;

        Shader.EnableKeyword("WeaponPreview");
    }

    /// <summary>Saves the scene's ambient and fog and replaces them with a flat ambient and no fog, for our camera only.</summary>
    private void OnPreRender(Camera camera)
    {
        if (camera != _camera || _camera == null) return;

        _savedAmbientMode = RenderSettings.ambientMode;
        _savedAmbientLight = RenderSettings.ambientLight;
        _savedAmbientSky = RenderSettings.ambientSkyColor;
        _savedAmbientEquator = RenderSettings.ambientEquatorColor;
        _savedAmbientGround = RenderSettings.ambientGroundColor;
        _savedAmbientIntensity = RenderSettings.ambientIntensity;
        _savedFog = RenderSettings.fog;

        RenderSettings.ambientMode = AmbientMode.Flat;
        RenderSettings.ambientLight = PreviewAmbient;
        RenderSettings.ambientSkyColor = PreviewAmbient;
        RenderSettings.ambientIntensity = 1f;
        RenderSettings.fog = false;
    }

    /// <summary>Puts back what <see cref="OnPreRender"/> and <see cref="OnPreCull"/> changed, for our camera only.</summary>
    private void OnPostRender(Camera camera)
    {
        if (camera != _camera || _camera == null) return;

        Shader.DisableKeyword("WeaponPreview");

        RenderSettings.ambientMode = _savedAmbientMode;
        RenderSettings.ambientLight = _savedAmbientLight;
        RenderSettings.ambientSkyColor = _savedAmbientSky;
        RenderSettings.ambientEquatorColor = _savedAmbientEquator;
        RenderSettings.ambientGroundColor = _savedAmbientGround;
        RenderSettings.ambientIntensity = _savedAmbientIntensity;
        RenderSettings.fog = _savedFog;
    }
}
