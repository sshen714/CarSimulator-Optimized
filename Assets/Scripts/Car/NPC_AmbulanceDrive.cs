using UnityEngine;
using UnityEngine.AI;
using System.Reflection;
using System.Collections.Generic;

public class NPC_AmbulanceDrive : NPC_WaypointDrive
{
    [Header("模式控制")]
    public bool isEmergency = false;
    public float emergencySpeed = 20.0f; // 稍微提高緊急模式速度，使其更明顯
    public float detectRadius = 5f;     // 💡 解決方式：縮小偵測半徑，避免車輛過早避讓

    [Header("自動判斷左右路口")]
    public Transform leftIntersectionCenter;
    public Transform rightIntersectionCenter;

    private bool emergencyOnLeftIntersection = true;
    private string emergencyEWState = "G";
    private string emergencyNSState = "R";

    [Header("緊急模式自動恢復")]
    public float emergencyPassDistance = 25f;
    private Transform currentEmergencyIntersection;
    private bool hasEmergencyIntersection = false;

    [Header("自動導航邏輯")]
    public bool useSmartNavigation = true;
    public int nodesToGoStraight = 1;
    private int nodesPassed = 0;

    [Header("特效組件")]
    public GameObject sirenLights;
    public AudioSource sirenAudio;

    private bool lastEmergencyState;
    private const float NearbyNotificationInterval = 0.1f;
    private float nextNearbyNotificationTime;

    public bool ambulanceWaitingAtRedLight;
    public bool ambulanceFullyStopped;

    protected override void Start()
    {
        base.Start();
        lastEmergencyState = isEmergency;

        if (leftIntersectionCenter == null)
        {
            GameObject obj = GameObject.Find("LeftIntersectionCenter");
            if (obj != null)
                leftIntersectionCenter = obj.transform;
        }

        if (rightIntersectionCenter == null)
        {
            GameObject obj = GameObject.Find("RightIntersectionCenter");
            if (obj != null)
                rightIntersectionCenter = obj.transform;
        }

        UnityEngine.Debug.Log(
            "Auto Find Centers：Left=" +
            (leftIntersectionCenter != null) +
            " Right=" +
            (rightIntersectionCenter != null)
        );
    }

    protected override void Update()
    {
        if (agent == null)
            return;

        HandleStateChange();
        HandleEffects();

        // 🚨 把下面這行加上雙斜線註解掉，救護車就不會自動關閉緊急模式了！
        // CheckEmergencyPassedIntersection();

        if (useSmartNavigation)
            SmartNavigation();
        else
            base.Update();
    }

    private void SmartNavigation()
    {
        bool shouldStop = false;

        if (!isEmergency)
        {
            if (targetNode != null && targetNode.isStopLine && targetNode.currentIsRed)
            {
                isWaitingAtRedLight = true;
                float dist = Vector3.Distance(transform.position, targetNode.transform.position);

                if (dist < 4.0f)
                {
                    shouldStop = true;
                    agent.isStopped = true;
                    agent.velocity = Vector3.zero;
                    agent.ResetPath();
                    isFullyStopped = true;
                    return;
                }
                else if (dist <= 15f)
                {
                    agent.speed = Mathf.Lerp(0, originalSpeed, dist / 15f);
                    agent.isStopped = false;
                    return;
                }
            }
            else
            {
                if (isWaitingAtRedLight)
                {
                    isWaitingAtRedLight = false;
                    isFullyStopped = false;
                    agent.isStopped = false;
                }
            }

            CheckForwardCollision();

            if (agent.isStopped)
                shouldStop = true;
        }
        else
        {
            agent.isStopped = false;
            // V2X 廣播會搜尋場景和周圍碰撞體；每 0.1 秒更新即可保持即時反應。
            if (Time.time >= nextNearbyNotificationTime)
            {
                NotifyNearbyCars();
                nextNearbyNotificationTime = Time.time + NearbyNotificationInterval;
            }
            CheckForwardCollisionCustom(4.0f);

            if (agent.isStopped)
                shouldStop = true;
        }

        if (shouldStop)
        {
            agent.isStopped = true;
            agent.velocity = Vector3.zero;
        }
        else
        {
            agent.isStopped = false;
        }

        if (!isWaitingAtRedLight && !agent.pathPending && agent.remainingDistance < 2.5f)
        {
            nodesPassed++;

            if (targetNode == null)
                return;

            TrafficNode nextNode = null;
            List<TrafficNode> choices = targetNode.nextNodes;

            if (choices != null && choices.Count > 0)
            {
                if (nodesPassed <= nodesToGoStraight || choices.Count == 1)
                    nextNode = FindBestForwardNode(choices);
                else
                    nextNode = FindMostLeftNode(choices);
            }

            if (nextNode != null)
            {
                targetNode = nextNode;
                agent.SetDestination(targetNode.transform.position);
            }
        }
    }

