using UnityEngine;
using System.Collections.Generic;

/// <summary>
/// Construction / building system with per-prefab and per-instance snap sizes (independent X/Y/Z),
/// no extra components, and a configurable screen-space aim offset.
/// </summary>
public class ConstructionSystem : MonoBehaviour
{
    [Header("Build Prefabs")]
    public GameObject[] buildPrefabs;
    public int selectedIndex = 0;

    [Header("Per-Prefab Snap Sizes (X/Y/Z)")]
    [Tooltip("Per-prefab snap cell size per axis. Index-aligned with buildPrefabs. <= 0 on an axis falls back to defaultCellSizeXYZ for that axis.")]
    public Vector3[] prefabSnapSizeXYZ;

    [Header("Default Grid Settings")]
    [Tooltip("Default grid cell size per axis. Used when a prefab size is not provided or <= 0 on an axis.")]
    public Vector3 defaultCellSizeXYZ = Vector3.one;
    [Tooltip("Optional snap offset added after snapping to grid.")]
    public Vector3 buildSnapOffset = Vector3.zero;

    [Header("References")]
    public ProceduralTerrainGenerator terrain;
    public Material ghostGreenMaterial;
    public Material ghostRedMaterial;
    public LayerMask placementMask = ~0;
    public float raycastDistance = 100f;
    public PlayerController playerController;
    public Transform cameraTransform;

    [Header("Preview Aim Offset")]
    [Tooltip("Horizontal offset of the preview aim in normalized screen units (-0.5..0.5). Negative moves left.")]
    [Range(-0.5f, 0.5f)] public float aimXOffset = -0.08f; // slightly left by default
    [Tooltip("Vertical offset of the preview aim in normalized screen units (-0.5..0.5). Positive moves up.")]
    [Range(-0.5f, 0.5f)] public float aimYOffset = 0f;

    [Header("Mode")]
    public bool buildMode = false;

    private GameObject ghostObject;
    private List<Vector3> candidatePositions = new List<Vector3>();
    private int candidateIndex = 0;

    // Cached terrain config
    private ProceduralTerrainConfig cfg;
    private Vector3 gridOrigin;

    // Internal: placed instance -> per-instance snap size (XYZ)
    private readonly Dictionary<GameObject, Vector3> placedSnapSizeXYZ = new Dictionary<GameObject, Vector3>(256);
    private readonly HashSet<GameObject> placedRoots = new HashSet<GameObject>(256);

    void Start()
    {
        if (cameraTransform == null && Camera.main != null)
            cameraTransform = Camera.main.transform;

        ResolveTerrainReference();
        InitializeGridParameters();

        SpawnGhost();
        SetGhostActive(buildMode);
        SyncBuildMode();
    }

    void Update()
    {
        if (Input.GetKeyDown(KeyCode.B))
        {
            buildMode = !buildMode;
            SetGhostActive(buildMode);
            SyncBuildMode();
        }

        if (!buildMode) return;
        if (buildPrefabs == null || buildPrefabs.Length == 0) return;

        HandleScrollSelection();
        UpdateGhostAndPlacement();
    }

    private void ResolveTerrainReference()
    {
        if (terrain == null)
            terrain = Object.FindFirstObjectByType<ProceduralTerrainGenerator>();

        if (terrain != null)
            cfg = terrain.config;

        if (cfg == null)
            Debug.LogWarning("ConstructionSystem: Terrain or config not found. Using default origin (0).");
    }

    private void InitializeGridParameters()
    {
        gridOrigin = (cfg != null) ? cfg.worldOffset : Vector3.zero;

        // If a component is <= 0, fall back to voxelScale on that axis
        float vs = (cfg != null) ? Mathf.Max(0.01f, cfg.voxelScale) : 1f;
        defaultCellSizeXYZ = new Vector3(
            defaultCellSizeXYZ.x > 0f ? defaultCellSizeXYZ.x : vs,
            defaultCellSizeXYZ.y > 0f ? defaultCellSizeXYZ.y : vs,
            defaultCellSizeXYZ.z > 0f ? defaultCellSizeXYZ.z : vs
        );
    }

    private void HandleScrollSelection()
    {
        float scroll = Input.GetAxis("Mouse ScrollWheel");
        if (Mathf.Abs(scroll) > 0.01f && buildPrefabs.Length > 0)
        {
            int direction = (scroll > 0) ? 1 : -1;
            selectedIndex = (selectedIndex + direction + buildPrefabs.Length) % buildPrefabs.Length;
            SpawnGhost();
        }
    }

