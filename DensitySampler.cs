using UnityEngine;
using System.Collections.Generic;

public class DensitySampler
{
    public struct DensityGates
    {
        public bool surface;
        public bool caves;
        public bool islands;
        public static DensityGates AllOn => new DensityGates{ surface=true, caves=true, islands=true };
    }

    public struct CarvingColliderData
    {
        public Vector3 center;
        public Vector3 size;
        public Quaternion rotation;
        public float carveStrength;
    }

    private readonly ProceduralTerrainConfig cfg;
    private readonly SimplexNoise surfaceNoise;
    private readonly FloatingIslandsModule islands;
    private readonly CaveSystem caveSystem;
    private TerrainModificationManager modificationManager;

    // Construction carving
    private LayerMask constructionLayerMask;
    private string requiredCarvingTag;
    private readonly List<CarvingColliderData> carvingColliders = new List<CarvingColliderData>(128);
    
    // Cache for construction colliders to avoid expensive FindObjectsOfType calls
    private Collider[] cachedConstructionColliders;
    private float lastColliderCacheTime = -1f;
    private const float ColliderCacheRefreshInterval = 1.0f; // Refresh cache every second

    public DensitySampler(ProceduralTerrainConfig cfg)
    {
        this.cfg = cfg;
        surfaceNoise = new SimplexNoise(cfg.seed);

        islands = cfg.islands;
        islands.Initialize(cfg.seed);
        islands.SetWorldOffset(cfg.worldOffset);

        caveSystem = cfg.caves;

        // Store construction carving configuration
        constructionLayerMask = cfg.constructionLayerMask;
        requiredCarvingTag = cfg.requiredCarvingTag;

        if (cfg.autoComputeSurfaceHeight)
        {
            cfg.surfaceBaseHeight = (cfg.surfaceLayerIndex + 0.5f) * cfg.chunkSizeY * cfg.voxelScale + cfg.worldOffset.y;
        }

        caveSystem.Initialize(
            cfg.seed,
            cfg.worldOffset,
            cfg.chunksX * cfg.chunkSizeXZ * cfg.voxelScale,
            cfg.chunksZ * cfg.chunkSizeXZ * cfg.voxelScale,
            cfg.chunksY * cfg.chunkSizeY * cfg.voxelScale,
            cfg.cavesStartLayer,
            cfg.cavesEndLayer < 0 ? cfg.surfaceLayerIndex - 1 : cfg.cavesEndLayer
        );

        caveSystem.SetSurfaceSampler(Fractal2D, cfg.surfaceNoiseAmplitude, cfg.surfaceBaseHeight);
        islands.SetSurfaceSampler(Fractal2D, cfg.surfaceNoiseAmplitude, cfg.surfaceBaseHeight);

        modificationManager = Object.FindFirstObjectByType<TerrainModificationManager>();
    }

    public void ExtendCavesToY(float worldMinY) => caveSystem.ExtendCoverageTo(worldMinY);

    // New: extend caves locally around a world-space XZ center and radius (cheap)
    public void ExtendCavesToYLocal(float worldMinY, Vector3 centerXZ, float radiusXZ)
    {
        caveSystem.ExtendCoverageToLocal(worldMinY, centerXZ.x, centerXZ.z, radiusXZ);
    }

    /// <summary>
    /// Manually refresh the construction collider cache.
    /// Call this after placing or removing construction objects to ensure immediate carving updates.
    /// </summary>
    public void RefreshCarvingColliderCache()
    {
        cachedConstructionColliders = null;
        lastColliderCacheTime = -1f;
    }

    /// <summary>
    /// Collect construction carving colliders within a specified region.
    /// Only colliders matching the layer mask and optional tag are included.
    /// Note: Uses cached colliders refreshed every ColliderCacheRefreshInterval seconds
    /// to avoid expensive FindObjectsOfType calls on every chunk generation.
    /// </summary>
    public void CollectCarvingColliders(Vector3 chunkMin, Vector3 chunkMax)
    {
        carvingColliders.Clear();
        
        // Skip if no construction layer mask is configured
        if (constructionLayerMask == 0) return;

        // Refresh cache if needed
        // Note: We use FindObjectsOfType with caching instead of Physics.OverlapBox because:
        // 1. We need to filter by layer mask which Physics queries don't support well
        // 2. Caching amortizes the cost across multiple chunk generations
        // 3. Construction objects are typically static and don't change frequently
        float currentTime = Time.realtimeSinceStartup;
        if (cachedConstructionColliders == null || currentTime - lastColliderCacheTime > ColliderCacheRefreshInterval)
        {
            cachedConstructionColliders = Object.FindObjectsOfType<Collider>();
            lastColliderCacheTime = currentTime;
        }
        
        foreach (var collider in cachedConstructionColliders)
        {
            // Skip null colliders (may have been destroyed)
            if (collider == null) continue;
            
            // Skip if not on construction layer
            if (((1 << collider.gameObject.layer) & constructionLayerMask) == 0)
                continue;
            
            // Skip if required tag is set and doesn't match
            if (!string.IsNullOrEmpty(requiredCarvingTag) && 
                !collider.gameObject.CompareTag(requiredCarvingTag))
                continue;
            
            // Get bounds and check if it overlaps with chunk region
            Bounds bounds = collider.bounds;
            
            // Expand chunk bounds slightly to catch colliders on edges
            Vector3 expandedMin = chunkMin - Vector3.one * 2f;
            Vector3 expandedMax = chunkMax + Vector3.one * 2f;
            
            // Simple AABB overlap test
            if (bounds.max.x < expandedMin.x || bounds.min.x > expandedMax.x ||
                bounds.max.y < expandedMin.y || bounds.min.y > expandedMax.y ||
                bounds.max.z < expandedMin.z || bounds.min.z > expandedMax.z)
                continue;
            
            // Add to carving list
            CarvingColliderData data = new CarvingColliderData
            {
                center = collider.bounds.center,
                size = collider.bounds.size,
                rotation = collider.transform.rotation,
                carveStrength = cfg.constructionCarveStrength
            };
            
            carvingColliders.Add(data);
        }
    }

