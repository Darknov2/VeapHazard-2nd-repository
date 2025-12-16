#if UNITY_EDITOR
using UnityEditor;
using UnityEngine;

/// <summary>
/// Editor convenience for regenerating terrain.
/// </summary>
[CustomEditor(typeof(ProceduralTerrainGenerator))]
public class EditorGenerateButton : Editor
{
    public override void OnInspectorGUI()
    {
        DrawDefaultInspector();
        var gen = (ProceduralTerrainGenerator)target;
        
    }
}
#endif