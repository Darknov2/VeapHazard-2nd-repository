using System.Collections;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.SceneManagement;

/// <summary>
/// SceneLoadManager (robust unload-first policy)
/// - Unloads previous scene first when possible, waits for it to disappear, otherwise uses LoadScene(Single) replacement.
/// - Never calls UnloadSceneAsync with an invalid Scene object; checks IsValid() && isLoaded first.
/// - Detects newly-loaded scenes via handle-diff for additive loads, maps instance keys "Name#n" -> Scene.
/// - Ensures GameState.SelectedWorldIndex is updated for the newly-loaded scene (uses indexHint or resolves from GameState.ScenePerIndex).
/// </summary>
public class SceneLoadManager : MonoBehaviour
{
    private static SceneLoadManager _instance;
    public static SceneLoadManager Instance
    {
        get
        {
            if (_instance == null)
            {
                _instance = FindObjectOfType<SceneLoadManager>();
                if (_instance == null)
                {
                    var go = new GameObject("SceneLoadManager");
                    _instance = go.AddComponent<SceneLoadManager>();
                    DontDestroyOnLoad(go);
                }
            }
            return _instance;
        }
    }

    public string currentLoadedInstanceKey = null; // "Main#1"
    public int currentLoadedIndex = -1;

    private Dictionary<string, Scene> instanceKeyToScene = new Dictionary<string, Scene>();
    private Dictionary<string, int> sceneNameCounters = new Dictionary<string, int>();

    public List<string> protectedSceneNames = new List<string>() { "DontDestroyOnLoad", "SceneLoadManager" };
    public string menuRootObjectName = "Menu";
    public float unloadWaitTimeout = 5f;

    void Awake()
    {
        if (_instance == null) _instance = this;
        DontDestroyOnLoad(this.gameObject);

        // Map active scene on startup so we have a valid previous mapping
        if (string.IsNullOrEmpty(currentLoadedInstanceKey))
        {
            var active = SceneManager.GetActiveScene();
            if (active.IsValid() && active.isLoaded)
            {
                int next = 1;
                string asset = active.name;
                if (sceneNameCounters.TryGetValue(asset, out int ex)) next = ex + 1;
                sceneNameCounters[asset] = next;
                string key = $"{asset}#{next}";
                instanceKeyToScene[key] = active;
                currentLoadedInstanceKey = key;
                currentLoadedIndex = -1;
                GameState.SelectedWorldSceneName = key;
                GameState.SelectedWorldIndex = -1;
                Debug.Log($"SceneLoadManager: Startup mapped active scene '{asset}' to instanceKey '{key}'.");
            }
        }
    }

    public void LoadWorldByName(string sceneName, string menuSceneNameToUnload = null)
    {
        if (string.IsNullOrEmpty(sceneName))
        {
            Debug.LogWarning("SceneLoadManager.LoadWorldByName called with null/empty sceneName");
            return;
        }
        StartCoroutine(UnloadPreviousThenLoad(sceneName, menuSceneNameToUnload));
    }

