using System.Collections.Generic;
using UnityEngine;
using UnityEngine.AI;
using Unity.AI.Navigation;

[RequireComponent(typeof(NavMeshSurface))]
public class NavMeshTerrainBaker : MonoBehaviour
{
    [Header("References")]
    public ProceduralTerrainGenerator generator;
    public NavMeshSurface surface;

    [Header("Bake Triggers")]
    public bool bakeOnInitialReady = true;
    public bool bakeOnStart = false;
    public bool monitorChunkChanges = true;
    [Range(0.1f, 10f)] public float debounceSeconds = 1.5f;
    public int changeThreshold = 0;

    [Header("Bounds")]
    public bool autoBoundsFromConfig = true;
    public float boundsPadding = 2f;
    public float fallbackBoundsSize = 100f; // Size of fallback bounds when no meshes are available

    [Header("Resolution")]
    public float overrideVoxelSize = 0.2f; // 0 = default
    public int overrideTileSize = 64;      // 0 = default

    [Header("Source Filtering")]
    public bool onlyActiveObjects = true;
    public LayerMask includeLayers = ~0;

    [Header("Validation/Sanitization")]
    public float maxAbsCoordinate = 100000f;
    public bool useMeshCollidersAsFallback = true;   // if MeshFilter is empty but MeshCollider has mesh
    public bool useRenderersAsFallback = false;      // use Renderer.bounds with MeshFilter mesh if available

    [Header("Diagnostics")]
    public bool verboseDiagnostics = false;
    public float meshWaitTimeout = 1.0f;             // seconds to wait for meshes to be available

    private int lastChildCount = -1;
    private bool pendingRebake;
    private float nextAllowedBakeTime;
    private Coroutine rebuildCoroutine;

    private void Reset()
    {
        generator = GetComponent<ProceduralTerrainGenerator>();
        surface = GetComponent<NavMeshSurface>();
    }

    private void Awake()
    {
        if (surface == null) surface = GetComponent<NavMeshSurface>();
        if (surface == null) surface = gameObject.AddComponent<NavMeshSurface>();
        if (generator == null) generator = GetComponent<ProceduralTerrainGenerator>();
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
        if (bakeOnStart) RebuildNavMesh();
        if (monitorChunkChanges && generator != null)
            lastChildCount = generator.transform.childCount;
    }

    private void Update()
    {
        if (!monitorChunkChanges || generator == null) return;

        int childCount = generator.transform.childCount;
        if (lastChildCount < 0) lastChildCount = childCount;

        int delta = Mathf.Abs(childCount - lastChildCount);
        if (delta > changeThreshold)
        {
            lastChildCount = childCount;
            QueueDebouncedRebake();
        }

        if (pendingRebake && Time.time >= nextAllowedBakeTime)
        {
            pendingRebake = false;
            RebuildNavMesh();
        }
    }

    private void HandleInitialReady()
    {
        if (bakeOnInitialReady) RebuildNavMesh();
    }

    private void QueueDebouncedRebake()
    {
        pendingRebake = true;
        nextAllowedBakeTime = Time.time + Mathf.Max(0.1f, debounceSeconds);
    }

    /// <summary>
    /// Request a NavMesh rebuild after a specified delay.
    /// Debounces multiple requests to run a single rebuild.
    /// </summary>
    public void RequestRebuildAfterDelay(float delay)
    {
        if (rebuildCoroutine != null)
        {
            StopCoroutine(rebuildCoroutine);
        }
        rebuildCoroutine = StartCoroutine(RebuildNavMeshRoutine(delay));
    }

    /// <summary>
    /// Request an immediate NavMesh rebuild (with minimal debounce delay).
    /// </summary>
    public void RequestRebuild()
    {
        RequestRebuildAfterDelay(0.1f);
    }

    /// <summary>
    /// Rebuild NavMesh immediately without any delay.
    /// </summary>
    public void RebuildNow()
    {
        if (rebuildCoroutine != null)
        {
            StopCoroutine(rebuildCoroutine);
            rebuildCoroutine = null;
        }
        RebuildNavMeshWithDiagnostics();
    }

    private System.Collections.IEnumerator RebuildNavMeshRoutine(float delay)
    {
        // Wait for the requested delay
        if (delay > 0f)
        {
            yield return new WaitForSeconds(delay);
        }

        // Wait for meshes to be available
        float waitStart = Time.time;
        bool meshesReady = false;
        while (Time.time - waitStart < meshWaitTimeout)
        {
            if (AreMeshesAvailable())
            {
                meshesReady = true;
                break;
            }
            yield return null;
        }

        if (!meshesReady && verboseDiagnostics)
        {
            Debug.LogWarning($"NavMeshTerrainBaker: Mesh wait timeout ({meshWaitTimeout}s) reached. Proceeding with rebuild anyway.");
        }

        RebuildNavMeshWithDiagnostics();
        rebuildCoroutine = null;
    }

