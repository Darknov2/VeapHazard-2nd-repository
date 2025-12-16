using UnityEngine;
using System.Collections.Generic;

/// <summary>
/// CaveSystem (fast) with local XZ-aware seeding and incremental insertion.
/// - Adds SeedBandLocal and ExtendCoverageToLocal so cave "worms" can be seeded only where needed (XZ + Y),
///   greatly reducing CPU when streaming in X/Z and when the player descends.
/// - Uses AddWormPathSegments to insert segments incrementally into the spatial hash (no full rebuild).
/// - Sample(...) now requests local extension when the queried Y is below current coverage, instead of a global extension.
/// 
/// Change: added a configurable maxTotalWorms to limit the total number of worms in the system.
/// </summary>
[System.Serializable]
public class CaveSystem
{
    public bool enabled = true;

    [Header("Roaming Worm Parameters")]
    public float wormsPerKm2Surface = 12f;
    public float wormsPerKm2Per50mDepth = 8f;
    public int   wormSteps = 240;
    public float stepLength = 1.4f;
    public float radius = 2.4f;
    public float carveStrength = 18f;

    [Header("Surface Merge")]
    public float bridgeMaxGap = 2.0f;
    public float bridgeBoost = 2.0f;

    [Header("Motion")]
    [Range(0f,1f)] public float inertia = 0.65f;
    [Range(0f,1f)] public float jitter = 0.35f;
    [Range(0f,0.5f)] public float verticalFlipChance = 0.02f;
    [Range(0f,1f)] public float downwardBias = 0.25f;

    [Header("Worm Count Limits")]
    [Tooltip("Maximum total number of worms in the cave system. Set to 0 or negative for unlimited.")]
    public int maxTotalWorms = 800;

    // Internal tuning
    private const float flowScale = 0.05f;
    private const int extensionBatch = 90;
    private const float extensionBuffer = 25f;
    private const float bandSkipDepthThreshold = 4f; // skip band if "depth" above surface is < this
    private const float bandMinSpawn = 4f;

    // Noise field
    private SimplexNoise flowNoise;

    // Worm data
    private readonly List<List<Vector3>> worms = new List<List<Vector3>>(128);
    private readonly List<Vector3> wormDirs = new List<Vector3>(128); // inertia directions

    private struct Segment { public Vector3 a,b,ab; public float ab2; public Vector3 min,max; }
    private readonly List<Segment> segments = new List<Segment>(8192);

    // Spatial hash
    private struct CellKey {
        public int x,y,z;
        public CellKey(int x,int y,int z){this.x=x;this.y=y;this.z=z;}
        public override int GetHashCode()=> x*73856093 ^ y*19349663 ^ z*83492791;
        public override bool Equals(object o)=> o is CellKey k && k.x==x && k.y==y && k.z==z;
    }
    private readonly Dictionary<CellKey,List<int>> cellToSeg = new Dictionary<CellKey,List<int>>(8192);
    private float cellSize;

    // Precomputed neighbor offsets (27 cells)
    private static readonly (int dx,int dy,int dz)[] Neighbor27 = BuildNeighbor27();
    private static (int,int,int)[] BuildNeighbor27()
    {
        var arr = new (int,int,int)[27];
        int i = 0;
        for (int dz=-1; dz<=1; dz++)
            for (int dy=-1; dy<=1; dy++)
                for (int dx=-1; dx<=1; dx++)
                    arr[i++] = (dx,dy,dz);
        return arr;
    }

    // World extents
    private Vector3 worldOffset;
    private float spanX, spanZ;

    // Surface sampler
    private System.Func<float,float,float> surfaceFractal2D;
    private float surfaceNoiseAmplitude;
    private float surfaceBaseHeight;

    // Coverage
    private float currentMinYCoverage = float.PositiveInfinity;
    private bool initialSeeded;

    // Cached random jitter directions to avoid per-step Random allocations
    private Vector3[] jitterCache;
    private int jitterIndex;

    // Caps to keep local seeding cheap
    private const int LocalWormCapPerCall = 250;
    private const int DefaultGlobalWormCap = 800;

