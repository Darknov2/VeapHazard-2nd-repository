using UnityEngine;

public class MenuManager : MonoBehaviour
{
    public GameObject menuCanvas;         // Reference to your main menu Canvas
    public GameObject gameCanvas;         // Reference to your secondary Canvas

    void Start()
    {
        Cursor.lockState = CursorLockMode.None;
        Cursor.visible = true;

        // Make sure menu is visible and game canvas is hidden
        if (menuCanvas != null) menuCanvas.SetActive(true);
        if (gameCanvas != null) gameCanvas.SetActive(false);
    }

    public void StartGame()
    {
        // Show the new canvas and hide the menu canvas
        if (menuCanvas != null) menuCanvas.SetActive(false);
        if (gameCanvas != null) gameCanvas.SetActive(true);
    }

    public void QuitGame()
    {
        Application.Quit();
#if UNITY_EDITOR
        UnityEditor.EditorApplication.isPlaying = false;
#endif
    }
}