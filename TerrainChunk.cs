using System;
using System.Collections;
using System.Collections.Generic;
using UnityEngine;

/// <summary>
/// TerrainChunk responsible for sampling densities and building the mesh (marching cubes).
/// - Public API used by ProceduralTerrainGenerator:
///   Initialize(config, sampler, coord, gates)
///   Generate()            // full generate (sampling -> mesh)
///   Regenerate()          // regenerate using current gates
///   SetGates(gates)       // change sampling gates (surface/caves/islands)
///   SetRendererEnabled(bool)
///   SetBaseMaterial(Material)
///   SetOverlayMaterial(Material, float fadeDuration)
/// - Includes optional "overlay" renderer that can be faded in (e.g. instance/stone material).
/// - Uses iso-bias near corners to avoid tiny cracks between chunk boundaries.
/// </summary>
[RequireComponent(typeof(MeshFilter), typeof(MeshRenderer), typeof(MeshCollider))]
public class TerrainChunk : MonoBehaviour
{
    public Vector3Int ChunkCoord { get; private set; }

    // config + sampler
    private ProceduralTerrainConfig cfg;
    private DensitySampler sampler;
    private DensitySampler.DensityGates gates;

    // Mesh components
    private Mesh mesh;
    private MeshFilter mf;
    private MeshRenderer mr;
    private MeshCollider mc;

    // Overlay (separate renderer using same mesh)
    private GameObject overlayGO;
    private MeshFilter overlayMF;
    private MeshRenderer overlayMR;
    private Material overlayMaterialInstance;
    private Coroutine overlayFadeRoutine;

    // marching cubes working data
    private float[] density;
    private int nx, ny, nz;
    private float scale;
    private float iso;

    private readonly float[] cube = new float[8];
    private readonly Vector3[] p = new Vector3[8];
    private readonly Vector3[] edgeVertex = new Vector3[12];

    private readonly List<Vector3> vertices = new List<Vector3>(32768);
    private readonly List<Vector3> normals = new List<Vector3>(32768);
    private readonly List<int> indices = new List<int>(65536);

    // Empty chunk pre-check
    [Header("Optimization")]
    [Tooltip("Enable sparse pre-sampling to skip chunks with no surface.")]
    public bool enableEmptyChunkSkip = true;
    [Tooltip("Stride used when sparsely probing densities for emptiness detection. Larger = faster, less precise.")]
    public int emptyCheckStride = 4;

    // Iso bias to avoid cracks
    [Header("Crack Fix")]
    [Tooltip("Apply tiny deterministic bias around iso to avoid seams.")]
    public bool useStableIsoBias = true;
    [Tooltip("Epsilon used when biasing values near iso.")]
    public float isoEpsilon = 1e-5f;

    // Edge index pairs for marching cubes interpolation
    private static readonly int[,] EdgeCorners =
    {
        {0,1},{1,2},{2,3},{3,0},
        {4,5},{5,6},{6,7},{7,4},
        {0,4},{1,5},{2,6},{3,7}
    };

    private void Awake()
    {
        // Cache components
        mf = GetComponent<MeshFilter>();
        mr = GetComponent<MeshRenderer>();
        mc = GetComponent<MeshCollider>();

        if (mesh == null)
        {
            mesh = new Mesh { name = "ChunkMesh", indexFormat = UnityEngine.Rendering.IndexFormat.UInt32 };
            mesh.MarkDynamic();
        }
        mf.sharedMesh = mesh;
    }

    /// <summary>
    /// Initialize the chunk. Sets transform position to chunk origin.
    /// </summary>
    public void Initialize(ProceduralTerrainConfig config, DensitySampler densitySampler, Vector3Int coord, DensitySampler.DensityGates initialGates)
    {
        cfg = config ?? throw new ArgumentNullException(nameof(config));
        sampler = densitySampler ?? throw new ArgumentNullException(nameof(densitySampler));
        gates = initialGates;
        ChunkCoord = coord;

        // Position = chunk origin (min corner). Use localPosition to keep transform parented correctly.
        transform.localPosition = new Vector3(
            coord.x * cfg.chunkSizeXZ * cfg.voxelScale,
            coord.y * cfg.chunkSizeY * cfg.voxelScale,
            coord.z * cfg.chunkSizeXZ * cfg.voxelScale
        ) + cfg.worldOffset;

        scale = cfg.voxelScale;
        iso = 0f; // sampler returns densities relative to cfg.isoLevel so treat iso==0

        // Ensure mesh assigned
        if (mf == null) mf = GetComponent<MeshFilter>();
        mf.sharedMesh = mesh;

        // Setup overlay child (hidden until used)
        if (overlayGO == null)
        {
            overlayGO = new GameObject("Overlay");
            overlayGO.transform.SetParent(transform, false);
            overlayMF = overlayGO.AddComponent<MeshFilter>();
            overlayMF.sharedMesh = mesh;
            overlayMR = overlayGO.AddComponent<MeshRenderer>();
            // match renderer shadow settings
            if (mr != null && overlayMR != null)
            {
                overlayMR.shadowCastingMode = mr.shadowCastingMode;
                overlayMR.receiveShadows = mr.receiveShadows;
            }
            overlayGO.SetActive(false);
        }
    }

