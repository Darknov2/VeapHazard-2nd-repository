using UnityEngine;
using System.Collections;
using System.Collections.Generic;
using System;
using System.Linq;
using Unity.AI.Navigation;
using UnityEngine.AI;

/// Revised streaming generator with prioritized, player-centered loading:
/// - Surface band chunks are prioritized and generated first.
/// - When player is moving downward, chunks below the player are generated top->down
///   so caves/surface transitions appear progressively and avoid visible pop-in below.
/// - Work is throttled per-frame (maxLoadsPerFrame) to avoid stalls while still making
///   the closest / most important chunks appear quickly.
/// - Ensures all chunks are assigned to the "Terrain" layer so NavMeshSurface can collect PhysicsColliders.
/// - Triggers a scoped NavMesh update per chunk right after the chunk mesh/collider is generated.
/// 
/// Tuning knobs (inspect the public fields below):
/// - maxLoadsPerFrame: how many new chunks may be created/generated per frame
/// - immediateRadiusChunks: chunks within this chunk-distance from the camera are treated as immediate high-priority candidates
/// - surfaceBandMargin: world units around surfaceBaseHeight that define the "surface band" (higher priority)
/// - preferDownWeight: when the camera is moving down, this weight biases lower chunks to be generated from top -> bottom
public class ProceduralTerrainGenerator : MonoBehaviour
{
    [Header("Config + Materials")]
    public ProceduralTerrainConfig config;
    public Material terrainMaterial;

    [Header("Vertical Streaming")]
    public bool verticalStreamingEnabled = true;
    public Transform target;
    public int viewRadiusY = 6;
    public int unloadMarginY = 2;
    public float streamingUpdateInterval = 0.2f;

    [Header("Streaming Behavior Tuning")]
    [Tooltip("Maximum number of chunk loads (Create + Generate) allowed per frame.")]
    public int maxLoadsPerFrame = 3;
    [Tooltip("If camera chunk-distance is <= this, mark chunk as immediate/high-priority.")]
    public int immediateRadiusChunks = 1;
    [Tooltip("World units around surfaceBaseHeight considered the surface band (highest priority).")]
    public float surfaceBandMargin = 1.0f;
    [Tooltip("Bias weight applied to prioritize lower chunks when player is moving down. Higher = stronger top->down priority.")]
    public float preferDownWeight = 4f;
    [Tooltip("If true, generate nearest chunks around the camera first.")]
    public bool preferCameraProximity = true;

    [Header("NavMesh Integration")]
    [Tooltip("Optional: Assign a NavMeshSurface to build/update when chunks are generated.")]
    public NavMeshSurface navMeshSurface;
    [Tooltip("Layers included for NavMesh collection. Should include only your Terrain layer.")]
    public LayerMask navmeshCollectLayers;
    [Tooltip("Extra padding added around the chunk bounds for local NavMesh updates.")]
    public float navmeshBoundsPadding = 0.5f;

    // Internal state
    private float nextStreamTime;
    private DensitySampler sampler;
    private readonly Dictionary<Vector3Int, TerrainChunk> chunks = new();

    // Pending load candidates and a small set for coalescing
    private readonly HashSet<Vector3Int> pendingSet = new HashSet<Vector3Int>();
    private readonly List<Vector3Int> pendingList = new List<Vector3Int>();

    // Last camera Y for direction detection
    private float lastTargetY;
    private bool firstUpdate = true;

    public event Action OnInitialTerrainReady;
    public bool IsInitialTerrainReady { get; private set; }

    private float lowestLoadedWorldY = float.PositiveInfinity;

    // Cached Terrain layer index
    private int terrainLayer = -1;

    private void Awake()
    {
        // Cache Terrain layer
        terrainLayer = LayerMask.NameToLayer("Terrain");
        if (terrainLayer < 0)
        {
            Debug.LogWarning("Layer 'Terrain' not found. Create it in Project Settings > Tags and Layers.");
        }

        // If a NavMeshSurface is assigned, ensure its layer mask matches the intended layers
        if (navMeshSurface != null)
        {
            if (navmeshCollectLayers == 0)
            {
                // Default to the surface's layerMask if provided; otherwise, try Terrain layer
                navmeshCollectLayers = navMeshSurface.layerMask != 0
                    ? navMeshSurface.layerMask
                    : (terrainLayer >= 0 ? (LayerMask)(1 << terrainLayer) : ~0);
            }
            else
            {
                navMeshSurface.layerMask = navmeshCollectLayers;
            }
        }
    }

