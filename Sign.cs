using UnityEngine;
using System;
using System.Reflection;
using System.Collections;
using UnityEngine.SceneManagement;

/// <summary>
/// Sign: world-edge travel trigger
/// - Knows the owner world index (set at spawn or via SignWorldIndexBinder).
/// - Has a cardinal side (North/East/South/West).
/// - When activated (via SignInteractor press E or OnMouseDown), it:
///     1) Records the exit direction in TravelState so the next scene can spawn the player at the opposite edge.
///     2) Resolves the neighbor world index based on the current world index and its side.
///     3) Loads the neighbor scene via SceneLoadManager.LoadWorldByIndex, which unloads the current scene in order.
/// - Uses WorldSelector (if present) via reflection for neighbor/scene resolution but works with GameState alone.
/// </summary>
public class Sign : MonoBehaviour
{
    [Tooltip("Index of the world this sign belongs to (set when spawning/binding).")]
    public int ownerWorldIndex = 0;

    public enum Side { North = 0, East = 1, South = 2, West = 3 }

    [Tooltip("Which cardinal side this sign represents (set when spawning or via DetermineSideFromCenter).")]
    public Side side = Side.North;

    [Tooltip("Key used for interaction when using OnTriggerStay (kept for compatibility).")]
    public KeyCode interactKey = KeyCode.E;

    [Tooltip("Verbose logging for debugging.")]
    public bool verboseLogs = false;

    // Reflection cached selector references (may be null if menu unloaded)
    private MonoBehaviour selectorInstance;
    private Type selectorType;
    private MethodInfo getNeighborIndexMethod;
    private MethodInfo loadSceneByIndexMethod;
    private MethodInfo getSceneNameForIndexMethod;
    private MethodInfo loadSceneByNameMethod;

    void Awake()
    {
        FindAndCacheSelector();
    }

    void OnMouseDown()
    {
#if UNITY_EDITOR || UNITY_STANDALONE
        if (verboseLogs) Debug.Log($"Sign[{name}] OnMouseDown -> ActivateSign()");
        ActivateSign();
#endif
    }

    // Optional trigger-based interaction support (legacy)
    void OnTriggerStay(Collider other)
    {
        if (other.CompareTag("Player") && Input.GetKeyDown(interactKey))
        {
            if (verboseLogs) Debug.Log($"Sign[{name}] OnTriggerStay + key -> ActivateSign()");
            ActivateSign();
        }
    }

    /// <summary>
    /// Main entry to travel: records exit direction, resolves neighbor index, and loads next scene.
    /// </summary>
    public void ActivateSign()
    {
        if (verboseLogs) Debug.Log($"Sign[{name}] ActivateSign called. ownerIndex={ownerWorldIndex}, side={side}");

        // 1) Record which direction the player is leaving from so the next scene can spawn them at the opposite edge.
        GameState.Cardinal exitDir = GameState.Cardinal.North;
        switch (side)
        {
            case Side.North: exitDir = GameState.Cardinal.North; break;
            case Side.East:  exitDir = GameState.Cardinal.East;  break;
            case Side.South: exitDir = GameState.Cardinal.South; break;
            case Side.West:  exitDir = GameState.Cardinal.West;  break;
        }
        TravelState.RecordEntrance(exitDir);

        // 2) Resolve neighbor world index
        int neighborIndex = ResolveNeighborIndex();
        if (neighborIndex < 0)
        {
            if (verboseLogs) Debug.Log($"Sign[{name}] no neighbor found for owner={ownerWorldIndex} side={side}");
            return;
        }

        // 3) Prefer selector.LoadSceneByIndex (if menu selector still present)
        if (selectorInstance != null && loadSceneByIndexMethod != null)
        {
            try
            {
                if (verboseLogs) Debug.Log($"Sign[{name}] invoking selector.LoadSceneByIndex({neighborIndex})");
                loadSceneByIndexMethod.Invoke(selectorInstance, new object[] { neighborIndex });
                return;
            }
            catch (Exception ex) { Debug.LogWarning($"Sign: selector.LoadSceneByIndex threw: {ex.Message}"); }
        }

        // Otherwise use SceneLoadManager, which will unload current scene first, then load neighbor, and set active
        if (SceneLoadManager.Instance != null)
        {
            if (verboseLogs) Debug.Log($"Sign[{name}] requesting SceneLoadManager.LoadWorldByIndex({neighborIndex})");
            SceneLoadManager.Instance.LoadWorldByIndex(neighborIndex, selectorInstance, GameState.MenuSceneName);
            return;
        }

        // Last resort: fall back to name resolution via selector or GameState, then local load/unload
        string sceneName = null;
        if (selectorInstance != null && getSceneNameForIndexMethod != null)
        {
            try { sceneName = getSceneNameForIndexMethod.Invoke(selectorInstance, new object[] { neighborIndex }) as string; }
            catch { }
        }
        if (string.IsNullOrEmpty(sceneName))
            sceneName = GameState.GetSceneNameForIndex(neighborIndex);

        if (!string.IsNullOrEmpty(sceneName))
        {
            StartCoroutine(LoadSceneAndUnloadPrevious(sceneName));
            return;
        }

        Debug.LogWarning($"Sign[{name}] could not start load for neighbor index {neighborIndex}: no loader or scene name available.");
    }