    public void Initialize(int seed, Vector3 worldOffset, float spanX, float spanZ, float spanY,
                           int cavesLowLayer, int cavesHighLayer)
    {
        this.worldOffset = worldOffset;
        this.spanX = spanX;
        this.spanZ = spanZ;

        flowNoise = new SimplexNoise(seed + 811);
        worms.Clear();
        wormDirs.Clear();
        segments.Clear();
        cellToSeg.Clear();

        // Use radius (not step) as cellSize for spatial hash to be selective
        cellSize = Mathf.Max(2f * radius, 2f * radius);

        initialSeeded = false;

        // Precompute jitter directions
        jitterCache = new Vector3[1024];
        var prng = new System.Random(seed ^ 0xABCDEF);
        for (int i=0;i<jitterCache.Length;i++)
        {
            jitterCache[i] = RandomUnit(prng) * jitter;
        }
        jitterIndex = 0;
    }

    public void SetSurfaceSampler(System.Func<float,float,float> fractal2D,
                                  float surfaceNoiseAmplitude,
                                  float surfaceBaseHeight)
    {
        surfaceFractal2D = fractal2D;
        this.surfaceNoiseAmplitude = surfaceNoiseAmplitude;
        this.surfaceBaseHeight = surfaceBaseHeight;

        if (!initialSeeded)
        {
            // Seed a modest band near surface globally so early chunks show connected caves.
            SeedBand(surfaceBaseHeight - 6f, surfaceBaseHeight + 4f);
            BuildSegmentsAndHash();
            UpdateCoverageMinY();
            initialSeeded = true;
        }
    }

    private float SurfaceY(float x,float z)
    {
        float v = surfaceFractal2D!=null ? surfaceFractal2D(x,z) : 0f;
        return v * surfaceNoiseAmplitude + surfaceBaseHeight;
    }

    // Helper to determine remaining global capacity (0 or less => unlimited)
    private int RemainingGlobalCapacity()
    {
        if (maxTotalWorms <= 0) return int.MaxValue;
        int rem = maxTotalWorms - worms.Count;
        return Mathf.Max(0, rem);
    }

    // --- Local seeding: only spawn worms inside a local XZ circle (much cheaper) ---
    // centerXZ in world coords, radiusXZ in world units
    public void SeedBandLocal(float minY, float maxY, float centerX, float centerZ, float radiusXZ)
    {
        if (!enabled) return;

        // Depth heuristic skip
        float centerY = (minY + maxY) * 0.5f;
        float avgDepth = Mathf.Max(0f, surfaceBaseHeight - centerY);
        if (avgDepth < bandSkipDepthThreshold && initialSeeded) return;

        // Local area in m^2
        float areaLocal = Mathf.PI * Mathf.Max(0.01f, radiusXZ) * Mathf.Max(0.01f, radiusXZ);
        float areaKm2 = areaLocal / 1_000_000f;
        float wormsPerKm2 = wormsPerKm2Surface + (avgDepth/50f)*wormsPerKm2Per50mDepth;
        int toSpawn = Mathf.Clamp(Mathf.RoundToInt(wormsPerKm2 * areaKm2), (int)bandMinSpawn, LocalWormCapPerCall);

        // Respect global remaining capacity
        int remaining = RemainingGlobalCapacity();
        if (remaining <= 0) return;
        if (toSpawn > remaining) toSpawn = remaining;

        // Deterministic-ish seed derived from local center/minY
        int seed = (int)(centerX * 73856093f) ^ (int)(centerZ * 19349663f) ^ (int)(minY*17f);
        System.Random prng = new System.Random(seed);

        for (int w = 0; w < toSpawn; w++)
        {
            // uniform point in circle
            float a = (float)prng.NextDouble() * Mathf.PI * 2f;
            float r = radiusXZ * Mathf.Sqrt((float)prng.NextDouble());
            float rx = centerX + Mathf.Cos(a) * r;
            float rz = centerZ + Mathf.Sin(a) * r;

            float surfY = SurfaceY(rx, rz);
            float startY = Mathf.Clamp(surfY - 0.7f, minY, maxY);

            var path = new List<Vector3>(wormSteps + 8);
            Vector3 pos = new Vector3(rx, startY, rz);

            Vector3 dir = RandomUnit(prng);
            dir.y *= 0.3f;
            dir.Normalize();

            // generate worm path
            for (int s = 0; s < wormSteps; s++)
            {
                path.Add(pos);
                dir = NextDirectionFast(pos, dir, prng);
                pos += dir * stepLength;
            }

            // add only if we still have capacity (another thread/call might have filled it)
            if (RemainingGlobalCapacity() <= 0) break;

            int wi = worms.Count;
            worms.Add(path);
            wormDirs.Add(dir);

            // Insert segments incrementally (cheap)
            AddWormPathSegments(path);
        }

        UpdateCoverageMinY();
    }

