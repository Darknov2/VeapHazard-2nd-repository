using UnityEngine;
using UnityEngine.AI;

/// <summary>
/// VeaperLeaper (wander + raycast vision + physics jump, stays upright, contact damage)
/// - Wanders using random waypoints.
/// - Uses raycast vision (range + FOV + LOS) to detect the player.
/// - Only faces/targets the player when the player is visible.
/// - Physics jump toward the player (upward + forward impulse), stays upright in air.
/// - NavMeshAgent is disabled during jump and re-enabled on landing.
/// - Deals damage to the player on contact (collision or trigger), with a short cooldown.
/// </summary>
[RequireComponent(typeof(Rigidbody))]
[RequireComponent(typeof(Collider))]
[DisallowMultipleComponent]
public class VeaperLeaper : MonoBehaviour
{
    [Header("Target")]
    public string playerTag = "Player";

    [Header("Vision (raycast)")]
    public float visionRange = 12f;
    [Range(1f, 180f)] public float fovAngle = 90f;
    public float eyeHeight = 1.2f;
    public LayerMask obstacleMask = ~0;

    [Header("Jump")]
    public float jumpDistance = 3f;
    public float jumpCooldown = 0.6f;
    public float jumpUpwardForce = 6f;
    public float jumpForwardForce = 4f;
    public float minAirTime = 0.08f;
    public float maxJumpTime = 2.5f;

    [Header("Grounding")]
    public bool requireGrounded = true;
    public float groundCheckDistance = 0.3f;
    public LayerMask groundMask = ~0;

    [Header("Pursuit (only when player visible)")]
    public bool moveTowardPlayer = true;
    public float moveSpeed = 3f;
    public float stopDistance = 1.25f;

    [Header("Wander")]
    public bool enableWander = true;
    public float wanderRadius = 15f;
    public float wanderInterval = 4f;
    public float wanderMinInterval = 2f;
    public float wanderMaxInterval = 6f;

    [Header("Contact Damage")]
    [Tooltip("Damage dealt to the player when touching them.")]
    public float contactDamage = 10f;
    [Tooltip("Cooldown between successive contact damage applications.")]
    public float damageCooldown = 0.35f;

    // Internals
    private Transform _player;
    private Rigidbody _rb;
    private Collider _col;
    private NavMeshAgent _agent;

    private bool _isJumping;
    private float _lastJumpTime = -999f;
    private float _jumpStartTime;

    private float _feetHeight;
    private float _probeRadius;

    private bool _playerVisible;
    private Vector3 _lastKnownPlayerPos;

    private RigidbodyConstraints _baseConstraints;
    private float _nextWanderTime;

    // Damage state
    private float _lastDamageTime = -999f;

    void Awake()
    {
        _rb = GetComponent<Rigidbody>();
        _col = GetComponent<Collider>();
        _agent = GetComponent<NavMeshAgent>();

        _baseConstraints = _rb.constraints;
        _rb.constraints = RigidbodyConstraints.FreezeRotationX | RigidbodyConstraints.FreezeRotationZ | (_baseConstraints & RigidbodyConstraints.FreezePosition);

        if (_agent != null)
        {
            _agent.autoBraking = false;
            _agent.stoppingDistance = Mathf.Min(stopDistance, 0.75f);
        }

        if (_col != null)
        {
            var b = _col.bounds;
            _feetHeight = b.extents.y + 0.05f;
            _probeRadius = Mathf.Max(0.05f, Mathf.Min(b.extents.x, b.extents.z) * 0.45f);
        }
        else
        {
            _feetHeight = 0.6f;
            _probeRadius = 0.2f;
        }
    }

    void Start()
    {
        TryAssignPlayer();
        ScheduleNextWander();
    }

