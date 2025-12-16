using System.Collections;
using UnityEngine;

/// <summary>
/// DoorToggle with pivot-aware rotation
/// - Rotates the door around a pivot point's origin axis (default pivot = this.transform).
/// - Supports aim-from-player (player.forward) and proximity interaction.
/// - Smoothly interpolates between closed (angle=0) and open (angle=openAngle) states,
///   always computing transform.position/rotation from the stored base (closed) transform
///   so the rotation pivots exactly around the pivot origin without drift.
/// 
/// Setup:
/// - Attach to the GameObject you want to rotate OR its visual root. For best results,
///   place this on the hinge/pivot object so its transform.position is the hinge origin,
///   or assign an explicit pivot Transform in the inspector.
/// - If pivot is assigned, the door will rotate around pivot.position using pivot.TransformDirection(rotationAxis).
/// - If you want aim-interaction, ensure the player GameObject is tagged (playerTag).
/// - If using proximity trigger, add a trigger collider (IsTrigger = true) on this object or a child.
/// </summary>
[DisallowMultipleComponent]
public class DoorToggle : MonoBehaviour
{
    [Header("Interaction")]
    public KeyCode interactKey = KeyCode.E;
    public string playerTag = "Player";

    [Tooltip("Allow interacting by aiming (player forward) and pressing the interact key.")]
    public bool allowAimInteraction = true;
    [Tooltip("Max distance to consider an aim interaction valid (raycast length from player).")]
    public float aimDistance = 4f;

    [Tooltip("Allow proximity interaction (trigger or distance check).")]
    public bool allowProximityInteraction = true;
    [Tooltip("If you don't provide a trigger collider or player has no Rigidbody, use distance fallback.")]
    public bool useDistanceFallbackIfNoTriggerEvents = true;
    [Tooltip("Distance used for proximity distance fallback (world units).")]
    public float proximityDistance = 2.0f;

    [Header("Pivot / Rotation")]
    [Tooltip("Optional pivot transform to rotate around. If null, the door's own transform is used as pivot origin.")]
    public Transform pivot;
    [Tooltip("Axis in pivot's local space to rotate around (e.g., Vector3.up).")]
    public Vector3 rotationAxis = Vector3.up;
    [Tooltip("Angle in degrees to rotate when opening (positive uses right-hand rule around axis).")]
    public float openAngle = 90f;

    [Header("Motion")]
    [Tooltip("Seconds to complete the rotation.")]
    public float duration = 0.4f;
    public bool allowInstantToggle = true;

    [Header("Debug")]
    public bool verbose = false;

    // internals
    private Transform _playerTransform;
    private bool _playerNearby = false;
    private bool _isOpen = false;
    private Coroutine _rotRoutine = null;
    private bool _triggerEventsFired = false;

    // Base (closed) state used as reference for all rotations to avoid drift
    private Vector3 _baseRelPos;        // transform.position - pivot.position (at closed)
    private Quaternion _baseRotation;   // transform.rotation (at closed)
    private Vector3 _axisWorld;         // normalized world-space axis (computed from pivot)

    // Current logical angle (0..openAngle)
    private float _currentAngle = 0f;

    void Awake()
    {
        if (pivot == null) pivot = transform;

        // compute base references (closed state)
        _baseRelPos = transform.position - pivot.position;
        _baseRotation = transform.rotation;

        // axis in world space (pivot local -> world)
        _axisWorld = pivot.TransformDirection(rotationAxis.normalized);
    }

    void Reset()
    {
        rotationAxis = Vector3.up;
        duration = 0.4f;
        proximityDistance = 2f;
        aimDistance = 4f;
    }

    void Update()
    {
        EnsurePlayerTransform();

        // Distance fallback: if no trigger events fired and fallback is enabled
        if (allowProximityInteraction && useDistanceFallbackIfNoTriggerEvents && !_triggerEventsFired && _playerTransform != null)
        {
            float d = Vector3.Distance(_playerTransform.position, transform.position);
            _playerNearby = d <= proximityDistance;
        }

        // Aim-based interaction (from player forward)
        if (allowAimInteraction && _playerTransform != null && Input.GetKeyDown(interactKey))
        {
            if (IsAimedAtDoorByPlayer())
            {
                ToggleSmooth();
                return;
            }
        }

        // Proximity/key interaction
        if (allowProximityInteraction && _playerNearby && Input.GetKeyDown(interactKey))
        {
            ToggleSmooth();
        }
    }

    private void EnsurePlayerTransform()
    {
        if (_playerTransform != null) return;
        if (!string.IsNullOrEmpty(playerTag))
        {
            var go = GameObject.FindWithTag(playerTag);
            if (go != null) _playerTransform = go.transform;
        }
    }

