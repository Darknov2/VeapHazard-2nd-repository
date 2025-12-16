using UnityEngine;

/// <summary>
/// Holds transient travel info between scenes (which sign/direction the player used).
/// Does not depend on GameState so it won't interfere with your existing persistence.
/// </summary>
public static class TravelState
{
    // The cardinal direction of the sign the player used in the PREVIOUS map to travel.
    // Set by Sign before requesting a load; read by PlayerEntranceSpawner in the next scene.
    public static GameState.Cardinal LastEntranceDirection = GameState.Cardinal.North;

    // Call this to record the direction just before loading the next scene.
    public static void RecordEntrance(GameState.Cardinal dir)
    {
        LastEntranceDirection = dir;
    }

    // Returns the opposite direction (e.g., coming from East -> spawn at West edge)
    public static GameState.Cardinal GetOpposite(GameState.Cardinal dir)
    {
        switch (dir)
        {
            case GameState.Cardinal.North: return GameState.Cardinal.South;
            case GameState.Cardinal.South: return GameState.Cardinal.North;
            case GameState.Cardinal.East:  return GameState.Cardinal.West;
            case GameState.Cardinal.West:  return GameState.Cardinal.East;
        }
        return GameState.Cardinal.North;
    }
}