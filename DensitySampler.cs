using UnityEngine;

public class DensitySampler
{
    public struct DensityGates
    {
        public bool surface;
        public bool caves;
        public bool islands;
        public static DensityGates AllOn => new DensityGates{ surface=true, caves=true, islands=true };
    }

    private readonly ProceduralTerrainConfig cfg;
    private readonly SimplexNoise surfaceNoise;
    private readonly FloatingIslandsModule islands;
    private readonly CaveSystem caveSystem;
    private TerrainModificationManager modificationManager;

    public DensitySampler(ProceduralTerrainConfig cfg)
    {
        this.cfg = cfg;
        surfaceNoise = new SimplexNoise(cfg.seed);

        islands = cfg.islands;
        islands.Initialize(cfg.seed);
        islands.SetWorldOffset(cfg.worldOffset);

        caveSystem = cfg.caves;

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

        float total = ground + islandDensity + caveDensity + editDensity;
        return total - iso;
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