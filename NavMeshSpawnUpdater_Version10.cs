using System.Collections.Generic;
using UnityEngine;
using UnityEngine.AI;
using Unity.AI.Navigation;

[RequireComponent(typeof(NavMeshSurface))]
public class NavMeshSpawnUpdater : MonoBehaviour
{
    public NavMeshSurface surface;
    public LayerMask collectLayers; // set to Terrain + Obstacles layers
    public float boundsPadding = 1f;
    public float minBoundsThicknessY = 2f;

    private void Reset()
    {
        surface = GetComponent<NavMeshSurface>();
        if (collectLayers == 0) collectLayers = surface.layerMask;
    }

    // Call this with an AABB that encompasses the newly spawned objects
    public void UpdateAround(Bounds bounds)
    {
        if (surface == null || surface.navMeshData == null) return;

        if (bounds.size.y < minBoundsThicknessY)
        {
            var s = bounds.size; s.y = minBoundsThicknessY;
            bounds.size = s;
        }
        bounds.Expand(boundsPadding * 2f);

        var sources = new List<NavMeshBuildSource>();
        NavMeshBuilder.CollectSources(
            bounds,
            collectLayers,
            NavMeshCollectGeometry.PhysicsColliders,    // colliders only
            surface.defaultArea,
            new List<NavMeshBuildMarkup>(),
            sources
        );

        if (sources.Count == 0) return;

        var settings = surface.GetBuildSettings();
        NavMeshBuilder.UpdateNavMeshDataAsync(surface.navMeshData, settings, sources, bounds);
    }
}