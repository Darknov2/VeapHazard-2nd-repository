using System.Collections.Generic;
using UnityEngine;

/// <summary>
/// Minimal persistent GameState used by WorldSelector & Sign.
/// - Static fields so they survive across scene loads until the application quits.
/// - Stores selected world info, per-index scene name mapping, and a simple grid mapping
///   (IndexToGrid / GridToIndexFlatten) used to compute neighbor indices.
/// </summary>
public static class GameState
{
    // Basic selection
    public static int SelectedWorldIndex = -1;
    public static string SelectedWorldSceneName = null;

    // Optional: remember menu scene name so SceneLoadManager can unload it
    public static string MenuSceneName = null;

    // Per-index scene name mapping (index -> scene name). Populated by WorldSelector.
    public static List<string> ScenePerIndex = null;

    // Grid metadata used for neighbor lookups.
    public static int GridSize = 0;
    public static List<Vector2Int> IndexToGrid = null;
    public static List<int> GridToIndexFlatten = null;

    // Cardinal directions used by Sign / neighbor lookup
    public enum Cardinal { North = 0, East = 1, South = 2, West = 3 }

    /// <summary>
    /// Safely returns the scene name for an index, or null if not available.
    /// </summary>
    public static string GetSceneNameForIndex(int index)
    {
        if (ScenePerIndex == null) return null;
        if (index < 0 || index >= ScenePerIndex.Count) return null;
        return ScenePerIndex[index];
    }

    /// <summary>
    /// Returns the neighbor index for ownerIndex in the given direction using persisted grid metadata.
    /// Returns -1 if no neighbor or insufficient metadata.
    /// </summary>
    public static int GetNeighborIndexFromPersisted(int ownerIndex, Cardinal dir)
    {
        if (IndexToGrid == null || GridToIndexFlatten == null) return -1;
        if (ownerIndex < 0 || ownerIndex >= IndexToGrid.Count) return -1;
        if (GridSize <= 0) return -1;

        Vector2Int pos = IndexToGrid[ownerIndex];
        int row = pos.x;
        int col = pos.y;

        int nRow = row;
        int nCol = col;
        switch (dir)
        {
            case Cardinal.North: nRow = row - 1; break;
            case Cardinal.South: nRow = row + 1; break;
            case Cardinal.West:  nCol = col - 1; break;
            case Cardinal.East:  nCol = col + 1; break;
        }

        if (nRow < 0 || nRow >= GridSize || nCol < 0 || nCol >= GridSize) return -1;
        int flat = nRow * GridSize + nCol;
        if (flat < 0 || flat >= GridToIndexFlatten.Count) return -1;
        return GridToIndexFlatten[flat];
    }

    /// <summary>
    /// Optional helper: builds the GridToIndexFlatten from IndexToGrid and GridSize.
    /// </summary>
    public static void RebuildGridFlatten()
    {
        if (GridSize <= 0 || IndexToGrid == null) return;
        GridToIndexFlatten = new List<int>(GridSize * GridSize);
        for (int i = 0; i < GridSize * GridSize; i++) GridToIndexFlatten.Add(-1);
        for (int idx = 0; idx < IndexToGrid.Count; idx++)
        {
            var p = IndexToGrid[idx];
            if (p.x < 0 || p.x >= GridSize || p.y < 0 || p.y >= GridSize) continue;
            int flat = p.x * GridSize + p.y;
            if (flat >= 0 && flat < GridToIndexFlatten.Count)
                GridToIndexFlatten[flat] = idx;
        }
    }
}