    // Add segments from a single worm path incrementally into segments & hash (avoids full rebuild)
    private void AddWormPathSegments(List<Vector3> path)
    {
        if (path == null || path.Count < 2) return;

        float inflate = Mathf.Max(radius, 0.0001f);
        for (int i = 1; i < path.Count; i++)
        {
            Vector3 a = path[i - 1];
            Vector3 b = path[i];
            Vector3 ab = b - a;
            float ab2 = ab.sqrMagnitude;
            if (ab2 < 1e-8f) continue;

            Vector3 min = new Vector3(Mathf.Min(a.x, b.x) - inflate, Mathf.Min(a.y, b.y) - inflate, Mathf.Min(a.z, b.z) - inflate);
            Vector3 max = new Vector3(Mathf.Max(a.x, b.x) + inflate, Mathf.Max(a.y, b.y) + inflate, Mathf.Max(a.z, b.z) + inflate);

            int si = segments.Count;
            segments.Add(new Segment { a = a, b = b, ab = ab, ab2 = ab2, min = min, max = max });

            int ix0 = Mathf.FloorToInt(min.x / cellSize);
            int iy0 = Mathf.FloorToInt(min.y / cellSize);
            int iz0 = Mathf.FloorToInt(min.z / cellSize);
            int ix1 = Mathf.FloorToInt(max.x / cellSize);
            int iy1 = Mathf.FloorToInt(max.y / cellSize);
            int iz1 = Mathf.FloorToInt(max.z / cellSize);

            for (int iz = iz0; iz <= iz1; iz++)
            for (int iy = iy0; iy <= iy1; iy++)
            for (int ix = ix0; ix <= ix1; ix++)
            {
                var key = new CellKey(ix, iy, iz);
                if (!cellToSeg.TryGetValue(key, out var list))
                {
                    list = new List<int>(2);
                    cellToSeg[key] = list;
                }
                list.Add(si);
            }
        }
    }

    // Original global seeding retained (fallback)
    private void SeedBand(float minY, float maxY)
    {
        float centerY = (minY + maxY) * 0.5f;
        float avgDepth = Mathf.Max(0f, surfaceBaseHeight - centerY);
        if (avgDepth < bandSkipDepthThreshold && initialSeeded) return;

        float areaKm2 = Mathf.Max(0.001f,(spanX * spanZ)/1_000_000f);
        float wormsPerKm2 = wormsPerKm2Surface + (avgDepth/50f)*wormsPerKm2Per50mDepth;
        int toSpawn = Mathf.Clamp(Mathf.RoundToInt(wormsPerKm2 * areaKm2), (int)bandMinSpawn, (maxTotalWorms > 0 ? maxTotalWorms : DefaultGlobalWormCap));

        // Respect remaining global capacity
        int remaining = RemainingGlobalCapacity();
        if (remaining <= 0) return;
        if (toSpawn > remaining) toSpawn = remaining;

        System.Random prng = new System.Random((int)(centerY*397) ^ 2222);

        for (int w = 0; w < toSpawn; w++)
        {
            float rx = (float)prng.NextDouble() * spanX + worldOffset.x;
            float rz = (float)prng.NextDouble() * spanZ + worldOffset.z;
            float surfY = SurfaceY(rx, rz);
            float startY = Mathf.Clamp(surfY - 0.7f, minY, maxY);

            var path = new List<Vector3>(wormSteps + 16);
            Vector3 pos = new Vector3(rx, startY, rz);

            Vector3 dir = RandomUnit(prng);
            dir.y *= 0.3f;
            dir.Normalize();

            for (int s = 0; s < wormSteps; s++)
            {
                path.Add(pos);
                dir = NextDirectionFast(pos, dir, prng);
                pos += dir * stepLength;
            }

            // add only if capacity remains
            if (RemainingGlobalCapacity() <= 0) break;

            worms.Add(path);
            wormDirs.Add(dir);
            AddWormPathSegments(path);
        }
    }