    /// <summary>
    /// Fallback loader when SceneLoadManager is not available.
    /// Loads the target scene additively and then unloads the previous scene safely.
    /// </summary>
    private IEnumerator LoadSceneAndUnloadPrevious(string sceneNameOrInstance)
    {
        if (string.IsNullOrEmpty(sceneNameOrInstance)) yield break;
        string asset = StripInstanceSuffix(sceneNameOrInstance);
        string previous = GameState.SelectedWorldSceneName;
        GameState.SelectedWorldSceneName = sceneNameOrInstance;

        AsyncOperation loadOp = SceneManager.LoadSceneAsync(asset, LoadSceneMode.Additive);
        if (loadOp == null) { Debug.LogWarning($"Sign: LoadSceneAsync null for {asset}"); yield break; }
        loadOp.allowSceneActivation = true;
        while (!loadOp.isDone) yield return null;
        yield return null;

        Scene loaded = SceneManager.GetSceneByName(asset);
        if (loaded.IsValid()) SceneManager.SetActiveScene(loaded);

        if (!string.IsNullOrEmpty(previous) && previous != sceneNameOrInstance)
        {
            string prevAsset = StripInstanceSuffix(previous);
            bool unloaded = false;
            for (int i = SceneManager.sceneCount - 1; i >= 0; i--)
            {
                var s = SceneManager.GetSceneAt(i);
                if (!s.isLoaded) continue;
                if (s.name != prevAsset) continue;
                if (s == SceneManager.GetActiveScene()) continue; // don't unload the active one
                var u = SceneManager.UnloadSceneAsync(s);
                if (u != null) { while (!u.isDone) yield return null; unloaded = true; }
            }
            if (!unloaded)
            {
                var prevScene = SceneManager.GetSceneByName(previous);
                if (prevScene.IsValid() && prevScene.isLoaded)
                {
                    var u2 = SceneManager.UnloadSceneAsync(prevScene);
                    if (u2 != null) while (!u2.isDone) yield return null;
                }
            }
        }
    }

    /// <summary>
    /// Resolves neighbor index using menu selector (if present) or GameState grid mapping.
    /// </summary>
    private int ResolveNeighborIndex()
    {
        if (selectorInstance == null) FindAndCacheSelector();
        if (selectorInstance != null && getNeighborIndexMethod != null)
        {
            try
            {
                object dirArg = MapSideToSelectorDirectionArgument(getNeighborIndexMethod);
                var r = getNeighborIndexMethod.Invoke(selectorInstance, new object[] { ownerWorldIndex, dirArg });
                if (r is int ri) return ri;
                return Convert.ToInt32(r);
            }
            catch { /* fall through to GameState */ }
        }

        GameState.Cardinal d = GameState.Cardinal.North;
        switch (side)
        {
            case Side.North: d = GameState.Cardinal.North; break;
            case Side.East:  d = GameState.Cardinal.East;  break;
            case Side.South: d = GameState.Cardinal.South; break;
            case Side.West:  d = GameState.Cardinal.West;  break;
        }
        return GameState.GetNeighborIndexFromPersisted(ownerWorldIndex, d);
    }