    void Update()
    {
        if (_player == null) TryAssignPlayer();

        // Visibility
        _playerVisible = CheckPlayerVisible(out Vector3 playerPos);
        if (_playerVisible) _lastKnownPlayerPos = playerPos;

        if (_isJumping)
        {
            KeepUprightYawOnly();
            CheckLanding();
            return;
        }

        if (_playerVisible && _player != null)
        {
            // Face player ONLY when visible
            FaceHorizontal(_lastKnownPlayerPos - transform.position);

            float dist = Vector3.Distance(transform.position, _lastKnownPlayerPos);

            // Pursuit
            if (moveTowardPlayer && dist > stopDistance)
            {
                Vector3 dir = _lastKnownPlayerPos - transform.position; dir.y = 0f;
                if (dir.sqrMagnitude > 0.0001f)
                {
                    Vector3 step = dir.normalized * moveSpeed * Time.deltaTime;
                    if (_agent != null && _agent.enabled) _agent.Move(step);
                    else transform.position += step;
                }
            }

            // Jump
            if (dist <= jumpDistance)
                TryJumpTowards(_lastKnownPlayerPos);
        }
        else
        {
            // Player not visible: wander (no facing)
            if (enableWander && Time.time >= _nextWanderTime)
            {
                WanderToRandomPoint();
                ScheduleNextWander();
            }
        }
    }

    // Vision: FOV + unobstructed ray to player collider center
    private bool CheckPlayerVisible(out Vector3 playerPos)
    {
        playerPos = Vector3.zero;
        if (_player == null) return false;

        Vector3 eye = transform.position + Vector3.up * eyeHeight;

        Vector3 target = _player.position;
        var playerCol = _player.GetComponent<Collider>();
        if (playerCol != null) target = playerCol.bounds.center;

        Vector3 toTarget = target - eye;
        float dist = toTarget.magnitude;
        if (dist > visionRange) return false;

        Vector3 forward = transform.forward; forward.y = 0f;
        Vector3 flatToTarget = toTarget; flatToTarget.y = 0f;
        if (flatToTarget.sqrMagnitude < 0.0001f) return false;

        float angle = Vector3.Angle(forward.normalized, flatToTarget.normalized);
        if (angle > fovAngle * 0.5f) return false;

        if (Physics.Raycast(eye, toTarget.normalized, out RaycastHit hit, dist, obstacleMask, QueryTriggerInteraction.Ignore))
        {
            if (hit.transform != _player && hit.transform.root != _player)
                return false;
        }

        playerPos = target;
        return true;
    }

    private void TryAssignPlayer()
    {
        if (!string.IsNullOrEmpty(playerTag))
        {
            var go = GameObject.FindWithTag(playerTag);
            if (go != null) _player = go.transform;
        }
    }

    private void FaceHorizontal(Vector3 toTarget)
    {
        Vector3 flat = toTarget; flat.y = 0f;
        if (flat.sqrMagnitude < 0.0001f) return;
        Quaternion target = Quaternion.LookRotation(flat.normalized, Vector3.up);
        transform.rotation = Quaternion.Slerp(transform.rotation, target, 12f * Time.deltaTime);
    }

    private void KeepUprightYawOnly()
    {
        Vector3 fwd = transform.forward; fwd.y = 0f;
        if (fwd.sqrMagnitude < 0.0001f) fwd = Vector3.forward;
        transform.rotation = Quaternion.LookRotation(fwd.normalized, Vector3.up);
        _rb.angularVelocity = Vector3.zero;
    }

    private void TryJumpTowards(Vector3 targetPos)
    {
        // Refresh cooldown if close to player to avoid sticky pause
        float distToPlayer = Vector3.Distance(transform.position, targetPos);
        if (distToPlayer <= 2.0f)
            _lastJumpTime = Time.time - jumpCooldown;

        if (Time.time - _lastJumpTime < jumpCooldown) return;
        if (requireGrounded && !IsGrounded()) return;

        _isJumping = true;
        _jumpStartTime = Time.time;
        _lastJumpTime = Time.time;

        if (_agent != null && _agent.enabled) _agent.enabled = false;

        Vector3 vel = _rb.linearVelocity; vel.y = 0f;
        _rb.linearVelocity = vel;

        Vector3 forward = targetPos - transform.position;
        forward.y = 0f;
        if (forward.sqrMagnitude < 0.0001f) forward = transform.forward;
        forward = forward.normalized;

        _rb.constraints = RigidbodyConstraints.FreezeRotationX | RigidbodyConstraints.FreezeRotationZ | (_rb.constraints & RigidbodyConstraints.FreezePosition);

        Vector3 impulse = Vector3.up * Mathf.Max(0f, jumpUpwardForce)
                        + forward     * Mathf.Max(0f, jumpForwardForce);
        _rb.AddForce(impulse, ForceMode.Impulse);

        KeepUprightYawOnly();
    }

