using UnityEngine;

public class PlayerBombInteractor : MonoBehaviour
{
    public float interactDistance = 5f;

    void Update()
    {
        Ray ray = Camera.main.ScreenPointToRay(new Vector3(Screen.width / 2f, Screen.height / 2f));
        RaycastHit hit;
        BombRaycastInteractable bombHit = null;
        if (Physics.Raycast(ray, out hit, interactDistance))
        {
            bombHit = hit.collider.GetComponent<BombRaycastInteractable>();
            if (bombHit != null)
            {
                bombHit.SetPlayerLooking(true);
            }
        }
        // Reset all other bombs
        foreach (BombRaycastInteractable bomb in SceneFind.All<BombRaycastInteractable>())
        {
            if (bomb != bombHit)
                bomb.SetPlayerLooking(false);
        }
    }
}