    private void NotifyNearbyCars()
    {
        IntersectionV2X[] intersections = FindObjectsOfType<IntersectionV2X>();

        foreach (var brain in intersections)
        {
            if (brain == null)
                continue;

            if (Vector3.Distance(transform.position, brain.transform.position) > 70f)
                continue;

            brain.AmbulanceApproach(this);

            AutoSetEmergencyDirection();

            currentEmergencyIntersection = brain.transform;
            hasEmergencyIntersection = true;

            ArduinoTrafficLightAutoSync sync =
                FindObjectOfType<ArduinoTrafficLightAutoSync>();

            if (sync != null)
            {
                if (emergencyOnLeftIntersection)
                    sync.LeftEmergency(emergencyEWState, emergencyNSState);
                else
                    sync.RightEmergency(emergencyEWState, emergencyNSState);
            }
        }

        Collider[] hits = Physics.OverlapSphere(transform.position, detectRadius);

        foreach (var hit in hits)
        {
            GameObject vehicleRoot = hit.transform.root.gameObject;

            if (!vehicleRoot.CompareTag("Car") || vehicleRoot == this.gameObject)
                continue;

            NPC_WaypointDrive npc = vehicleRoot.GetComponent<NPC_WaypointDrive>();

            if (npc != null)
                npc.YieldForAmbulance(this);
        }
    }

    private void AutoSetEmergencyDirection()
    {
        if (leftIntersectionCenter == null || rightIntersectionCenter == null)
        {
            UnityEngine.Debug.LogWarning("找不到 LeftIntersectionCenter 或 RightIntersectionCenter");
            return;
        }

        float leftDist = Vector3.Distance(transform.position, leftIntersectionCenter.position);
        float rightDist = Vector3.Distance(transform.position, rightIntersectionCenter.position);

        emergencyOnLeftIntersection = leftDist < rightDist;

        float x = Mathf.Abs(transform.forward.x);
        float z = Mathf.Abs(transform.forward.z);

        if (x > z)
        {
            emergencyEWState = "G";
            emergencyNSState = "R";
        }
        else
        {
            emergencyEWState = "R";
            emergencyNSState = "G";
        }

        UnityEngine.Debug.Log(
            "自動判斷：左路口=" + emergencyOnLeftIntersection +
            " 左距離=" + leftDist.ToString("F1") +
            " 右距離=" + rightDist.ToString("F1") +
            " EW=" + emergencyEWState +
            " NS=" + emergencyNSState
        );
    }

    private void CheckEmergencyPassedIntersection()
    {
        if (!isEmergency)
            return;

        if (!hasEmergencyIntersection || currentEmergencyIntersection == null)
            return;

        float dist = Vector3.Distance(transform.position, currentEmergencyIntersection.position);

        Vector3 dirToIntersection =
            (currentEmergencyIntersection.position - transform.position).normalized;

        float dot = Vector3.Dot(transform.forward, dirToIntersection);

        if (dist > emergencyPassDistance && dot < 0f)
        {
            isEmergency = false;
            hasEmergencyIntersection = false;
            currentEmergencyIntersection = null;

            ArduinoTrafficLightAutoSync sync =
                FindObjectOfType<ArduinoTrafficLightAutoSync>();

            if (sync != null)
                sync.ClearAllEmergency();
        }
    }