    // Screen-space ray with configurable offset (centers preview more to the left if aimXOffset < 0)
    private Ray GetBuildRay()
    {
        Camera cam = null;
        if (cameraTransform != null) cam = cameraTransform.GetComponent<Camera>();
        if (cam == null) cam = Camera.main;

        if (cam != null)
        {
            float sx = Mathf.Clamp01(0.5f + aimXOffset);
            float sy = Mathf.Clamp01(0.5f + aimYOffset);
            Vector3 sp = new Vector3(cam.pixelWidth * sx, cam.pixelHeight * sy, 0f);
            return cam.ScreenPointToRay(sp);
        }

        if (cameraTransform != null)
            return new Ray(cameraTransform.position, cameraTransform.forward);

        return new Ray(Vector3.zero, Vector3.forward);
    }

    private void UpdateGhostAndPlacement()
    {
        if (ghostObject == null) return;

        Vector3 currentSize = GetCurrentPrefabCellSizeXYZ();

        Ray ray = GetBuildRay();

        RaycastHit hit;
        bool foundObject = false;
        Vector3 gridPos = Vector3.zero;

        float angle = GetCardinalYRotation(ray.direction);
        Quaternion rot = Quaternion.Euler(0, angle, 0);

        if (Physics.Raycast(ray, out hit, raycastDistance, placementMask))
        {
            GameObject hitRoot = GetPlacedRoot(hit.collider.gameObject);
            if (IsPlacedObject(hitRoot))
            {
                foundObject = true;
                Vector3 targetSize = GetCellSizeFromObject(hitRoot);
                Vector3 objectGrid = SnapToGrid(hitRoot.transform.position, targetSize);

                candidatePositions = GetAvailableAdjacentPositions(objectGrid, targetSize);
                if (candidatePositions.Count > 0)
                {
                    if (Input.GetKeyDown(KeyCode.Tab))
                        candidateIndex = (candidateIndex + 1) % candidatePositions.Count;

                    gridPos = candidatePositions[candidateIndex];
                    Vector3 dir = (gridPos - objectGrid).normalized;
                    angle = GetCardinalDirectionAngle(dir);
                    rot = Quaternion.Euler(0, angle, 0);
                }
                else
                {
                    gridPos = objectGrid;
                    candidateIndex = 0;
                }
            }
        }

        if (!foundObject)
        {
            if (Physics.Raycast(ray, out hit, raycastDistance, placementMask))
                gridPos = SnapToGrid(hit.point, currentSize);
            else
                gridPos = SnapToGrid(ray.origin + ray.direction * 5f, currentSize);

            candidateIndex = 0;
            candidatePositions.Clear();
        }

        Vector3 placePosition = gridPos + buildSnapOffset;
        ghostObject.transform.position = placePosition;
        ghostObject.transform.rotation = rot;

        bool canPlace = !IsOccupied(placePosition, currentSize);
        SetGhostMaterial(ghostObject.transform, canPlace ? ghostGreenMaterial : ghostRedMaterial);

        if (canPlace && Input.GetMouseButtonDown(0))
        {
            var obj = Instantiate(buildPrefabs[selectedIndex], placePosition, rot);

            // Do NOT change tag (keeps whatever tag the prefab already has)
            // Record this instance as a placed object with its per-instance snap size
            placedRoots.Add(obj);
            placedSnapSizeXYZ[obj] = currentSize;
        }
    }

    private void SyncBuildMode()
    {
        if (playerController != null)
            playerController.buildMode = buildMode;
        if (ghostObject != null)
            ghostObject.SetActive(buildMode);
    }

    // Per-prefab size (XYZ)
    private Vector3 GetCurrentPrefabCellSizeXYZ()
    {
        Vector3 size = defaultCellSizeXYZ;

        if (prefabSnapSizeXYZ != null && selectedIndex >= 0 && selectedIndex < prefabSnapSizeXYZ.Length)
        {
            Vector3 pref = prefabSnapSizeXYZ[selectedIndex];
            size = new Vector3(
                pref.x > 0f ? pref.x : defaultCellSizeXYZ.x,
                pref.y > 0f ? pref.y : defaultCellSizeXYZ.y,
                pref.z > 0f ? pref.z : defaultCellSizeXYZ.z
            );
        }

        // Minimum per-axis to avoid degenerate boxes
        size.x = Mathf.Max(0.01f, size.x);
        size.y = Mathf.Max(0.01f, size.y);
        size.z = Mathf.Max(0.01f, size.z);
        return size;
    }

    // Per-instance size lookup
    private Vector3 GetCellSizeFromObject(GameObject obj)
    {
        GameObject root = GetPlacedRoot(obj);
        if (placedSnapSizeXYZ.TryGetValue(root, out var s))
            return s;
        return defaultCellSizeXYZ;
    }

