using UnityEngine;

/// <summary>
/// Adds invisible border walls (BoxColliders) around the current procedural terrain footprint (XZ).
/// Safe lifecycle: never creates/destroys during OnValidate/Awake/CheckConsistency.
/// - Build once in Start (Play Mode) or when explicitly requested via RebuildBorders()
/// - Editor-time preview is optional via context menu and only runs when not playing
/// </summary>
public class ProceduralTerrainBorders : MonoBehaviour
{
    [Header("Border Settings")]
    [Tooltip("How thick the border walls are (world units).")]
    public float borderThickness = 2f;

    [Tooltip("How tall the border walls are (world units).")]
    public float borderHeight = 40f;

    [Tooltip("Center height of the walls. If true, centers at config.surfaceBaseHeight; otherwise uses customCenterY.")]
    public bool centerAtSurfaceBaseHeight = true;

    [Tooltip("Used when centerAtSurfaceBaseHeight is false.")]
    public float customCenterY = 0f;

    [Tooltip("Create colliders as triggers instead of solid walls.")]
    public bool isTrigger = false;

    [Header("References")]
    public ProceduralTerrainGenerator generator; // assign your ProceduralTerrainGenerator here

    private const string BorderName = "InvisibleEdgeBorder";

    // Cache flag so we don't rebuild multiple times in a single frame
    private bool builtThisPlaySession = false;

    private void Start()
    {
        // Only build at runtime (Play Mode). Avoid any creation/destruction in OnValidate/Awake.
        if (Application.isPlaying)
        {
            SafeRebuildBordersRuntime();
            builtThisPlaySession = true;
        }
    }

    // Runtime-safe rebuild: destroys previous borders using Destroy and creates new ones
    public void SafeRebuildBordersRuntime()
    {
        if (generator == null || generator.config == null)
        {
            Debug.LogWarning("ProceduralTerrainBorders: Missing generator or config.");
            return;
        }

        // Clear previous borders created by this component
        ClearExistingBordersRuntime();

        var cfg = generator.config;

        // Terrain footprint in world units
        float cellXZ = cfg.chunkSizeXZ * cfg.voxelScale;
        float width  = cfg.chunksX * cellXZ;
        float length = cfg.chunksZ * cellXZ;

        // World-space min/max using worldOffset as the terrain origin
        float minX = cfg.worldOffset.x;
        float minZ = cfg.worldOffset.z;
        float maxX = minX + width;
        float maxZ = minZ + length;

        // Vertical placement: center at surfaceBaseHeight by default
        float centerY = centerAtSurfaceBaseHeight ? cfg.surfaceBaseHeight : customCenterY;

        // Build 4 walls (runtime-safe creation)
        // Left wall (along Z)
        CreateBorderRuntime(
            position: new Vector3(minX - borderThickness * 0.5f, centerY, minZ + length * 0.5f),
            size:     new Vector3(borderThickness, borderHeight, length)
        );

        // Right wall (along Z)
        CreateBorderRuntime(
            position: new Vector3(maxX + borderThickness * 0.5f, centerY, minZ + length * 0.5f),
            size:     new Vector3(borderThickness, borderHeight, length)
        );

        // Bottom wall (along X)
        CreateBorderRuntime(
            position: new Vector3(minX + width * 0.5f, centerY, minZ - borderThickness * 0.5f),
            size:     new Vector3(width, borderHeight, borderThickness)
        );

        // Top wall (along X)
        CreateBorderRuntime(
            position: new Vector3(minX + width * 0.5f, centerY, maxZ + borderThickness * 0.5f),
            size:     new Vector3(width, borderHeight, borderThickness)
        );
    }

    private void CreateBorderRuntime(Vector3 position, Vector3 size)
    {
        var border = new GameObject(BorderName);
        border.transform.SetParent(transform, worldPositionStays: true);
        border.transform.position = position;
        border.transform.rotation = Quaternion.identity;
        border.transform.localScale = Vector3.one;

        var box = border.AddComponent<BoxCollider>();
        box.isTrigger = isTrigger;
        box.size = size;
    }

    private void ClearExistingBordersRuntime()
    {
        // Destroy only children we created (by name) using Destroy (safe at runtime)
        for (int i = transform.childCount - 1; i >= 0; i--)
        {
            var child = transform.GetChild(i);
            if (child != null && child.name == BorderName)
            {
                Destroy(child.gameObject);
            }
        }
    }

#if UNITY_EDITOR
    // Optional editor-only preview, executed ONLY when not in Play Mode and via context menu, not OnValidate.
    [ContextMenu("Rebuild Borders (Editor Preview)")]
    private void RebuildBordersEditorMenu()
    {
        if (Application.isPlaying)
        {
            Debug.LogWarning("Use SafeRebuildBordersRuntime() while playing.");
            return;
        }
        if (generator == null || generator.config == null)
        {
            Debug.LogWarning("ProceduralTerrainBorders: Missing generator or config.");
            return;
        }

        // Clear previous editor borders (DestroyImmediate is permitted in editor when not playing)
        for (int i = transform.childCount - 1; i >= 0; i--)
        {
            var child = transform.GetChild(i);
            if (child != null && child.name == BorderName)
                DestroyImmediate(child.gameObject);
        }

        var cfg = generator.config;

        float cellXZ = cfg.chunkSizeXZ * cfg.voxelScale;
        float width  = cfg.chunksX * cellXZ;
        float length = cfg.chunksZ * cellXZ;

        float minX = cfg.worldOffset.x;
        float minZ = cfg.worldOffset.z;
        float maxX = minX + width;
        float maxZ = minZ + length;

        float centerY = centerAtSurfaceBaseHeight ? cfg.surfaceBaseHeight : customCenterY;

        CreateBorderEditor(new Vector3(minX - borderThickness * 0.5f, centerY, minZ + length * 0.5f), new Vector3(borderThickness, borderHeight, length));
        CreateBorderEditor(new Vector3(maxX + borderThickness * 0.5f, centerY, minZ + length * 0.5f), new Vector3(borderThickness, borderHeight, length));
        CreateBorderEditor(new Vector3(minX + width * 0.5f, centerY, minZ - borderThickness * 0.5f), new Vector3(width, borderHeight, borderThickness));
        CreateBorderEditor(new Vector3(minX + width * 0.5f, centerY, maxZ + borderThickness * 0.5f), new Vector3(width, borderHeight, borderThickness));
    }

    private void CreateBorderEditor(Vector3 position, Vector3 size)
    {
        var border = new GameObject(BorderName);
        border.transform.SetParent(transform, worldPositionStays: true);
        border.transform.position = position;
        border.transform.rotation = Quaternion.identity;
        border.transform.localScale = Vector3.one;

        var box = border.AddComponent<BoxCollider>();
        box.isTrigger = isTrigger;
        box.size = size;
    }
#endif
}