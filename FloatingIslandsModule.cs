using UnityEngine;

/// <summary>
/// Infinite floating islands (no limits in X, Y, or Z).
/// - Stateless: per-cell deterministic hashing; no prebuilt lists.
/// - 3D grid (XZ and Y): islands continue to appear as the player moves up/down.
/// - Islands contribute only above the terrain surface; a small "bridge" density fuses near-surface gaps,
///   yielding one continuous iso-surface without penetration beneath the terrain.
/// - Very fast and GC-free: only a tiny neighborhood of cells around the sample point is evaluated.
///
/// New:
/// - public bool enabled = true; Inspector checkbox to enable/disable islands globally.
/// </summary>
[System.Serializable]
public class FloatingIslandsModule
{
    [Header("Enable/Disable")]
    [Tooltip("Master switch for floating islands generation.")]
    public bool enabled = true;

    [Header("Generation")]
    [Tooltip("Use stateless, per-cell hashing so islands spawn infinitely in X, Y, Z.")]
    public bool infinite = true;

    [Tooltip("Logical grid size for island seeding in XZ (world units).")]
    public float cellSizeXZ = 160f;

    [Tooltip("Logical grid size for island seeding in Y (world units).")]
    public float cellSizeY = 80f;

    [Tooltip("Max number of candidate islands evaluated per cell.")]
    [Range(1, 3)] public int maxIslandsPerCell = 2;

    [Tooltip("Chance (per candidate) to spawn an island in a cell.")]
    [Range(0f, 1f)] public float spawnProbability = 0.35f;

    [Header("Island Shape")]
    public Vector2 radiusRange   = new Vector2(8, 26);

    [Tooltip("Exponent shaping spherical falloff. Higher => sharper edges.")]
    public float islandFalloff = 1.6f;

    [Tooltip("Base positive density magnitude inside the island volume.")]
    public float densityBoost = 6f;

    [Header("Noise Displacement")]
    [Tooltip("Frequency of surface displacement noise.")]
    public float noiseScale = 0.12f;

    [Tooltip("Displacement amplitude (scaled by island radius).")]
    public float noiseDisplacement = 4f;

    [Header("Surface Merge (Bridging Density Only)")]
    [Tooltip("Maximum vertical gap (surfaceY -> island bottom) that will be filled by bridge density.")]
    public float bridgeMaxGap = 3.0f;

    [Tooltip("Horizontal factor (fraction of island radius) within which bridge density is applied.")]
    public float bridgeHorizontalFactor = 0.85f;

    [Tooltip("Bridge density boost added near the surface when gap < bridgeMaxGap.")]
    public float bridgeDensityBoost = 4f;

    [Header("Fade Above Surface")]
    [Tooltip("Height above surface over which island density fades in (fraction of radius).")]
    public float mergeFadeHeightFactor = 0.4f;
    [Tooltip("Exponent shaping the fade (1 = linear, >1 = steeper near top, <1 = stronger early).")]
    public float mergeFadePower  = 0.85f;

    [Header("Performance")]
    [Tooltip("Inflate island bound for quick AABB skip. 1.15 is a good default.")]
    public float boundsInflateFactor = 1.15f;

    [Header("Debug")]
    public bool drawCellBounds = false;
    public bool drawCenters = false;

    // Seed and utilities
    private int globalSeed = 12345;
    private Vector3 worldOffset;
    private SimplexNoise noise;

    // Surface sampler data (provided by DensitySampler)
    private System.Func<float, float, float> surfaceFractal2D;
    private float surfaceNoiseAmplitude;
    private float surfaceBaseHeight;

    public void Initialize(int globalSeed)
    {
        this.globalSeed = globalSeed;
        if (noise == null)
        {
            int mixed = MixSeed(globalSeed);
            noise = new SimplexNoise(mixed);
        }
    }

    public void SetWorldOffset(Vector3 offset) => worldOffset = offset;

    public void SetSurfaceSampler(System.Func<float, float, float> fractal2D,
                                  float surfaceNoiseAmplitude,
                                  float surfaceBaseHeight)
    {
        surfaceFractal2D = fractal2D;
        this.surfaceNoiseAmplitude = surfaceNoiseAmplitude;
        this.surfaceBaseHeight = surfaceBaseHeight;
    }