    // Grid / snapping (per-axis)
    public Vector3 SnapToGrid(Vector3 worldPos, Vector3 cellSize)
    {
        Vector3 local = worldPos - gridOrigin;
        int x = Mathf.FloorToInt(local.x / cellSize.x);
        int y = Mathf.FloorToInt(local.y / cellSize.y);
        int z = Mathf.FloorToInt(local.z / cellSize.z);
        return gridOrigin + new Vector3(x * cellSize.x, y * cellSize.y, z * cellSize.z);
    }

    // Backward-compat scalar overload if you need it elsewhere
    public Vector3 SnapToGrid(Vector3 worldPos, float cellSizeScalar)
        => SnapToGrid(worldPos, new Vector3(cellSizeScalar, cellSizeScalar, cellSizeScalar));

    // Direction / rotation helpers
    public float GetCardinalYRotation(Vector3 forward)
    {
        Vector3 flatForward = new Vector3(forward.x, 0, forward.z).normalized;
        if (flatForward.sqrMagnitude < 1e-6f) return 0f;
        float angle = Mathf.Atan2(flatForward.x, flatForward.z) * Mathf.Rad2Deg;
        float snappedAngle = Mathf.Round(angle / 90f) * 90f;
        if (snappedAngle < 0) snappedAngle += 360f;
        return snappedAngle;
    }

    public float GetCardinalDirectionAngle(Vector3 dir)
    {
        dir = new Vector3(Mathf.Round(dir.x), 0, Mathf.Round(dir.z));
        if (dir == Vector3.right) return 90f;
        if (dir == Vector3.left) return 270f;
        if (dir == Vector3.forward) return 0f;
        if (dir == Vector3.back) return 180f;
        return GetCardinalYRotation(dir);
    }

    // Occupancy / adjacency
    private bool IsPlacedObject(GameObject obj)
    {
        GameObject root = GetPlacedRoot(obj);
        return placedRoots.Contains(root) || placedSnapSizeXYZ.ContainsKey(root);
    }

    private GameObject GetPlacedRoot(GameObject go)
    {
        if (go == null) return null;
        Transform t = go.transform;
        // Prefer the highest ancestor that is tracked as a placed root
        while (t.parent != null)
        {
            if (placedRoots.Contains(t.gameObject) || placedSnapSizeXYZ.ContainsKey(t.gameObject))
                return t.gameObject;
            t = t.parent;
        }
        return t.gameObject; // actual root
    }

    private List<Vector3> GetAvailableAdjacentPositions(Vector3 center, Vector3 size)
    {
        List<Vector3> positions = new List<Vector3>(4);
        Vector3 right = new Vector3(size.x, 0f, 0f);
        Vector3 fwd   = new Vector3(0f, 0f, size.z);

        Vector3[] offsets = { right, -right, fwd, -fwd };
        for (int i = 0; i < offsets.Length; i++)
        {
            Vector3 pos = center + offsets[i];
            if (!IsOccupied(pos, size))
                positions.Add(pos);
        }
        return positions;
    }

    private bool IsOccupied(Vector3 cellPos, Vector3 cellSize)
    {
        // Use half-extents; shrink slightly to reduce false positives
        Vector3 half = cellSize * 0.45f;
        Collider[] colliders = Physics.OverlapBox(cellPos, half, Quaternion.identity, placementMask);
        foreach (var col in colliders)
        {
            GameObject root = GetPlacedRoot(col.gameObject);
            if (IsPlacedObject(root)) return true;
        }
        return false;
    }

    // Ghost handling
    private void SpawnGhost()
    {
        if (buildPrefabs == null || buildPrefabs.Length == 0)
        {
            if (ghostObject != null) Destroy(ghostObject);
            return;
        }

        if (ghostObject != null) Destroy(ghostObject);
        ghostObject = Instantiate(buildPrefabs[selectedIndex]);
        foreach (var col in ghostObject.GetComponentsInChildren<Collider>())
            col.enabled = false;
        SetGhostMaterial(ghostObject.transform, ghostGreenMaterial);
        ghostObject.transform.rotation = Quaternion.identity;
        SetGhostActive(buildMode);
    }

    private void SetGhostActive(bool active)
    {
        if (ghostObject != null)
            ghostObject.SetActive(active);
    }

    private void SetGhostMaterial(Transform root, Material mat)
    {
        if (mat == null) return;
        foreach (var renderer in root.GetComponentsInChildren<Renderer>())
            renderer.material = mat;
    }

#if UNITY_EDITOR
    private void OnDrawGizmosSelected()
    {
        Gizmos.color = Color.yellow;
        Gizmos.DrawWireSphere(gridOrigin, 0.25f);
    }
#endif
}