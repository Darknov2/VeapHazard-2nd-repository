using UnityEngine;
using System.Collections.Generic;

/// <summary>
/// Terrain modification manager (optimized).
/// Gizmo drawing removed per request.
/// </summary>
public class TerrainModificationManager : MonoBehaviour
{
    [Header("References")]
    public ProceduralTerrainGenerator generator;
    public bool autoFindGenerator = true;

    [Header("Edits")]
    [Tooltip("Limit number of modifiers (0 = unlimited). Oldest dropped if exceeded (hash rebuilt).")]
    public int maxModifiers = 0;

    [Header("Spatial Hash")]
    public bool useSpatialHash = true;
    [Tooltip("World units per hash cell (typical brush radius).")]
    public float hashCellSize = 8f;

    // Brush/modifier
    public struct SphereMod
    {
        public Vector3 center;
        public float radius;
        public float strength; // >0 add, <0 carve
    }

    private readonly List<SphereMod> modifiers = new List<SphereMod>(256);
    public List<SphereMod> GetSphereModifiers() => modifiers; // fallback if spatial hash not used

    // Spatial hash: cell -> list of modifier indices
    private struct CellKey
    {
        public int x, y, z;
        public CellKey(int x, int y, int z) { this.x = x; this.y = y; this.z = z; }
        public override int GetHashCode() => x * 73856093 ^ y * 19349663 ^ z * 83492791;
        public override bool Equals(object obj) => obj is CellKey k && x == k.x && y == k.y && z == k.z;
    }
    private readonly Dictionary<CellKey, List<int>> cellToMods = new Dictionary<CellKey, List<int>>(1024);

    private ProceduralTerrainConfig cfg;

    private void Awake()
    {
        ResolveGeneratorRef();
    }

    private void ResolveGeneratorRef()
    {
        if (generator == null && autoFindGenerator)
            generator = Object.FindFirstObjectByType<ProceduralTerrainGenerator>();
        if (generator != null)
            cfg = generator.config;
    }

    // Query nearby modifiers (used by DensitySampler)
    public bool TryGetNearbyModifiers(Vector3 worldPos, List<SphereMod> outList)
    {
        outList.Clear();
        if (!useSpatialHash || cellToMods.Count == 0)
        {
            outList.AddRange(modifiers);
            return outList.Count > 0;
        }

        float s = Mathf.Max(0.001f, hashCellSize);
        int cx = Mathf.FloorToInt(worldPos.x / s);
        int cy = Mathf.FloorToInt(worldPos.y / s);
        int cz = Mathf.FloorToInt(worldPos.z / s);

        HashSet<int> dedup = null;
        for (int dz = -1; dz <= 1; dz++)
        for (int dy = -1; dy <= 1; dy++)
        for (int dx = -1; dx <= 1; dx++)
        {
            var key = new CellKey(cx + dx, cy + dy, cz + dz);
            if (!cellToMods.TryGetValue(key, out var list)) continue;

            if (dedup == null) dedup = new HashSet<int>();
            for (int i = 0; i < list.Count; i++)
            {
                int idx = list[i];
                if (idx < 0 || idx >= modifiers.Count) continue;
                if (dedup.Add(idx)) outList.Add(modifiers[idx]);
            }
        }

        return outList.Count > 0;
    }

    public void ApplySphere(Vector3 center, float radius, float strength)
    {
        ResolveGeneratorRef();
        radius = Mathf.Max(0.01f, radius);

        if (maxModifiers > 0 && modifiers.Count >= maxModifiers)
        {
            modifiers.RemoveAt(0);
            RebuildSpatialHash();
        }

        int newIndex = modifiers.Count;
        modifiers.Add(new SphereMod { center = center, radius = radius, strength = strength });

        if (useSpatialHash) InsertIntoSpatialHash(newIndex);

        MarkRegionDirty(center, radius);
    }

    private void InsertIntoSpatialHash(int modIndex)
    {
        float s = Mathf.Max(0.001f, hashCellSize);
        SphereMod m = modifiers[modIndex];

        int ix0 = Mathf.FloorToInt((m.center.x - m.radius) / s);
        int iy0 = Mathf.FloorToInt((m.center.y - m.radius) / s);
        int iz0 = Mathf.FloorToInt((m.center.z - m.radius) / s);
        int ix1 = Mathf.FloorToInt((m.center.x + m.radius) / s);
        int iy1 = Mathf.FloorToInt((m.center.y + m.radius) / s);
        int iz1 = Mathf.FloorToInt((m.center.z + m.radius) / s);

        for (int iz = iz0; iz <= iz1; iz++)
        for (int iy = iy0; iy <= iy1; iy++)
        for (int ix = ix0; ix <= ix1; ix++)
        {
            var key = new CellKey(ix, iy, iz);
            if (!cellToMods.TryGetValue(key, out var list))
            {
                list = new List<int>(4);
                cellToMods[key] = list;
            }
            list.Add(modIndex);
        }
    }

    private void RebuildSpatialHash()
    {
        cellToMods.Clear();
        for (int i = 0; i < modifiers.Count; i++)
            InsertIntoSpatialHash(i);
    }

    private void MarkRegionDirty(Vector3 center, float radius)
    {
        if (generator == null || cfg == null) return;

        float csX = cfg.chunkSizeXZ * cfg.voxelScale;
        float csY = cfg.chunkSizeY  * cfg.voxelScale;
        float csZ = cfg.chunkSizeXZ * cfg.voxelScale;

        Vector3 local = center - cfg.worldOffset;

        int minX = Mathf.FloorToInt((local.x - radius) / csX);
        int maxX = Mathf.FloorToInt((local.x + radius) / csX);
        int minY = Mathf.FloorToInt((local.y - radius) / csY);
        int maxY = Mathf.FloorToInt((local.y + radius) / csY);
        int minZ = Mathf.FloorToInt((local.z - radius) / csZ);
        int maxZ = Mathf.FloorToInt((local.z + radius) / csZ);

        for (int y = minY; y <= maxY; y++)
        for (int z = minZ; z <= maxZ; z++)
        for (int x = minX; x <= maxX; x++)
            generator.EnqueueChunkRebuild(new Vector3Int(x, y, z));
    }
}