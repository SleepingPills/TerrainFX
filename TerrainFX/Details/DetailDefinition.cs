using System;
using System.Collections.Generic;
using GPUInstancer;
using UnityEngine;

namespace TerrainFX.Details;

/// <summary>
/// The detail manager a definition is currently being added to, plus shortcuts to its terrain and settings.
/// Passed to the <see cref="DetailDefinition.Density"/> and <see cref="DetailDefinition.Configure"/> callbacks.
/// </summary>
public sealed class DetailContext
{
    public GPUInstancerDetailManager Manager { get; }
    public Terrain Terrain => Manager.terrain;
    public TerrainData TerrainData => Manager.terrain.terrainData;
    public GPUInstancerTerrainSettings Settings => Manager.terrainSettings;

    /// <summary>
    /// True for the manager that draws details through a scope. Every terrain has two managers, one for the main
    /// camera and one for the scope camera, and each definition is added to both.
    /// </summary>
    public bool IsOptic => Manager.IsOptic;

    public DetailContext(GPUInstancerDetailManager manager)
    {
        Manager = manager;
    }
}

/// <summary>
/// How the materials of a mesh detail look in one season. Every field is optional: a field that is left unset keeps
/// what the material has in the asset bundle, so a look can start out as nothing more than a colour.
/// These are the same four material properties the game changes for its own seasonal materials on the
/// "p0/Bumped Specular SMap" shader. A property the material's shader does not have is skipped.
/// </summary>
public sealed class DetailSeasonLook
{
    /// <summary>Replaces the material's main texture (_MainTex). Leave it null to keep the material's own texture.</summary>
    public Texture MainTexture;

    /// <summary>Replaces the material's normal map (_BumpMap). Leave it null to keep the material's own normal map.</summary>
    public Texture NormalMap;

    /// <summary>Replaces the material's colour (_Color). Leave it null to keep the material's own colour.</summary>
    public Color? Color;

    /// <summary>Replaces the material's base tint (_BaseTintColor). Leave it null to keep the material's own tint.</summary>
    public Color? BaseTintColor;
}

/// <summary>
/// Describes one custom terrain detail. Set <see cref="Prefab"/> to get a mesh detail (rocks, twigs), or set
/// <see cref="Texture"/> to get a texture detail (grass style crossed quads drawn with the game's foliage shader).
/// Exactly one of the two must be set.
/// </summary>
public sealed class DetailDefinition
{
    /// <summary>
    /// Unique name of the detail. The game uses a hash of this name as the random seed for placing the instances,
    /// so renaming a detail moves all of its instances to new positions.
    /// </summary>
    public string Name;

    /// <summary>
    /// Mesh details only: the object to copy meshes and materials from. It needs a MeshFilter and a MeshRenderer,
    /// and may have child renderers or a LODGroup. Every material must use a shader that is registered with
    /// <see cref="DetailShaders"/>. The object has to exist for as long as the plugin runs, which
    /// <see cref="DetailTemplates"/> takes care of.
    /// </summary>
    public GameObject Prefab;

    /// <summary>
    /// Mesh details only: how the detail's materials look in each season. The look is applied to every material of
    /// <see cref="Prefab"/>. A season without an entry shows the materials exactly as they are in the asset bundle,
    /// with two exceptions: AutumnLate uses the Autumn entry and SpringEarly uses the Spring entry when they have
    /// none of their own.
    /// </summary>
    public readonly Dictionary<ESeason, DetailSeasonLook> SeasonLooks = new();

    /// <summary>Texture details only: the image drawn on the quads.</summary>
    public Texture2D Texture;

    /// <summary>
    /// Texture details only, optional: a material to use instead of the game's foliage material. Its shader must be
    /// registered with <see cref="DetailShaders"/>. <see cref="Texture"/> still has to be set.
    /// </summary>
    public Material TextureMaterial;

    /// <summary>Smallest and largest random scale of an instance along X and Z.</summary>
    public Vector2 WidthRange = Vector2.one;

    /// <summary>Smallest and largest random scale of an instance along Y. An instance that gets a large width also gets a large height.</summary>
    public Vector2 HeightRange = Vector2.one;

    /// <summary>
    /// How far away, in metres, instances are drawn by the main camera. Leave it null to use the distance the map
    /// uses for its own details (375 m on the one map checked so far).
    /// </summary>
    public float? MaxDistance;

    /// <summary>
    /// How far away, in metres, instances are drawn when looking through a scope. Leave it null to use the distance
    /// the map uses for its own details through scopes (1125 m on the one map checked so far).
    /// </summary>
    public float? MaxDistanceOptic;

    /// <summary>
    /// Scales the distances at which the detail switches LOD or disappears for being too small on screen. 2 doubles
    /// those distances, 0.5 halves them. Only affects prefabs with a LODGroup and texture details with crossed quads;
    /// a mesh detail without a LODGroup is always drawn up to its maximum distance. The default of 2.5 is the value
    /// every one of the game's own details uses on the one map checked so far.
    /// </summary>
    public float LodBiasAdjustment = 2.5f;

    /// <summary>
    /// Strength of the game's density mask for this detail (the game multiplies the mask channel by this number).
    /// It does nothing unless the density mask is switched on for the prototype, which custom details leave off;
    /// switch it on in <see cref="Configure"/> (useDensityMask, densityMapIndex, densityChanelMask, densityMinMax).
    /// The default of 5 is the value every one of the game's own details uses on the one map checked so far.
    /// </summary>
    public float DensityFadeFactor = 5f;

    /// <summary>
    /// Whether instances cast shadows. Leave it null to do what the game does for its own details: texture details
    /// cast shadows and mesh details don't. Shadows also need the "Grass Shadow" graphics setting, and details seen
    /// through a scope never cast any.
    /// </summary>
    public bool? CastShadows;

    /// <summary>
    /// Mesh details only: the Unity layer the instances are drawn on. Leave it null to use the layer the manager
    /// uses for texture details.
    /// </summary>
    public int? Layer;

    /// <summary>Texture details only: how many quads are crossed to form one instance, from 1 to 4.</summary>
    public int QuadCount = 2;

    /// <summary>Texture details only: draw one quad that always faces the camera instead of crossed quads.</summary>
    public bool Billboard;

    /// <summary>Texture details only: one of the two tints the game blends between using its noise texture.</summary>
    public Color HealthyColor = Color.white;

    /// <summary>Texture details only: the other tint the game blends towards using its noise texture.</summary>
    public Color DryColor = Color.white;

    /// <summary>Scale of the noise pattern that blends between <see cref="HealthyColor"/> and <see cref="DryColor"/>.</summary>
    public float NoiseSpread = 0.2f;

    /// <summary>
    /// Number of cells along one side of the density map. 0 means "use the terrain's detail resolution" (1024 on the
    /// one map checked so far).
    /// </summary>
    public int DensityResolution;

    /// <summary>
    /// Callback that decides how many instances go into each cell of the density map. It is called once for each
    /// detail manager and receives a map in which every cell is 0. If no callback is set, the detail is still added
    /// to the manager, but no instances of it are placed.
    /// </summary>
    public Action<DetailDensityMap, DetailContext> Density;

    /// <summary>
    /// Optional callback for settings this class has no field for. It receives the finished
    /// <see cref="GPUInstancerDetailPrototype"/> after every field above has been applied to it, so whatever the
    /// callback changes is what the game ends up using.
    /// </summary>
    public Action<GPUInstancerDetailPrototype, DetailContext> Configure;

    public bool IsMesh => Prefab != null;
}