    /// <summary>
    /// Change gates (used by generator to enable caves after surface pass).
    /// </summary>
    public void SetGates(DensitySampler.DensityGates newGates)
    {
        gates = newGates;
    }

    /// <summary>
    /// Regenerate using current gates.
    /// </summary>
    public void Regenerate()
    {
        Generate();
    }

    /// <summary>
    /// Main generate function. Samples densities and builds mesh.
    /// Safe to call from coroutine or main thread; heavy work is done inline.
    /// </summary>
    public void Generate()
    {
        if (cfg == null || sampler == null)
        {
            Debug.LogWarning("TerrainChunk.Generate called before Initialize.");
            return;
        }

        int sizeXZ = cfg.chunkSizeXZ;
        int sizeY = cfg.chunkSizeY;

        // Calculate chunk bounds for carving collider collection
        Vector3 basePos = transform.localPosition;
        Vector3 chunkMin = basePos;
        Vector3 chunkMax = basePos + new Vector3(
            sizeXZ * scale,
            sizeY * scale,
            sizeXZ * scale
        );

        // Collect carving colliders in this chunk region
        sampler.CollectCarvingColliders(chunkMin, chunkMax);

        // Early skip using sparse probe
        if (enableEmptyChunkSkip && IsChunkHomogeneousSparse(sizeXZ, sizeY))
        {
            ClearMeshAndDisableCollider();
            return;
        }

        nx = sizeXZ + 1;
        ny = sizeY + 1;
        nz = sizeXZ + 1;
        int needed = nx * ny * nz;
        if (density == null || density.Length != needed) density = new float[needed];

        Vector3 basePos = transform.localPosition;

        // Pre-sample densities
        for (int y = 0; y <= sizeY; y++)
        {
            float py = basePos.y + y * scale;
            for (int z = 0; z <= sizeXZ; z++)
            {
                float pz = basePos.z + z * scale;
                int yzBase = ((y * nz) + z) * nx;
                for (int x = 0; x <= sizeXZ; x++)
                {
                    float px = basePos.x + x * scale;
                    density[yzBase + x] = sampler.SampleDensity(new Vector3(px, py, pz), gates);
                }
            }
        }

        // Full homogeneity test
        if (enableEmptyChunkSkip && IsHomogeneousFull())
        {
            ClearMeshAndDisableCollider();
            return;
        }

        vertices.Clear();
        normals.Clear();
        indices.Clear();

        // Marching cubes
        for (int y = 0; y < sizeY; y++)
        {
            int y0 = y, y1 = y + 1;
            for (int z = 0; z < sizeXZ; z++)
            {
                int z0 = z, z1 = z + 1;
                for (int x = 0; x < sizeXZ; x++)
                {
                    int x0 = x, x1 = x + 1;

                    cube[0] = AdjustNearIso(density[Idx(x0, y0, z0)], GlobalVX(x0), GlobalVY(y0), GlobalVZ(z0));
                    cube[1] = AdjustNearIso(density[Idx(x1, y0, z0)], GlobalVX(x1), GlobalVY(y0), GlobalVZ(z0));
                    cube[2] = AdjustNearIso(density[Idx(x1, y0, z1)], GlobalVX(x1), GlobalVY(y0), GlobalVZ(z1));
                    cube[3] = AdjustNearIso(density[Idx(x0, y0, z1)], GlobalVX(x0), GlobalVY(y0), GlobalVZ(z1));
                    cube[4] = AdjustNearIso(density[Idx(x0, y1, z0)], GlobalVX(x0), GlobalVY(y1), GlobalVZ(z0));
                    cube[5] = AdjustNearIso(density[Idx(x1, y1, z0)], GlobalVX(x1), GlobalVY(y1), GlobalVZ(z0));
                    cube[6] = AdjustNearIso(density[Idx(x1, y1, z1)], GlobalVX(x1), GlobalVY(y1), GlobalVZ(z1));
                    cube[7] = AdjustNearIso(density[Idx(x0, y1, z1)], GlobalVX(x0), GlobalVY(y1), GlobalVZ(z1));

                    bool anyAbove = false, anyBelowEq = false;
                    for (int i = 0; i < 8; i++)
                    {
                        if (cube[i] > iso) anyAbove = true; else anyBelowEq = true;
                        if (anyAbove && anyBelowEq) break;
                    }
                    if (!(anyAbove && anyBelowEq)) continue;

                    float fx0 = x0 * scale, fx1 = x1 * scale;
                    float fy0 = y0 * scale, fy1 = y1 * scale;
                    float fz0 = z0 * scale, fz1 = z1 * scale;

                    p[0] = new Vector3(fx0, fy0, fz0);
                    p[1] = new Vector3(fx1, fy0, fz0);
                    p[2] = new Vector3(fx1, fy0, fz1);
                    p[3] = new Vector3(fx0, fy0, fz1);
                    p[4] = new Vector3(fx0, fy1, fz0);
                    p[5] = new Vector3(fx1, fy1, fz0);
                    p[6] = new Vector3(fx1, fy1, fz1);
                    p[7] = new Vector3(fx0, fy1, fz1);

                    int cubeIndex = 0;
                    if (cube[0] > iso) cubeIndex |= 1;
                    if (cube[1] > iso) cubeIndex |= 2;
                    if (cube[2] > iso) cubeIndex |= 4;
                    if (cube[3] > iso) cubeIndex |= 8;
                    if (cube[4] > iso) cubeIndex |= 16;
                    if (cube[5] > iso) cubeIndex |= 32;
                    if (cube[6] > iso) cubeIndex |= 64;
                    if (cube[7] > iso) cubeIndex |= 128;

                    int edgeFlags = MarchingCubesTables.EdgeTable[cubeIndex];
                    if (edgeFlags == 0) continue;

                    for (int e = 0; e < 12; e++)
                    {
                        if ((edgeFlags & (1 << e)) != 0)
                        {
                            int a = EdgeCorners[e, 0];
                            int b = EdgeCorners[e, 1];
                            edgeVertex[e] = MarchingCubesTables.VertexInterp(iso, p[a], p[b], cube[a], cube[b]);
                        }
                    }

                    for (int t = 0; t < 16; t += 3)
                    {
                        int a0 = MarchingCubesTables.TriTable[cubeIndex, t];
                        if (a0 == -1) break;
                        int a1 = MarchingCubesTables.TriTable[cubeIndex, t + 1];
                        int a2 = MarchingCubesTables.TriTable[cubeIndex, t + 2];

                        Vector3 v0 = edgeVertex[a0];
                        Vector3 v1 = edgeVertex[a1];
                        Vector3 v2 = edgeVertex[a2];

                        Vector3 faceN = Vector3.Cross(v1 - v0, v2 - v0).normalized;

                        int baseIndex = vertices.Count;
                        vertices.Add(v0); vertices.Add(v1); vertices.Add(v2);
                        normals.Add(faceN); normals.Add(faceN); normals.Add(faceN);
                        indices.Add(baseIndex); indices.Add(baseIndex + 1); indices.Add(baseIndex + 2);
                    }
                }
            }
        }

        if (indices.Count == 0)
        {
            ClearMeshAndDisableCollider();
            return;
        }

        // apply mesh
        mesh.Clear();
        mesh.SetVertices(vertices);
        mesh.SetTriangles(indices, 0, true);
        mesh.SetNormals(normals);
        mesh.RecalculateBounds();
        mf.sharedMesh = mesh;
        overlayMF.sharedMesh = mesh;

        mc.sharedMesh = mesh;
        mc.enabled = true;
    }

