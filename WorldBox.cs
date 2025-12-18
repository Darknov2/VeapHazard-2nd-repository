using UnityEngine;
using System;
using System.Reflection;

// Lightweight component to store the world index on each world box (UI cell / prefab).
// This version is tolerant to multiple WorldSelector implementations (including WorldSelector_RandomGroup).
public class WorldBox : MonoBehaviour
{
    [Tooltip("Index in the world selector grid (0-based). Matches the order in WorldSelector.sceneNames or SceneGroups")]
    public int worldIndex = 0;

    [Tooltip("Optional display name for debug / tooltip usage")]
    public string displayName;

    void Start()
    {
        // If displayName already set (by the selector when creating the box), keep it.
        if (!string.IsNullOrEmpty(displayName)) return;

        // Try to obtain a name from a WorldSelector_RandomGroup (preferred)
        var rg = SceneFind.First(typeof(MonoBehaviour)) as MonoBehaviour;
        // Find by type name to avoid compile-time dependency
        var all = SceneFind.All<MonoBehaviour>();
        foreach (var mb in all)
        {
            var t = mb.GetType();
            if (t.Name == "WorldSelector_RandomGroup")
            {
                // Try to read sceneGroups[index].groupName via reflection
                try
                {
                    var sgField = t.GetField("sceneGroups", BindingFlags.Public | BindingFlags.Instance);
                    if (sgField != null)
                    {
                        var sceneGroups = sgField.GetValue(mb) as System.Collections.IList;
                        if (sceneGroups != null && worldIndex >= 0 && worldIndex < sceneGroups.Count)
                        {
                            var group = sceneGroups[worldIndex];
                            if (group != null)
                            {
                                // group is a SceneGroup type; try to get its groupName field
                                var gType = group.GetType();
                                var gNameField = gType.GetField("groupName", BindingFlags.Public | BindingFlags.Instance);
                                if (gNameField != null)
                                {
                                    var gname = gNameField.GetValue(group) as string;
                                    if (!string.IsNullOrEmpty(gname))
                                    {
                                        displayName = gname;
                                        return;
                                    }
                                }
                            }
                        }
                    }
                }
                catch (Exception) { /* ignore and fall back */ }
            }
            // If there's a selector named "WorldSelector" that exposes GetSceneNameAtIndex, try that
            if (t.Name == "WorldSelector")
            {
                try
                {
                    var mi = t.GetMethod("GetSceneNameAtIndex", BindingFlags.Public | BindingFlags.Instance);
                    if (mi != null)
                    {
                        var result = mi.Invoke(mb, new object[] { worldIndex }) as string;
                        if (!string.IsNullOrEmpty(result))
                        {
                            displayName = result;
                            return;
                        }
                    }
                }
                catch (Exception) { /* ignore and fall back */ }
            }
        }

        // Fallback default label
        displayName = $"World {worldIndex + 1}";
    }

    // Optional helper to retrieve a scene name for this world box using available selectors.
    // Returns null if none found.
    public string GetSceneName()
    {
        // Try WorldSelector_RandomGroup first
        var all = SceneFind.All<MonoBehaviour>();
        foreach (var mb in all)
        {
            var t = mb.GetType();
            if (t.Name == "WorldSelector_RandomGroup")
            {
                try
                {
                    var sgField = t.GetField("sceneGroups", BindingFlags.Public | BindingFlags.Instance);
                    if (sgField != null)
                    {
                        var sceneGroups = sgField.GetValue(mb) as System.Collections.IList;
                        if (sceneGroups != null && worldIndex >= 0 && worldIndex < sceneGroups.Count)
                        {
                            var group = sceneGroups[worldIndex];
                            if (group != null)
                            {
                                var gType = group.GetType();
                                var scenesField = gType.GetField("scenes", BindingFlags.Public | BindingFlags.Instance);
                                if (scenesField != null)
                                {
                                    var scenes = scenesField.GetValue(group) as System.Collections.IList;
                                    if (scenes != null && scenes.Count > 0)
                                    {
                                        var first = scenes[0] as string;
                                        return first;
                                    }
                                }
                                // if no scenes field, try groupName
                                var gNameField = gType.GetField("groupName", BindingFlags.Public | BindingFlags.Instance);
                                if (gNameField != null)
                                {
                                    var gname = gNameField.GetValue(group) as string;
                                    if (!string.IsNullOrEmpty(gname))
                                        return gname;
                                }
                            }
                        }
                    }
                }
                catch (Exception) { /* ignore and try others */ }
            }
            if (t.Name == "WorldSelector")
            {
                try
                {
                    var mi = t.GetMethod("GetSceneNameAtIndex", BindingFlags.Public | BindingFlags.Instance);
                    if (mi != null)
                    {
                        var result = mi.Invoke(mb, new object[] { worldIndex }) as string;
                        if (!string.IsNullOrEmpty(result))
                            return result;
                    }
                }
                catch (Exception) { /* ignore */ }
            }
        }
        return null;
    }
}