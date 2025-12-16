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

    private int lastChildCount = -1;
    private bool pendingRebake;
    private float nextAllowedBakeTime;

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

    public void RebuildNavMesh()
    {
        if (surface == null) return;

        var sources = new List<NavMeshBuildSource>(256);
        Transform root = generator != null ? generator.transform : transform;

        // Collect from MeshFilters
        var filters = root.GetComponentsInChildren<MeshFilter>(true);
        foreach (var mf in filters)
        {
            if (mf == null) continue;
            if (onlyActiveObjects && !mf.gameObject.activeInHierarchy) continue;
            if (((1 << mf.gameObject.layer) & includeLayers.value) == 0) continue;

            Mesh mesh = mf.sharedMesh;
            if (!IsValidMesh(mesh)) continue; // silent skip of empty meshes

            if (!ValidateAndFixMesh(mesh)) continue; // skip if irreparable

            sources.Add(new NavMeshBuildSource
            {
                shape = NavMeshBuildSourceShape.Mesh,
                sourceObject = mesh,
                transform = mf.transform.localToWorldMatrix,
                area = 0
            });
        }

        // Optional: fallback to MeshCollider meshes
        if (useMeshCollidersAsFallback)
        {
            var mcs = root.GetComponentsInChildren<MeshCollider>(true);
            foreach (var mc in mcs)
            {
                if (mc == null) continue;
                if (onlyActiveObjects && !mc.gameObject.activeInHierarchy) continue;
                if (((1 << mc.gameObject.layer) & includeLayers.value) == 0) continue;

                Mesh mesh = mc.sharedMesh;
                if (!IsValidMesh(mesh)) continue;
                if (!ValidateAndFixMesh(mesh)) continue;

                sources.Add(new NavMeshBuildSource
                {
                    shape = NavMeshBuildSourceShape.Mesh,
                    sourceObject = mesh,
                    transform = mc.transform.localToWorldMatrix,
                    area = 0
                });
            }
        }

        if (sources.Count == 0)
        {
            // No valid meshes available yet; just skip building this cycle
            return;
        }

        // Bounds
        Bounds buildBounds = autoBoundsFromConfig && generator != null && generator.config != null
            ? ComputeBoundsFromConfig(generator.config)
            : ComputeBoundsFromRenderers(root);

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
        if (data == null) return;

        surface.RemoveData();
        surface.navMeshData = data;
        surface.AddData();
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