    /// <summary>
    /// Maps our Side to whatever argument type the selector's GetNeighborIndex expects (enum or int).
    /// </summary>
    private object MapSideToSelectorDirectionArgument(MethodInfo neighborMethod)
    {
        if (neighborMethod == null) return (int)side;
        var pars = neighborMethod.GetParameters();
        if (pars.Length < 2) return (int)side;
        var dirType = pars[1].ParameterType;
        if (dirType.IsEnum) { try { return Enum.Parse(dirType, side.ToString()); } catch { return Convert.ChangeType((int)side, Enum.GetUnderlyingType(dirType)); } }
        if (dirType == typeof(int) || dirType == typeof(long) || dirType == typeof(short)) return Convert.ChangeType((int)side, dirType);
        return (int)side;
    }

    /// <summary>
    /// Finds any object named WorldSelector* and caches its methods via reflection.
    /// </summary>
    private void FindAndCacheSelector()
    {
        selectorInstance = null; selectorType = null;
        getNeighborIndexMethod = null; loadSceneByIndexMethod = null;
        getSceneNameForIndexMethod = null; loadSceneByNameMethod = null;

        var all = FindObjectsOfType<MonoBehaviour>();
        foreach (var mb in all)
        {
            var t = mb.GetType();
            if (t.Name.IndexOf("WorldSelector", StringComparison.OrdinalIgnoreCase) >= 0)
            {
                selectorInstance = mb; selectorType = t; break;
            }
        }
        if (selectorInstance != null)
        {
            getNeighborIndexMethod = selectorType.GetMethod("GetNeighborIndex", BindingFlags.Public | BindingFlags.Instance)
                                   ?? selectorType.GetMethod("GetNeighborIndex", BindingFlags.NonPublic | BindingFlags.Instance);
            loadSceneByIndexMethod = selectorType.GetMethod("LoadSceneByIndex", BindingFlags.Public | BindingFlags.Instance)
                                  ?? selectorType.GetMethod("LoadSceneByIndex", BindingFlags.NonPublic | BindingFlags.Instance);
            getSceneNameForIndexMethod = selectorType.GetMethod("GetSceneNameForIndex", BindingFlags.Public | BindingFlags.Instance)
                                      ?? selectorType.GetMethod("GetSceneNameForIndex", BindingFlags.NonPublic | BindingFlags.Instance);
            loadSceneByNameMethod = selectorType.GetMethod("LoadSceneByNameFromSign", BindingFlags.Public | BindingFlags.Instance)
                                ?? selectorType.GetMethod("LoadSceneByName", BindingFlags.Public | BindingFlags.Instance);
        }
    }

    private string StripInstanceSuffix(string maybeInstanceKey)
    {
        if (string.IsNullOrEmpty(maybeInstanceKey)) return maybeInstanceKey;
        int p = maybeInstanceKey.LastIndexOf('#');
        if (p < 0) return maybeInstanceKey;
        return maybeInstanceKey.Substring(0, p);
    }

    /// <summary>
    /// Determines which side this sign is on relative to a given center point,
    /// and sets 'side' accordingly. Also useful when signs are auto-spawned.
    /// </summary>
    public void DetermineSideFromCenter(Vector3 center)
    {
        Vector3 dir = transform.position - center;
        dir.y = 0f;
        if (dir.sqrMagnitude <= 0.0001f) return;
        dir.Normalize();
        float nx = dir.x;
        float nz = dir.z;
        if (Mathf.Abs(nx) > Mathf.Abs(nz)) side = (nx > 0) ? Side.East : Side.West;
        else side = (nz > 0) ? Side.North : Side.South;
        if (verboseLogs) Debug.Log($"Sign[{name}] DetermineSideFromCenter assigned side={side}");
    }
}