    private Vector3 NextDirectionFast(Vector3 pos, Vector3 prevDir, System.Random prng)
    {
        float s = flowScale;
        float baseVal = flowNoise.Noise(pos.x*s,pos.y*s,pos.z*s);
        float dx = flowNoise.Noise((pos.x+0.7f)*s,pos.y*s,pos.z*s) - baseVal;
        float dy = flowNoise.Noise(pos.x*s,(pos.y+0.7f)*s,pos.z*s) - baseVal;
        float dz = flowNoise.Noise(pos.x*s,pos.y*s,(pos.z+0.7f)*s) - baseVal;

        Vector3 flowDir = new Vector3(dx, dy - downwardBias, dz);
        if (flowDir == Vector3.zero) flowDir = Vector3.forward;
        flowDir.Normalize();

        // Jitter from cache (wrap-around)
        Vector3 jitterVec = jitterCache[jitterIndex++];
        if (jitterIndex >= jitterCache.Length) jitterIndex = 0;

        // Occasional vertical flip
        if ((float)prng.NextDouble() < verticalFlipChance) flowDir.y = -flowDir.y;

        Vector3 dir = prevDir * inertia + flowDir * (1f - inertia) + jitterVec;
        if (dir == Vector3.zero) dir = prevDir != Vector3.zero ? prevDir : Vector3.forward;
        dir.Normalize();
        return dir;
    }

    private static Vector3 RandomUnit(System.Random prng)
    {
        float x = (float)prng.NextDouble()*2f-1f;
        float y = (float)prng.NextDouble()*2f-1f;
        float z = (float)prng.NextDouble()*2f-1f;
        Vector3 v = new Vector3(x,y,z);
        if (v == Vector3.zero) v = Vector3.forward;
        return v.normalized;
    }

    private void BuildSegmentsAndHash()
    {
        segments.Clear();
        cellToSeg.Clear();

        float inflate = Mathf.Max(radius, 0.0001f);
        for(int wi=0; wi<worms.Count; wi++)
        {
            var path = worms[wi];
            for(int i=1; i<path.Count; i++)
            {
                Vector3 a = path[i-1];
                Vector3 b = path[i];
                Vector3 ab = b-a;
                float ab2 = ab.sqrMagnitude;
                if (ab2 < 1e-8f) continue;

                Vector3 min = new Vector3(Mathf.Min(a.x,b.x) - inflate,
                                          Mathf.Min(a.y,b.y) - inflate,
                                          Mathf.Min(a.z,b.z) - inflate);
                Vector3 max = new Vector3(Mathf.Max(a.x,b.x) + inflate,
                                          Mathf.Max(a.y,b.y) + inflate,
                                          Mathf.Max(a.z,b.z) + inflate);

                int si = segments.Count;
                segments.Add(new Segment{a=a,b=b,ab=ab,ab2=ab2,min=min,max=max});

                int ix0 = Mathf.FloorToInt(min.x / cellSize);
                int iy0 = Mathf.FloorToInt(min.y / cellSize);
                int iz0 = Mathf.FloorToInt(min.z / cellSize);
                int ix1 = Mathf.FloorToInt(max.x / cellSize);
                int iy1 = Mathf.FloorToInt(max.y / cellSize);
                int iz1 = Mathf.FloorToInt(max.z / cellSize);

                for (int iz=iz0; iz<=iz1; iz++)
                for (int iy=iy0; iy<=iy1; iy++)
                for (int ix=ix0; ix<=ix1; ix++)
                {
                    var key = new CellKey(ix,iy,iz);
                    if (!cellToSeg.TryGetValue(key, out var list))
                    {
                        list = new List<int>(4);
                        cellToSeg[key] = list;
                    }
                    list.Add(si);
                }
            }
        }
    }

    private void UpdateCoverageMinY()
    {
        float minY = float.PositiveInfinity;
        for (int wi=0; wi<worms.Count; wi++)
        {
            var p = worms[wi];
            for(int i=0; i<p.Count; i++)
            {
                float y = p[i].y;
                if (y < minY) minY = y;
            }
        }
        currentMinYCoverage = minY;
    }

    // Public: extend coverage globally (legacy)
    public void ExtendCoverageTo(float targetMinY)
    {
        float bandTop = targetMinY - 5f;
        float bandBottom = targetMinY - 50f;
        SeedBand(bandBottom, bandTop);

        var prng = new System.Random((int)(targetMinY*927) ^ 8181);
        for(int wi=0; wi<worms.Count; wi++)
        {
            var path = worms[wi];
            if (path.Count == 0) continue;

            Vector3 dir = wormDirs[wi];
            Vector3 pos = path[path.Count - 1];
            int added = 0;
            while(pos.y > targetMinY - extensionBuffer && added < extensionBatch)
            {
                dir = NextDirectionFast(pos, dir, prng);
                pos += dir * stepLength;
                path.Add(pos);
                added++;
            }
            wormDirs[wi] = dir;
        }

        BuildSegmentsAndHash();
        UpdateCoverageMinY();
    }

