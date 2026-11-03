using UnityEngine;

public class BillboardUI : MonoBehaviour
{
    private Camera mainCamera;

    void Start()
    {
        // Automatically gets the headset camera (the point between your eyes)
        mainCamera = Camera.main;
    }

    void LateUpdate()
    {
        if (mainCamera == null) return;

        // 1. Compute the direction vector between the panel and the headset
        Vector3 directionToCamera = mainCamera.transform.position - transform.position;

        // This line locks rotation on the Y axis, so that the billboard only rotates around the vertical axis.
         //directionToCamera.y = 0; 

        // 2. Apply the rotation. 
        // The "-" sign is essential because, by default, Canvas text in Unity 
        // appears reversed (mirrored) when oriented directly toward the camera.
        transform.rotation = Quaternion.LookRotation(-directionToCamera);
    }
}