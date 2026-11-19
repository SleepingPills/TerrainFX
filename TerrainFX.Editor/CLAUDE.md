# Project
The objective is to build an in-game editor for TerrainFX features. This project requires work on both TerrainFX 
itself where most of the plumbing for defining the details and loading the data will live, and TerrainFX.Editor 
which will allow the user to edit and save the detail painted on the map. 

## Editor Characteristics
* Mode selection: the UI should support different modes, one of them being Detail Painting. Later on we'll also be 
  adding other modes, like adding various volume effects (dust clouds, falling leaves, etc)
* Detail Preview: we want to be able to see a gently rotating preview of the detail we are painting
* Brush preview: some sort of circle or other guide mapped onto the terrain to visualize where we are painting and 
  how big the radius is. Given cells are discrete, maybe highlight the cells that will be affected if possible?
* A nice UI with clickable buttons for changing from brush to eraser, changing the size and the density painted
* The UI should also show some sort of scrollable grid of the available details including a preview of the detail model
* Take inspiration from Photoshop, where one has a neat little window showing the brush shape (the detail preview), 
  size, density, etc. Alongside the ability to browse available brushes and switch between them.
* Ability to define hotkeys for the various functionalities

## Required TerrainFX Characteristics
### Define Custom Details in a Concise Way
Ideally in code (no configuration files to manage), in a single function we define the custom features one by one.

E.g something like:

```csharp
void DefineDetails(...)
{
    DetailRegistry.Add(new Detail(...));
    DetailRegistry.Add(new Detail(...));
    DetailRegistry.Add(new Detail(...));
}
```

NB: We must be able to define custom textures and colors for seasons (other than summer, which is the base season). 
If no texture is defined for a season, it must fall back on the base season (summer)
 
### Per-Level Data
Each level gets its own density maps. Store the density maps in the TerrainFX folder under a subfolder per map. If 
there's no existing folder, create it.

#### Storage
Store density maps in a grayscale png file if possible, so that they can also be opened in graphic editors.

### Code Architecture Directives
* Keep code simple, traceable and easy to reason about
* Few indirections
* Few abstraction
* Composition over inheritance
* Keep reflection to a minimum
* Use for loops instead of foreach
* Never use linq unless there's absolutely no other way

In this respect, get rid of the various Action hooks as they are difficult to follow.
