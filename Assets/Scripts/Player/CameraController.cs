using UnityEngine;
using Mirror;

public class CameraController : MonoBehaviour
{
    [SerializeField] Transform followTarget;

    [SerializeField] float rotationSpeed = 2f;
    [SerializeField] float distance = 5;

    [SerializeField] float minVerticalAngle = -45;
    [SerializeField] float maxVerticalAngle = 45;

    [SerializeField] Vector2 framingOffset;

    [SerializeField] bool invertX;
    [SerializeField] bool invertY;

    float rotationX;
    float rotationY;

    float invertXVal;
    float invertYVal;

    //void Start()
    //{
    //    Cursor.visible = false;
    //    Cursor.lockState = CursorLockMode.Locked;
    //}

    void LateUpdate()
    {
        // 运行时生成玩家后，再绑定 followTarget（只绑定本地玩家）
        if (followTarget == null && NetworkClient.active && NetworkClient.localPlayer != null)
        {
            followTarget = NetworkClient.localPlayer.transform;
        }

        if (followTarget == null) return; // 没绑到就先别算

        invertXVal = invertX ? -1 : 1;
        invertYVal = invertY ? -1 : 1;

        rotationX += Input.GetAxis("Camera Y") * invertYVal * rotationSpeed;
        rotationX = Mathf.Clamp(rotationX, minVerticalAngle, maxVerticalAngle);

        rotationY += Input.GetAxis("Camera X") * invertXVal * rotationSpeed;

        var targetRotation = Quaternion.Euler(rotationX, rotationY, 0);
        var focusPostion = followTarget.position + new Vector3(framingOffset.x, framingOffset.y, 0);

        transform.position = focusPostion - targetRotation * new Vector3(0, 0, distance);
        transform.rotation = targetRotation;
    }

    public Quaternion PlanarRotation => Quaternion.Euler(0, rotationY, 0);
}