    // New: extend coverage locally around an XZ center and radius
    public void ExtendCoverageToLocal(float targetMinY, float centerX, float centerZ, float radiusXZ)
    {
        float bandTop = targetMinY + 2f;
        float bandBottom = targetMinY - 60f;
        SeedBandLocal(bandBottom, bandTop, centerX, centerZ, Mathf.Max(1f, radiusXZ));

        // extend tails of existing worms a bit (cheap)
        var prng = new System.Random((int)(targetMinY*927) ^ 8181);
        for (int wi = 0; wi < worms.Count; wi++)
        {
            var path = worms[wi];
            if (path.Count == 0) continue;
            Vector3 dir = wormDirs[wi];
            Vector3 pos = path[path.Count - 1];
            int added = 0;
            while (pos.y > targetMinY - extensionBuffer && added < extensionBatch/4)
            {
                dir = NextDirectionFast(pos, dir, prng);
                pos += dir * stepLength;
                path.Add(pos);
                added++;
            }
            wormDirs[wi] = dir;
        }

        UpdateCoverageMinY();
    }

    public float Sample(Vector3 worldPos, float surfaceY, bool cavesGate)
    {
        if (!enabled || !cavesGate || segments.Count == 0) return 0f;

        // If sample is deeper than our current coverage, request local extension around worldPos XZ
        if (worldPos.y < currentMinYCoverage - extensionBuffer)
        {
            // Request local extension around this sample column (cheap)
            float localRadius = Mathf.Max(16f, radius * 6f);
            ExtendCoverageToLocal(worldPos.y - extensionBuffer, worldPos.x, worldPos.z, localRadius);
            // return 0 for now; the generator will rebuild affected chunks when they are generated
            // or subsequent Sample calls after extension will see carved segments.
            return 0f;
        }

        // Spatial hash nearest segment (precomputed 27 neighbor cells)
        float minDist2 = float.MaxValue;
        int cx = Mathf.FloorToInt(worldPos.x / cellSize);
        int cy = Mathf.FloorToInt(worldPos.y / cellSize);
        int cz = Mathf.FloorToInt(worldPos.z / cellSize);

        for (int i=0;i<Neighbor27.Length;i++)
        {
            var (dx,dy,dz) = Neighbor27[i];
            var key = new CellKey(cx+dx, cy+dy, cz+dz);
            if (!cellToSeg.TryGetValue(key, out var list)) continue;

            for (int j=0; j<list.Count; j++)
            {
                var s = segments[list[j]];
                // Tight AABB reject
                if (worldPos.x < s.min.x || worldPos.x > s.max.x ||
                    worldPos.y < s.min.y || worldPos.y > s.max.y ||
                    worldPos.z < s.min.z || worldPos.z > s.max.z)
                    continue;

                float t = Mathf.Clamp01(Vector3.Dot(worldPos - s.a, s.ab) / s.ab2);
                Vector3 closest = s.a + s.ab * t;
                float d2 = (worldPos - closest).sqrMagnitude;
                if (d2 < minDist2) minDist2 = d2;
            }
        }

        float r = radius;
        if (minDist2 >= r * r) return 0f;

        // Carve density (negative)
        float dist = Mathf.Sqrt(minDist2);
        float inner = 1f - dist / r;
        float carve = -carveStrength * inner;

        // Bridge near surface (positive) to fuse with terrain
        float gap = worldPos.y - surfaceY;
        if (gap > 0f && gap <= bridgeMaxGap)
        {
            float vert = 1f - Mathf.Clamp01(gap / Mathf.Max(bridgeMaxGap,0.0001f));
            carve += bridgeBoost * inner * vert;
        }

        return carve;
    }

#if UNITY_EDITOR
    public void DrawDebug()
    {
        if (worms == null) return;
        UnityEditor.Handles.color = Color.magenta;
        for (int wi=0; wi<worms.Count; wi++)
        {
            var p = worms[wi];
            for (int i=1;i<p.Count;i++)
                UnityEditor.Handles.DrawLine(p[i-1], p[i]);
        }
    }
#endif
}