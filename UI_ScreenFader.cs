using UnityEngine;
using UnityEngine.UI;
using System.Collections;

[RequireComponent(typeof(Canvas))]
public class ScreenFader : MonoBehaviour
{
    [Header("References")]
    [Tooltip("Full-screen Image to control opacity (should cover the entire screen).")]
    public Image overlayImage;

    [Header("Defaults")]
    [Tooltip("Color to use when fully faded (alpha will be controlled).")]
    public Color overlayColor = Color.black;

    void Awake()
    {
        if (overlayImage != null)
        {
            overlayImage.color = new Color(overlayColor.r, overlayColor.g, overlayColor.b, 0f); // start transparent
            overlayImage.raycastTarget = true; // blocks clicks when faded
        }
    }

    public void FadeToBlack(float duration)
    {
        if (overlayImage == null) return;
        StopAllCoroutines();
        StartCoroutine(FadeRoutine(1f, duration));
    }

    public void FadeToTransparent(float duration)
    {
        if (overlayImage == null) return;
        StopAllCoroutines();
        StartCoroutine(FadeRoutine(0f, duration));
    }

    private IEnumerator FadeRoutine(float targetAlpha, float duration)
    {
        float startAlpha = overlayImage.color.a;
        float t = 0f;
        duration = Mathf.Max(0.01f, duration);

        while (t < 1f)
        {
            t += Time.unscaledDeltaTime / duration; // use unscaled for consistent fade even if timeScale changes
            float a = Mathf.Lerp(startAlpha, targetAlpha, t);
            overlayImage.color = new Color(overlayColor.r, overlayColor.g, overlayColor.b, a);
            yield return null;
        }

        overlayImage.color = new Color(overlayColor.r, overlayColor.g, overlayColor.b, targetAlpha);
    }
}