using UnityEngine;
using UnityEngine.Events;

public class PlayerHealth : MonoBehaviour
{
    [Header("Health")]
    public float maxHealth = 100f;
    public float currentHealth = 100f;

    [Header("Invulnerability")]
    public bool enableIFrames = true;
    public float invulnerableSeconds = 0.2f;

    [Header("Events")]
    public UnityEvent onDamaged;
    public UnityEvent onHealed;
    public UnityEvent onDied;

    // References used for game-over
    [Header("Game Over")]
    [Tooltip("A component that can disable player input/movement when dead.")]
    public MonoBehaviour playerControllerToDisable; // e.g., your movement script
    [Tooltip("UI fader to turn screen black on death.")]
    public ScreenFader screenFader;
    [Tooltip("Fade duration to black.")]
    public float fadeDuration = 0.8f;

    private float _lastDamageTime = -999f;
    private bool _isDead;

    void Awake()
    {
        currentHealth = Mathf.Clamp(currentHealth, 0f, maxHealth);
    }

    public bool IsAlive => !_isDead;

    public void Damage(float amount)
    {
        if (!IsAlive) return;
        if (amount <= 0f) return;

        if (enableIFrames && Time.time - _lastDamageTime < invulnerableSeconds)
            return;

        _lastDamageTime = Time.time;
        currentHealth = Mathf.Max(0f, currentHealth - amount);
        onDamaged?.Invoke();

        if (currentHealth <= 0f)
            Die();
    }

    public void Heal(float amount)
    {
        if (!IsAlive) return;
        if (amount <= 0f) return;

        float before = currentHealth;
        currentHealth = Mathf.Min(maxHealth, currentHealth + amount);
        if (currentHealth > before)
            onHealed?.Invoke();
    }

    public void SetMaxHealth(float newMax, bool refill = true)
    {
        maxHealth = Mathf.Max(1f, newMax);
        currentHealth = Mathf.Clamp(currentHealth, 0f, maxHealth);
        if (refill) currentHealth = maxHealth;
    }

    public float GetHealthPercent()
    {
        if (maxHealth <= 0f) return 0f;
        return currentHealth / maxHealth;
    }

    private void Die()
    {
        if (_isDead) return;
        _isDead = true;

        // Disable player movement/input
        if (playerControllerToDisable != null)
            playerControllerToDisable.enabled = false;

        // Optional: also freeze a Rigidbody or CharacterController if you use one
        var rb = GetComponent<Rigidbody>();
        if (rb != null)
        {
            rb.linearVelocity = Vector3.zero;
            rb.angularVelocity = Vector3.zero;
        }

        // Fade to black
        if (screenFader != null)
            screenFader.FadeToBlack(fadeDuration);

        onDied?.Invoke();
    }
}