    // Raycast from the player's eyes along their forward direction to detect door hit
    private bool IsAimedAtDoorByPlayer()
    {
        if (_playerTransform == null) return false;
        Vector3 origin = _playerTransform.position + Vector3.up * 0.9f; // small eye offset
        Vector3 dir = _playerTransform.forward;
        if (Physics.Raycast(origin, dir, out RaycastHit hit, aimDistance, ~0, QueryTriggerInteraction.Ignore))
        {
            if (hit.collider != null && (hit.collider.transform == transform || hit.collider.transform.IsChildOf(transform)))
                return true;
        }
        return false;
    }

    // Public API: toggle with smooth animation
    public void ToggleSmooth()
    {
        float target = _isOpen ? 0f : openAngle;
        StartAngleRotation(_currentAngle, target, duration);
        _isOpen = !_isOpen;
        if (verbose) Debug.Log($"DoorToggle: ToggleSmooth -> isOpen={_isOpen}");
    }

    // Public API: toggle instantly
    public void ToggleInstant()
    {
        if (!allowInstantToggle) return;
        float target = _isOpen ? 0f : openAngle;
        ApplyAngleImmediate(target);
        _isOpen = !_isOpen;
        if (verbose) Debug.Log($"DoorToggle: ToggleInstant -> isOpen={_isOpen}");
    }

    // Public generic Interact method (can be called by SignInteractor or other systems)
    public void Interact()
    {
        ToggleSmooth();
    }

    // Starts smooth rotation from 'fromAngle' to 'toAngle' over 'time'
    private void StartAngleRotation(float fromAngle, float toAngle, float time)
    {
        StopRotationCoroutineIfAny();
        _rotRoutine = StartCoroutine(RotateAngleRoutine(fromAngle, toAngle, Mathf.Max(0f, time)));
    }

    private void StopRotationCoroutineIfAny()
    {
        if (_rotRoutine != null)
        {
            StopCoroutine(_rotRoutine);
            _rotRoutine = null;
        }
    }

    private IEnumerator RotateAngleRoutine(float from, float to, float time)
    {
        float t = 0f;
        _currentAngle = from;
        if (time <= 0f)
        {
            ApplyAngleImmediate(to);
            yield break;
        }

        while (t < 1f)
        {
            t += Time.deltaTime / time;
            float a = Mathf.Lerp(from, to, Mathf.SmoothStep(0f, 1f, t));
            ApplyAngleImmediate(a);
            yield return null;
        }
        ApplyAngleImmediate(to);
        _rotRoutine = null;
    }

    // Apply absolute angle (degrees) relative to closed/base state.
    // This computes new position & rotation by rotating the base (closed) pose around pivot origin.
    private void ApplyAngleImmediate(float angleDeg)
    {
        _currentAngle = angleDeg;

        // Build rotation delta around world axis
        Quaternion delta = Quaternion.AngleAxis(angleDeg, _axisWorld);

        // Rotate base relative position and apply
        Vector3 rotatedRel = delta * _baseRelPos;
        transform.position = pivot.position + rotatedRel;

        // Rotate base rotation and apply
        transform.rotation = delta * _baseRotation;
    }

    // ---- Trigger callbacks for proximity ----
    private void OnTriggerEnter(Collider other)
    {
        _triggerEventsFired = true;
        if (!allowProximityInteraction) return;
        if (other.CompareTag(playerTag))
        {
            _playerNearby = true;
            if (verbose) Debug.Log("DoorToggle: Player entered trigger.");
        }
        else
        {
            var cc = other.GetComponent<CharacterController>();
            if (cc != null)
            {
                _playerNearby = true;
                if (verbose) Debug.Log("DoorToggle: CharacterController entered trigger.");
            }
        }
    }

    private void OnTriggerExit(Collider other)
    {
        if (!allowProximityInteraction) return;
        if (other.CompareTag(playerTag))
        {
            _playerNearby = false;
            if (verbose) Debug.Log("DoorToggle: Player exited trigger.");
        }
        else
        {
            var cc = other.GetComponent<CharacterController>();
            if (cc != null)
            {
                _playerNearby = false;
                if (verbose) Debug.Log("DoorToggle: CharacterController exited trigger.");
            }
        }
    }

#if UNITY_EDITOR
    void OnDrawGizmosSelected()
    {
        if (pivot == null) pivot = transform;
        Gizmos.color = Color.yellow;
        Vector3 axisWorld = pivot.TransformDirection(rotationAxis.normalized);
        Gizmos.DrawLine(pivot.position, pivot.position + axisWorld * 0.6f);

        Gizmos.color = Color.cyan;
        Gizmos.DrawWireSphere(transform.position, proximityDistance);

        Gizmos.color = Color.magenta;
        if (_playerTransform != null)
            Gizmos.DrawLine(_playerTransform.position + Vector3.up * 0.9f, _playerTransform.position + Vector3.up * 0.9f + _playerTransform.forward * aimDistance);
    }
#endif
}