    private void ClearMeshAndDisableCollider()
    {
        mesh.Clear();
        mf.sharedMesh = mesh;
        if (overlayMF != null) overlayMF.sharedMesh = mesh;
        mc.sharedMesh = null;
        mc.enabled = false;
        if (overlayGO != null) overlayGO.SetActive(false);
    }

    private int Idx(int x, int y, int z) => ((y * nz) + z) * nx + x;

    // Global lattice positions for deterministic iso bias
    private int GlobalVX(int localVX) => ChunkCoord.x * cfg.chunkSizeXZ + localVX;
    private int GlobalVY(int localVY) => ChunkCoord.y * cfg.chunkSizeY + localVY;
    private int GlobalVZ(int localVZ) => ChunkCoord.z * cfg.chunkSizeXZ + localVZ;

    private float AdjustNearIso(float v, int gx, int gy, int gz)
    {
        if (!useStableIsoBias) return v;
        float dv = v - iso;
        if (Mathf.Abs(dv) >= isoEpsilon) return v;
        int h = gx * 73856093 ^ gy * 19349663 ^ gz * 83492791;
        float sign = ((h & 1) == 0) ? 1f : -1f;
        return iso + sign * isoEpsilon;
    }

    // Sparse homogeneity probe
    private bool IsChunkHomogeneousSparse(int sizeXZ, int sizeY)
    {
        Vector3 basePos = transform.localPosition;
        int strideX = Mathf.Clamp(emptyCheckStride, 1, sizeXZ);
        int strideY = Mathf.Clamp(emptyCheckStride, 1, sizeY);
        int strideZ = Mathf.Clamp(emptyCheckStride, 1, sizeXZ);

        bool sawAbove = false, sawBelowEq = false;

        for (int y = 0; y <= sizeY; y += strideY)
        {
            float py = basePos.y + y * scale;
            for (int z = 0; z <= sizeXZ; z += strideZ)
            {
                float pz = basePos.z + z * scale;
                for (int x = 0; x <= sizeXZ; x += strideX)
                {
                    float px = basePos.x + x * scale;
                    float d = sampler.SampleDensity(new Vector3(px, py, pz), gates);
                    if (d > iso) sawAbove = true; else sawBelowEq = true;
                    if (sawAbove && sawBelowEq) return false;
                }
            }
        }
        return true;
    }

