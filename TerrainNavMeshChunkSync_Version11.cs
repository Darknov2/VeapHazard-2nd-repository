using System.Collections.Generic;
using UnityEngine;
using UnityEngine.AI;
using Unity.AI.Navigation;

/// TerrainNavMeshChunkSync
/// Rebakes ONLY the modified terrain inside a single chunk and avoids full-surface builds.
/// - Collects PhysicsColliders (not render meshes) from a dedicated Terrain layer
/// - Builds local NavMeshData for the chunk bounds after you modify/regenerate the chunk
/// - Eliminates "invalid vertex data" warnings caused by empty MeshRenderers
///
/// Usage:
/// 1) Add to the same GameObject as ProceduralTerrainGenerator.
/// 2) Assign navSurface (NavMeshSurface). Set its Layer Mask to your "Terrain" layer.
/// 3) Ensure TerrainChunk GameObjects and children are on the "Terrain" layer.
/// 4) After TerrainChunk.Generate() finishes (MeshCollider enabled), call:
///    RebuildChunkNavMesh(chunkBounds)  OR  RebuildChunkNavMesh(chunkX, chunkY, chunkZ)
///
/// Notes:
/// - Requires Unity.AI.Navigation package
/// - Include using Unity.AI.Navigation and using UnityEngine.AI
/// - This component maintains per-chunk NavMeshDataInstances. Rebuilding replaces only that chunk’s area.
[RequireComponent(typeof(ProceduralTerrainGenerator))]
public class TerrainNavMeshChunkSync : MonoBehaviour
{
    [Header("References")]
    public ProceduralTerrainGenerator generator;
    public NavMeshSurface navSurface;

    [Header("Collection")]
    [Tooltip("Layers included when collecting sources. Should match your NavMeshSurface Layer Mask, e.g., Terrain.")]
    public LayerMask collectLayers = 0; // e.g., LayerMask.GetMask("Terrain")

    [Tooltip("Extra padding around chunk bounds when building.")]
    public float boundsPadding = 0.5f;

    [Tooltip("Minimum Y thickness for bounds to avoid thin volumes.")]
    public float minBoundsThicknessY = 5f;

    [Header("Agent")]
    [Tooltip("Agent type ID; should match NavMeshSurface agent type.")]
    public int agentTypeId = 0;

    // Per-chunk NavMesh instances
    private readonly Dictionary<Vector3Int, NavMeshDataInstance> chunkNavInstances = new Dictionary<Vector3Int, NavMeshDataInstance>();

    private void Reset()
    {
        generator = GetComponent<ProceduralTerrainGenerator>();
        navSurface = GetComponent<NavMeshSurface>();
    }

    private void Awake()
    {
        if (generator == null) generator = GetComponent<ProceduralTerrainGenerator>();
        if (navSurface == null) navSurface = GetComponent<NavMeshSurface>();

        // Default collectLayers to surface mask if not set
        if (collectLayers == 0 && navSurface != null && navSurface.layerMask != 0)
            collectLayers = navSurface.layerMask;

        // Default agentTypeId to surface’s agent if unset
        if (navSurface != null && agentTypeId == 0)
            agentTypeId = navSurface.GetBuildSettings().agentTypeID;
    }

    private void OnEnable()
    {
        if (generator != null)
            generator.OnInitialTerrainReady += HandleInitialReady;
    }

    private void OnDisable()
    {
        if (generator != null)
            generator.OnInitialTerrainReady -= HandleInitialReady;

        // Remove all per-chunk NavMesh instances
        foreach (var kv in chunkNavInstances)
            kv.Value.Remove();
        chunkNavInstances.Clear();
    }

    // Avoid calling a full BuildNavMesh here; let chunks rebake locally as they finish.
    private void HandleInitialReady()
    {
        // Optional: Do nothing or build once using PhysicsColliders if you want an initial whole-surface bake.
        // If you must, use BuildWholeNavMeshWithColliders() below. By default, we skip to prevent "invalid vertex data".
        // BuildWholeNavMeshWithColliders();
    }

    /// Rebuild only the NavMesh for the specified chunk indices (call right after you modify that chunk).
    public void RebuildChunkNavMesh(int chunkX, int chunkY, int chunkZ)
    {
        if (!ValidateReady()) return;
        Bounds b = GetChunkWorldBounds(chunkX, chunkY, chunkZ, boundsPadding);
        RebuildChunkNavMesh(b, new Vector3Int(chunkX, chunkY, chunkZ));
    }

    /// Rebuild only the NavMesh for the specified changed bounds (call right after you modify terrain within these bounds).
    public void RebuildChunkNavMesh(Bounds changedBounds)
    {
        if (!ValidateReady()) return;
        Vector3Int key = GuessChunkKey(changedBounds);
        RebuildChunkNavMesh(changedBounds, key);
    }

