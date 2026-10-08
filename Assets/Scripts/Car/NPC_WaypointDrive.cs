using UnityEngine;
using UnityEngine.AI;
using System.Collections;

[RequireComponent(typeof(NavMeshAgent))]
public class NPC_WaypointDrive : MonoBehaviour
    {
        protected NavMeshAgent agent;
        public TrafficNode targetNode; 

        [Header("行車與雷達")]
        public float sensorLength = 3f;
        public Vector3 sensorOffset = new Vector3(0, 0.5f, 2.5f);
        
        [Header("雷達尺寸 (半長寬)")]
        public Vector3 boxHalfExtents = new Vector3(1.0f, 1.0f, 0.2f); 

        [Header("避讓診斷（Play 模式觀察）")]
        [SerializeField] private string yieldStatus = "正常行駛";
        [SerializeField] private float actualYieldOffset;
        [SerializeField] private float availableYieldOffset;
        private BoxCollider vehicleBody;
        public float VehicleHalfWidth { get; private set; } = 1f;
        public float VehicleHalfLength { get; private set; } = 2.5f;

        protected bool isYielding = false;
        protected float originalSpeed;
        public bool IsYielding => isYielding;

        protected bool v2xForceGo = false;
        protected bool v2xForceStop = false;

        private enum YieldState { None, MovingToSide, AligningForward }
        private YieldState currentYieldState = YieldState.None;
        private Coroutine yieldRoutine;
        private IntersectionV2X[] intersections;

        protected bool isWaitingAtRedLight = false; // 💡【關鍵新增】紅燈等待狀態鎖
        protected bool isFullyStopped = false;      // 💡【關鍵新增】徹底停止狀態鎖

        protected virtual void Awake() {
            agent = GetComponent<NavMeshAgent>();
            vehicleBody = GetComponent<BoxCollider>();
            if (vehicleBody != null)
            {
                Vector3 scale = vehicleBody.transform.lossyScale;
                VehicleHalfWidth = Mathf.Max(0.5f, vehicleBody.size.x * Mathf.Abs(scale.x) * 0.5f);
                VehicleHalfLength = Mathf.Max(1f, vehicleBody.size.z * Mathf.Abs(scale.z) * 0.5f);
            }
        }

        protected virtual void Start() {
            if (agent != null) {
                // 用實際碰撞體與縮放決定寬度，避免所有車都被當成半徑 2 米。
                agent.radius = VehicleHalfWidth + 0.2f;

                originalSpeed = agent.speed;
                intersections = FindObjectsOfType<IntersectionV2X>();
                if (targetNode != null) agent.SetDestination(targetNode.transform.position);
            }
        }

    public void YieldForAmbulance(NPC_AmbulanceDrive ambulance)
    {
        // 如果已經在閃了，就不要理會
        if (isYielding || currentYieldState != YieldState.None || ambulance == null || v2xForceStop) return;
        if (agent == null || !agent.isActiveAndEnabled || !agent.isOnNavMesh) return;

        // 防呆：對向車直接無視
        if (Vector3.Dot(transform.forward, ambulance.transform.forward) < -0.2f) return;

        Vector3 ambulancePos = ambulance.transform.position;
        if (Vector3.Distance(transform.position, ambulancePos) > 55f) return;

        // 救護車在前方的話不讓路
        Vector3 toAmbulance = (ambulancePos - transform.position).normalized;
        if (Vector3.Dot(transform.forward, toAmbulance) > 0.2f) return;

        // 🚨【關鍵修正】：如果在路口內，絕對加速清空！
        if (IsInIntersection())
        {
            V2X_Accelerate();
            return;
        }

        // 🚨【關鍵修正】：如果距離停止線非常近 (把 50f 改成 15f)
        if (targetNode != null && targetNode.isStopLine)
        {
            float distToStopLine = Vector3.Distance(transform.position, targetNode.transform.position);
            
            // 停止線附近保留車道；只有已放行時才能加速清空。
            if (distToStopLine < 15f)
            {
                if (!targetNode.currentIsRed) V2X_Accelerate();
                return; // 不執行 S 型避讓
            }
        }

        // 💡 如果以上都不是 (代表在一般道路，或是排在車陣後方)，就乖乖執行 S 型靠邊停車！
        if (agent.isActiveAndEnabled && agent.isOnNavMesh)
        {
            agent.isStopped = false;
        }
        yieldRoutine = StartCoroutine(S_CurveYieldRoutine(ambulance));
    }

    private IEnumerator S_CurveYieldRoutine(NPC_AmbulanceDrive ambulance)
    {
        isYielding = true;
        currentYieldState = YieldState.MovingToSide;
        v2xForceGo = false;
        v2xForceStop = false;

        agent.isStopped = false;
        agent.ResetPath();
        agent.autoBraking = true;

        agent.speed = Mathf.Min(agent.speed, originalSpeed * 0.8f);

        // 預留兩車的半寬、導航餘裕與間距，大車需要更寬的通道。
        float targetOffset = VehicleHalfWidth + ambulance.VehicleHalfWidth + 1f;
        float passClearance = VehicleHalfLength + ambulance.VehicleHalfLength + 2f;
        Vector3 segmentStart = transform.position;
        NavMeshPath yieldPath = new NavMeshPath();
        yieldStatus = "靠邊移動";
        actualYieldOffset = 0f;
        availableYieldOffset = 0f;

        Vector3 initialRoadDir = targetNode != null ? (targetNode.transform.position - transform.position).normalized : transform.forward;
        // 固定通過判斷的方向，避免救護車轉彎時被誤判為已超車。
        Vector3 passDirection = Vector3.ProjectOnPlane(initialRoadDir, Vector3.up).normalized;
        if (passDirection.sqrMagnitude < 0.01f)
            passDirection = Vector3.ProjectOnPlane(transform.forward, Vector3.up).normalized;
        float currentOffset = 0f;
        float timer = 0f;
        float passedTime = 0f;
        float nextPathUpdate = 0f;
        TrafficNode lastPathNode = null;

        while (isActiveAndEnabled && agent != null && agent.isActiveAndEnabled && agent.isOnNavMesh && targetNode != null)
        {
            timer += Time.deltaTime;

            bool emergencyEnded = ambulance == null || !ambulance.isActiveAndEnabled || !ambulance.isEmergency;
            if (!emergencyEnded)
            {
                Vector3 toAmbulance = ambulance.transform.position - transform.position;
                bool passed = Vector3.Dot(passDirection, toAmbulance) > passClearance;
                // 救護車已離開通知範圍（例如轉往其他道路）時也可解除。
                passedTime = passed || toAmbulance.sqrMagnitude > 55f * 55f
                    ? passedTime + Time.deltaTime : 0f;
            }

            if (emergencyEnded || passedTime >= 0.5f)
            {
                Vector3 mergeDirection = targetNode.transform.position - transform.position;
                float mergeDistance = Mathf.Min(8f, mergeDirection.magnitude);
                if (float.IsPositiveInfinity(NearestVehicleDistance(mergeDirection.normalized, mergeDistance)))
                    break;

                agent.isStopped = true;
                agent.velocity = Vector3.zero;
                yieldStatus = "等待安全併回：有車阻擋";
                yield return null;
                continue;
            }

            if (targetNode != null)
            {
                Vector3 toNode = targetNode.transform.position - transform.position;
                if (targetNode.isStopLine && targetNode.currentIsRed && toNode.magnitude <= 4f)
                {
                    agent.isStopped = true;
                    agent.velocity = Vector3.zero;
                    yieldStatus = "停止線紅燈停等";
                    yield return null;
                    continue;
                }

                Vector3 segment = targetNode.transform.position - segmentStart;
                float segmentLength = segment.magnitude;
                Vector3 roadDirection = segmentLength > 0.1f ? segment / segmentLength : transform.forward;
                float progress = Vector3.Dot(transform.position - segmentStart, roadDirection);
                // 靠邊後離節點中心較遠，改用沿路線的進度判斷，避免大車卡在偏移節點。
                bool reached = segmentLength < 0.1f ||
                    (progress >= segmentLength - 2f && toNode.magnitude <= targetOffset + VehicleHalfLength + 2f);
                if (reached && (!targetNode.isStopLine || !targetNode.currentIsRed))
                {
                    TrafficNode next = targetNode.GetNextNode();
                    if (next != null)
                    {
                        segmentStart = targetNode.transform.position;
                        targetNode = next;
                        segment = targetNode.transform.position - segmentStart;
                        segmentLength = segment.magnitude;
                        roadDirection = segmentLength > 0.1f ? segment / segmentLength : transform.forward;
                        progress = Vector3.Dot(transform.position - segmentStart, roadDirection);
                    }
                }

                Vector3 offsetDir = Vector3.Cross(Vector3.up, roadDirection).normalized;
                actualYieldOffset = Mathf.Max(0f, Vector3.Dot(transform.position - segmentStart, offsetDir));
                currentOffset = Mathf.Lerp(currentOffset, targetOffset, Time.deltaTime * 4.0f);
                // 只看前方約一個車長，讓車現在就開始側移，而不是到遠處節點才靠邊。
                float lookAhead = Mathf.Max(6f, VehicleHalfLength * 2f);
                Vector3 routeTarget = segmentStart + roadDirection *
                    Mathf.Clamp(progress + lookAhead, 0f, segmentLength);

                if (agent.isActiveAndEnabled && agent.isOnNavMesh)
                {
                    // 避免每幀觸發 NavMesh 路徑重算；切換節點時仍立即更新。
                    if (timer >= nextPathUpdate || targetNode != lastPathNode)
                    {
                        if (!TrySetYieldPath(routeTarget, offsetDir, currentOffset, yieldPath))
                        {
                            yieldStatus = "停止：路肩與原車道都無完整路徑";
                            agent.isStopped = true;
                            agent.velocity = Vector3.zero;
                            yield return null;
                            continue;
                        }
                        nextPathUpdate = timer + 0.1f;
                        lastPathNode = targetNode;
                    }
                }
                else
                {
                    break;
                }
            }

            // 避讓仍必須保有緊急煞停，不能只以固定低速推向前車。
            // 尚未讓出足夠寬度前維持慢行，不因固定秒數提前降至半速。
            float yieldSpeed = actualYieldOffset >= targetOffset - 0.3f ? 0.5f : 0.8f;
            ApplyForwardCollision(originalSpeed * yieldSpeed);

            yield return null;
        }

        ReturnToTrack();
    }

    private bool TrySetYieldPath(Vector3 routeTarget, Vector3 right, float desiredOffset, NavMeshPath path)
    {
        // 路肩不夠寬時逐步縮小偏移；最後嘗試原車道，避免重複停在同一個無效點。
        int steps = Mathf.Max(1, Mathf.CeilToInt(desiredOffset / 0.5f));
        for (int i = 0; i <= steps; i++)
        {
            float offset = desiredOffset * (steps - i) / steps;
            NavMeshHit hit;
            if (!NavMesh.SamplePosition(routeTarget + right * offset, out hit, 0.4f, agent.areaMask)) continue;
            if (!agent.CalculatePath(hit.position, path) || path.status != NavMeshPathStatus.PathComplete) continue;
            if (!agent.SetPath(path)) continue;
            availableYieldOffset = Mathf.Max(0f, Vector3.Dot(hit.position - routeTarget, right));
            return true;
        }
        return false;
    }


    // 🚑 救護車呼叫：路口強制清空 (救護車靠近中)
    // 請確保傳入 ambulanceForward 參數
   public void IntersectionYield(Vector3 ambulanceForward) {
        if (isYielding || currentYieldState != YieldState.None) return;

        float directionMatch = Vector3.Dot(transform.forward, ambulanceForward);
        if (directionMatch < 0.5f) return;

        // 💡 關鍵過濾：如果我根本還沒靠近路口，就無視這個「強制停下」的廣播
        if (targetNode != null && targetNode.isStopLine) {
            float distToStopLine = Vector3.Distance(transform.position, targetNode.transform.position);
            // 如果距離路口大於 15 米，我當作沒聽到，繼續開我的直行車
            if (distToStopLine > 15f && !IsInIntersection()) return; 
        } else if (!IsInIntersection()) {
            return; // 目標根本不是路口，無視
        }

        // 執行停等邏輯
        if (IsInIntersection()) {
            agent.speed = originalSpeed * 0.5f; 
            agent.isStopped = false;
        } else {
            agent.isStopped = true;
            agent.velocity = Vector3.zero;
        }
        
        isYielding = true;
        yieldRoutine = StartCoroutine(WaitAndReturn(5.0f));
    }

    
    protected virtual void Update() {
        if (targetNode == null || agent == null) return;

        // 💡【關鍵修正】將避讓判斷移到最前面！只要在 S 型避讓中，就絕對不執行任何其他駕駛邏輯。
        if (isYielding) return; 

        // 💡【關鍵修正】重構 Update 邏輯，賦予 V2X 指令最高優先級
        if (v2xForceGo)
        {
            if (agent.isActiveAndEnabled && agent.isOnNavMesh) {
                agent.isStopped = false;
            }
            CheckForwardCollision();
            // ❌ 刪除 return，讓它能繼續往下執行尋找下一個節點！
        }
        else if (v2xForceStop)
        {
            if (agent.isActiveAndEnabled && agent.isOnNavMesh) {
                agent.isStopped = true;
                agent.velocity = Vector3.zero;
            }
            return;
        }

        // 如果沒有被 V2X 指令攔截，才執行正常駕駛邏輯
        if (!v2xForceGo && !v2xForceStop) {
            bool stoppedByLight = HandleTrafficLights();
            if (stoppedByLight) {
                if (!agent.isStopped) ApplyForwardCollision(agent.speed);
                return;
            }

            CheckForwardCollision();
            if (agent.isStopped) return;
        }

        // 節點導航邏輯 (所有情況都需要)
        if (!agent.isStopped && !isWaitingAtRedLight) {
            float distToTarget = Vector3.Distance(transform.position, targetNode.transform.position);
            
            // 必須「真的有路徑且到達」或「物理距離真的很近」才算抵達節點
            bool reachedByNav = (!agent.pathPending && agent.hasPath && agent.remainingDistance < 2.5f);
            bool reachedByPhysics = (distToTarget < 3.0f);

            // 💡【關鍵防禦】只有在非等待紅燈的狀態下，才允許切換節點
            if (!isWaitingAtRedLight && (reachedByNav || reachedByPhysics)) {
                TrafficNode nextNode = targetNode.GetNextNode(); 
                if (nextNode != null) {
                    targetNode = nextNode;
                    if (agent.isOnNavMesh) agent.SetDestination(targetNode.transform.position);
                } else {
                    Destroy(gameObject); // 真的開到道路盡頭才銷毀
                }
            }
            // 🛡️ 防呆：如果路徑斷了(hasPath=false)，但離目標還很遠，強制重新導航，而不是自毀
            else if (!agent.hasPath && !agent.pathPending && distToTarget >= 3.0f) {
                if (agent.isOnNavMesh) agent.SetDestination(targetNode.transform.position);
            }
        }
    }

    protected bool EnsureAgentOnNavMesh() {
        if (agent == null) return false;
        if (agent.isActiveAndEnabled && agent.isOnNavMesh) return true;

        NavMeshHit hit;
        if (NavMesh.SamplePosition(transform.position, out hit, 3.0f, NavMesh.AllAreas)) {
            agent.Warp(hit.position);
            return true;
        }

        return false;
    }

    protected virtual void ReturnToTrack() {
        yieldRoutine = null;
        currentYieldState = YieldState.None;
        isYielding = false;
        yieldStatus = "正常行駛";
        actualYieldOffset = 0f;
        availableYieldOffset = 0f;
        if (agent == null) return;
        agent.autoBraking = true; // 恢復正常的自動煞車行為
        
        // 🛡️ 【關鍵防呆】確保車輛還在導航網格上才能恢復，否則會噴紅字錯誤死當！
        if (agent.isActiveAndEnabled && agent.isOnNavMesh) {
            agent.isStopped = false;
            agent.speed = originalSpeed;
            agent.acceleration = 12f; // 恢復時稍微加速

            if (targetNode != null) {
                // 只跳過確實抵達的普通節點，避免彎道上因朝向改變跳錯路線。
                int safety = 0;
                while (safety < 5) {
                    Vector3 toNode = targetNode.transform.position - transform.position;
                    if (!targetNode.isStopLine && toNode.magnitude < 4.0f) {
                        TrafficNode next = targetNode.GetNextNode();
                        if (next != null) {
                            targetNode = next;
                            safety++;
                            continue;
                        }
                    }
                    break;
                }
                
                agent.ResetPath(); // 徹底清除舊的 S 型路徑殘留
                if (agent.isOnNavMesh) agent.SetDestination(targetNode.transform.position);
            }
        }
    }

    // 輔助協程：等救護車過去後自動恢復
    private IEnumerator WaitAndReturn(float delay) {
        yield return new WaitForSeconds(delay);
        ReturnToTrack();
    }
   

    protected void ResetToNormal() {
        CancelYieldRoutine();
        currentYieldState = YieldState.None;
        isYielding = false;
        agent.isStopped = false;
        agent.speed = originalSpeed;
        agent.acceleration = 8f; 
        if (targetNode != null) agent.SetDestination(targetNode.transform.position);
    }

    protected virtual bool HandleTrafficLights() {
        if (v2xForceGo) return false;
        if (isYielding || currentYieldState != YieldState.None) return false; 

        if (targetNode.isStopLine && targetNode.currentIsRed) {
            isWaitingAtRedLight = true; // 進入等待狀態
            float dist = Vector3.Distance(transform.position, targetNode.transform.position);
            
            if (dist <= 4.0f) {
                // 💡【新方法】改用 isStopped 和 ResetPath() 來凍結車輛，避免 Editor 報錯
                agent.isStopped = true;
                agent.velocity = Vector3.zero;
                agent.ResetPath();
                isFullyStopped = true;
                return true; // 告訴 Update 我正在等紅燈
            } else if (dist <= 15f) {
                // 在 15 米內，就開始根據距離降低速度，準備停車
                agent.speed = Mathf.Lerp(0, originalSpeed, dist / 15f);
                agent.isStopped = false;
                return true; // 告訴 Update 正在處理紅燈，阻止 CheckForwardCollision 覆寫速度
            } else {
                // 距離還很遠，繼續正常行駛
                agent.isStopped = false;
                return false;
            }
        } else {
            // 從紅燈變綠燈的瞬間
            if (isWaitingAtRedLight) {
                isWaitingAtRedLight = false;
                isFullyStopped = false;
                agent.isStopped = false;
                agent.speed = originalSpeed;
                if (targetNode != null) agent.SetDestination(targetNode.transform.position);
            }
        }
        
        return false; // 綠燈或一般節點
    }

    protected void CheckForwardCollision()
    {
        ApplyForwardCollision(originalSpeed * (v2xForceGo ? 1.5f : 1f));
    }

    protected void ApplyForwardCollision(float desiredSpeed)
    {
        if (agent == null || !agent.isActiveAndEnabled || !agent.isOnNavMesh) return;
        float lookAhead = Mathf.Max(8f, sensorLength, agent.velocity.magnitude + 3f);
        // 停住後仍朝計畫路徑偵測，避免零速度時改回車頭方向而永遠無法側移。
        Vector3 toSteeringTarget = agent.hasPath ? agent.steeringTarget - transform.position : Vector3.zero;
        Vector3 direction = toSteeringTarget.sqrMagnitude > 0.01f
            ? toSteeringTarget.normalized : transform.forward;
        float distance = NearestVehicleDistance(direction, lookAhead);
        if (distance <= 3f)
        {
            agent.isStopped = true;
            agent.velocity = Vector3.zero;
            if (isYielding) yieldStatus = "停止：移動方向有車阻擋";
            return;
        }

        agent.isStopped = false;
        if (isYielding) yieldStatus = availableYieldOffset < 0.3f ? "路肩不足：沿原車道慢行" : "靠邊慢行，等待救護車通過";
        float speedLimit = Mathf.Max(0f, desiredSpeed);
        if (!float.IsPositiveInfinity(distance))
            speedLimit *= Mathf.InverseLerp(3f, lookAhead, distance);
        // 煞車立即限速，起步漸進加速；V2X 通行也必須遵守前車間距。
        agent.speed = speedLimit < agent.speed ? speedLimit
            : Mathf.MoveTowards(agent.speed, speedLimit, Mathf.Max(1f, agent.acceleration) * Time.deltaTime);
    }

    private float NearestVehicleDistance(Vector3 direction, float distance)
    {
        if (direction.sqrMagnitude < 0.01f) return float.PositiveInfinity;
        Vector3 bodyCenter = vehicleBody != null ? vehicleBody.transform.TransformPoint(vehicleBody.center)
            : transform.position + Vector3.up * sensorOffset.y;
        float frontExtent = Mathf.Abs(Vector3.Dot(direction, transform.forward)) * VehicleHalfLength
            + Mathf.Abs(Vector3.Dot(direction, transform.right)) * VehicleHalfWidth;
        Vector3 origin = bodyCenter + direction * frontExtent;
        float radius = Mathf.Max(0.5f, VehicleHalfWidth);
        float nearest = float.PositiveInfinity;
        // 多筆命中避免自己的碰撞體或路面遮住真正的前車。
        foreach (var hit in Physics.SphereCastAll(origin, radius, direction, distance, ~0, QueryTriggerInteraction.Ignore))
        {
            var other = hit.collider.GetComponentInParent<NPC_WaypointDrive>();
            if (other == null || other == this) continue;
            if (Vector3.Dot(direction, other.transform.position - transform.position) <= 0f) continue;
            nearest = Mathf.Min(nearest, hit.distance);
        }
        // Cast 不可靠地回報起點已重疊的物件，另外檢查雷達起點。
        foreach (var collider in Physics.OverlapSphere(origin, radius, ~0, QueryTriggerInteraction.Ignore))
        {
            var other = collider.GetComponentInParent<NPC_WaypointDrive>();
            if (other != null && other != this &&
                Vector3.Dot(direction, other.transform.position - transform.position) > 0f)
                return 0f;
        }
        return nearest;
    }

    protected bool IsInIntersection() {
        if (targetNode == null || targetNode.isStopLine || intersections == null) return false;
        foreach (var intersection in intersections)
        {
            if (intersection == null || !intersection.isActiveAndEnabled) continue;
            Vector3 offset = transform.position - intersection.transform.position;
            // 保留高度檢查，避免把高架橋上的車當成下方路口車輛。
            if (Mathf.Abs(offset.y) > 5f) continue;
            offset.y = 0f;
            if (offset.sqrMagnitude <= intersection.intersectionCoreRadius * intersection.intersectionCoreRadius)
                return true;
        }
        return false;
    }

    private void CancelYieldRoutine()
    {
        if (yieldRoutine != null) StopCoroutine(yieldRoutine);
        yieldRoutine = null;
        currentYieldState = YieldState.None;
        isYielding = false;
        yieldStatus = "V2X 接管";
        actualYieldOffset = 0f;
        availableYieldOffset = 0f;
    }

    // 💡【關鍵修改】將 OnDrawGizmosSelected 改為 OnDrawGizmos
    // 讓所有 NPC 車輛在 Play 模式下都能持續顯示雷達範圍，方便除錯。
    private void OnDrawGizmos() {
        Gizmos.color = new Color(1, 0, 0, 0.3f); // 讓顏色淡一點，避免擋住視線
        Vector3 sensorPos = transform.TransformPoint(sensorOffset);
        Gizmos.matrix = Matrix4x4.TRS(sensorPos, transform.rotation, Vector3.one);
        // 💡【關鍵修正】畫圖時也使用共用的 boxHalfExtents 變數，確保視覺與物理同步
        Gizmos.DrawWireCube(Vector3.forward * (sensorLength / 2f), boxHalfExtents * 2);
    }

    // 🚑 救護車 V2X 指令：加速逃離
    public void V2X_Accelerate() 
    {
        // 加上 isOnNavMesh 防呆，確保不會噴紅字
        if (agent != null && agent.isActiveAndEnabled && agent.isOnNavMesh)
        {
            bool takingControl = !v2xForceGo || isYielding;
            CancelYieldRoutine();
            v2xForceGo = true;
            yieldStatus = "路口 V2X 通行";
            v2xForceStop = false;
            currentYieldState = YieldState.None;
            isYielding = false;
            isWaitingAtRedLight = false;
            isFullyStopped = false;
            
            if (takingControl) agent.isStopped = false;
            agent.acceleration = 60f;
            
            // 💡 【關鍵修改】關閉自動煞車！確保車子在路口內逃亡時，經過節點絕對不會卡頓減速
            agent.autoBraking = false; 
            
            if (targetNode != null && (takingControl || !agent.hasPath))
                agent.SetDestination(targetNode.transform.position);
        }
    }

    // 🚑 救護車 V2X 指令：原地強制煞停
    public void V2X_ForceStop() 
    {
        if (agent != null && agent.isActiveAndEnabled && agent.isOnNavMesh)
        {
            CancelYieldRoutine();
            v2xForceGo = false;
            v2xForceStop = true;
            yieldStatus = "路口 V2X 強制停車";
            currentYieldState = YieldState.None;
            isYielding = false;
            agent.isStopped = true;
            agent.velocity = Vector3.zero;
        }
    }

    // 🚑 救護車過後恢復正常
    public void V2X_Reset()
    {
        if (agent != null)
        {
            v2xForceGo = false;
            v2xForceStop = false;
            // 路口計時結束不能把仍在等待救護車的靠邊車拉回原車道。
            if (isYielding || !agent.isActiveAndEnabled || !agent.isOnNavMesh) return;
            yieldStatus = "正常行駛";
            agent.isStopped = false;
            agent.speed = originalSpeed;
            agent.acceleration = 8f; // 恢復正常加速度
            agent.autoBraking = true;
            if (targetNode != null) agent.SetDestination(targetNode.transform.position);
        }
    }
    
}
