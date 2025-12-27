# Construction Carving Feature

## Overview

This feature enables terrain to automatically carve around construction objects, preventing terrain from overlapping with buildings, structures, or other placed objects. The carving system uses strict filtering to ensure only designated objects affect the terrain.

## Configuration

Configuration is done through the `ProceduralTerrainConfig` asset:

### Fields

1. **Construction Layer Mask** (`constructionLayerMask`)
   - Type: `LayerMask`
   - Purpose: Specifies which layers contain objects that should carve terrain
   - Usage: Set this to include only your "Construction" layer (or whatever layer you use for buildings)
   - Example: If your construction objects are on layer 8 ("Construction"), set this to layer 8

2. **Required Carving Tag** (`requiredCarvingTag`)
   - Type: `string`
   - Purpose: Optional additional filter by GameObject tag
   - Usage: Leave empty to carve all objects on the construction layer, or set to a specific tag (e.g., "Building") to only carve objects with that tag
   - Example: Set to "Building" to only carve objects tagged as "Building"

3. **Construction Carve Strength** (`constructionCarveStrength`)
   - Type: `float`
   - Default: `10.0`
   - Purpose: Controls how strongly construction objects carve terrain
   - Usage: Higher values create more aggressive carving; lower values create gentler carving
   - Recommended range: 5.0 to 20.0

## How It Works

### Filtering Logic

The carving system applies **strict filtering** to determine which colliders should carve terrain:

1. **Layer Check**: Only colliders on layers matching `constructionLayerMask` are considered
2. **Tag Check** (if configured): If `requiredCarvingTag` is not empty, only colliders with that exact tag are included
3. **Both conditions must be met**: A collider must pass BOTH the layer check AND the tag check (if configured)

This ensures that trees, foliage, enemies, or other objects NOT on the construction layer (or without the required tag) are **never carved**.

### Carving Process

1. When a terrain chunk is generated, it collects all carving colliders in the chunk region
2. For each collider that passes the filtering:
   - The collider's bounds (center, size, rotation) are stored
   - During density sampling, points inside the collider bounds receive negative density (carving)
   - The carving strength falls off smoothly from the center to the edges

### Performance Optimization

- **Caching**: Collider lookups are cached and refreshed every 1 second to avoid expensive scene searches
- **Manual Refresh**: When the `ConstructionSystem` places a new object, it automatically refreshes the cache
- **Spatial Culling**: Only colliders overlapping with the current chunk region are processed

## Usage Example

### Setup

1. Create a Unity Layer called "Construction" (e.g., layer 8)
2. Set all your building/structure prefabs to use the "Construction" layer
3. (Optional) Tag your building prefabs with "Building" tag
4. Open your `ProceduralTerrainConfig` asset
5. Set `constructionLayerMask` to include only the "Construction" layer
6. (Optional) Set `requiredCarvingTag` to "Building"
7. Adjust `constructionCarveStrength` to taste (start with 10.0)

### Placing Objects

When you place a construction object using the `ConstructionSystem`:
1. The object is instantiated at the placement position
2. The carving collider cache is automatically refreshed
3. The next time terrain chunks are regenerated, they will carve around the new object

### Manual Cache Refresh

If you place or remove construction objects through other means (not using `ConstructionSystem`), you can manually refresh the cache:

```csharp
ProceduralTerrainGenerator generator = FindObjectOfType<ProceduralTerrainGenerator>();
if (generator != null && generator.Sampler != null)
{
    generator.Sampler.RefreshCarvingColliderCache();
}
```

## Troubleshooting

### Terrain is not carving around my objects

**Check:**
1. Is the object's layer included in `constructionLayerMask`?
2. If `requiredCarvingTag` is set, does the object have that tag?
3. Is `constructionCarveStrength` > 0?
4. Has the terrain chunk been regenerated since placing the object?

### Terrain is carving around trees/foliage

**Fix:**
1. Ensure trees and foliage are NOT on the Construction layer
2. If they have the required tag, remove it or change the tag filter
3. The strict filtering should prevent this if configured correctly

### Performance issues

**Optimize:**
1. Keep the number of construction colliders reasonable (the system caches, but extreme numbers may still impact performance)
2. Consider using simpler collider shapes (box colliders are fastest)
3. The cache refresh interval is 1 second by default, which should be fine for most use cases

## Technical Details

### Files Modified

- `ProceduralTerrainConfig.cs`: Added configuration fields
- `DensitySampler.cs`: Implemented collider collection, filtering, and carving density calculation
- `TerrainChunk.cs`: Calls collider collection before chunk generation
- `ProceduralTerrainGenerator.cs`: Exposed Sampler property for external access
- `ConstructionSystem.cs`: Refreshes carving cache when objects are placed

### Data Flow

1. Config → DensitySampler (stores layer mask, tag, and strength)
2. TerrainChunk.Generate() → DensitySampler.CollectCarvingColliders()
3. DensitySampler filters colliders by layer AND tag
4. DensitySampler.SampleDensity() → CalculateCarvingDensity() for each filtered collider
5. Negative density is added, causing terrain to be carved away
