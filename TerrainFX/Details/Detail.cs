using System.Collections.Generic;
using UnityEngine;

namespace TerrainFX.Details;

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
/// Exactly one of the two must be set. Details are declared in <see cref="DetailDefinitions.DefineDetails"/>, and
/// where their instances go is painted in game and stored per map by <see cref="DetailDensityStore"/>.
/// </summary>
public sealed class Detail
{
    /// <summary>
    /// Unique name of the detail. The game uses a hash of this name as the random seed for placing the instances,
    /// so renaming a detail moves all of its instances to new positions. The name is also part of the file name the
    /// painted density is saved under, so renaming a detail also leaves its painted density behind.
    /// </summary>
    public string Name;

    /// <summary>
    /// Mesh details only: the object to copy meshes and materials from. It needs a MeshFilter and a MeshRenderer,
    /// and may have child renderers or a LODGroup. Every material must use a shader that is registered with
    /// <see cref="DetailShaders"/>. The object has to exist for as long as the plugin runs, which
    /// <see cref="DetailAssets.Mesh"/> takes care of.
    /// </summary>
    public GameObject Prefab;

    /// <summary>
    /// Mesh details only: how the detail's materials look in each season. The look is applied to every material of
    /// <see cref="Prefab"/>. A season without an entry shows the materials exactly as they are in the asset bundle,
    /// with two exceptions: AutumnLate uses the Autumn entry and SpringEarly uses the Spring entry when they have
    /// none of their own.
    /// </summary>
    public readonly Dictionary<ESeason, DetailSeasonLook> SeasonLooks = new();

    /// <summary>
    /// The seasons in which this detail is not shown at all: it is not put on any terrain when a raid in one of these
    /// seasons loads, and it cannot be painted during such a raid. Every season has to be listed on its own, AutumnLate
    /// and SpringEarly do not follow Autumn and Spring. When the list is left empty the detail is shown in every season.
    /// </summary>
    public readonly List<ESeason> HiddenSeasons = [];

    /// <summary>
    /// True for the descriptions <see cref="DetailStock"/> makes of the game's own detail assets
    /// (<see cref="StockAsset.Detail"/>). Such a detail only describes the asset for the editor (name, counts, preview)
    /// and is never put on a detail manager: <see cref="DetailInjector.EnsureOnTerrain"/> reports it as unavailable.
    /// Only <see cref="DetailStock"/> sets it; it is false for every detail declared in
    /// <see cref="DetailDefinitions.DefineDetails"/>.
    /// </summary>
    public bool IsStock;

    /// <summary>Texture details only: the image drawn on the quads.</summary>
    public Texture2D Texture;

    /// <summary>
    /// Texture details only, optional: a material to use instead of the game's foliage material. Its shader must be
    /// registered with <see cref="DetailShaders"/>. <see cref="Texture"/> still has to be set. When it is left null,
    /// the game's own foliage material is used.
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
    /// Mesh details only: blends between LOD levels over a short band instead of switching from one to the next
    /// abruptly. It only does something for a prefab with a LODGroup whose shader supports cross-fading (the
    /// LOD_FADE_CROSSFADE shader variant); the log says at map load whether the shader has it. With a shader that does
    /// not, both LOD levels may be drawn on top of each other inside the band. Set it to false to get the game's plain
    /// LOD switch. The default is true.
    /// </summary>
    public bool LodCrossFade = true;

    /// <summary>
    /// Mesh details only: how wide the cross-fade band is when <see cref="LodCrossFade"/> is on, as a share from 0 to
    /// 1 of the on-screen size range in which a LOD level is shown. The game picks LOD levels by how much of the screen
    /// height an instance covers, so with 0.1 the blend covers the last tenth of that range and ends where the next
    /// level takes over. Larger values blend over a longer distance. The default of 0.1 is the game's own default.
    /// </summary>
    public float LodFadeWidth = 0.1f;

    /// <summary>
    /// Strength of the game's density mask for this detail (the game multiplies the mask channel by this number).
    /// Custom details leave the game's density mask switched off, so this value currently has no effect. The default
    /// of 5 is the value every one of the game's own details uses on the one map checked so far.
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
    /// How instance counts are written into this detail's density PNG: each pixel holds the count of its cell
    /// multiplied by this number, so the default of 16 makes painted areas clearly visible in an image editor. The
    /// largest count a cell can hold is therefore 255 divided by this number (15 by default). Set it to 1 to store
    /// the raw counts, which allows up to 255 instances per cell. Changing it after painting makes the saved files
    /// read back with different counts.
    /// </summary>
    public int DensityScale = 16;

    /// <summary>The largest number of instances one density cell of this detail can hold, see <see cref="DensityScale"/>.</summary>
    public int MaxCount => 255 / Mathf.Clamp(DensityScale, 1, 255);

    /// <summary>True when the detail is a mesh detail, which is the case when <see cref="Prefab"/> is set.</summary>
    public bool IsMesh => Prefab != null;

    /// <summary>Creates a detail with the given unique name. Set the remaining fields with an object initializer.</summary>
    public Detail(string name)
    {
        Name = name;
    }
}
