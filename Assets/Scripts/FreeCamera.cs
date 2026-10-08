using UnityEngine;

public class FreeCamera : MonoBehaviour
{
    [Header("移動速度設定")]
    public float moveSpeed = 20f;      // 一般飛行速度
    public float sprintSpeed = 60f;    // 按住 Shift 的衝刺速度

    [Header("滑鼠轉向速度")]
    public float lookSpeed = 2f;

    private float yaw = 0f;
    private float pitch = 0f;

    void Start()
    {
        // 記住攝影機一開始的角度
        yaw = transform.eulerAngles.y;
        pitch = transform.eulerAngles.x;
    }

    void Update()
    {
        // 🖱️ 1. 旋轉視角 (按住「滑鼠右鍵」拖曳來轉頭)
        if (Input.GetMouseButton(1))
        {
            yaw += lookSpeed * Input.GetAxis("Mouse X");
            pitch -= lookSpeed * Input.GetAxis("Mouse Y");

            // 限制上下看，避免脖子扭斷 (翻轉)
            pitch = Mathf.Clamp(pitch, -90f, 90f);

            transform.eulerAngles = new Vector3(pitch, yaw, 0f);
        }

        // ⌨️ 2. 鍵盤移動設定
        // 檢查有沒有按住 Shift 鍵加速
        float currentSpeed = Input.GetKey(KeyCode.LeftShift) ? sprintSpeed : moveSpeed;

        float h = Input.GetAxis("Horizontal"); // A, D 或 左右方向鍵
        float v = Input.GetAxis("Vertical");   // W, S 或 上下方向鍵

        // 垂直升降 (Q 下降, E 上升)
        float upDown = 0f;
        if (Input.GetKey(KeyCode.E)) upDown = 1f;
        if (Input.GetKey(KeyCode.Q)) upDown = -1f;

        // 🚀 3. 執行移動 (根據攝影機現在「看」的方向前進)
        Vector3 moveDirection = (transform.forward * v) + (transform.right * h) + (transform.up * upDown);
        transform.position += moveDirection * currentSpeed * Time.deltaTime;
    }
}