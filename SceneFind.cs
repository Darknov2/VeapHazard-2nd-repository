using System;
using UnityEngine;

/// <summary>
/// Compatibility wrapper for Unity's FindObjectOfType/FindObjectsOfType APIs.
/// Uses newer APIs (FindFirstObjectByType/FindAnyObjectByType/FindObjectsByType) on Unity 2023.1+
/// and falls back to deprecated APIs on older versions, suppressing warnings inside the wrapper.
/// </summary>
public static class SceneFind
{
    /// <summary>
    /// Finds the first active loaded object of the specified type.
    /// On Unity 2023.1+, uses FindFirstObjectByType (deterministic order).
    /// On older versions, falls back to FindObjectOfType.
    /// </summary>
    public static T First<T>() where T : UnityEngine.Object
    {
#if UNITY_2023_1_OR_NEWER
        return UnityEngine.Object.FindFirstObjectByType<T>();
#else
#pragma warning disable CS0618 // Type or member is obsolete
        return UnityEngine.Object.FindObjectOfType<T>();
#pragma warning restore CS0618
#endif
    }

    /// <summary>
    /// Finds the first active loaded object of the specified type (non-generic overload).
    /// On Unity 2023.1+, uses FindFirstObjectByType.
    /// On older versions, falls back to FindObjectOfType.
    /// </summary>
    public static UnityEngine.Object First(Type type)
    {
#if UNITY_2023_1_OR_NEWER
        return UnityEngine.Object.FindFirstObjectByType(type);
#else
#pragma warning disable CS0618 // Type or member is obsolete
        return UnityEngine.Object.FindObjectOfType(type);
#pragma warning restore CS0618
#endif
    }

    /// <summary>
    /// Finds any active loaded object of the specified type (non-deterministic order).
    /// On Unity 2023.1+, uses FindAnyObjectByType (faster than First).
    /// On older versions, falls back to FindObjectOfType.
    /// </summary>
    public static T Any<T>() where T : UnityEngine.Object
    {
#if UNITY_2023_1_OR_NEWER
        return UnityEngine.Object.FindAnyObjectByType<T>();
#else
#pragma warning disable CS0618 // Type or member is obsolete
        return UnityEngine.Object.FindObjectOfType<T>();
#pragma warning restore CS0618
#endif
    }

    /// <summary>
    /// Finds all loaded objects of the specified type.
    /// On Unity 2023.1+, uses FindObjectsByType with appropriate sort mode.
    /// On older versions, falls back to FindObjectsOfType.
    /// </summary>
    /// <param name="includeInactive">Whether to include inactive objects in the search.</param>
    public static T[] All<T>(bool includeInactive = false) where T : UnityEngine.Object
    {
#if UNITY_2023_1_OR_NEWER
        var sortMode = UnityEngine.FindObjectsSortMode.None;
        var inactiveMode = includeInactive ? UnityEngine.FindObjectsInactive.Include : UnityEngine.FindObjectsInactive.Exclude;
        return UnityEngine.Object.FindObjectsByType<T>(inactiveMode, sortMode);
#else
#pragma warning disable CS0618 // Type or member is obsolete
        return UnityEngine.Object.FindObjectsOfType<T>(includeInactive);
#pragma warning restore CS0618
#endif
    }
}
