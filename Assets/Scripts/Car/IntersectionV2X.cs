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

        ApplyEmergencyLights();
        ForceUpdateStopLineNodes(verticalRed, false);
        ForceUpdateStopLineNodes(horizontalRed, true);

        NotifyNearbyNPCs(ambulance);
        resetTimer = 8.0f; // 延長重設時間
    }

    // 套件的交通燈會在 Update 中照常切換；在同一幀最後維持救護車優先燈號。
    void LateUpdate()
    {
        if (isOverridden)
            ApplyEmergencyLights();
    }

    void ApplyEmergencyLights()
    {
        // --- 🚑 直向：強制變綠 ---
        foreach(var r in verticalRed) if(r != null) r.SetActive(false);
        foreach(var y in verticalYellow) if(y != null) y.SetActive(false);
        foreach(var g in verticalGreen) if(g != null) g.SetActive(true);

        // --- 🛑 橫向：強制變紅 ---
        foreach(var r in horizontalRed) if(r != null) r.SetActive(true);
        foreach(var y in horizontalYellow) if(y != null) y.SetActive(false);
        foreach(var g in horizontalGreen) if(g != null) g.SetActive(false);
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
            // 靠邊避讓由自己的協程控制；路口廣播不能提早把它拉回車道。
            if (npc.IsYielding) continue;

            float distToCenter = Vector3.Distance(npc.transform.position, transform.position);
            
            TrafficNode stopLine = npc.targetNode;
            bool isWaitingAtStopLine = stopLine != null && stopLine.isStopLine;

            // 只在本路口橫向紅燈的停止線附近強制停車；更遠處交給一般紅燈邏輯減速。
            bool isAtThisStopLine = isWaitingAtStopLine &&
                                    stopLine.redLightModel != null &&
                                    horizontalRed.Contains(stopLine.redLightModel) &&
                                    Vector3.Distance(npc.transform.position, stopLine.transform.position) <= 4f &&
                                    Vector3.Dot(npc.transform.forward, stopLine.transform.position - npc.transform.position) >= 0f;
            
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
                // 已抵達本路口的橫向停止線時，才補上 V2X 強制停車。
                else if (isAtThisStopLine)
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