    private bool IsHomogeneousFull()
    {
        if (density == null || density.Length == 0) return true;
        float minV = float.PositiveInfinity, maxV = float.NegativeInfinity;
        for (int i = 0; i < density.Length; i++)
        {
            float v = density[i];
            if (v < minV) minV = v;
            if (v > maxV) maxV = v;
            if (minV <= iso && maxV > iso) return false;
        }
        return true;
    }

    // Renderer control
    public void SetRendererEnabled(bool enabled)
    {
        if (mr != null) mr.enabled = enabled;
        if (overlayMR != null) overlayMR.enabled = enabled;
    }

    // Allow generator to set base material after Initialize
    public void SetBaseMaterial(Material mat)
    {
        if (mr != null && mat != null) mr.sharedMaterial = mat;
    }

    // Overlay material fade-in (instantiates material)
    public void SetOverlayMaterial(Material mat, float fadeDuration = 1f)
    {
        if (mat == null || overlayMR == null) return;

        if (overlayFadeRoutine != null) StopCoroutine(overlayFadeRoutine);

        overlayMaterialInstance = new Material(mat);
        overlayMR.material = overlayMaterialInstance;
        overlayMF.sharedMesh = mf.sharedMesh;
        overlayGO.SetActive(true);

        overlayFadeRoutine = StartCoroutine(FadeOverlay(0f, 1f, Mathf.Max(0.01f, fadeDuration)));
    }

    private IEnumerator FadeOverlay(float from, float to, float duration)
    {
        float t = 0f, inv = 1f / Mathf.Max(0.0001f, duration);

        bool useBaseColor = overlayMaterialInstance.HasProperty("_BaseColor");
        bool useColor = overlayMaterialInstance.HasProperty("_Color");
        bool useAlphaFloat = overlayMaterialInstance.HasProperty("_Alpha");
        bool useCutoff = overlayMaterialInstance.HasProperty("_Cutoff");

        Color baseCol = useBaseColor ? overlayMaterialInstance.GetColor("_BaseColor")
                      : useColor ? overlayMaterialInstance.GetColor("_Color")
                      : overlayMaterialInstance.HasProperty("_TintColor") ? overlayMaterialInstance.GetColor("_TintColor")
                      : overlayMaterialInstance.color;

        while (t < 1f)
        {
            t += Time.deltaTime * inv;
            float a = Mathf.Lerp(from, to, Mathf.SmoothStep(0f, 1f, t));
            if (useBaseColor)
            {
                Color c = baseCol; c.a = a;
                overlayMaterialInstance.SetColor("_BaseColor", c);
            }
            else if (useColor)
            {
                Color c = baseCol; c.a = a;
                overlayMaterialInstance.SetColor("_Color", c);
            }
            else if (useAlphaFloat)
            {
                overlayMaterialInstance.SetFloat("_Alpha", a);
            }
            else if (useCutoff)
            {
                overlayMaterialInstance.SetFloat("_Cutoff", Mathf.Lerp(1f, 0f, a));
            }
            else
            {
                overlayMR.enabled = a > 0.5f;
            }

            yield return null;
        }

        // final set
        if (useBaseColor) { Color c = baseCol; c.a = to; overlayMaterialInstance.SetColor("_BaseColor", c); }
        else if (useColor) { Color c = baseCol; c.a = to; overlayMaterialInstance.SetColor("_Color", c); }
        else if (useAlphaFloat) overlayMaterialInstance.SetFloat("_Alpha", to);
        else if (useCutoff) overlayMaterialInstance.SetFloat("_Cutoff", Mathf.Lerp(1f, 0f, to));
        else overlayMR.enabled = to > 0.5f;

        overlayFadeRoutine = null;
    }
}