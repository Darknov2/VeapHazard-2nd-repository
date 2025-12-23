using UnityEngine;
using UnityEngine.UI;

[RequireComponent(typeof(Canvas))]
public class PlayerHealthBarUI : MonoBehaviour
{
    [Header("References")]
    public PlayerHealth playerHealth;
    [Tooltip("Slider representing health percent (0..1). Use a filled Image if preferred.")]
    public Slider healthSlider;
    [Tooltip("Optional text to show numeric health.")]
    public Text healthText;
    [Tooltip("Optional gradient to color the fill by health percent.")]
    public Gradient colorByPercent;
    [Tooltip("Image whose color will be set by gradient (usually Slider Fill).")]
    public Image fillImage;

    [Header("Follow target (optional)")]
    [Tooltip("If set, health bar will follow this transform (e.g., for world-space canvases).")]
    public Transform followTarget;
    public Vector3 followOffset = new Vector3(0f, 2f, 0f);

    void Reset()
    {
        playerHealth = SceneFind.First<PlayerHealth>();
        healthSlider = GetComponentInChildren<Slider>();
        healthText = GetComponentInChildren<Text>();
        if (healthSlider != null && healthSlider.maxValue != 1f)
            healthSlider.maxValue = 1f; // percent mode
    }

    void Start()
    {
        if (playerHealth == null)
            playerHealth = SceneFind.First<PlayerHealth>();

        // Hook into events to refresh instantly
        if (playerHealth != null)
        {
            playerHealth.onDamaged.AddListener(Refresh);
            playerHealth.onHealed.AddListener(Refresh);
            playerHealth.onDied.AddListener(Refresh);
        }
        Refresh();
    }

    void Update()
    {
        // Smooth follow in world-space
        if (followTarget != null && GetComponent<Canvas>().renderMode == RenderMode.WorldSpace)
        {
            transform.position = followTarget.position + followOffset;
            transform.rotation = Quaternion.LookRotation(transform.position - Camera.main.transform.position, Vector3.up);
        }
    }

    public void Refresh()
    {
        if (playerHealth == null) return;
        float percent = playerHealth.GetHealthPercent();

        if (healthSlider != null)
            healthSlider.value = percent;

        if (healthText != null)
            healthText.text = $"{Mathf.RoundToInt(playerHealth.currentHealth)}/{Mathf.RoundToInt(playerHealth.maxHealth)}";

        if (fillImage != null && colorByPercent != null)
            fillImage.color = colorByPercent.Evaluate(percent);
    }
}