    public float SampleDensity(Vector3 worldPos, in DensityGates gates)
    {
        float iso = cfg.isoLevel;
        float surfaceY = Fractal2D(worldPos.x, worldPos.z) * cfg.surfaceNoiseAmplitude + cfg.surfaceBaseHeight;

        float sdf = surfaceY - worldPos.y;
        float ground = sdf > 0f ? Mathf.Min(sdf * cfg.groundDensityScale, cfg.maxGroundDensity > 0f ? cfg.maxGroundDensity : float.MaxValue) : 0f;

        // Respect both the gate and the module's enabled checkbox
        float islandDensity = (gates.islands && islands.enabled) ? islands.Sample(worldPos, surfaceY) : 0f;
        float caveDensity   = gates.caves ? caveSystem.Sample(worldPos, surfaceY, true) : 0f;

        float editDensity = 0f;
        if (modificationManager != null)
        {
            var mods = modificationManager.GetSphereModifiers();
            for (int i=0;i<mods.Count;i++)
            {
                var m = mods[i];
                float d = Vector3.Distance(worldPos, m.center);
                if (d > m.radius) continue;
                float t = 1f - d / m.radius;
                editDensity += m.strength * t * t;
            }
        }

        // Apply construction carving from colliders
        float carvingDensity = 0f;
        for (int i = 0; i < carvingColliders.Count; i++)
        {
            var carver = carvingColliders[i];
            carvingDensity += CalculateCarvingDensity(worldPos, carver);
        }

        float total = ground + islandDensity + caveDensity + editDensity + carvingDensity;
        return total - iso;
    }

    /// <summary>
    /// Calculate the carving density contribution from a single collider.
    /// Returns negative density (carving) when the point is inside the collider bounds.
    /// </summary>
    private float CalculateCarvingDensity(Vector3 worldPos, CarvingColliderData carver)
    {
        // Calculate signed distance to box (approximation)
        Vector3 localPos = worldPos - carver.center;
        
        // Rotate point into box local space (inverse rotation)
        localPos = Quaternion.Inverse(carver.rotation) * localPos;
        
        // Calculate distance to box surface
        Vector3 halfSize = carver.size * 0.5f;
        Vector3 d = new Vector3(
            Mathf.Abs(localPos.x) - halfSize.x,
            Mathf.Abs(localPos.y) - halfSize.y,
            Mathf.Abs(localPos.z) - halfSize.z
        );
        
        // Signed distance to box
        float maxD = Mathf.Max(d.x, Mathf.Max(d.y, d.z));
        
        // If inside the box, apply carving with falloff
        if (maxD < 0f)
        {
            float maxHalfSize = Mathf.Max(halfSize.x, Mathf.Max(halfSize.y, halfSize.z));
            // Guard against division by zero for degenerate colliders
            if (maxHalfSize > 0.001f)
            {
                float falloff = 1f - Mathf.Abs(maxD) / maxHalfSize;
                return -carver.carveStrength * falloff;
            }
        }
        
        return 0f;
    }

    private float Fractal2D(float x, float z)
    {
        float amp=1f, freq=cfg.surfaceNoiseScale, sum=0f, max=0f;
        for(int i=0;i<cfg.surfaceOctaves;i++)
        {
            float n = surfaceNoise.Noise(x*freq,0f,z*freq);
            sum += n*amp;
            max += amp;
            amp *= cfg.surfacePersistence;
            freq*= cfg.surfaceLacunarity;
        }
        return sum/Mathf.Max(max,1e-6f);
    }

#if UNITY_EDITOR
    public void DrawDebug() => caveSystem.DrawDebug();
#endif
}