using UnityEngine;
using UnityEngine.AI;

[RequireComponent(typeof(CharacterController))]
public class PlayerController : MonoBehaviour
{
    [Header("Movement Settings")]
    public float moveSpeed = 5f;
    public float sprintSpeed = 10f;
    public float gravity = -9.81f;
    public float jumpHeight = 2f;

    [Header("Camera Settings")]
    public Transform cameraTransform;
    public float lookSpeed = 2.0f;
    public float lookXLimit = 45.0f;

    [Header("Raycast Settings")]
    public float raycastDistance = 3f;
    public LayerMask raycastLayerMask = ~0;

    [Header("Interaction")]
    public bool interactionVerboseLogs = true;

    [Header("Physics Push")]
    public float pushForce = 5f;

    [Header("Build Mode")]
    public bool buildMode = false;

    [Header("References")]
    [Tooltip("Optional: assign the PlayerInteractor that owns the center-screen raycast. If null this script falls back to its own Physics.Raycast.")]
    public PlayerInteractor playerInteractor;

    // ---- Removed legacy terrain-edit fields and methods (use InputTerrainBrush instead) ----

    private CharacterController characterController;
    private Vector3 velocity;
    private bool isGrounded;
    private float rotationX = 0;

    void Start()
    {
        characterController = GetComponent<CharacterController>();
        if (cameraTransform == null && Camera.main != null)
            cameraTransform = Camera.main.transform;

        // try auto-assigning the interactor if not set
        if (playerInteractor == null)
        {
            playerInteractor = GetComponent<PlayerInteractor>() ?? GetComponentInChildren<PlayerInteractor>() ?? FindObjectOfType<PlayerInteractor>();
        }

        Cursor.lockState = CursorLockMode.Locked;
        Cursor.visible = false;
    }

    void Update()
    {
        HandleMovement();

        HandleDestroyObject();
        TryPushObjects();

        // NOTE: Terrain editing removed from Player; attach InputTerrainBrush to a manager or player GameObject instead.
    }

    private void HandleMovement()
    {
        isGrounded = characterController.isGrounded;
        if (isGrounded && velocity.y < 0) velocity.y = -2f;

        float currentSpeed = Input.GetKey(KeyCode.LeftShift) ? sprintSpeed : moveSpeed;

        float moveX = Input.GetAxis("Horizontal");
        float moveZ = Input.GetAxis("Vertical");
        Vector3 move = transform.right * moveX + transform.forward * moveZ;
        characterController.Move(move * currentSpeed * Time.deltaTime);

        if (Input.GetButtonDown("Jump") && isGrounded)
            velocity.y = Mathf.Sqrt(jumpHeight * -2f * gravity);

        velocity.y += gravity * Time.deltaTime;
        characterController.Move(velocity * Time.deltaTime);

        float mouseX = Input.GetAxis("Mouse X") * lookSpeed;
        float mouseY = Input.GetAxis("Mouse Y") * lookSpeed;

        rotationX -= mouseY;
        rotationX = Mathf.Clamp(rotationX, -lookXLimit, lookXLimit);

        if (cameraTransform != null)
            cameraTransform.localRotation = Quaternion.Euler(rotationX, 0, 0);
        transform.Rotate(Vector3.up * mouseX);
    }



    private void HandleDestroyObject()
    {
        if (Input.GetKeyDown(KeyCode.E))
        {
            RaycastHit hit;
            bool hasHit = false;

            // Use PlayerInteractor's raycast when available; otherwise fall back to local Physics.Raycast
            if (playerInteractor != null)
            {
                hasHit = playerInteractor.RaycastCenter(out hit, raycastDistance, raycastLayerMask);
            }
            else
            {
                if (cameraTransform == null && Camera.main != null) cameraTransform = Camera.main.transform;
                hasHit = Physics.Raycast(cameraTransform.position, cameraTransform.forward, out hit, raycastDistance, raycastLayerMask);
            }

            if (hasHit)
            {
                GameObject targetObject = hit.collider.gameObject;

                if (targetObject.CompareTag("Destroyable"))
                {
                    Transform parent = targetObject.transform.parent;
                    if (parent != null && parent.CompareTag("Destroyable"))
                    {
                        if (interactionVerboseLogs) Debug.Log($"Destroying child: {targetObject.name} of parent: {parent.name}");
                        Destroy(targetObject);
                    }
                    else
                    {
                        if (interactionVerboseLogs) Debug.Log($"Destroying object: {targetObject.name}");
                        Destroy(targetObject);
                    }
                }
            }
        }
    }

    private void TryPushObjects()
    {
        // Example push behaviour: raycast forward and apply impulse to rigidbodies
        if (Input.GetMouseButtonDown(0))
        {
            RaycastHit hit;
            bool hasHit = false;
            if (playerInteractor != null)
                hasHit = playerInteractor.RaycastCenter(out hit, raycastDistance, raycastLayerMask);
            else
                hasHit = Physics.Raycast(cameraTransform.position, cameraTransform.forward, out hit, raycastDistance, raycastLayerMask);

            if (hasHit && hit.rigidbody != null)
            {
                if (!hit.rigidbody.isKinematic)
                {
                    Vector3 dir = (hit.point - transform.position).normalized;
                    hit.rigidbody.AddForce(dir * pushForce, ForceMode.Impulse);
                }
            }
        }
    }
}
