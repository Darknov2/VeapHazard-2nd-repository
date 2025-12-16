using UnityEngine;

/// <summary>
/// Lightweight component to handle "press E to interact with sign under crosshair".
/// - Attach this to the Player (or Player camera) GameObject.
/// - Configure raycastDistance and layerMask in inspector.
/// - On KeyDown(E) it raycasts from the provided camera and calls Sign.ActivateSign()
///   on the first Sign component hit (prefers Sign component over tag).
/// - This keeps your existing PlayerController untouched.
/// </summary>
[DisallowMultipleComponent]
public class SignInteractor : MonoBehaviour
{
    [Tooltip("Camera used for raycasts. If null, Camera.main will be used.")]
    public Camera cam;

    [Tooltip("Max distance to check for a sign.")]
    public float raycastDistance = 3f;

    [Tooltip("Layer mask for raycast. Default = everything.")]
    public LayerMask raycastLayerMask = ~0;

    [Tooltip("Key used to interact with signs.")]
    public KeyCode interactKey = KeyCode.E;

    // Optional: draw debug ray in Scene view for development
    public bool drawDebugRay = false;
    public Color debugRayColor = Color.cyan;
    public float debugRayDuration = 0.1f;

    void Reset()
    {
        if (cam == null && Camera.main != null)
            cam = Camera.main;
    }

    void Start()
    {
        if (cam == null)
            cam = Camera.main;

        if (cam == null)
            Debug.LogWarning("SignInteractor: No camera assigned and Camera.main is null. Assign a camera in the inspector.");
    }

    void Update()
    {
        if (Input.GetKeyDown(interactKey))
        {
            TryActivateSignUnderCrosshair();
        }
    }

    /// <summary>
    /// Raycast from camera forward and activate the first Sign hit.
    /// Returns true if a sign was activated.
    /// </summary>
    public bool TryActivateSignUnderCrosshair()
    {
        if (cam == null) return false;

        Ray ray = new Ray(cam.transform.position, cam.transform.forward);

        if (drawDebugRay)
            Debug.DrawRay(ray.origin, ray.direction * raycastDistance, debugRayColor, debugRayDuration);

        if (Physics.Raycast(ray, out RaycastHit hit, raycastDistance, raycastLayerMask))
        {
            // Prefer direct Sign component on the collider
            Sign sign = hit.collider.GetComponent<Sign>() ?? hit.collider.GetComponentInParent<Sign>();
            if (sign != null)
            {
                sign.ActivateSign();
                return true;
            }

            // If not found by component, but you rely on tag, attempt tag-based lookup then component
            if (hit.collider.CompareTag("Sign"))
            {
                // Try parent (in case tag sits on a child)
                var parentSign = hit.collider.GetComponentInParent<Sign>();
                if (parentSign != null)
                {
                    parentSign.ActivateSign();
                    return true;
                }
            }
        }

        return false;
    }
}