    private bool AreMeshesAvailable()
    {
        Transform root = generator != null ? generator.transform : transform;
        
        // Check if there are any valid meshes under the roots
        var filters = root.GetComponentsInChildren<MeshFilter>(true);
        foreach (var mf in filters)
        {
            if (mf != null && mf.sharedMesh != null && mf.sharedMesh.vertexCount > 0)
            {
                return true;
            }
        }

        // Also check for SkinnedMeshRenderers
        var skinned = root.GetComponentsInChildren<SkinnedMeshRenderer>(true);
        foreach (var smr in skinned)
        {
            if (smr != null && smr.sharedMesh != null && smr.sharedMesh.vertexCount > 0)
            {
                return true;
            }
        }

        return false;
    }

    public void RebuildNavMesh()
    {
        RebuildNavMeshWithDiagnostics();
    }

    private void RebuildNavMeshWithDiagnostics()
    {
        if (surface == null) return;

        var sources = new List<NavMeshBuildSource>(256);
        var bakedMeshes = new List<Mesh>(); // Track baked meshes for cleanup
        Transform root = generator != null ? generator.transform : transform;

        // Also check for spawned structures container
        Transform spawnContainer = null;
        if (generator != null)
        {
            spawnContainer = generator.transform.Find("SpawnedStructures");
        }

        List<Transform> roots = new List<Transform> { root };
        if (spawnContainer != null)
        {
            roots.Add(spawnContainer);
        }

        int includedMeshFilters = 0;
        int includedSkinnedMeshRenderers = 0;
        int includedMeshColliders = 0;
        int skippedObjects = 0;

        if (verboseDiagnostics)
        {
            Debug.Log($"NavMeshTerrainBaker: Starting rebuild. Scanning {roots.Count} root(s).");
        }

        // Collect from MeshFilters
        foreach (var scanRoot in roots)
        {
            var filters = scanRoot.GetComponentsInChildren<MeshFilter>(true);
            foreach (var mf in filters)
            {
                if (mf == null) continue;
                if (onlyActiveObjects && !mf.gameObject.activeInHierarchy) { skippedObjects++; continue; }
                if (((1 << mf.gameObject.layer) & includeLayers.value) == 0) { skippedObjects++; continue; }

                Mesh mesh = mf.sharedMesh;
                if (!IsValidMesh(mesh)) { skippedObjects++; continue; }
                if (!ValidateAndFixMesh(mesh)) { skippedObjects++; continue; }

                sources.Add(new NavMeshBuildSource
                {
                    shape = NavMeshBuildSourceShape.Mesh,
                    sourceObject = mesh,
                    transform = mf.transform.localToWorldMatrix,
                    area = 0
                });
                includedMeshFilters++;
            }
        }

        // Collect from SkinnedMeshRenderers
        foreach (var scanRoot in roots)
        {
            var skinned = scanRoot.GetComponentsInChildren<SkinnedMeshRenderer>(true);
            foreach (var smr in skinned)
            {
                if (smr == null) continue;
                if (onlyActiveObjects && !smr.gameObject.activeInHierarchy) { skippedObjects++; continue; }
                if (((1 << smr.gameObject.layer) & includeLayers.value) == 0) { skippedObjects++; continue; }

                Mesh mesh = smr.sharedMesh;
                if (!IsValidMesh(mesh)) { skippedObjects++; continue; }

                // Bake the skinned mesh to a regular mesh
                Mesh bakedMesh = new Mesh();
                smr.BakeMesh(bakedMesh);
                if (!IsValidMesh(bakedMesh)) { skippedObjects++; continue; }
                if (!ValidateAndFixMesh(bakedMesh)) { skippedObjects++; continue; }

                bakedMeshes.Add(bakedMesh); // Track for cleanup
                sources.Add(new NavMeshBuildSource
                {
                    shape = NavMeshBuildSourceShape.Mesh,
                    sourceObject = bakedMesh,
                    transform = smr.transform.localToWorldMatrix,
                    area = 0
                });
                includedSkinnedMeshRenderers++;
            }
        }

        // Optional: fallback to MeshCollider meshes
        if (useMeshCollidersAsFallback)
        {
            foreach (var scanRoot in roots)
            {
                var mcs = scanRoot.GetComponentsInChildren<MeshCollider>(true);
                foreach (var mc in mcs)
                {
                    if (mc == null) continue;
                    if (onlyActiveObjects && !mc.gameObject.activeInHierarchy) { skippedObjects++; continue; }
                    if (((1 << mc.gameObject.layer) & includeLayers.value) == 0) { skippedObjects++; continue; }

                    Mesh mesh = mc.sharedMesh;
                    if (!IsValidMesh(mesh)) { skippedObjects++; continue; }
                    if (!ValidateAndFixMesh(mesh)) { skippedObjects++; continue; }

                    sources.Add(new NavMeshBuildSource
                    {
                        shape = NavMeshBuildSourceShape.Mesh,
                        sourceObject = mesh,
                        transform = mc.transform.localToWorldMatrix,
                        area = 0
                    });
                    includedMeshColliders++;
                }
            }
        }

        if (verboseDiagnostics)
        {
            Debug.Log($"NavMeshTerrainBaker: Included {includedMeshFilters} MeshFilters, {includedSkinnedMeshRenderers} SkinnedMeshRenderers, {includedMeshColliders} MeshColliders. Skipped {skippedObjects} objects.");
        }

        if (sources.Count == 0)
        {
            if (verboseDiagnostics)
            {
                Debug.LogWarning("NavMeshTerrainBaker: No valid meshes available. Skipping rebuild.");
            }
            return;
        }

        // Compute bounds from collected sources
        Bounds buildBounds;
        if (autoBoundsFromConfig && generator != null && generator.config != null)
        {
            // Use config-based bounds as fallback if no sources to compute from
            buildBounds = ComputeBoundsFromConfig(generator.config);
            if (sources.Count > 0)
            {
                // Expand config bounds to include actual mesh sources
                Bounds sourceBounds = ComputeBoundsFromSources(sources);
                buildBounds.Encapsulate(sourceBounds);
            }
        }
        else
        {
            buildBounds = ComputeBoundsFromSources(sources);
        }

        if (verboseDiagnostics)
        {
            Debug.Log($"NavMeshTerrainBaker: Build bounds: center={buildBounds.center}, size={buildBounds.size}");
        }

        // Settings
        var settings = GetBuildSettings(surface.agentTypeID);
        if (overrideVoxelSize > 0f)
        {
            settings.overrideVoxelSize = true;
            settings.voxelSize = Mathf.Max(0.05f, overrideVoxelSize);
        }
        if (overrideTileSize > 0)
        {
            settings.overrideTileSize = true;
            settings.tileSize = Mathf.Max(8, overrideTileSize);
        }

        // Build and apply
        var data = NavMeshBuilder.BuildNavMeshData(settings, sources, buildBounds, Vector3.zero, Quaternion.identity);
        if (data == null)
        {
            if (verboseDiagnostics)
            {
                Debug.LogError("NavMeshTerrainBaker: Failed to build NavMeshData.");
            }
            return;
        }

        // Safely remove old data and assign new
        if (surface.navMeshData != null)
        {
            surface.RemoveData();
        }
        surface.navMeshData = data;
        surface.AddData();

        if (verboseDiagnostics)
        {
            Debug.Log("NavMeshTerrainBaker: NavMesh rebuild complete.");
        }

        // Clean up baked meshes to prevent memory leaks
        foreach (var bakedMesh in bakedMeshes)
        {
            if (bakedMesh != null)
            {
                Object.DestroyImmediate(bakedMesh);
            }
        }
    }