    // Core: sample positive density at worldPos (merge to terrain via bridging)
    public float Sample(Vector3 worldPos, float surfaceY)
    {
        // Early-out if disabled via checkbox
        if (!enabled) return 0f;

        float maxR = Mathf.Max(1f, radiusRange.y) * boundsInflateFactor;
        int reachXZ = Mathf.Max(1, Mathf.CeilToInt(maxR / Mathf.Max(1f, cellSizeXZ)));
        int reachY  = Mathf.Max(1, Mathf.CeilToInt(maxR / Mathf.Max(1f, cellSizeY)));

        Vector3 p = worldPos - worldOffset;
        int cx = Mathf.FloorToInt(p.x / cellSizeXZ);
        int cy = Mathf.FloorToInt(p.y / cellSizeY);
        int cz = Mathf.FloorToInt(p.z / cellSizeXZ);

        float total = 0f;

        for (int dy = -reachY; dy <= reachY; dy++)
        for (int dz = -reachXZ; dz <= reachXZ; dz++)
        for (int dx = -reachXZ; dx <= reachXZ; dx++)
        {
            int ix = cx + dx;
            int iy = cy + dy;
            int iz = cz + dz;

            for (int k = 0; k < maxIslandsPerCell; k++)
            {
                uint h = Hash4((uint)ix, (uint)iy, (uint)iz, (uint)(k ^ globalSeed));
                if (Rand01(ref h) > spawnProbability) continue;

                float lx = (ix + Rand01(ref h)) * cellSizeXZ;
                float ly = (iy + Rand01(ref h)) * cellSizeY;
                float lz = (iz + Rand01(ref h)) * cellSizeXZ;

                float rr = Mathf.Lerp(radiusRange.x, radiusRange.y, Rand01(ref h));
                Vector3 center = new Vector3(lx, ly, lz) + worldOffset;

                float inflate = rr * boundsInflateFactor;
                if (worldPos.x < center.x - inflate || worldPos.x > center.x + inflate ||
                    worldPos.y < center.y - inflate || worldPos.y > center.y + inflate ||
                    worldPos.z < center.z - inflate || worldPos.z > center.z + inflate)
                    continue;

                Vector3 to = worldPos - center;
                float dist = to.magnitude;
                if (dist > rr * 1.8f) continue;

                float baseShape = 1f - Mathf.Pow(dist / rr, Mathf.Max(0.1f, islandFalloff));
                if (baseShape <= 0f) continue;

                float n = noise.Noise(worldPos.x * noiseScale, worldPos.y * noiseScale, worldPos.z * noiseScale);
                float displaced = baseShape + n * (noiseDisplacement / rr);
                if (displaced <= 0f) continue;

                float islandDensity = displaced * densityBoost;

                float aboveSurface = worldPos.y - surfaceY;
                if (aboveSurface < 0f) continue;

                float mergeFadeHeight = Mathf.Max(1f, rr * mergeFadeHeightFactor);
                if (aboveSurface < mergeFadeHeight)
                {
                    float tFade = Mathf.Clamp01(aboveSurface / mergeFadeHeight);
                    islandDensity *= Mathf.Pow(tFade, mergeFadePower);
                }

                float bottomY = center.y - rr;
                float gap = bottomY - surfaceY;
                if (gap > 0f && gap <= bridgeMaxGap)
                {
                    float horiz = new Vector2(to.x, to.z).magnitude;
                    if (horiz < rr * bridgeHorizontalFactor)
                    {
                        float horizFactor = 1f - Mathf.Pow(horiz / (rr * bridgeHorizontalFactor), 2f);
                        float vertFactor  = 1f - ((worldPos.y - surfaceY) / Mathf.Max(gap, 0.0001f));
                        islandDensity += bridgeDensityBoost * horizFactor * Mathf.Clamp01(vertFactor);
                    }
                }

                if (islandDensity > 0f) total += islandDensity;
            }
        }

#if UNITY_EDITOR
        if (drawCellBounds)
        {
            Vector3 cmin = new Vector3(cx * cellSizeXZ, cy * cellSizeY, cz * cellSizeXZ) + worldOffset;
            UnityEditor.Handles.color = new Color(0, 1, 1, 0.2f);
            UnityEditor.Handles.DrawWireCube(
                cmin + new Vector3(cellSizeXZ * 0.5f, cellSizeY * 0.5f, cellSizeXZ * 0.5f),
                new Vector3(cellSizeXZ, cellSizeY, cellSizeXZ)
            );
        }
#endif

        return total;
    }

    private float SampleSurfaceY(float x, float z)
    {
        float v = surfaceFractal2D != null ? surfaceFractal2D(x, z) : 0f;
        return v * surfaceNoiseAmplitude + surfaceBaseHeight;
    }

    private static int MixSeed(int seed)
    {
        unchecked
        {
            uint u = (uint)seed ^ 0x9E3779B9u;
            u ^= u >> 16; u *= 2246822519u;
            u ^= u >> 13; u *= 3266489917u;
            u ^= u >> 16;
            return (int)u;
        }
    }

    private static uint Hash4(uint x, uint y, uint z, uint w)
    {
        unchecked
        {
            uint h = 2166136261u;
            h = (h ^ x) * 16777619u;
            h = (h ^ y) * 16777619u;
            h = (h ^ z) * 16777619u;
            h = (h ^ w) * 16777619u;
            h ^= h >> 15; h *= 2246822519u;
            h ^= h >> 13; h *= 3266489917u;
            h ^= h >> 16;
            return h;
        }
    }

    private static float Rand01(ref uint state)
    {
        unchecked
        {
            state ^= 2747636419u;
            state *= 2654435769u;
            state ^= state >> 16;
            state *= 2654435769u;
            state ^= state >> 16;
            return (state & 0xFFFFFF) / 16777215f;
        }
    }

#if UNITY_EDITOR
    public void DebugDraw(Vector3 at)
    {
        if (!drawCenters) return;
        Vector3 p = at - worldOffset;
        int cx = Mathf.FloorToInt(p.x / cellSizeXZ);
        int cy = Mathf.FloorToInt(p.y / cellSizeY);
        int cz = Mathf.FloorToInt(p.z / cellSizeXZ);
        Vector3 center = new Vector3((cx + 0.5f) * cellSizeXZ, (cy + 0.5f) * cellSizeY, (cz + 0.5f) * cellSizeXZ) + worldOffset;
        Gizmos.color = Color.yellow;
        Gizmos.DrawSphere(center, 0.6f);
    }
#endif
}