    private void Start()
    {
        if (config == null)
        {
            Debug.LogError("No ProceduralTerrainConfig assigned.");
            return;
        }
        if (target == null && Camera.main != null)
            target = Camera.main.transform;

        sampler = new DensitySampler(config);
        IsInitialTerrainReady = false;

        if (verticalStreamingEnabled) StartCoroutine(InitialVerticalWarmup());
        else if (config.generateOnStart) GenerateAll();

        if (target != null) lastTargetY = target.position.y;
    }

    private IEnumerator InitialVerticalWarmup()
    {
        // Generate immediate surface band around start position so world looks ready quickly.
        UpdateVerticalStreaming(true);
        while (!InitialBandLoaded())
        {
            yield return null;
            UpdateVerticalStreaming(false);
        }
        yield return null;
        MarkInitialReadyOnce();

        // Optional: build whole NavMesh initially
        if (navMeshSurface != null)
        {
            navMeshSurface.BuildNavMesh();
        }
    }

    public void GenerateAll()
    {
        if (sampler == null) sampler = new DensitySampler(config);
        StopAllCoroutines();
        ClearExisting();
        if (config.asyncGeneration) StartCoroutine(GenerateRoutine());
        else GenerateImmediate();
    }

    private void ClearExisting()
    {
        foreach (var kv in chunks) if (kv.Value) Destroy(kv.Value.gameObject);
        chunks.Clear();
        pendingSet.Clear();
        pendingList.Clear();
        IsInitialTerrainReady = false;
        lowestLoadedWorldY = float.PositiveInfinity;
        firstUpdate = true;
    }

    private void Update()
    {
        if (!verticalStreamingEnabled || target == null) return;
        if (Time.time >= nextStreamTime)
        {
            UpdateVerticalStreaming(false);
            nextStreamTime = Time.time + Mathf.Max(0.01f, streamingUpdateInterval);
        }
    }

