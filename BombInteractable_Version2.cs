using UnityEngine;

/// <summary>
/// Bomb object that explodes when the player is looking at it and presses E.
/// When exploded, objects with non-convex MeshColliders get their collider swapped for a BoxCollider,
/// their Rigidbody is set to non-kinematic, and an explosion force is applied.
/// </summary>
public class BombRaycastInteractable : MonoBehaviour
{
    [Header("Explosion Settings")]
    public float explosionRadius = 8f;
    public float explosionForce = 800f;
    public float upwardsModifier = 1.0f;

    [Header("Interaction Settings")]
    public KeyCode interactKey = KeyCode.E;

    // Set this from your player script when raycast hits the bomb
    private bool isPlayerLooking = false;

    /// <summary>
    /// Call this from your player script to indicate if the player is looking at the bomb via raycast
    /// </summary>
    public void SetPlayerLooking(bool looking)
    {
        isPlayerLooking = looking;
    }

    void Update()
    {
        if (isPlayerLooking && Input.GetKeyDown(interactKey))
        {
            Explode();
        }
    }

    void Explode()
    {
        Collider[] colliders = Physics.OverlapSphere(transform.position, explosionRadius);
        foreach (Collider col in colliders)
        {
            Rigidbody rb = col.attachedRigidbody;
            if (rb != null)
            {
                // Check for non-convex MeshCollider
                MeshCollider meshCol = col as MeshCollider;
                if (meshCol != null && !meshCol.convex)
                {
                    // Remove the MeshCollider
                    Destroy(meshCol);

                    // Add a BoxCollider as a substitute
                    BoxCollider box = col.gameObject.AddComponent<BoxCollider>();

                    // Optionally, set box size to match original bounds
                    MeshFilter mf = col.GetComponent<MeshFilter>();
                    if (mf != null && mf.sharedMesh != null)
                    {
                        box.center = mf.sharedMesh.bounds.center;
                        box.size = mf.sharedMesh.bounds.size;
                    }
                }

                rb.isKinematic = false; // Enable physics
                rb.AddExplosionForce(explosionForce, transform.position, explosionRadius, upwardsModifier, ForceMode.Impulse);
            }
        }
        Destroy(gameObject);
    }
}