    private TrafficNode FindMostLeftNode(List<TrafficNode> choices)
    {
        TrafficNode bestNode = choices[0];
        float minX = float.MaxValue;

        foreach (var node in choices)
        {
            Vector3 relativePos = transform.InverseTransformPoint(node.transform.position);

            if (relativePos.x < minX)
            {
                minX = relativePos.x;
                bestNode = node;
            }
        }

        return bestNode;
    }

    private TrafficNode FindBestForwardNode(List<TrafficNode> choices)
    {
        TrafficNode bestNode = choices[0];
        float maxDot = -2.0f;

        foreach (var node in choices)
        {
            Vector3 dirToNode =
                (node.transform.position - transform.position).normalized;

            float dot = Vector3.Dot(transform.forward, dirToNode);

            if (dot > maxDot)
            {
                maxDot = dot;
                bestNode = node;
            }
        }

        return bestNode;
    }

    private void HandleStateChange()
    {
        if (isEmergency == lastEmergencyState)
            return;

        agent.ResetPath();

        ArduinoTrafficLightAutoSync sync =
            FindObjectOfType<ArduinoTrafficLightAutoSync>();

        if (!isEmergency)
        {
            agent.speed = originalSpeed;
            agent.acceleration = 8f;
            agent.angularSpeed = 120f;

            isWaitingAtRedLight = false;
            isFullyStopped = false;
            agent.isStopped = false;

            hasEmergencyIntersection = false;
            currentEmergencyIntersection = null;

            if (sync != null)
                sync.ClearAllEmergency();
        }
        else
        {
            agent.speed = emergencySpeed;
            agent.acceleration = 40f;
            agent.angularSpeed = 1000f;
            nextNearbyNotificationTime = 0f;

            isWaitingAtRedLight = false;
            isFullyStopped = false;
            agent.isStopped = false;
        }

        if (targetNode != null)
            agent.SetDestination(targetNode.transform.position);

        lastEmergencyState = isEmergency;
    }

    protected void CheckForwardCollisionCustom(float dist)
    {
        RaycastHit hit;
        // 💡【終極修復方案】將「雷射筆」升級成「龜派氣功」，避免偵測死角
        // 使用 SphereCast 射出一條有寬度的射線，確保能掃到前方車輛
        float castRadius = 1.5f;
        Vector3 sensorStartPoint = transform.TransformPoint(sensorOffset);
        if (Physics.SphereCast(sensorStartPoint, castRadius, transform.forward, out hit, dist))
        {
            // 確保不會偵測到自己
            if (hit.collider.CompareTag("Car") && hit.transform.root != this.transform.root)
            {
                agent.isStopped = true;
                agent.velocity = Vector3.zero;
                return;
            }
        }

        agent.isStopped = false;
    }

    private void HandleEffects()
    {
        if (sirenLights != null && sirenLights.activeSelf != isEmergency)
            sirenLights.SetActive(isEmergency);

        if (isEmergency && sirenLights != null)
        {
            LightManager manager = sirenLights.GetComponent<LightManager>();

            if (manager != null)
            {
                FieldInfo field = typeof(LightManager).GetField(
                    "sirenMode",
                    BindingFlags.NonPublic | BindingFlags.Instance
                );

                if (field != null)
                    field.SetValue(manager, 2);

                Light[] lights =
                    sirenLights.GetComponentsInChildren<Light>(true);

                foreach (Light l in lights)
                    l.enabled = true;
            }
        }

        if (sirenAudio != null)
        {
            if (isEmergency && !sirenAudio.isPlaying)
                sirenAudio.Play();
            else if (!isEmergency && sirenAudio.isPlaying)
                sirenAudio.Stop();
        }
    }
}
