using UnityEngine;

[CreateAssetMenu(fileName = "ProceduralTerrainConfig", menuName = "Terrain/Procedural Terrain Config")]
public class ProceduralTerrainConfig : ScriptableObject
{
    [Header("General")]
    public int seed = 1234;
    public float isoLevel = 0f;
    public Vector3 worldOffset;

    [Header("Chunk Grid (3D)")]
    public int chunksX = 8;
    public int chunksY = 4;
    public int chunksZ = 8;
    public int chunkSizeXZ = 32;
    public int chunkSizeY = 32;
    public float voxelScale = 1f;

    [Header("Layer Layout")]
    public int surfaceLayerIndex = 2;
    public int islandsStartLayer = 3;
    public int cavesStartLayer = 0;
    public int cavesEndLayer = 1;
    public bool autoComputeSurfaceHeight = true;

    [Header("Surface Height Field")]
    public float surfaceBaseHeight = 60f;
    public float surfaceNoiseScale = 0.008f;
    public float surfaceNoiseAmplitude = 40f;
    public int surfaceOctaves = 4;
    public float surfacePersistence = 0.5f;
    public float surfaceLacunarity = 2f;

    [Header("Ground Volume")]
    public float groundDensityScale = 0.25f;
    public float maxGroundDensity = 25f;

    [Header("Caves (Tunnels + Worms Only)")]
    public CaveSystem caves = new CaveSystem();

    [Header("Floating Islands")]
    public FloatingIslandsModule islands = new FloatingIslandsModule();

    [Header("Construction Carving")]
    [Tooltip("Layer mask for colliders that should carve terrain (e.g., Construction layer).")]
    public LayerMask constructionLayerMask = 0;
    
    [Tooltip("Optional: if set, only colliders with this tag will carve terrain.")]
    public string requiredCarvingTag = "";
    
    [Tooltip("Strength of terrain carving around construction colliders.")]
    public float constructionCarveStrength = 10f;

    [Header("Generation")]
    public bool asyncGeneration = true;
    public int maxParallelChunks = 4;
    public bool generateOnStart = true;

    [Header("Mesh / Vertex Deduplication")]
    [Tooltip("If true, identical/near-identical vertices inside a chunk are merged.")]
    public bool deduplicateVertices = true;

    [Tooltip("Quantization epsilon used to weld vertices (world units). Smaller -> fewer merges.")]
    public float dedupEpsilon = 0.0005f;

    [Tooltip("If true, accumulate and average normals for smooth shading. If false, use flat face normals.")]
    public bool smoothNormals = true;
}