    private void UpdateVerticalStreaming(bool force)
    {
        if (sampler == null) sampler = new DensitySampler(config);

        float sY = config.chunkSizeY * config.voxelScale;
        Vector3 tpos = target.position - config.worldOffset;
        int centerY = Mathf.FloorToInt(tpos.y / sY);

        int vrY = Mathf.Max(1, viewRadiusY);
        int unloadSpanY = vrY + Mathf.Max(0, unloadMarginY);

        HashSet<Vector3Int> desired = BuildDesiredVerticalSet(centerY, vrY);

        // Unload chunks outside unloadSpanY
        List<Vector3Int> toUnload = new List<Vector3Int>();
        foreach (var kv in chunks)
        {
            int dy = Mathf.Abs(kv.Key.y - centerY);
            if (dy > unloadSpanY) toUnload.Add(kv.Key);
        }
        foreach (var c in toUnload)
        {
            if (chunks[c]) Destroy(chunks[c].gameObject);
            chunks.Remove(c);
            pendingSet.Remove(c);
            pendingList.Remove(c);
        }

        // Determine movement direction
        float verticalDelta = 0f;
        if (!firstUpdate)
        {
            verticalDelta = target.position.y - lastTargetY;
        }
        else firstUpdate = false;
        lastTargetY = target.position.y;
        bool movingDown = verticalDelta < -0.01f;

        // Enqueue newly desired chunks
        foreach (var c in desired)
        {
            if (chunks.ContainsKey(c)) continue;
            if (pendingSet.Add(c))
                pendingList.Add(c);
        }

        if (pendingList.Count == 0) return;

        // Build priority score list
        Vector3 camPos = target.position;
        float surfaceY = config.surfaceBaseHeight;

        var scored = new List<(Vector3Int c, float score)>(pendingList.Count);
        foreach (var c in pendingList)
        {
            // chunk world center Y
            float chunkCenterY = (c.y * config.chunkSizeY + config.chunkSizeY * 0.5f) * config.voxelScale + config.worldOffset.y;

            // surface band flag
            float chunkTopY = (c.y * config.chunkSizeY + config.chunkSizeY) * config.voxelScale + config.worldOffset.y;
            float chunkBottomY = (c.y * config.chunkSizeY) * config.voxelScale + config.worldOffset.y;
            bool isSurfaceBand = (surfaceY + surfaceBandMargin >= chunkBottomY) && (surfaceY - surfaceBandMargin <= chunkTopY);

            // horizontal distance from camera to chunk column (x,z)
            float chunkCenterX = (c.x * config.chunkSizeXZ + config.chunkSizeXZ * 0.5f) * config.voxelScale + config.worldOffset.x;
            float chunkCenterZ = (c.z * config.chunkSizeXZ + config.chunkSizeXZ * 0.5f) * config.voxelScale + config.worldOffset.z;
            float horizDist = Vector2.SqrMagnitude(new Vector2(chunkCenterX - camPos.x, chunkCenterZ - camPos.z));

            // immediate boost
            int camChunkX = Mathf.FloorToInt((camPos.x - config.worldOffset.x) / (config.chunkSizeXZ * config.voxelScale));
            int camChunkY = Mathf.FloorToInt((camPos.y - config.worldOffset.y) / (config.chunkSizeY * config.voxelScale));
            int camChunkZ = Mathf.FloorToInt((camPos.z - config.worldOffset.z) / (config.chunkSizeXZ * config.voxelScale));
            int dx = Mathf.Abs(c.x - camChunkX);
            int dy = Mathf.Abs(c.y - camChunkY);
            int dz = Mathf.Abs(c.z - camChunkZ);
            int chunkManhattan = dx + dy + dz;

            float score = horizDist; // base: prefer horizontally close

            if (isSurfaceBand)
            {
                score *= 0.01f;
                score -= 10000f;
            }

            if (preferCameraProximity)
            {
                score += (float)chunkManhattan * 10f;
            }

            if (movingDown)
            {
                if (chunkCenterY <= camPos.y)
                {
                    score += (camPos.y - chunkCenterY) / preferDownWeight;
                }
                else
                {
                    score += (chunkCenterY - camPos.y) * 0.5f;
                }
            }
            else
            {
                score += Mathf.Abs(chunkCenterY - camPos.y) * 0.5f;
            }

            if (dx <= immediateRadiusChunks && dy <= immediateRadiusChunks && dz <= immediateRadiusChunks)
                score -= 5000f;

            scored.Add((c, score));
        }

        // sort ascending by score and process up to maxLoadsPerFrame
        scored.Sort((a, b) => a.score.CompareTo(b.score));

        int loads = 0;
        var processedThisFrame = new List<Vector3Int>(Mathf.Min(maxLoadsPerFrame, scored.Count));
        for (int i = 0; i < scored.Count && loads < maxLoadsPerFrame; i++)
        {
            var cell = scored[i].c;
            if (chunks.ContainsKey(cell)) { pendingSet.Remove(cell); processedThisFrame.Add(cell); continue; }

            var chunk = CreateChunkGO(cell);
            chunks.Add(cell, chunk);

            if (config.asyncGeneration)
                StartCoroutine(GenerateChunkAsync(chunk));
            else
            {
                chunk.Generate();
                TryUpdateNavMeshForChunk(chunk);
            }

            float worldY = chunk.transform.position.y;
            if (worldY < lowestLoadedWorldY)
            {
                lowestLoadedWorldY = worldY;
                sampler.ExtendCavesToY(lowestLoadedWorldY);
            }

            loads++;
            processedThisFrame.Add(cell);
        }

        if (processedThisFrame.Count > 0)
        {
            foreach (var p in processedThisFrame) pendingSet.Remove(p);
            pendingList.Clear();
            pendingList.AddRange(pendingSet);
        }
    }

    private HashSet<Vector3Int> BuildDesiredVerticalSet(int centerY, int vrY)
    {
        var set = new HashSet<Vector3Int>();
        for (int dy = -vrY; dy <= vrY; dy++)
        {
            int cy = centerY + dy;
            for (int z = 0; z < config.chunksZ; z++)
                for (int x = 0; x < config.chunksX; x++)
                    set.Add(new Vector3Int(x, cy, z));
        }
        return set;
    }

    private bool InitialBandLoaded()
    {
        if (target == null) return false;
        float sY = config.chunkSizeY * config.voxelScale;
        int centerY = Mathf.FloorToInt((target.position - config.worldOffset).y / sY);
        int vrY = Mathf.Max(1, viewRadiusY);
        var needed = BuildDesiredVerticalSet(centerY, vrY);
        foreach (var c in needed)
            if (!chunks.ContainsKey(c)) return false;
        return true;
    }

    private IEnumerator GenerateChunkAsync(TerrainChunk chunk)
    {
        chunk.Generate();
        // After mesh/collider ready, update local navmesh area for this chunk
        TryUpdateNavMeshForChunk(chunk);
        yield return null;
    }

    private TerrainChunk CreateChunkGO(Vector3Int coord)
    {
        GameObject go = new($"Chunk_{coord.x}_{coord.y}_{coord.z}");
        go.transform.SetParent(transform, false);

        // Assign Terrain layer recursively so MeshCollider and children are on the Terrain layer
        if (terrainLayer >= 0) SetLayerRecursively(go, terrainLayer);

        var chunk = go.AddComponent<TerrainChunk>();
        var mr = go.GetComponent<MeshRenderer>();
        mr.sharedMaterial = terrainMaterial;

        chunk.Initialize(config, sampler, coord, DensitySampler.DensityGates.AllOn);
        return chunk;
    }

