using UnityEngine;

/// <summary>
/// Replacement terrain editing input helper — performs brush edits where the player is looking.
/// - Use this instead of the Player's old editing code (Player no longer applies edits).
/// - Left mouse: remove (negative strength). Right mouse: add (positive strength).
/// - Uses ScreenPointToRay from camera center (with optional small offsets) so edits appear where you look.
/// </summary>
[DisallowMultipleComponent]
public class InputTerrainBrush : MonoBehaviour
{
    [Header("References")]
    public TerrainModificationManager modificationManager; // auto-find if null
    public Camera playerCamera; // optional, falls back to Camera.main

    [Header("Brush")]
    public float brushRadius = 2f;
    [Tooltip("Absolute magnitude of the brush strength. Left-click will apply -strength (remove), right-click +strength (add).")]
    public float brushStrength = 10f;
    public float defaultFallbackDistance = 5f;

    [Header("Aim offset (screen normalized, -0.5..0.5)")]
    [Range(-0.5f, 0.5f)] public float aimXOffset = 0f;
    [Range(-0.5f, 0.5f)] public float aimYOffset = 0f;

    [Header("Raycast")]
    public LayerMask raycastMask = ~0;
    public float maxRayDistance = 100f;

    private void Awake()
    {
        if (playerCamera == null && Camera.main != null) playerCamera = Camera.main;
        if (modificationManager == null)
            modificationManager = Object.FindFirstObjectByType<TerrainModificationManager>();
    }

    private void Update()
    {
        // Left click = remove, Right click = add
        if (Input.GetMouseButtonDown(0))
        {
            ApplyBrushAtAim(-Mathf.Abs(brushStrength));
        }
        else if (Input.GetMouseButtonDown(1))
        {
            ApplyBrushAtAim(Mathf.Abs(brushStrength));
        }
    }

    private void ApplyBrushAtAim(float strength)
    {
        if (modificationManager == null)
        {
            Debug.LogWarning("InputTerrainBrush: TerrainModificationManager not assigned/found.");
            return;
        }

        Camera cam = playerCamera != null ? playerCamera : Camera.main;
        if (cam == null)
        {
            Debug.LogWarning("InputTerrainBrush: No camera available for aim ray.");
            return;
        }

        // Build screen point near center with optional offsets
        float sx = Mathf.Clamp01(0.5f + aimXOffset);
        float sy = Mathf.Clamp01(0.5f + aimYOffset);
        Vector3 sp = new Vector3(cam.pixelWidth * sx, cam.pixelHeight * sy, 0f);

        Ray ray = cam.ScreenPointToRay(sp);
        RaycastHit hit;
        Vector3 worldPos;

        if (Physics.Raycast(ray, out hit, maxRayDistance, raycastMask))
        {
            // Use surface hit point when available
            worldPos = hit.point;
        }
        else
        {
            // fallback to a point in front of camera
            worldPos = ray.origin + ray.direction.normalized * defaultFallbackDistance;
        }

        modificationManager.ApplySphere(worldPos, brushRadius, strength);
    }

#if UNITY_EDITOR
    private void OnDrawGizmosSelected()
    {
        // Draw fallback preview in editor when selected
        Camera cam = playerCamera != null ? playerCamera : Camera.main;
        if (cam == null) return;

        float sx = Mathf.Clamp01(0.5f + aimXOffset);
        float sy = Mathf.Clamp01(0.5f + aimYOffset);
        Vector3 sp = new Vector3(cam.pixelWidth * sx, cam.pixelHeight * sy, 0f);
        Ray ray = cam.ScreenPointToRay(sp);

        if (Physics.Raycast(ray, out RaycastHit h, maxRayDistance, raycastMask))
        {
            Gizmos.color = new Color(0f, 1f, 0f, 0.35f);
            Gizmos.DrawSphere(h.point, brushRadius);
            Gizmos.color = Color.green;
            Gizmos.DrawWireSphere(h.point, brushRadius);
        }
        else
        {
            Vector3 p = ray.origin + ray.direction.normalized * defaultFallbackDistance;
            Gizmos.color = new Color(1f, 1f, 0f, 0.15f);
            Gizmos.DrawSphere(p, brushRadius);
            Gizmos.color = Color.yellow;
            Gizmos.DrawWireSphere(p, brushRadius);
        }
    }
#endif
}