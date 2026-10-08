using UnityEngine;
using System.Collections.Generic;

public class IntersectionV2X : MonoBehaviour
{
    [Header("🚥 直向燈組 (1, 2, 5, 6) - 救護車通行的方向")]
    public List<GameObject> verticalRed; 
    public List<GameObject> verticalYellow; 
    public List<GameObject> verticalGreen;

    [Header("🚥 橫向燈組 (3, 4, 7, 8) - 要擋住的方向")]
    public List<GameObject> horizontalRed;
    public List<GameObject> horizontalYellow; 
    public List<GameObject> horizontalGreen;

    [Header("🚗 路口中央保留區")]
    public float intersectionCoreRadius = 30f; 

    // --- 💡 新增的狀態變數 ---
    private float resetTimer = 0f;
    private bool isOverridden = false; // 是否正處於被救護車覆寫的狀態
    private Dictionary<GameObject, bool> originalLightStates = new Dictionary<GameObject, bool>(); // 用來記住燈號原始狀態

    public void AmbulanceApproach(NPC_AmbulanceDrive ambulance)
    {
        // 如果不是在覆寫狀態，就先儲存當前的燈號狀態
        if (!isOverridden)
        {
            originalLightStates.Clear();
            StoreLightStates(verticalRed);
            StoreLightStates(verticalYellow);
            StoreLightStates(verticalGreen);
            StoreLightStates(horizontalRed);
            StoreLightStates(horizontalYellow);
            StoreLightStates(horizontalGreen);
        }

        isOverridden = true;

        // --- 🚑 直向：強制變綠 ---
        foreach(var r in verticalRed) if(r != null) r.SetActive(false);
        foreach(var y in verticalYellow) if(y != null) y.SetActive(false);
        foreach(var g in verticalGreen) if(g != null) g.SetActive(true);

        // 💡【關鍵新增】直接找到對應的 TrafficNode，強制更新其內部狀態為綠燈
        ForceUpdateStopLineNodes(verticalRed, false);

        // --- 🛑 橫向：強制變紅 ---
        foreach(var r in horizontalRed) if(r != null) r.SetActive(true);
        foreach(var y in horizontalYellow) if(y != null) y.SetActive(false);
        foreach(var g in horizontalGreen) if(g != null) g.SetActive(false);

        NotifyNearbyNPCs(ambulance);
        resetTimer = 8.0f; // 延長重設時間
    }

    void Update() 
    {
        if (isOverridden && resetTimer > 0) 
        {
            resetTimer -= Time.deltaTime;
            if (resetTimer <= 0) 
            {
                RestoreOriginalLights(); // 恢復燈號
                ResetNPCs();
            }
        }
    }

    void NotifyNearbyNPCs(NPC_AmbulanceDrive ambulance)
    {
        Collider[] hits = Physics.OverlapSphere(transform.position, 60f);
        Vector3 ambDir = ambulance != null ? ambulance.transform.forward.normalized : Vector3.forward;

        foreach (var hit in hits)
        {
            var vehicleRoot = hit.transform.root.gameObject;
            if (!vehicleRoot.CompareTag("Car")) continue;
            if (vehicleRoot.GetComponent<NPC_AmbulanceDrive>() != null) continue;

            var npc = vehicleRoot.GetComponent<NPC_WaypointDrive>();
            if (npc == null) continue;

            float distToCenter = Vector3.Distance(npc.transform.position, transform.position);
            
            // 💡 判斷這台車是不是「還沒過停止線」(目標節點是 StopLine)
            bool isWaitingAtStopLine = (npc.targetNode != null && npc.targetNode.isStopLine);

            Vector3 dirToCenter = (transform.position - npc.transform.position).normalized;
            bool isHeadingToCenter = Vector3.Dot(npc.transform.forward, dirToCenter) > 0.1f;
            
            float forwardDot = Vector3.Dot(npc.transform.forward, ambDir);
            bool isSameDirection = forwardDot > 0.4f;

            // 🚥 【精準分流邏輯】 🚥

            // 1. 同向車 (救護車正前方的車)：開特權！
            // 因為它擋到救護車了，只要靠近路口，不管三七二十一直接踩油門衝過去清空！
            if (isSameDirection && distToCenter <= intersectionCoreRadius)
            {
                npc.V2X_Accelerate();
                continue; 
            }

            // 2. 橫向與對向車 (非同向)：必須遵守規矩！
            if (!isSameDirection)
            {
                // 如果它「已經越過停止線」卡在路口正中央了，只能叫它加速逃離
                if (distToCenter <= intersectionCoreRadius && !isWaitingAtStopLine)
                {
                    npc.V2X_Accelerate();
                }
                // 💡 如果它「還沒過停止線」或者正在朝著路口開，乖乖給我死死煞停！
                else if (isHeadingToCenter || isWaitingAtStopLine)
                {
                    npc.V2X_ForceStop();
                }
            }
        }
    }

    void ResetNPCs() 
    {
        Collider[] hits = Physics.OverlapSphere(transform.position, 70f);
        foreach (var h in hits) 
        {
            var vehicleRoot = h.transform.root.gameObject;
            var npc = vehicleRoot.GetComponent<NPC_WaypointDrive>();
            if (npc != null) npc.V2X_Reset();
        }
    }

    // --- 💡 以下為新增的輔助方法 ---

    // 儲存燈號狀態
    void StoreLightStates(List<GameObject> lights)
    {
        foreach (var light in lights)
        {
            if (light != null && !originalLightStates.ContainsKey(light))
            {
                originalLightStates.Add(light, light.activeSelf);
            }
        }
    }

    // 恢復燈號狀態
    void RestoreOriginalLights()
    {
        if (!isOverridden) return;

        foreach (var entry in originalLightStates)
        {
            if (entry.Key != null)
            {
                entry.Key.SetActive(entry.Value);
            }
        }
        isOverridden = false;
    }

    // 強制更新停止線節點的狀態
    void ForceUpdateStopLineNodes(List<GameObject> redLights, bool isRed)
    {
        TrafficNode[] allNodes = FindObjectsOfType<TrafficNode>();
        foreach (var node in allNodes) {
            if (node.isStopLine && redLights.Contains(node.redLightModel)) {
                node.currentIsRed = isRed;
            }
        }
    }
}