    public void LoadWorldByIndex(int index, MonoBehaviour selector = null, string menuSceneNameToUnload = null)
    {
        string scene = null;
        bool selectorProvided = selector != null;
        if (selectorProvided)
        {
            var mi = selector.GetType().GetMethod("GetSceneNameForIndex",
                System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Instance)
                ?? selector.GetType().GetMethod("GetSceneNameForIndex",
                System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
            if (mi != null)
            {
                try { scene = mi.Invoke(selector, new object[] { index }) as string; }
                catch (System.Exception ex) { Debug.LogWarning($"SceneLoadManager: selector.GetSceneNameForIndex threw: {ex.Message}"); }
            }

            if (string.IsNullOrEmpty(scene))
            {
                var miPersist = selector.GetType().GetMethod("PersistMappingToGameStatePublic",
                    System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Instance);
                if (miPersist != null)
                {
                    try
                    {
                        miPersist.Invoke(selector, null);
                    }
                    catch { }
                    if (mi != null)
                    {
                        try { scene = mi.Invoke(selector, new object[] { index }) as string; } catch { }
                    }
                }
            }
        }

        if (string.IsNullOrEmpty(scene)) scene = GameState.GetSceneNameForIndex(index);
        if (string.IsNullOrEmpty(scene))
        {
            int gsCount = GameState.ScenePerIndex == null ? 0 : GameState.ScenePerIndex.Count;
            Debug.LogWarning($"SceneLoadManager.LoadWorldByIndex: no scene for index {index}. SelectorPresent={selectorProvided} GameStateCount={gsCount}");
            return;
        }

        GameState.SelectedWorldIndex = index;
        StartCoroutine(UnloadPreviousThenLoad(scene, menuSceneNameToUnload, index));
    }

    // Main orchestration coroutine
    private IEnumerator UnloadPreviousThenLoad(string sceneAssetName, string menuSceneNameToUnload, int indexHint = -1)
    {
        LogSceneList("Before unload");

        string previousInstanceKey = currentLoadedInstanceKey;
        string previousAssetName = AssetNameFromInstanceKey(previousInstanceKey);

        // Try to save previous via SaveManager if present (best-effort, reflection)
        if (!string.IsNullOrEmpty(previousInstanceKey))
        {
            TryCallSaveManager(previousInstanceKey);
        }

        // Re-map if we have an instance key but no valid Scene (attempt to find loaded scene with same asset name)
        if (!string.IsNullOrEmpty(previousInstanceKey))
        {
            if (!instanceKeyToScene.ContainsKey(previousInstanceKey) || !instanceKeyToScene[previousInstanceKey].IsValid())
            {
                if (!string.IsNullOrEmpty(previousAssetName))
                {
                    for (int i = 0; i < SceneManager.sceneCount; i++)
                    {
                        Scene s = SceneManager.GetSceneAt(i);
                        if (s.IsValid() && s.isLoaded && s.name == previousAssetName)
                        {
                            instanceKeyToScene[previousInstanceKey] = s;
                            Debug.Log($"SceneLoadManager: Re-mapped instanceKey '{previousInstanceKey}' -> scene handle {s.handle}");
                            break;
                        }
                    }
                }
            }
        }

        // Attempt to unload previous first when possible
        if (!string.IsNullOrEmpty(previousInstanceKey))
        {
            bool unloadStarted = false;

            // Unload by mapped Scene object if we have it and there is more than 1 loaded scene
            if (instanceKeyToScene.TryGetValue(previousInstanceKey, out Scene mapped) && mapped.IsValid() && mapped.isLoaded && SceneManager.sceneCount > 1)
            {
                Debug.Log($"SceneLoadManager: Unloading mapped previous scene (handle={mapped.handle}) for key '{previousInstanceKey}'");
                var u = SceneManager.UnloadSceneAsync(mapped);
                if (u != null)
                {
                    unloadStarted = true;
                    while (!u.isDone) yield return null;
                    instanceKeyToScene.Remove(previousInstanceKey);
                    Debug.Log($"SceneLoadManager: Unloaded mapped previous scene for key '{previousInstanceKey}'");
                }
                else Debug.LogWarning($"SceneLoadManager: UnloadSceneAsync returned null for mapped scene (key={previousInstanceKey})");
            }
            else
            {
                // Try unloading scenes by asset name (only when more than 1 scene loaded)
                if (!string.IsNullOrEmpty(previousAssetName) && SceneManager.sceneCount > 1)
                {
                    for (int i = SceneManager.sceneCount - 1; i >= 0; i--)
                    {
                        Scene s = SceneManager.GetSceneAt(i);
                        if (!s.isLoaded) continue;
                        if (s.name == previousAssetName)
                        {
                            Debug.Log($"SceneLoadManager: Unloading scene-by-name '{s.name}' (handle={s.handle}) matching previous asset");
                            var u2 = SceneManager.UnloadSceneAsync(s);
                            if (u2 != null)
                            {
                                unloadStarted = true;
                                while (!u2.isDone) yield return null;
                                Debug.Log($"SceneLoadManager: Unloaded previous scene '{s.name}' (handle={s.handle})");
                            }
                            else Debug.LogWarning($"SceneLoadManager: UnloadSceneAsync returned null for scene '{s.name}'");
                        }
                    }
                }
            }

            // If unload didn't start (likely previous was the only scene), try persistent-root destroy and wait briefly
            if (!unloadStarted)
            {
                Debug.LogWarning($"SceneLoadManager: Could not begin unload for previous '{previousInstanceKey}'. Attempting persistent-root destroy/fallback and will proceed after timeout.");
                TryDestroyPersistentMenuRoot();
                float startWait = Time.time;
                while (IsScenePresent(previousAssetName, previousInstanceKey) && Time.time - startWait < unloadWaitTimeout)
                    yield return null;
            }
            else
            {
                float startWait = Time.time;
                while (IsScenePresent(previousAssetName, previousInstanceKey) && Time.time - startWait < unloadWaitTimeout)
                    yield return null;
            }

            // Final check whether previous is gone
            if (IsScenePresent(previousAssetName, previousInstanceKey))
            {
                Debug.LogWarning($"SceneLoadManager: Previous '{previousInstanceKey}' still present after wait. Proceeding (may replace via Single).");
            }
            else
            {
                Debug.Log($"SceneLoadManager: Previous '{previousInstanceKey}' no longer present; continuing to load '{sceneAssetName}'.");
                currentLoadedInstanceKey = null;
                currentLoadedIndex = -1;
                GameState.SelectedWorldSceneName = null;
                GameState.SelectedWorldIndex = -1;
            }
        }

        // Decide whether we must do a Single replace (can't unload last scene)
        bool requireSingleReplace = false;
        if (!string.IsNullOrEmpty(previousInstanceKey) && SceneManager.sceneCount == 1 && IsScenePresent(previousAssetName, previousInstanceKey))
            requireSingleReplace = true;

        // If the single scene already matches the requested asset, nothing to do
        if (!requireSingleReplace && SceneManager.sceneCount == 1)
        {
            var only = SceneManager.GetSceneAt(0);
            if (only.IsValid() && only.name == sceneAssetName)
            {
                Debug.Log($"SceneLoadManager: Target '{sceneAssetName}' already the only scene -> skipping load.");
                // ensure SelectedWorldIndex updated (try resolve)
                if (indexHint >= 0)
                {
                    GameState.SelectedWorldIndex = indexHint;
                    currentLoadedIndex = indexHint;
                }
                else
                {
                    if (GameState.ScenePerIndex != null)
                    {
                        int foundIndex = GameState.ScenePerIndex.IndexOf(sceneAssetName);
                        if (foundIndex >= 0) { GameState.SelectedWorldIndex = foundIndex; currentLoadedIndex = foundIndex; }
                    }
                }
                yield break;
            }
        }

        // Capture handles
        HashSet<int> beforeHandles = new HashSet<int>();
        for (int i = 0; i < SceneManager.sceneCount; i++) beforeHandles.Add(SceneManager.GetSceneAt(i).handle);

        if (requireSingleReplace)
        {
            Debug.Log($"SceneLoadManager: Replacing previous with LoadScene(Single) for '{sceneAssetName}'.");
            var lop = SceneManager.LoadSceneAsync(sceneAssetName, LoadSceneMode.Single);
            if (lop == null) { Debug.LogError($"SceneLoadManager: LoadScene(Single) returned null for '{sceneAssetName}'"); yield break; }
            lop.allowSceneActivation = true;
            while (!lop.isDone) yield return null;

            Scene newScene = SceneManager.GetActiveScene();
            int next = 1;
            if (sceneNameCounters.TryGetValue(sceneAssetName, out int ex2)) next = ex2 + 1;
            sceneNameCounters[sceneAssetName] = next;
            string iKey = $"{sceneAssetName}#{next}";
            instanceKeyToScene[iKey] = newScene;
            currentLoadedInstanceKey = iKey;

            // set index: prefer indexHint, otherwise try to resolve by GameState.ScenePerIndex
            if (indexHint >= 0) { currentLoadedIndex = indexHint; GameState.SelectedWorldIndex = indexHint; }
            else
            {
                int foundIndex = -1;
                if (GameState.ScenePerIndex != null) foundIndex = GameState.ScenePerIndex.IndexOf(sceneAssetName);
                currentLoadedIndex = foundIndex;
                GameState.SelectedWorldIndex = foundIndex;
            }

            GameState.SelectedWorldSceneName = iKey;
            Debug.Log($"SceneLoadManager: Replaced and mapped instanceKey '{iKey}' -> handle {newScene.handle}");
            yield return null;
            try { var selector = FindObjectOfType<WorldSelector_PersistScenes>(); selector?.PersistMappingToGameStatePublic(); } catch { }
            LogSceneList("After Single replace");
            yield break;
        }

        // Additive load
        Debug.Log($"SceneLoadManager: Loading '{sceneAssetName}' additively.");
        var loadOp = SceneManager.LoadSceneAsync(sceneAssetName, LoadSceneMode.Additive);
        if (loadOp == null) { Debug.LogError($"SceneLoadManager: LoadSceneAsync returned null for '{sceneAssetName}'"); yield break; }
        loadOp.allowSceneActivation = true;
        while (!loadOp.isDone) yield return null;
        yield return null;

        // Detect newly-loaded Scene by handle diff
        Scene created = new Scene();
        bool createdFound = false;
        for (int i = 0; i < SceneManager.sceneCount; i++)
        {
            var s = SceneManager.GetSceneAt(i);
            if (!beforeHandles.Contains(s.handle))
            {
                created = s;
                createdFound = true;
                Debug.Log($"SceneLoadManager: Detected newly-loaded scene '{s.name}' handle={s.handle}");
                break;
            }
        }
        if (!createdFound)
        {
            created = SceneManager.GetSceneByName(sceneAssetName);
            if (!created.IsValid() && SceneManager.sceneCount > 0) created = SceneManager.GetSceneAt(SceneManager.sceneCount - 1);
        }

        int counter = 1;
        if (sceneNameCounters.TryGetValue(sceneAssetName, out int existingCounter)) counter = existingCounter + 1;
        sceneNameCounters[sceneAssetName] = counter;
        string instanceKey = $"{sceneAssetName}#{counter}";

        if (created.IsValid() && created.isLoaded)
        {
            instanceKeyToScene[instanceKey] = created;
            SceneManager.SetActiveScene(created);
            Debug.Log($"SceneLoadManager: Mapped instanceKey '{instanceKey}' -> scene '{created.name}' (handle={created.handle}) and set active.");
        }
        else
        {
            instanceKeyToScene[instanceKey] = created;
            Debug.LogWarning($"SceneLoadManager: Created mapping for '{instanceKey}' but Scene object invalid.");
        }

        currentLoadedInstanceKey = instanceKey;

        // set index: prefer indexHint, otherwise try to resolve by GameState.ScenePerIndex
        if (indexHint >= 0)
        {
            currentLoadedIndex = indexHint;
            GameState.SelectedWorldIndex = indexHint;
        }
        else
        {
            int resolvedIndex = -1;
            if (GameState.ScenePerIndex != null) resolvedIndex = GameState.ScenePerIndex.IndexOf(sceneAssetName);
            currentLoadedIndex = resolvedIndex;
            GameState.SelectedWorldIndex = resolvedIndex;
        }

        GameState.SelectedWorldSceneName = instanceKey;
        Debug.Log($"SceneLoadManager: Selected world instance = '{instanceKey}', selectedIndex={GameState.SelectedWorldIndex}");

        yield return null;
        try { var selector = FindObjectOfType<WorldSelector_PersistScenes>(); selector?.PersistMappingToGameStatePublic(); } catch { }
        LogSceneList("After additive load");

        // Safety cleanup
        bool didExtra = false;
        for (int i = SceneManager.sceneCount - 1; i >= 0; i--)
        {
            Scene s = SceneManager.GetSceneAt(i);
            if (!s.isLoaded) continue;
            if (instanceKeyToScene.TryGetValue(currentLoadedInstanceKey, out var kept) && kept.IsValid() && s.handle == kept.handle) continue;
            if (protectedSceneNames.Contains(s.name)) continue;
            if (string.IsNullOrEmpty(s.name)) continue;
            Debug.Log($"SceneLoadManager: Safety-unloading unexpected scene '{s.name}' handle={s.handle}");
            var u = SceneManager.UnloadSceneAsync(s);
            if (u != null) { while (!u.isDone) yield return null; didExtra = true; Debug.Log($"SceneLoadManager: Safety-unloaded '{s.name}'"); }
            else Debug.LogWarning($"SceneLoadManager: Safety UnloadSceneAsync returned null for '{s.name}'");
        }
        if (!didExtra) Debug.Log("SceneLoadManager: No extra scenes to safety-unload.");
    }

    private void TryCallSaveManager(string instanceKey)
    {
        try
        {
            var smType = System.Type.GetType("SaveManager");
            if (smType != null)
            {
                var inst = smType.GetProperty("Instance", System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Static)?.GetValue(null, null);
                var saveM = smType.GetMethod("SaveSceneState");
                if (inst != null && saveM != null)
                {
                    saveM.Invoke(inst, new object[] { instanceKey });
                    Debug.Log($"SceneLoadManager: Invoked SaveManager.SaveSceneState for '{instanceKey}'");
                }
            }
        }
        catch { /* ignore */ }
    }

    private bool IsScenePresent(string assetName, string instanceKey)
    {
        if (!string.IsNullOrEmpty(instanceKey) && instanceKeyToScene.TryGetValue(instanceKey, out var mapped) && mapped.IsValid() && mapped.isLoaded)
            return true;
        if (!string.IsNullOrEmpty(assetName))
        {
            for (int i = 0; i < SceneManager.sceneCount; i++)
            {
                var s = SceneManager.GetSceneAt(i);
                if (s.IsValid() && s.isLoaded && s.name == assetName) return true;
            }
        }
        return false;
    }

    private void TryDestroyPersistentMenuRoot()
    {
        if (string.IsNullOrEmpty(menuRootObjectName)) return;
        var go = GameObject.Find(menuRootObjectName);
        if (go != null) { Debug.Log($"SceneLoadManager: Destroying persistent root '{menuRootObjectName}'"); Destroy(go); return; }
        var goAlt = GameObject.Find("Menu");
        if (goAlt != null) { Debug.Log("SceneLoadManager: Destroying persistent GameObject named 'Menu'"); Destroy(goAlt); return; }
    }

    private void LogSceneList(string header)
    {
        Debug.Log($"--- SceneLister: {header} | SceneCount={SceneManager.sceneCount} ---");
        for (int i = 0; i < SceneManager.sceneCount; i++)
        {
            var s = SceneManager.GetSceneAt(i);
            Debug.Log($"Scene[{i}] name='{s.name}' path='{s.path}' handle={s.handle} isLoaded={s.isLoaded} isActive={(s == SceneManager.GetActiveScene())}");
        }
        Debug.Log($"--- End SceneLister: {header} ---");
    }

    public static string AssetNameFromInstanceKey(string instanceKey)
    {
        if (string.IsNullOrEmpty(instanceKey)) return instanceKey;
        int p = instanceKey.LastIndexOf('#');
        if (p < 0) return instanceKey;
        return instanceKey.Substring(0, p);
    }

    public void SetCurrentLoadedInstance(string instanceKey, int index = -1)
    {
        currentLoadedInstanceKey = instanceKey;
        currentLoadedIndex = index;
        GameState.SelectedWorldSceneName = instanceKey;
        GameState.SelectedWorldIndex = index;
    }
}