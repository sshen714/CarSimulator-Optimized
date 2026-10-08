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
    private readonly List<TrafficNode> controlledStopLines = new List<TrafficNode>();
    private bool stopLinesCached;
    private Vector3 emergencyApproachDirection;

    public void AmbulanceApproach(NPC_AmbulanceDrive ambulance)
    {
        if (ambulance == null) return;
        if (!stopLinesCached) CacheStopLines();
        // 如果不是在覆寫狀態，就先儲存當前的燈號狀態
        if (!isOverridden)
        {
            emergencyApproachDirection = ResolveApproachDirection(ambulance);
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
        foreach (var node in controlledStopLines)
        {
            if (node == null) continue;
            bool mustStop = !IsSameApproach(transform.position - node.transform.position, emergencyApproachDirection);
            node.SetEmergencySignal(this, mustStop);
        }

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

        foreach (var hit in hits)
        {
            var vehicleRoot = hit.transform.root.gameObject;
            if (!vehicleRoot.CompareTag("Car")) continue;
            if (vehicleRoot.GetComponent<NPC_AmbulanceDrive>() != null) continue;

            var npc = vehicleRoot.GetComponent<NPC_WaypointDrive>();
            if (npc == null) continue;
            TrafficNode stopLine = npc.targetNode;
            // 停止線的緊急停等優先，NPC 自己依距離減速，不能在遠處直接煞死。
            if (stopLine != null && stopLine.MustStopForEmergency) continue;
            // 靠邊避讓由自己的協程控制；路口廣播不能提早把它拉回車道。
            if (npc.IsYielding) continue;

            float distToCenter = Vector3.Distance(npc.transform.position, transform.position);
            
            bool isWaitingAtStopLine = stopLine != null && stopLine.isStopLine;

            if (isWaitingAtStopLine)
            {
                if (controlledStopLines.Contains(stopLine) &&
                    IsSameApproach(transform.position - stopLine.transform.position, emergencyApproachDirection))
                    npc.V2X_Accelerate();
                continue;
            }

            // 已越過停止線的車先駛離，避免橫向或對向車停在路口中央。
            if (distToCenter <= intersectionCoreRadius)
                npc.V2X_Accelerate();
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
        foreach (var node in controlledStopLines)
            if (node != null) node.ClearEmergencySignal(this);
    }

    void OnDisable()
    {
        RestoreOriginalLights();
    }

    internal static bool IsSameApproach(Vector3 incoming, Vector3 ambulanceDirection)
    {
        incoming.y = 0f;
        ambulanceDirection.y = 0f;
        return incoming.sqrMagnitude > 0.01f && ambulanceDirection.sqrMagnitude > 0.01f &&
            Vector3.Dot(incoming.normalized, ambulanceDirection.normalized) >= 0.7f;
    }

    private Vector3 ResolveApproachDirection(NPC_AmbulanceDrive ambulance)
    {
        TrafficNode best = null;
        float nearest = float.PositiveInfinity;
        foreach (var node in controlledStopLines)
        {
            if (node == null) continue;
            Vector3 incoming = transform.position - node.transform.position;
            if (!IsSameApproach(incoming, ambulance.transform.forward)) continue;
            float distance = (node.transform.position - ambulance.transform.position).sqrMagnitude;
            if (distance < nearest) { nearest = distance; best = node; }
        }
        // 救護車通過或轉彎時仍維持原入口，避免對向突然被當成同向。
        return best != null ? transform.position - best.transform.position : ambulance.transform.forward;
    }

    private void CacheStopLines()
    {
        controlledStopLines.Clear();
        var controllers = FindObjectsOfType<IntersectionV2X>();
        foreach (var node in FindObjectsOfType<TrafficNode>())
        {
            if (!node.isStopLine) continue;
            IntersectionV2X owner = null;
            foreach (var controller in controllers)
            {
                if (node.redLightModel != null &&
                    (controller.verticalRed.Contains(node.redLightModel) || controller.horizontalRed.Contains(node.redLightModel)))
                { owner = controller; break; }
            }
            // 場景有未綁紅燈的停止線；用同高度、附近最近路口補上緊急規則。
            if (owner == null)
            {
                float nearest = float.PositiveInfinity;
                foreach (var controller in controllers)
                {
                    Vector3 offset = node.transform.position - controller.transform.position;
                    if (Mathf.Abs(offset.y) > 5f) continue;
                    offset.y = 0f;
                    float range = controller.intersectionCoreRadius + 20f;
                    if (offset.sqrMagnitude > range * range || offset.sqrMagnitude >= nearest) continue;
                    nearest = offset.sqrMagnitude;
                    owner = controller;
                }
            }
            if (owner == this) controlledStopLines.Add(node);
        }
        stopLinesCached = true;
    }
}
