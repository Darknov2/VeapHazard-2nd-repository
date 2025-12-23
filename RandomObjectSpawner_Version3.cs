using System.Collections;
using System.Collections.Generic;
using UnityEngine;
using Unity.AI.Navigation;

/// Spawns random prefab structures on the terrain AFTER ProceduralTerrainGenerator has finished.
/// - Uses grid-aligned placement with origin at (0,0) in XZ and configurable cell size.
/// - Detects ground via downward raycast, then quantizes Y to the nearest lower grid layer.
/// - Keeps original prefab rotation (no leaning / no normal alignment).
public class RandomObjectSpawner : MonoBehaviour
{
    [Header("References")]
    public ProceduralTerrainGenerator generator;

    [Header("Prefabs")]
    [Tooltip("Prefabs to spawn randomly on the terrain.")]
    public GameObject[] structurePrefabs;

    [Header("Spawn Settings")]
    [Tooltip("How many structures to spawn once terrain is ready.")]
    public int initialSpawnCount = 20;

    [Tooltip("Avoid spawning too close to terrain edges (world units).")]
    public float edgePadding = 0f;

    [Header("Grid")]
    [Tooltip("Grid origin in world XZ. Defaults to (0,0).")]
    public Vector2 gridOriginXZ = Vector2.zero;

    [Tooltip("Grid cell size (applies to X, Z, and Y quantization).")]
    public float gridCellSize = 4f;

    [Tooltip("Optional world-space offset added after snapping.")]
    public Vector3 snapOffset = Vector3.zero;

    [Header("Ground Detection (Raycast)")]
    [Tooltip("Layers considered terrain for placement raycasts.")]
    public LayerMask groundMask = ~0;

    [Tooltip("Extra height above terrain top to start the downward ray.")]
    public float rayStartPadding = 10f;

    [Tooltip("Max distance for the downward raycast.")]
    public float maxRayDistance = 2000f;

    [Header("Sampling")]
    [Tooltip("Max tries per structure to find a valid point before skipping.")]
    public int maxTriesPerSpawn = 40;

    [Header("NavMesh Rebuild")]
    [Tooltip("Extra delay after spawning before triggering NavMesh rebuild.")]
    public float extraBakeDelay = 0.05f;
    [Tooltip("Name of the spawn container child of the generator.")]
    public string spawnContainerName = "SpawnedStructures";

    private bool hasBounds;
    private Vector3 minBound, maxBound;
    private int gridXMin, gridXMax, gridZMin, gridZMax;

    private void Awake()
    {
        if (generator == null)
            generator = FindFirstObjectByType<ProceduralTerrainGenerator>();
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
    }

    private void Start()
    {
        // In case terrain was already generated
        if (generator != null && generator.IsInitialTerrainReady)
            HandleInitialReady();
    }

    private void HandleInitialReady()
    {
        BuildBoundsFromConfig();
        BuildGridIndexRanges();
        StartCoroutine(SpawnInitialRoutine());
    }

    private IEnumerator SpawnInitialRoutine()
    {
        // Allow one frame after terrain ready (e.g., colliders finalize)
        yield return null;
        SpawnMany(initialSpawnCount);
        
        // Wait for spawned meshes to be ready, then rebuild NavMesh
        yield return new WaitForEndOfFrame();
        if (extraBakeDelay > 0f)
            yield return new WaitForSeconds(extraBakeDelay);
        
        TriggerNavMeshRebuild();
    }

    private void BuildBoundsFromConfig()
    {
        hasBounds = false;
        if (generator == null || generator.config == null) return;

        var cfg = generator.config;

        // Convert chunk counts and per-chunk voxel dimensions to world-space extents
        float chunkExtentXZ = cfg.chunkSizeXZ * cfg.voxelScale;
        float chunkExtentY  = cfg.chunkSizeY  * cfg.voxelScale;

        float width  = cfg.chunksX * chunkExtentXZ;
        float height = cfg.chunksY * chunkExtentY;
        float length = cfg.chunksZ * chunkExtentXZ;

        Vector3 min = cfg.worldOffset;
        Vector3 max = cfg.worldOffset + new Vector3(width, height, length);

        // Apply edge padding on XZ
        min.x += edgePadding; min.z += edgePadding;
        max.x -= edgePadding; max.z -= edgePadding;

        minBound = min;
        maxBound = max;
        hasBounds = true;
    }

    private void BuildGridIndexRanges()
    {
        if (!hasBounds) return;
        float cell = Mathf.Max(0.0001f, gridCellSize);

        gridXMin = Mathf.CeilToInt((minBound.x - gridOriginXZ.x) / cell);
        gridXMax = Mathf.FloorToInt((maxBound.x - gridOriginXZ.x) / cell);
        gridZMin = Mathf.CeilToInt((minBound.z - gridOriginXZ.y) / cell);
        gridZMax = Mathf.FloorToInt((maxBound.z - gridOriginXZ.y) / cell);
    }

