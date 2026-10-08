using UnityEngine;
using System.Collections;
using System.Collections.Generic;

public class NPC_CarSpawner : MonoBehaviour
{
    [Header("生成設定")]
    public GameObject ambulancePrefab; // 救護車 Prefab
    public GameObject[] carPrefabs;    // 普通車 Prefab
    public float spawnInterval = 3f;
    public int maxCars = 150;

    [Header("出生點的節點")]
    public List<TrafficNode> startNodes;

    [Header("救護車指定生成節點")]
    [Tooltip("如果不指定，將會在第一個 startNodes 節點生成救護車")] 
    public TrafficNode ambulanceStartNode;

    [Header("攝影機跟隨設定")]
    // 💡 這裡改成對應新的類別名稱 AmbulanceCameraFollow
    public AmbulanceCameraFollow cameraFollowScript; 

    private bool hasAmbulanceSpawned = false;

    void Start()
    {
        if (ambulancePrefab == null)
        {
            Debug.LogWarning("NPC_CarSpawner: ambulancePrefab 尚未指定。");
        }
        if (startNodes == null || startNodes.Count == 0)
        {
            Debug.LogWarning("NPC_CarSpawner: startNodes 尚未填入任何 TrafficNode。");
        }
        if (ambulanceStartNode != null && (startNodes == null || !startNodes.Contains(ambulanceStartNode)))
        {
            Debug.LogWarning("NPC_CarSpawner: ambulanceStartNode 不在 startNodes 列表中，將無法按指定節點生成。請把該節點加入 startNodes。\n指定節點=" + ambulanceStartNode.name);
        }

        StartCoroutine(SpawnRoutine());
    }

    IEnumerator SpawnRoutine()
    {

        // --- 【新增】在生成普通車之前，先強行生成一台救護車 ---
        if (!hasAmbulanceSpawned && ambulancePrefab != null && startNodes.Count > 0)
        {
            // 決定出生點：有指定就用指定的，沒指定就用第一個 StartNode
            TrafficNode spawnNode = (ambulanceStartNode != null) ? ambulanceStartNode : startNodes[0];

            GameObject newAmb = Instantiate(ambulancePrefab, spawnNode.transform.position, spawnNode.transform.rotation);
            newAmb.tag = "Car";
            newAmb.name = "Ambulance(Clone)";

            NPC_AmbulanceDrive drive = newAmb.GetComponent<NPC_AmbulanceDrive>();
            if (drive != null) drive.targetNode = spawnNode;

            if (cameraFollowScript != null) cameraFollowScript.SetTarget(newAmb.transform);

            hasAmbulanceSpawned = true;
            Debug.Log("🚑 救護車已優先生成在: " + spawnNode.name);
        }
        
        while (true)
        {
            GameObject[] currentCars = GameObject.FindGameObjectsWithTag("Car");
            int currentCount = currentCars.Length;

            if (currentCount < maxCars && startNodes.Count > 0)
            {
                foreach (TrafficNode startNode in startNodes)
                {
                    if (currentCount >= maxCars) break;

                    // 💡【關鍵新增】在生成前，先檢查出生點周圍是否已經有車，避免擠成一團
                    // 用一個小半徑 (例如 4 米) 的球體來偵測
                    Collider[] hitColliders = Physics.OverlapSphere(startNode.transform.position, 4.0f);
                    bool isBlocked = false;
                    foreach (var hitCollider in hitColliders)
                    {
                        if (hitCollider.transform.root.CompareTag("Car"))
                        {
                            isBlocked = true;
                            break;
                        }
                    }
                    if (isBlocked) continue; // 如果出生點被擋住，就跳過這個點，換下一個

                    GameObject prefabToSpawn;
                    bool shouldSpawnAmbulanceHere = !hasAmbulanceSpawned && ambulancePrefab != null &&
                        (ambulanceStartNode == null ? true : startNode == ambulanceStartNode);

                    if (shouldSpawnAmbulanceHere)
                    {
                        prefabToSpawn = ambulancePrefab;
                        GameObject newAmb = Instantiate(prefabToSpawn, startNode.transform.position, startNode.transform.rotation);
                        newAmb.tag = "Car";
                        newAmb.name = "Ambulance(Clone)";
                        Debug.Log("Spawned ambulance at " + newAmb.transform.position + ", start node=" + startNode.name);

                        // 設定導航目標
                        NPC_AmbulanceDrive drive = newAmb.GetComponent<NPC_AmbulanceDrive>();
                        if (drive != null)
                        {
                            drive.targetNode = startNode;
                        }
                        else
                        {
                            Debug.LogWarning("Ambulance prefab 沒有 NPC_AmbulanceDrive 組件！");
                        }

                        // 🎥 關鍵：呼叫新的攝影機腳本鎖定這台救護車
                        if (cameraFollowScript != null)
                        {
                            cameraFollowScript.SetTarget(newAmb.transform);
                        }
                        else
                        {
                            Debug.LogWarning("AmbulanceCameraFollow 尚未指定到 NPC_CarSpawner。");
                        }

                        hasAmbulanceSpawned = true;
                    }
                    else
                    {
                        prefabToSpawn = carPrefabs[Random.Range(0, carPrefabs.Length)];
                        GameObject newCar = Instantiate(prefabToSpawn, startNode.transform.position, startNode.transform.rotation);
                        
                        NPC_WaypointDrive drive = newCar.GetComponent<NPC_WaypointDrive>();
                        if (drive != null) drive.targetNode = startNode;
                    }

                    currentCount++;
                }
            }
            yield return new WaitForSeconds(spawnInterval);
        }
    }
}