    private void RebuildChunkNavMesh(Bounds bounds, Vector3Int chunkKey)
    {
        if (!ValidateReady()) return;

        // Ensure minimum thickness in Y
        if (bounds.size.y < minBoundsThicknessY)
        {
            Vector3 size = bounds.size;
            size.y = minBoundsThicknessY;
            bounds.size = size;
        }
        bounds.Expand(boundsPadding * 2f);

        // Collect sources using PhysicsColliders (avoids empty MeshRenderer sources)
        var sources = new List<NavMeshBuildSource>();
        NavMeshBuilder.CollectSources(
            bounds,
            collectLayers == 0 ? navSurface.layerMask : collectLayers,
            NavMeshCollectGeometry.PhysicsColliders,
            navSurface.defaultArea,
            new List<NavMeshBuildMarkup>(),
            sources
        );

        // If no sources, remove any existing data for this chunk
        if (sources.Count == 0)
        {
            if (chunkNavInstances.TryGetValue(chunkKey, out var inst))
            {
                inst.Remove();
                chunkNavInstances.Remove(chunkKey);
            }
            return;
        }

        // Build fresh NavMeshData ONLY for this bounded area (rebake)
        var settings = navSurface.GetBuildSettings();
        settings.agentTypeID = agentTypeId;

        NavMeshData newData = NavMeshBuilder.BuildNavMeshData(
            settings,
            sources,
            bounds,
            Vector3.zero,
            Quaternion.identity
        );

        if (newData == null)
        {
            Debug.LogWarning($"TerrainNavMeshChunkSync: BuildNavMeshData returned null for chunk {chunkKey}.");
            return;
        }

        // Replace or add the chunk’s NavMeshDataInstance
        if (chunkNavInstances.TryGetValue(chunkKey, out var existingInstance))
        {
            existingInstance.Remove();
            chunkNavInstances.Remove(chunkKey);
        }

        var instance = NavMesh.AddNavMeshData(newData, Vector3.zero, Quaternion.identity);
        chunkNavInstances[chunkKey] = instance;
    }

    private bool ValidateReady()
    {
        if (generator == null || generator.config == null)
        {
            Debug.LogWarning("TerrainNavMeshChunkSync: Missing ProceduralTerrainGenerator or config.");
            return false;
        }
        if (navSurface == null)
        {
            Debug.LogWarning("TerrainNavMeshChunkSync: Missing NavMeshSurface.");
            return false;
        }
        return true;
    }

    private Bounds GetChunkWorldBounds(int cx, int cy, int cz, float padding)
    {
        var cfg = generator.config;

        float chunkSizeX = cfg.chunkSizeXZ * cfg.voxelScale;
        float chunkSizeY = cfg.chunkSizeY  * cfg.voxelScale;
        float chunkSizeZ = cfg.chunkSizeXZ * cfg.voxelScale;

        Vector3 min = cfg.worldOffset + new Vector3(
            cx * chunkSizeX,
            cy * chunkSizeY,
            cz * chunkSizeZ
        );

        Vector3 size = new Vector3(
            chunkSizeX,
            Mathf.Max(chunkSizeY, minBoundsThicknessY),
            chunkSizeZ
        );

        Bounds b = new Bounds(min + size * 0.5f, size);
        b.Expand(padding * 2f);
        return b;
    }

    private Vector3Int GuessChunkKey(Bounds b)
    {
        var cfg = generator.config;
        float sx = cfg.chunkSizeXZ * cfg.voxelScale;
        float sy = cfg.chunkSizeY  * cfg.voxelScale;
        float sz = cfg.chunkSizeXZ * cfg.voxelScale;

        Vector3 local = b.center - cfg.worldOffset;
        int cx = Mathf.FloorToInt(local.x / sx);
        int cy = Mathf.FloorToInt(local.y / sy);
        int cz = Mathf.FloorToInt(local.z / sz);
        return new Vector3Int(cx, cy, cz);
    }

    // Optional: full bake using PhysicsColliders (use sparingly)
    private void BuildWholeNavMeshWithColliders()
    {
        if (!ValidateReady()) return;

        var cfg = generator.config;
        float sx = cfg.chunkSizeXZ * cfg.voxelScale;
        float sy = cfg.chunkSizeY  * cfg.voxelScale;
        Bounds worldBounds = new Bounds(
            cfg.worldOffset + new Vector3(cfg.chunksX * sx, cfg.chunksY * sy, cfg.chunksZ * sx) * 0.5f,
            new Vector3(cfg.chunksX * sx, Mathf.Max(cfg.chunksY * sy, minBoundsThicknessY), cfg.chunksZ * sx)
        );

        var sources = new List<NavMeshBuildSource>();
        NavMeshBuilder.CollectSources(
            worldBounds,
            collectLayers == 0 ? navSurface.layerMask : collectLayers,
            NavMeshCollectGeometry.PhysicsColliders,
            navSurface.defaultArea,
            new List<NavMeshBuildMarkup>(),
            sources
        );

        var settings = navSurface.GetBuildSettings();
        settings.agentTypeID = agentTypeId;

        var newData = NavMeshBuilder.BuildNavMeshData(
            settings,
            sources,
            worldBounds,
            Vector3.zero,
            Quaternion.identity
        );
        if (newData != null)
        {
            // Remove any per-chunk instances to avoid overlap
            foreach (var kv in chunkNavInstances) kv.Value.Remove();
            chunkNavInstances.Clear();

            // Replace the surface data using NavMeshSurface helpers
            navSurface.RemoveData();
            navSurface.navMeshData = newData;
            navSurface.AddData();
        }
    }
}