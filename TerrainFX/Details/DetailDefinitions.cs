using UnityEngine;

namespace TerrainFX.Details;

/// <summary>The single place where the custom details are declared.</summary>
internal static class DetailDefinitions
{
    /// <summary>Registers every custom detail. Called once at startup, before any map loads.</summary>
    public static void DefineDetails()
    {
        // Each detail is one DetailRegistry.Add call. The name must be unique; it is also part of the file name its
        // painted density is saved under (see DetailDensityStore), so renaming a detail leaves its painting behind.
        //
        // A mesh detail takes a direct child of the "TFX Foliage" prefab in the asset bundle as its Prefab:
        //
        //     DetailRegistry.Add(new Detail("TFX_MyRock") { Prefab = DetailAssets.Mesh("MyRock") });
        //
        // A texture detail takes the image drawn on its crossed quads as its Texture:
        //
        //     DetailRegistry.Add(new Detail("TFX_MyGrass") { Texture = DetailAssets.Texture("Assets/TerrainFX/MyGrass.png") });
        //
        // Mesh details can change their look per season. Summer is the base season: it and every season without an
        // entry keep the materials as they are in the bundle. A look can replace the texture, the normal map, the
        // colour and the base tint; whatever it leaves unset falls back to the material's own value:
        //
        //     SeasonLooks =
        //     {
        //         [ESeason.Winter] = new DetailSeasonLook
        //         {
        //             MainTexture = DetailAssets.Texture("Assets/TerrainFX/MyRock_Winter.png"),
        //             Color = new Color(0.9f, 0.9f, 1f)
        //         }
        //     }
        //
        // A detail can be left out of a raid entirely in some seasons. Each season is listed on its own:
        //
        //     HiddenSeasons = { ESeason.Winter, ESeason.SpringEarly }

        // DetailRegistry.Add(new Detail("TFX_Chunk_4")
        // {
        //     Prefab = DetailAssets.Mesh("Chunk_4"),
        //     WidthRange = new Vector2(0.8f, 1.2f),
        //     HeightRange = new Vector2(0.8f, 1.2f),
        //     // Colours only, the textures stay the ones from the bundle. Summer has no entry and is left as authored.
        //     SeasonLooks =
        //     {
        //         [ESeason.Spring] = new DetailSeasonLook { Color = new Color(0.8f, 0.95f, 0.6f) },
        //         [ESeason.Autumn] = new DetailSeasonLook { Color = new Color(0.9f, 0.6f, 0.3f) },
        //         [ESeason.AutumnLate] = new DetailSeasonLook { Color = new Color(0.6f, 0.45f, 0.3f) },
        //         [ESeason.Winter] = new DetailSeasonLook { Color = new Color(0.55f, 0.5f, 0.45f) }
        //     }
        // });

        // DetailRegistry.Add(new Detail("TFX_DemoTuft")
        // {
        //     Texture = DetailAssets.DemoTuft(),
        //     WidthRange = new Vector2(0.4f, 0.7f),
        //     HeightRange = new Vector2(0.4f, 0.7f)
        // });

        Plugin.Log.LogInfo($"Registered {DetailRegistry.Details.Count} custom detail(s)");
    }
}