    private void SetLayerRecursively(GameObject obj, int layer)
    {
        obj.layer = layer;
        for (int i = 0; i < obj.transform.childCount; i++)
            SetLayerRecursively(obj.transform.GetChild(i).gameObject, layer);
    }

    private void GenerateImmediate()
    {
        for (int y = 0; y < config.chunksY; y++)
            for (int z = 0; z < config.chunksZ; z++)
                for (int x = 0; x < config.chunksX; x++)
                {
                    var c = new Vector3Int(x, y, z);
                    var chunk = CreateChunkGO(c);
                    chunks.Add(c, chunk);
                    chunk.Generate();
                    TryUpdateNavMeshForChunk(chunk);

                    float worldY = chunk.transform.position.y;
                    if (worldY < lowestLoadedWorldY)
                    {
                        lowestLoadedWorldY = worldY;
                        sampler.ExtendCavesToY(lowestLoadedWorldY);
                    }
                }
        StartCoroutine(MarkReadyNextFrame());
        Debug.Log("Terrain generation complete.");
    }

    private IEnumerator GenerateRoutine()
    {
        int parallel = Mathf.Max(1, config.maxParallelChunks);
        List<Coroutine> running = new();

        for (int y = 0; y < config.chunksY; y++)
            for (int z = 0; z < config.chunksZ; z++)
                for (int x = 0; x < config.chunksX; x++)
                {
                    var coord = new Vector3Int(x, y, z);
                    var chunk = CreateChunkGO(coord);
                    chunks.Add(coord, chunk);

                    var c = StartCoroutine(GenerateChunkAsync(chunk));
                    running.Add(c);

                    float worldY = chunk.transform.position.y;
                    if (worldY < lowestLoadedWorldY)
                    {
                        lowestLoadedWorldY = worldY;
                        sampler.ExtendCavesToY(lowestLoadedWorldY);
                    }

                    if (running.Count >= parallel)
                    {
                        foreach (var r in running) yield return r;
                        running.Clear();
                    }
                }
        foreach (var r in running) yield return r;

        yield return null;
        MarkInitialReadyOnce();
        Debug.Log("Terrain generation complete.");
    }

    private IEnumerator MarkReadyNextFrame()
    {
        yield return null;
        MarkInitialReadyOnce();
    }

    private void MarkInitialReadyOnce()
    {
        if (IsInitialTerrainReady) return;
        IsInitialTerrainReady = true;
        try { OnInitialTerrainReady?.Invoke(); } catch { }
    }

    /// <summary>
    /// Called by TerrainModificationManager to rebuild a loaded chunk after an edit.
    /// This keeps the old API and also refreshes local NavMesh around the chunk.
    /// </summary>
    public void EnqueueChunkRebuild(Vector3Int coord)
    {
        if (chunks.TryGetValue(coord, out var chunk) && chunk != null)
        {
            if (config.asyncGeneration)
                StartCoroutine(GenerateChunkAsync(chunk));
            else
            {
                chunk.Generate();
                TryUpdateNavMeshForChunk(chunk);
            }
        }
    }

    /// <summary>
    /// Compute chunk bounds and update NavMesh locally using PhysicsColliders.
    /// Avoids RenderMeshes collection to skip empty meshes.
    /// </summary>
    private void TryUpdateNavMeshForChunk(TerrainChunk chunk)
    {
        if (navMeshSurface == null) return;

        // Compute world-space bounds of this chunk
        Vector3 size = new Vector3(
            config.chunkSizeXZ * config.voxelScale,
            config.chunkSizeY  * config.voxelScale,
            config.chunkSizeXZ * config.voxelScale
        );
        Bounds bounds = new Bounds(chunk.transform.position + size * 0.5f, size);
        bounds.Expand(navmeshBoundsPadding * 2f);

        // Collect sources using PhysicsColliders on the selected layers
        var sources = new List<NavMeshBuildSource>();
        NavMeshBuilder.CollectSources(
            bounds,
            navMeshSurface.layerMask,                      // ensure this includes only Terrain
            NavMeshCollectGeometry.PhysicsColliders,       // prefer colliders over render meshes
            navMeshSurface.defaultArea,
            new List<NavMeshBuildMarkup>(),
            sources
        );

        var settings = navMeshSurface.GetBuildSettings();

        // Update only this area asynchronously
        NavMeshBuilder.UpdateNavMeshDataAsync(
            navMeshSurface.navMeshData,
            settings,
            sources,
            bounds
        );
    }
}