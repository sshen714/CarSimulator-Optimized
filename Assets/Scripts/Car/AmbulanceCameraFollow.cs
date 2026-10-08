using UnityEngine;

public class AmbulanceCameraFollow : MonoBehaviour
{
    private Transform target;       // 追蹤目標
    
    [Header("跟隨設定")]
    public Vector3 offset = new Vector3(0, 6, -10); // 跟隨的距離偏移 (Y高一點，Z後退一點)
    public float smoothSpeed = 0.125f;           // 平滑係數 (數字越小越滑)
    public float lookAtHeight = 1.5f;            // 盯著車子的高度偏移

    // 讓 Spawner 呼叫這個方法來更新追蹤目標
    public void SetTarget(Transform newTarget)
    {
        target = newTarget;
        Debug.Log("攝影機目標已鎖定: " + newTarget.name);
    }

    void LateUpdate()
    {
        if (target == null) return;

        // 計算想要到達的位置 (基於目標的旋轉，這樣車子轉彎攝影機會跟著轉)
        Vector3 desiredPosition = target.position + target.TransformDirection(offset);
        
        // 使用 Lerp 讓移動平滑，不會抖動
        Vector3 smoothedPosition = Vector3.Lerp(transform.position, desiredPosition, smoothSpeed);
        transform.position = smoothedPosition;

        // 攝影機永遠盯著車子中心看
        transform.LookAt(target.position + Vector3.up * lookAtHeight);
    }
}