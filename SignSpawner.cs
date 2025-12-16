using UnityEngine;
using UnityEngine.AI;

/// <summary>
/// Spawns four Sign prefabs at the center of each edge of the procedural terrain
/// once generation is complete: North, East, South, West.
/// - Computes edge centers from ProceduralTerrainGenerator.config (chunks/size/voxelScale/worldOffset).
/// - Raycasts to ground to place signs on the surface.
/// - Sets Sign.side and ownerWorldIndex (from GameState.SelectedWorldIndex).
/// - Orients each sign to face inward toward the terrain center.
/// - Optional: ensures placement is on/near NavMesh.
///
/// Usage:
/// - Add this component to a scene GameObject.
/// - Assign 'generator' and 'signPrefab' in the inspector.
/// - Optionally tweak 'edgePadding' and 'groundMask'.
/// </summary>
[DisallowMultipleComponent]
public class SignSpawner : MonoBehaviour
{
    [Header("References")]
    [Tooltip("Procedural terrain generator that exposes config and readiness.")]
    public ProceduralTerrainGenerator generator;

    [Tooltip("Sign prefab to instantiate at the four edge centers.")]
    public GameObject signPrefab;

    [Header("Placement")]
    [Tooltip("Avoid placing exactly on the boundary; inward padding in world units.")]
    public float edgePadding = 2f;

    [Tooltip("Vertical lift above ground hit to avoid clipping.")]
    public float spawnYOffset = 0.05f;

    [Tooltip("Layers considered ground for the placement raycasts.")]
    public LayerMask groundMask = ~0;

    [Header("NavMesh (optional)")]
    [Tooltip("Snap placed signs to nearest NavMesh position if available.")]
    public bool snapToNavMesh = false;

    [Tooltip("Max distance to search for a nearby NavMesh position.")]
    public float navMeshMaxSnapDistance = 2f;

    [Tooltip("NavMesh areas used when sampling (default: all).")]
    public int navMeshAreaMask = NavMesh.AllAreas;

    [Header("Debug")]
    public bool verboseLogs = false;

    private bool _spawned = false;

    private void Reset()
    {
        generator = FindFirstObjectByType<ProceduralTerrainGenerator>();
    }

    private void Awake()
    {
        if (generator == null)
            generator = FindFirstObjectByType<ProceduralTerrainGenerator>();
    }

    private void OnEnable()
    {
        if (generator != null)
            generator.OnInitialTerrainReady += HandleTerrainReady;
    }

    private void OnDisable()
    {
        if (generator != null)
            generator.OnInitialTerrainReady -= HandleTerrainReady;
    }

    private void Start()
    {
        // If terrain was already generated (e.g., play mode reload)
        if (generator != null && generator.IsInitialTerrainReady)
            HandleTerrainReady();
    }

    private void HandleTerrainReady()
    {
        if (_spawned) return;

        if (generator == null || generator.config == null)
        {
            Debug.LogWarning("[SignSpawner] Missing generator or generator.config.");
            return;
        }

        if (signPrefab == null)
        {
            Debug.LogError("[SignSpawner] signPrefab is not assigned.");
            return;
        }

        // Compute bounds from generator.config
        var cfg = generator.config;

        float cellXZ = cfg.chunkSizeXZ * cfg.voxelScale;
        float cellY  = cfg.chunkSizeY  * cfg.voxelScale;

        float width  = Mathf.Max(0f, cfg.chunksX) * cellXZ;
        float length = Mathf.Max(0f, cfg.chunksZ) * cellXZ;
        float height = Mathf.Max(0f, cfg.chunksY) * cellY;

        Vector3 min = cfg.worldOffset;
        Vector3 max = cfg.worldOffset + new Vector3(width, height, length);

        // Apply padding inward on XZ
        min.x += edgePadding;
        min.z += edgePadding;
        max.x -= edgePadding;
        max.z -= edgePadding;

        Vector3 center = (min + max) * 0.5f;

        // Edge centers (in world space, using min/max XZ, center Z/X)
        Vector3 north = new Vector3(center.x, center.y, max.z);  // +Z
        Vector3 south = new Vector3(center.x, center.y, min.z);  // -Z
        Vector3 east  = new Vector3(max.x,    center.y, center.z); // +X
        Vector3 west  = new Vector3(min.x,    center.y, center.z); // -X

        // Place signs
        SpawnSignAt(north, Sign.Side.North, center);
        SpawnSignAt(east,  Sign.Side.East,  center);
        SpawnSignAt(south, Sign.Side.South, center);
        SpawnSignAt(west,  Sign.Side.West,  center);

        _spawned = true;
    }

    private void SpawnSignAt(Vector3 edgeCenter, Sign.Side side, Vector3 terrainCenter)
    {
        // Raycast down to find ground
        const float castHeight = 100f;
        Vector3 rayOrigin = new Vector3(edgeCenter.x, edgeCenter.y + castHeight, edgeCenter.z);

        Vector3 spawnPos = edgeCenter;
        if (Physics.Raycast(rayOrigin, Vector3.down, out RaycastHit hit, castHeight * 2f, groundMask, QueryTriggerInteraction.Ignore))
        {
            spawnPos = hit.point + hit.normal * Mathf.Max(0f, spawnYOffset);
        }
        else
        {
            if (verboseLogs) Debug.LogWarning($"[SignSpawner] Ground raycast did not hit at edge {side}. Placing at edgeCenter Y.");
            spawnPos.y = terrainCenter.y; // fallback height
        }

        // Instantiate
        var go = Instantiate(signPrefab, spawnPos, Quaternion.identity);

        // Orient to face inward (toward terrain center), keep upright
        Vector3 toCenter = terrainCenter - go.transform.position;
        toCenter.y = 0f;
        if (toCenter.sqrMagnitude > 0.0001f)
            go.transform.rotation = Quaternion.LookRotation(toCenter.normalized, Vector3.up);

        // Optional NavMesh snap
        if (snapToNavMesh)
        {
            if (NavMesh.SamplePosition(go.transform.position, out NavMeshHit nmh, navMeshMaxSnapDistance, navMeshAreaMask))
                go.transform.position = nmh.position;
        }

        // Configure Sign component
        var sign = go.GetComponent<Sign>();
        if (sign == null)
        {
            sign = go.AddComponent<Sign>();
            if (verboseLogs) Debug.LogWarning("[SignSpawner] signPrefab had no Sign component; added one dynamically.");
        }

        sign.side = side;
        sign.ownerWorldIndex = (GameState.SelectedWorldIndex >= 0) ? GameState.SelectedWorldIndex : 0;

        // Allow Sign to recompute side from center if needed and adjust rotation
        sign.DetermineSideFromCenter(terrainCenter);
    }
}