    private void CheckLanding()
    {
        if (Time.time - _jumpStartTime > maxJumpTime)
        {
            EndJump();
            return;
        }

        if (Time.time - _jumpStartTime < minAirTime)
            return;

        if (IsGrounded())
            EndJump();
    }

    private void EndJump()
    {
        _isJumping = false;
        _rb.constraints = RigidbodyConstraints.FreezeRotationX | RigidbodyConstraints.FreezeRotationZ | (_baseConstraints & RigidbodyConstraints.FreezePosition);

        if (_agent != null)
        {
            if (NavMesh.SamplePosition(transform.position, out NavMeshHit hit, 2.0f, NavMesh.AllAreas))
                _agent.Warp(hit.position);
            else
                _agent.nextPosition = transform.position;

            _agent.enabled = true;
            _agent.isStopped = false;
            _agent.ResetPath();
        }

        KeepUprightYawOnly();

        if (_player != null && Vector3.Distance(transform.position, _player.position) <= 2.0f)
            _lastJumpTime = Time.time - jumpCooldown;
    }

    private bool IsGrounded()
    {
        Vector3 origin = transform.position + Vector3.up * (_feetHeight + 0.02f);
        float distance = _feetHeight + groundCheckDistance;
        return Physics.SphereCast(origin, _probeRadius, Vector3.down, out _, distance, groundMask, QueryTriggerInteraction.Ignore);
    }

    // Wander helpers
    private void ScheduleNextWander()
    {
        if (!enableWander) return;
        float interval = wanderInterval > 0f ? wanderInterval : Random.Range(wanderMinInterval, wanderMaxInterval);
        _nextWanderTime = Time.time + Mathf.Max(0.3f, interval);
    }

    private void WanderToRandomPoint()
    {
        Vector3 randomDir = Random.insideUnitSphere;
        randomDir.y = 0f;
        randomDir = randomDir.normalized * wanderRadius;

        Vector3 dest = transform.position + randomDir;

        if (_agent != null)
        {
            if (NavMesh.SamplePosition(dest, out NavMeshHit hit, wanderRadius, NavMesh.AllAreas))
                _agent.SetDestination(hit.position);
            else
                _agent.SetDestination(dest);
        }
        else
        {
            Vector3 step = dest - transform.position;
            step.y = 0f;
            if (step.sqrMagnitude > 0.01f)
                transform.position += step.normalized * moveSpeed * Time.deltaTime;
        }
    }

    // Contact damage: trigger or collision
    void OnTriggerEnter(Collider other) { TryApplyContactDamage(other.gameObject); }
    void OnTriggerStay(Collider other)  { TryApplyContactDamage(other.gameObject); }
    void OnCollisionEnter(Collision collision) { TryApplyContactDamage(collision.gameObject); }
    void OnCollisionStay(Collision collision)  { TryApplyContactDamage(collision.gameObject); }

    private void TryApplyContactDamage(GameObject other)
    {
        if (Time.time - _lastDamageTime < damageCooldown) return;
        if (!other.CompareTag(playerTag)) return;

        var health = other.GetComponent<PlayerHealth>();
        if (health == null)
            health = other.GetComponentInParent<PlayerHealth>();
        if (health == null) return;

        health.Damage(contactDamage);
        _lastDamageTime = Time.time;
    }

#if UNITY_EDITOR
    void OnDrawGizmosSelected()
    {
        // Vision cone debug
        Gizmos.color = new Color(1f, 0.8f, 0.2f, 0.25f);
        Vector3 origin = transform.position + Vector3.up * eyeHeight;
        Vector3 left = Quaternion.Euler(0f, -fovAngle * 0.5f, 0f) * transform.forward;
        Vector3 right = Quaternion.Euler(0f, +fovAngle * 0.5f, 0f) * transform.forward;
        Gizmos.DrawLine(origin, origin + left.normalized * visionRange);
        Gizmos.DrawLine(origin, origin + right.normalized * visionRange);
        Gizmos.DrawWireSphere(origin, 0.1f);

        // Wander radius
        Gizmos.color = new Color(0.3f, 0.9f, 1f, 0.25f);
        Gizmos.DrawWireSphere(transform.position, wanderRadius);

        // Jump trigger radius
        Gizmos.color = new Color(1f, 0.3f, 0.3f, 0.35f);
        Gizmos.DrawWireSphere(transform.position, jumpDistance);
    }
#endif
}