    private void SpawnMany(int count)
    {
        if (structurePrefabs == null || structurePrefabs.Length == 0) return;
        if (!hasBounds) BuildBoundsFromConfig();
        if (!hasBounds) return;

        float cell = Mathf.Max(0.0001f, gridCellSize);

        // Ensure spawn container exists
        Transform spawnContainer = GetOrCreateSpawnContainer();

        // Build the list of grid cells in XZ within bounds
        var cells = new List<Vector2Int>(Mathf.Max(1, (gridXMax - gridXMin + 1) * (gridZMax - gridZMin + 1)));
        for (int gx = gridXMin; gx <= gridXMax; gx++)
            for (int gz = gridZMin; gz <= gridZMax; gz++)
                cells.Add(new Vector2Int(gx, gz));

        if (cells.Count == 0) return;

        for (int i = 0; i < count; i++)
        {
            // Try up to maxTries to find a valid grid cell position
            bool placed = false;
            for (int t = 0; t < Mathf.Max(1, maxTriesPerSpawn) && !placed; t++)
            {
                var cellIndex = cells[Random.Range(0, cells.Count)];

                // Compute grid-aligned XZ center for this cell
                float x = gridOriginXZ.x + cellIndex.x * cell;
                float z = gridOriginXZ.y + cellIndex.y * cell;

                // Ground detection via downward ray + Y quantization
                Vector3 pos = FindGroundGridPosition(x, z, cell);
                if (!float.IsNaN(pos.y))
                {
                    var prefab = structurePrefabs[Random.Range(0, structurePrefabs.Length)];
                    // Keep prefab’s original rotation; do not lean or randomize yaw
                    GameObject spawned = Instantiate(prefab, pos, prefab.transform.rotation);
                    spawned.transform.SetParent(spawnContainer, true);
                    placed = true;
                }
            }
        }
    }

    // Detect ground via raycast at the grid cell center (x,z),
    // then quantize Y down to the nearest grid layer based on gridCellSize.
    private Vector3 FindGroundGridPosition(float x, float z, float cellSize)
    {
        // Start the ray above the terrain’s top
        float topY = (generator != null && generator.config != null)
            ? generator.config.worldOffset.y
              + (generator.config.chunksY * generator.config.chunkSizeY * generator.config.voxelScale)
              + Mathf.Max(0f, rayStartPadding)
            : maxBound.y + Mathf.Max(0f, rayStartPadding);

        Vector3 rayOrigin = new Vector3(x, topY, z);

        if (Physics.Raycast(rayOrigin, Vector3.down, out RaycastHit hit, Mathf.Max(1000f, maxRayDistance), groundMask, QueryTriggerInteraction.Ignore))
        {
            // Quantize Y down to the nearest grid layer; baseline Y origin is 0 unless you want a custom baseline.
            float yOrigin = 0f;
            int gridY = Mathf.FloorToInt((hit.point.y - yOrigin) / cellSize);
            float y = yOrigin + gridY * cellSize;
            return new Vector3(x, y, z) + snapOffset;
        }

        // No ground found; return NaN Y to indicate failure
        return new Vector3(x, float.NaN, z);
    }

    private Transform GetOrCreateSpawnContainer()
    {
        if (generator == null) return transform;
        
        Transform container = generator.transform.Find(spawnContainerName);
        if (container == null)
        {
            GameObject containerGO = new GameObject(spawnContainerName);
            containerGO.transform.SetParent(generator.transform, false);
            container = containerGO.transform;
        }
        return container;
    }

    private void TriggerNavMeshRebuild()
    {
        // Try to use NavMeshTerrainBaker if available
        NavMeshTerrainBaker baker = generator != null ? generator.GetComponent<NavMeshTerrainBaker>() : null;
        
        if (baker != null)
        {
            Debug.Log("[RandomObjectSpawner] Triggering NavMesh rebuild via NavMeshTerrainBaker.RebuildNavMesh()");
            baker.RebuildNavMesh();
        }
        else
        {
            // Fallback: use NavMeshSurface if it exists (don't auto-create)
            NavMeshSurface surface = generator != null ? generator.GetComponent<NavMeshSurface>() : null;
            
            if (surface != null)
            {
                surface.collectObjects = CollectObjects.Children;
                Debug.Log("[RandomObjectSpawner] Triggering NavMesh rebuild via NavMeshSurface.BuildNavMesh()");
                surface.BuildNavMesh();
            }
            else
            {
                Debug.LogWarning("[RandomObjectSpawner] No NavMeshTerrainBaker or NavMeshSurface found. Please add one to enable NavMesh baking for spawned structures.");
            }
        }
    }
}