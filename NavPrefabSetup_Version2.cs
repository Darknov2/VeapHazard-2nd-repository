using UnityEngine;
using Unity.AI.Navigation;
using UnityEngine.AI;

/// Configure a spawned prefab so it contributes to NavMesh baking.
/// - For obstacles: adds a NavMeshModifier to mark as Not Walkable (or a custom area).
/// - For interiors: adds a NavMeshModifierVolume to mark floor space as Walkable (or a custom area).
/// Attach this component to your obstacle/interior prefabs or add it at runtime.
public class NavPrefabSetup : MonoBehaviour
{
    [Header("Obstacle Settings")]
    [Tooltip("Treat this prefab as an obstacle (e.g., rock, wall, furniture).")]
    public bool isObstacle = true;

    [Tooltip("Automatically add/configure a NavMeshModifier on this GameObject.")]
    public bool addObstacleModifier = true;

    [Tooltip("Area name to assign for obstacles (as defined in Navigation window).")]
    public string obstacleAreaName = "Not Walkable";

    [Tooltip("Optional: explicit area index. If <0, resolves by name.")]
    public int obstacleAreaIndex = -1;

    [Tooltip("Apply to all agent types. Set false to limit via agent types on the component.")]
    public bool affectAllAgentsForObstacle = true;

    [Header("Interior Settings")]
    [Tooltip("Treat this prefab as an interior with walkable volume (rooms, corridors).")]
    public bool isInterior = false;

    [Tooltip("Automatically add/configure a NavMeshModifierVolume for walkable interior space.")]
    public bool addInteriorVolume = true;

    [Tooltip("Center of the interior volume, relative to this GameObject.")]
    public Vector3 interiorCenter = Vector3.zero;

    [Tooltip("Size of the interior volume (typically matches room floor extents).")]
    public Vector3 interiorSize = new Vector3(8f, 3f, 8f);

    [Tooltip("Area name to assign for interior walkable volume.")]
    public string interiorAreaName = "Walkable";

    [Tooltip("Optional: explicit area index. If <0, resolves by name.")]
    public int interiorAreaIndex = -1;

    [Tooltip("Apply to all agent types. Set false to limit via agent types on the component.")]
    public bool affectAllAgentsForInterior = true;

    private void Awake()
    {
        if (addObstacleModifier && isObstacle)
            EnsureObstacleModifier();

        if (addInteriorVolume && isInterior)
            EnsureInteriorVolume();
    }

    private void EnsureObstacleModifier()
    {
        // Ensure a collider exists; NavMesh uses colliders (when collecting PhysicsColliders)
        var col = GetComponent<Collider>();
        if (col == null) col = gameObject.AddComponent<BoxCollider>();

        var mod = gameObject.GetComponent<NavMeshModifier>();
        if (mod == null) mod = gameObject.AddComponent<NavMeshModifier>();

        mod.overrideArea = true;
        mod.area = ResolveAreaIndex(obstacleAreaIndex, obstacleAreaName);
        mod.ignoreFromBuild = false;

        if (affectAllAgentsForObstacle)
        {
            // -1 means all agent types (Unity’s modifier API supports per-agent filters in inspector;
            // at runtime, calling AffectsAgentType(-1) sets “Affects Agents: All”)
            mod.AffectsAgentType(-1);
        }
    }

    private void EnsureInteriorVolume()
    {
        var vol = gameObject.GetComponent<NavMeshModifierVolume>();
        if (vol == null) vol = gameObject.AddComponent<NavMeshModifierVolume>();

        vol.center = interiorCenter;
        vol.size = interiorSize;
        vol.area = ResolveAreaIndex(interiorAreaIndex, interiorAreaName);

        if (affectAllAgentsForInterior)
        {
            vol.AffectsAgentType(-1);
        }

        // Optionally ensure there is a floor collider for agents to stand on if needed
        // Many setups rely on terrain colliders; interiors might be multi-story prefabs with their own colliders.
        if (GetComponent<Collider>() == null)
        {
            // Add a thin box collider to represent floor if none exists
            var floor = gameObject.AddComponent<BoxCollider>();
            floor.center = interiorCenter;
            var size = interiorSize;
            if (size.y < 0.1f) size.y = 0.1f;
            floor.size = new Vector3(size.x, 0.1f, size.z);
        }
    }

    private int ResolveAreaIndex(int explicitIndex, string areaName)
    {
        if (explicitIndex >= 0) return explicitIndex;
        int idx = NavMesh.GetAreaFromName(areaName);
        if (idx < 0)
        {
            Debug.LogWarning($"NavPrefabSetup: Area '{areaName}' not found. Defaulting to Walkable.");
            idx = NavMesh.GetAreaFromName("Walkable");
            if (idx < 0) idx = 0; // fallback
        }
        return idx;
    }
}