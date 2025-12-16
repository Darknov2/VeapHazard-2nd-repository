using System;
using System.Linq;
using System.Reflection;
using UnityEngine;

/// <summary>
/// Safer PerSceneTerrainBinder (no compile-time dependency on PerSceneTerrainConfig)
/// - Accepts any ScriptableObject-like config asset (UnityEngine.Object).
/// - Instantiates a runtime copy (so the asset on disk is not mutated).
/// - Attempts to assign the runtime copy to a matching field/property on the ProceduralTerrainGenerator
///   (prefers a member named 'config', then falls back to the first compatible one).
/// - Optionally invokes a parameterless refresh/regenerate method on the generator (best-effort).
/// 
/// This avoids compile errors when the concrete PerSceneTerrainConfig type/class isn't present
/// and prevents runtime mutation of ScriptableObject assets (which can cause editor flicker).
/// </summary>
[DisallowMultipleComponent]
public class PerSceneTerrainBinder : MonoBehaviour
{
    [Tooltip("Procedural terrain generator instance in the scene. If null, will attempt to find one at Awake.")]
    public ProceduralTerrainGenerator generator;

    [Tooltip("Per-scene terrain config asset (ScriptableObject). This will NOT be modified - a runtime clone is created.")]
    public UnityEngine.Object configAsset;

    [Tooltip("If true, the binder will attempt to call a parameterless refresh/regenerate method on the generator after assigning the runtime copy.")]
    public bool autoRefreshGenerator = true;

    [Tooltip("Optional: names of methods to try calling on the generator to refresh it after assigning the runtime config (first match wins).")]
    public string[] candidateRefreshMethodNames = new string[]
    {
        "ApplyConfig", "OnConfigChanged", "Regenerate", "RegenerateTerrain", "Refresh", "Generate", "Init", "Initialize", "StartGeneration"
    };

    private void Reset()
    {
        generator = FindFirstObjectByType<ProceduralTerrainGenerator>();
    }

    private void Awake()
    {
        if (generator == null)
            generator = FindFirstObjectByType<ProceduralTerrainGenerator>();

        if (generator == null)
        {
            Debug.LogWarning("[PerSceneTerrainBinder] ProceduralTerrainGenerator not found in scene.");
            return;
        }

        if (configAsset == null)
        {
            Debug.LogWarning("[PerSceneTerrainBinder] configAsset not assigned; generator will keep its existing config.");
            return;
        }

        // Create a runtime copy/instance of the config asset so we don't mutate the original asset
        UnityEngine.Object runtimeCopy;
        try
        {
            runtimeCopy = Instantiate(configAsset);
            runtimeCopy.name = configAsset.name + "_RuntimeCopy";
        }
        catch (Exception ex)
        {
            Debug.LogError($"[PerSceneTerrainBinder] Failed to instantiate config asset: {ex.Message}");
            return;
        }

        Type gType = generator.GetType();
        bool assigned = false;

        // 1) Try a public field or property explicitly named "config"
        var fieldByName = gType.GetField("config", BindingFlags.Public | BindingFlags.Instance);
        if (fieldByName != null)
        {
            if (IsAssignableToField(fieldByName, runtimeCopy))
            {
                fieldByName.SetValue(generator, runtimeCopy);
                assigned = true;
            }
        }

        if (!assigned)
        {
            var propByName = gType.GetProperty("config", BindingFlags.Public | BindingFlags.Instance);
            if (propByName != null && propByName.CanWrite)
            {
                if (IsAssignableToProperty(propByName, runtimeCopy))
                {
                    propByName.SetValue(generator, runtimeCopy, null);
                    assigned = true;
                }
            }
        }

        // 2) Fallback: find any field/property compatible with runtime copy type (public or non-public)
        if (!assigned)
        {
            var fields = gType.GetFields(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)
                              .OrderBy(f => f.Name);
            foreach (var f in fields)
            {
                if (IsAssignableToField(f, runtimeCopy))
                {
                    f.SetValue(generator, runtimeCopy);
                    assigned = true;
                    break;
                }
            }
        }

        if (!assigned)
        {
            var props = gType.GetProperties(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)
                             .Where(p => p.CanWrite)
                             .OrderBy(p => p.Name);
            foreach (var p in props)
            {
                if (IsAssignableToProperty(p, runtimeCopy))
                {
                    p.SetValue(generator, runtimeCopy, null);
                    assigned = true;
                    break;
                }
            }
        }

        if (!assigned)
        {
            Debug.LogError("[PerSceneTerrainBinder] Could not assign runtime config to generator - no compatible field/property found.");
            DestroyImmediate(runtimeCopy);
            return;
        }

        if (autoRefreshGenerator)
            TryRefreshGenerator(generator);

        if (Debug.isDebugBuild || Application.isEditor)
            Debug.Log("[PerSceneTerrainBinder] Applied runtime copy of config to generator (asset left unchanged).");
    }

    private bool IsAssignableToField(FieldInfo f, UnityEngine.Object runtimeCopy)
    {
        if (f == null || runtimeCopy == null) return false;
        // Accept if the field type is assignable from runtime copy type, or if field type is UnityEngine.Object or ScriptableObject base.
        Type fieldType = f.FieldType;
        if (fieldType.IsAssignableFrom(runtimeCopy.GetType())) return true;
        if (typeof(UnityEngine.Object).IsAssignableFrom(fieldType)) return true;
        return false;
    }

    private bool IsAssignableToProperty(PropertyInfo p, UnityEngine.Object runtimeCopy)
    {
        if (p == null || runtimeCopy == null) return false;
        Type propType = p.PropertyType;
        if (propType.IsAssignableFrom(runtimeCopy.GetType())) return true;
        if (typeof(UnityEngine.Object).IsAssignableFrom(propType)) return true;
        return false;
    }

    private void TryRefreshGenerator(ProceduralTerrainGenerator gen)
    {
        if (gen == null) return;
        Type gt = gen.GetType();

        foreach (var name in candidateRefreshMethodNames)
        {
            var mi = gt.GetMethod(name, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic, null, Type.EmptyTypes, null);
            if (mi != null)
            {
                try
                {
                    mi.Invoke(gen, null);
                    if (Debug.isDebugBuild || Application.isEditor)
                        Debug.Log($"[PerSceneTerrainBinder] Invoked generator method '{name}' to refresh after config change.");
                    return;
                }
                catch (Exception ex)
                {
                    Debug.LogWarning($"[PerSceneTerrainBinder] Method '{name}' found but invocation threw: {ex.Message}");
                }
            }
        }

        // No candidate refresh method found; try toggling a commonly used readiness flag if present
        var readyField = gt.GetField("IsInitialTerrainReady", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
        if (readyField != null && readyField.FieldType == typeof(bool))
        {
            try
            {
                readyField.SetValue(gen, false);
                if (Debug.isDebugBuild || Application.isEditor)
                    Debug.Log("[PerSceneTerrainBinder] Cleared generator.IsInitialTerrainReady = false to trigger regeneration if generator checks this flag.");
            }
            catch { /* ignore */ }
        }
    }
}