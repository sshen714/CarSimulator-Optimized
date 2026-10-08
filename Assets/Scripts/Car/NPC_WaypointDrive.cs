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

        protected bool isYielding = false;
        protected float originalSpeed;
        public bool IsYielding => isYielding;

        protected bool v2xForceGo = false;
        protected bool v2xForceStop = false;

        private enum YieldState { None, MovingToSide, AligningForward }
        private YieldState currentYieldState = YieldState.None;

        protected bool isWaitingAtRedLight = false; // 💡【關鍵新增】紅燈等待狀態鎖
        protected bool isFullyStopped = false;      // 💡【關鍵新增】徹底停止狀態鎖

        protected virtual void Awake() {
            agent = GetComponent<NavMeshAgent>();
        }

        protected virtual void Start() {
            if (agent != null) {
                // 💡【關鍵修改】增加 NavMeshAgent 的避讓半徑，減少車輛重疊
                // 再次增加避讓半徑，強制車輛之間保持更寬的物理距離。
                agent.radius = 2.0f;

                originalSpeed = agent.speed;
                if (targetNode != null) agent.SetDestination(targetNode.transform.position);
            }
        }

    public void YieldForAmbulance(NPC_AmbulanceDrive ambulance)
    {
        // 如果已經在閃了，就不要理會
        if (isYielding || currentYieldState != YieldState.None || ambulance == null) return;

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
            
            // 💡 只有距離路口 15 米內 (大約排在第一、第二台) 的車，才強制闖紅燈加速離開
            if (distToStopLine < 15f)
            {
                V2X_Accelerate(); // 呼叫全速衝刺
                return; // 不執行 S 型避讓
            }
        }

        // 💡 如果以上都不是 (代表在一般道路，或是排在車陣後方)，就乖乖執行 S 型靠邊停車！
        if (agent.isActiveAndEnabled && agent.isOnNavMesh)
        {
            agent.isStopped = false;
        }
        StartCoroutine(S_CurveYieldRoutine(ambulance));
    }

    private IEnumerator S_CurveYieldRoutine(NPC_AmbulanceDrive ambulance)
    {
        isYielding = true;
        currentYieldState = YieldState.MovingToSide;

        agent.isStopped = false;
        agent.ResetPath();
        agent.autoBraking = false;
        agent.velocity = transform.forward * agent.speed;

        agent.speed = originalSpeed * 1.8f;
        agent.acceleration = 120f;
        agent.angularSpeed = 800f;

        float targetOffset = 3.4f; // 目標偏移 3.4 米

        Vector3 initialRoadDir = targetNode != null ? (targetNode.transform.position - transform.position).normalized : transform.forward;
        Vector3 roadRight = Vector3.Cross(Vector3.up, initialRoadDir).normalized;
        float rightClear = targetOffset;
        float leftClear = targetOffset;

        // 🛠️ 關鍵修正 2：把雷達發射點「往上抬高 0.8 米」！
        // 避免雷達掃到地板或車子自己的碰撞體，導致誤判左右沒空間
        Vector3 rayOrigin = transform.position + Vector3.up * 0.8f;
        RaycastHit sideHit;

        if (Physics.Raycast(rayOrigin, roadRight, out sideHit, targetOffset))
        {
            // 如果撞到自己的車，忽略它
            if (sideHit.collider.transform.root != this.transform.root)
                rightClear = Mathf.Max(0.5f, sideHit.distance - 0.8f);
        }
        if (Physics.Raycast(rayOrigin, -roadRight, out sideHit, targetOffset))
        {
            if (sideHit.collider.transform.root != this.transform.root)
                leftClear = Mathf.Max(0.5f, sideHit.distance - 0.8f);
        }

        // 💡【關鍵修改】一律靠右避讓
        // 不再判斷左邊空間，強制往右邊 (roadRight) 避讓。
        targetOffset = rightClear;
        Vector3 offsetDir = roadRight;

        if (rightClear < 1.0f && leftClear < 1.0f)
        {
            agent.isStopped = true;
            agent.velocity = Vector3.zero;
            yield return new WaitForSeconds(0.5f);
        }

        float currentOffset = 0f;
        float timer = 0f;
        float yieldDuration = 5.5f; // 💡 縮短避讓總時間
        float nextPathUpdate = 0f;
        TrafficNode lastPathNode = null;

        while (timer < yieldDuration)
        {
            timer += Time.deltaTime;

            if (ambulance != null)
            {
                Vector3 toCar = transform.position - ambulance.transform.position;

                // 🚨 修正兇手一：內積改成 < -0.2f
                // 只有當「救護車已經超越我，跑到我前面了」，才解除避讓狀態！
                if (toCar.magnitude > 5f && Vector3.Dot(ambulance.transform.forward, toCar.normalized) < -0.2f)
                {
                    break;
                }
            }

            if (targetNode != null)
            {
                Vector3 toNode = targetNode.transform.position - transform.position;
                if (toNode.magnitude < 5.0f || Vector3.Dot(transform.forward, toNode.normalized) < 0.3f)
                {
                    TrafficNode next = targetNode.GetNextNode();
                    if (next != null)
                    {
                        targetNode = next;
                        toNode = targetNode.transform.position - transform.position;
                    }
                }

                // 🚨 修正兇手二：每一幀都重新計算「現在馬路的右側」
                // 確保在彎道時，車子依然會完美地貼著路邊轉彎
                Vector3 currentRoadDir = toNode.normalized;
                offsetDir = Vector3.Cross(Vector3.up, currentRoadDir).normalized;

                currentOffset = Mathf.Lerp(currentOffset, targetOffset, Time.deltaTime * 4.0f);
                Vector3 dynamicTarget = targetNode.transform.position + (offsetDir * currentOffset);

                if (agent.isActiveAndEnabled && agent.isOnNavMesh)
                {
                    // 避免每幀觸發 NavMesh 路徑重算；切換節點時仍立即更新。
                    if (timer >= nextPathUpdate || targetNode != lastPathNode)
                    {
                        agent.SetDestination(dynamicTarget);
                        nextPathUpdate = timer + 0.1f;
                        lastPathNode = targetNode;
                    }
                }
                else
                {
                    break;
                }
            }

            // 💡【保留】在 S 型避讓時的跟車邏輯
            RaycastHit frontHit;
            Vector3 frontSensorStart = transform.position + (Vector3.up * sensorOffset.y) + (transform.forward * sensorOffset.z);
            if (Physics.BoxCast(frontSensorStart, boxHalfExtents, transform.forward, out frontHit, transform.rotation, 5.0f))
            {
                if (frontHit.collider.CompareTag("Car") && frontHit.collider.transform.root != this.transform.root)
                {
                    // 發現前車，切換為慢速跟車模式，而不是完全停止
                    agent.speed = originalSpeed * 0.8f;
                }
            }

            if (timer > 2.0f)
            {
                agent.speed = Mathf.Lerp(agent.speed, originalSpeed * 0.5f, Time.deltaTime * 2.0f);
            }

            yield return null;
        }

        ReturnToTrack();
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
        StartCoroutine(WaitAndReturn(5.0f)); 
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
            if (stoppedByLight) return;

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
        currentYieldState = YieldState.None;
        isYielding = false;
        agent.autoBraking = true; // 恢復正常的自動煞車行為
        
        // 🛡️ 【關鍵防呆】確保車輛還在導航網格上才能恢復，否則會噴紅字錯誤死當！
        if (agent.isActiveAndEnabled && agent.isOnNavMesh) {
            agent.isStopped = false;
            agent.speed = originalSpeed;
            agent.acceleration = 12f; // 恢復時稍微加速

            if (targetNode != null) {
                // 如果目前的點已經在屁股後面，就跳到下一個
                int safety = 0;
                while (safety < 5) {
                    Vector3 toNode = targetNode.transform.position - transform.position;
                    if (Vector3.Dot(transform.forward, toNode.normalized) < 0.3f || toNode.magnitude < 5.0f) {
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
        RaycastHit hit;
        float castRadius = 1.5f;
        float detectDistance = 5.0f; // 💡 把雷達看遠一點，提早反應

        Vector3 sensorStartPoint = transform.position + (Vector3.up * sensorOffset.y) + (transform.forward * sensorOffset.z);

        if (Physics.SphereCast(sensorStartPoint, castRadius, transform.forward, out hit, detectDistance))
        {
            if (hit.collider.CompareTag("Car") && hit.transform.root != this.transform.root)
            {
                // 檢查是不是對向車 (對向車不要理它)
                if (Vector3.Dot(transform.forward, hit.transform.forward) < -0.2f) return;

                float dist = hit.distance;

                // 1. 🚨 終極危險距離：徹底煞停
                if (dist < 3.0f)
                {
                    if (agent.isActiveAndEnabled && agent.isOnNavMesh) {
                        agent.isStopped = true;
                        agent.velocity = Vector3.zero;
                    }
                }
                // 2. ⚠️ 安全距離內：啟動 ACC 平滑跟車減速
                else if (dist < 13.0f)
                {
                    if (agent.isActiveAndEnabled && agent.isOnNavMesh) {
                        agent.isStopped = false;

                        if (!v2xForceGo) // 如果不是在逃離救護車
                        {
                            // 距離越近，safeSpeedRatio 越接近 0；距離越遠，越接近 1
                            float safeSpeedRatio = (dist - 4.0f) / (15.0f - 4.0f);
                            float targetSpeed = originalSpeed * safeSpeedRatio;

                            // 💡 使用 Lerp 讓車速「平滑」過渡到目標速度，就不會點頭頓挫了！
                            agent.speed = Mathf.Lerp(agent.speed, targetSpeed, Time.deltaTime * 5f);
                        }
                    }
                }
                return; // 處理完前車狀況，提早結束
            }
        }

        // 3. 🟢 前方無車：平滑恢復正常速度
        if (agent.isActiveAndEnabled && agent.isOnNavMesh) {
            agent.isStopped = false;
            if (!v2xForceGo && !isWaitingAtRedLight && !isYielding) {
                // 慢慢加速回原本的速度，避免爆衝
                agent.speed = Mathf.Lerp(agent.speed, originalSpeed, Time.deltaTime * 2f);
            }
        }
    }

    protected bool IsInIntersection() {
        if (targetNode == null) return false;
        float dist = Vector3.Distance(transform.position, targetNode.transform.position);
        return dist > 4f && !targetNode.isStopLine;
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
            v2xForceGo = true;
            v2xForceStop = false;
            currentYieldState = YieldState.None;
            isYielding = false;
            isWaitingAtRedLight = false;
            isFullyStopped = false;
            
            agent.isStopped = false;
            agent.speed = Mathf.Max(agent.speed, originalSpeed * 1.5f); // 速度再拉高一點
            agent.acceleration = 60f;
            
            // 💡 【關鍵修改】關閉自動煞車！確保車子在路口內逃亡時，經過節點絕對不會卡頓減速
            agent.autoBraking = false; 
            
            if (targetNode != null && !agent.hasPath)
                agent.SetDestination(targetNode.transform.position);
        }
    }

    // 🚑 救護車 V2X 指令：原地強制煞停
    public void V2X_ForceStop() 
    {
        if (agent != null)
        {
            v2xForceGo = false;
            v2xForceStop = true;
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
            agent.isStopped = false;
            agent.speed = originalSpeed;
            agent.acceleration = 8f; // 恢復正常加速度
            agent.autoBraking = true;
            if (targetNode != null) agent.SetDestination(targetNode.transform.position);
        }
    }
    
}
