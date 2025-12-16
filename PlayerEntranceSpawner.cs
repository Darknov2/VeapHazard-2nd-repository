using System.Collections;
using UnityEngine;
using UnityEngine.AI;

/// <summary>
/// After the new map has finished generating, spawns the player next to the sign
/// corresponding to the direction they came from in the previous map.
/// - Reads TravelState.LastEntranceDirection, uses the opposite side for placement (East -> spawn at West edge).
/// - Waits for ProceduralTerrainGenerator.IsInitialTerrainReady, then finds the Sign prefab placed on the edge.
/// - Places player near that sign with a configurable offset and faces them toward the map center.
/// </summary>
[DisallowMultipleComponent]
public class PlayerEntranceSpawner : MonoBehaviour
{
    [Header("Player")]
    public Transform player;
    public string playerTag = "Player";

    [Header("References")]
    public ProceduralTerrainGenerator generator;

    [Header("Spawn settings")]
    [Tooltip("Offset applied from the entrance sign to place the player without clipping.")]
    public Vector3 spawnOffset = new Vector3(0f, 0f, -1.5f);

    [Tooltip("If true, snap the player to the NavMesh (if available) after placement.")]
    public bool snapToNavMesh = true;

    [Tooltip("Max snap distance for NavMesh sampling.")]
    public float navMeshMaxSnapDistance = 2f;

    public int navMeshAreaMask = NavMesh.AllAreas;

    [Header("Debug")]
    public bool verboseLogs = false;

    void Reset()
    {
        if (player == null)
        {
            var go = GameObject.FindWithTag(playerTag);
            if (go != null) player = go.transform;
        }
        generator = FindFirstObjectByType<ProceduralTerrainGenerator>();
    }

    void Start()
    {
        if (player == null)
        {
            var go = GameObject.FindWithTag(playerTag);
            if (go != null) player = go.transform;
        }
        if (generator == null)
            generator = FindFirstObjectByType<ProceduralTerrainGenerator>();

        StartCoroutine(SpawnRoutine());
    }

    private IEnumerator SpawnRoutine()
    {
        // Wait for terrain generation to complete
        float timeout = 15f;
        float start = Time.time;
        if (generator != null)
        {
            while (!generator.IsInitialTerrainReady && Time.time - start < timeout)
                yield return null;
        }
        // One extra frame for NavMesh or post-setup
        yield return null;

        // Find the target sign (opposite of the entrance direction)
        var targetSide = TravelState.GetOpposite(TravelState.LastEntranceDirection);
        Sign targetSign = FindEntranceSign(targetSide);

        if (targetSign == null)
        {
            if (verboseLogs) Debug.LogWarning($"[PlayerEntranceSpawner] No Sign found for side={targetSide}. Player spawn unchanged.");
            yield break;
        }

        if (player == null)
        {
            var go = GameObject.FindWithTag(playerTag);
            if (go != null) player = go.transform;
        }
        if (player == null)
        {
            Debug.LogError("[PlayerEntranceSpawner] Player not found. Ensure the player is tagged correctly or assign the Transform.");
            yield break;
        }

        // Place player near the sign and face toward map center
        Vector3 pos = targetSign.transform.position + targetSign.transform.TransformDirection(spawnOffset);
        player.position = pos;

        Vector3 center = ComputeTerrainCenter();
        Vector3 toCenter = center - player.position;
        toCenter.y = 0f;
        if (toCenter.sqrMagnitude > 0.0001f)
            player.rotation = Quaternion.LookRotation(toCenter.normalized, Vector3.up);

        if (snapToNavMesh)
        {
            if (NavMesh.SamplePosition(player.position, out NavMeshHit hit, navMeshMaxSnapDistance, navMeshAreaMask))
                player.position = hit.position;
        }

        if (verboseLogs) Debug.Log($"[PlayerEntranceSpawner] Spawned player near {targetSide} sign at {player.position}.");
    }

    private Sign FindEntranceSign(GameState.Cardinal side)
    {
        var signs = FindObjectsOfType<Sign>(true);
        Sign best = null;
        foreach (var s in signs)
        {
            // Match by Sign.Side and by ownerWorldIndex if desired (optional)
            if (MatchesSide(s.side, side))
            {
                best = s;
                break;
            }
        }
        return best;
    }

    private bool MatchesSide(Sign.Side signSide, GameState.Cardinal target)
    {
        switch (target)
        {
            case GameState.Cardinal.North: return signSide == Sign.Side.North;
            case GameState.Cardinal.South: return signSide == Sign.Side.South;
            case GameState.Cardinal.East:  return signSide == Sign.Side.East;
            case GameState.Cardinal.West:  return signSide == Sign.Side.West;
        }
        return false;
    }

    private Vector3 ComputeTerrainCenter()
    {
        if (generator == null || generator.config == null)
            return Vector3.zero;

        var cfg = generator.config;
        float cellXZ = cfg.chunkSizeXZ * cfg.voxelScale;
        float width  = Mathf.Max(0f, cfg.chunksX) * cellXZ;
        float length = Mathf.Max(0f, cfg.chunksZ) * cellXZ;

        Vector3 min = cfg.worldOffset;
        Vector3 max = cfg.worldOffset + new Vector3(width, 0f, length);
        return (min + max) * 0.5f;
    }
}