    private Bounds ComputeBoundsFromSources(List<NavMeshBuildSource> sources)
    {
        if (sources.Count == 0)
        {
            return new Bounds(Vector3.zero, Vector3.one * Mathf.Max(1f, fallbackBoundsSize));
        }

        // Initialize with the first mesh bounds
        Bounds bounds = new Bounds(Vector3.zero, Vector3.zero);
        bool initialized = false;

        foreach (var source in sources)
        {
            if (source.sourceObject is Mesh mesh && mesh != null)
            {
                // Transform mesh bounds to world space
                Bounds meshBounds = mesh.bounds;
                Matrix4x4 matrix = source.transform;

                // Get the 8 corners of the bounds
                Vector3[] corners = new Vector3[8];
                corners[0] = meshBounds.min;
                corners[1] = new Vector3(meshBounds.min.x, meshBounds.min.y, meshBounds.max.z);
                corners[2] = new Vector3(meshBounds.min.x, meshBounds.max.y, meshBounds.min.z);
                corners[3] = new Vector3(meshBounds.min.x, meshBounds.max.y, meshBounds.max.z);
                corners[4] = new Vector3(meshBounds.max.x, meshBounds.min.y, meshBounds.min.z);
                corners[5] = new Vector3(meshBounds.max.x, meshBounds.min.y, meshBounds.max.z);
                corners[6] = new Vector3(meshBounds.max.x, meshBounds.max.y, meshBounds.min.z);
                corners[7] = meshBounds.max;

                // Transform corners to world space and encapsulate
                foreach (var corner in corners)
                {
                    Vector3 worldCorner = matrix.MultiplyPoint3x4(corner);
                    if (!initialized)
                    {
                        bounds = new Bounds(worldCorner, Vector3.zero);
                        initialized = true;
                    }
                    else
                    {
                        bounds.Encapsulate(worldCorner);
                    }
                }
            }
        }

        // Add padding
        bounds.Expand(boundsPadding * 2f);
        return bounds;
    }

    private static bool IsValidMesh(Mesh mesh)
    {
        if (mesh == null) return false;
        if (mesh.vertexCount <= 0) return false;
        var tris = mesh.triangles;
        if (tris == null || tris.Length < 3) return false;
        return true;
    }

    private bool ValidateAndFixMesh(Mesh mesh)
    {
        // verts finite + clamp absurd coords
        var v = mesh.vertices;
        bool changed = false;
        float clamp = Mathf.Max(1000f, maxAbsCoordinate);

        for (int i = 0; i < v.Length; i++)
        {
            var p = v[i];
            if (!IsFinite(p))
            {
                v[i] = Vector3.zero;
                changed = true;
            }
            else if (Mathf.Abs(p.x) > clamp || Mathf.Abs(p.y) > clamp || Mathf.Abs(p.z) > clamp)
            {
                v[i] = new Vector3(
                    Mathf.Clamp(p.x, -clamp, clamp),
                    Mathf.Clamp(p.y, -clamp, clamp),
                    Mathf.Clamp(p.z, -clamp, clamp)
                );
                changed = true;
            }
        }
        if (changed)
        {
            mesh.vertices = v;
            mesh.RecalculateBounds();
            if (mesh.normals == null || mesh.normals.Length != mesh.vertexCount)
                mesh.RecalculateNormals();
        }

        // index range check
        var tris = mesh.triangles;
        int vCount = mesh.vertexCount;
        for (int i = 0; i < tris.Length; i++)
        {
            int idx = tris[i];
            if ((uint)idx >= (uint)vCount) return false; // bad topology -> skip
        }

        // bounds sanity
        var b = mesh.bounds;
        if (!IsFinite(b.center) || !IsFinite(b.size))
        {
            mesh.bounds = new Bounds(Vector3.zero, Vector3.one * 10f);
        }
        return true;
    }

    private NavMeshBuildSettings GetBuildSettings(int agentTypeId)
    {
        if (agentTypeId >= 0)
        {
            var s = NavMesh.GetSettingsByID(agentTypeId);
            if (!s.Equals(default(NavMeshBuildSettings))) return s;
        }
        int count = NavMesh.GetSettingsCount();
        if (count > 0) return NavMesh.GetSettingsByIndex(0);
        return NavMesh.CreateSettings();
    }

    private Bounds ComputeBoundsFromConfig(ProceduralTerrainConfig cfg)
    {
        float cellXZ = cfg.chunkSizeXZ * cfg.voxelScale;
        float cellY  = cfg.chunkSizeY  * cfg.voxelScale;

        float width  = cfg.chunksX * cellXZ;
        float length = cfg.chunksZ * cellXZ;
        float height = cfg.chunksY * cellY;

        Vector3 min = cfg.worldOffset;
        Vector3 max = cfg.worldOffset + new Vector3(width, height, length);

        min -= Vector3.one * boundsPadding;
        max += Vector3.one * boundsPadding;

        return new Bounds((min + max) * 0.5f, (max - min));
    }

    private Bounds ComputeBoundsFromRenderers(Transform root)
    {
        var renderers = root.GetComponentsInChildren<Renderer>(true);
        if (renderers.Length == 0)
            return new Bounds(root.position, Vector3.one * 100f);

        Bounds b = renderers[0].bounds;
        for (int i = 1; i < renderers.Length; i++)
            b.Encapsulate(renderers[i].bounds);
        b.Expand(boundsPadding * 2f);
        return b;
    }

    private static bool IsFinite(Vector3 p)
        => float.IsFinite(p.x) && float.IsFinite(p.y) && float.IsFinite(p.z);
}