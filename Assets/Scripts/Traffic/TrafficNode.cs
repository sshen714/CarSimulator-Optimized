using UnityEngine;
using System.Collections.Generic;

public class TrafficNode : MonoBehaviour
{
    public List<TrafficNode> nextNodes;

    [Header("🚦 紅綠燈系統設定")]
    [Tooltip("打勾代表這個點是路口的停止線")]
    public bool isStopLine = false;   
    
    [Tooltip("目前是不是紅燈？(這個會自動跟隨套件跳動！)")]
    public bool currentIsRed = false; 

    [Header("🔌 連接紅綠燈套件")]
    [Tooltip("把負責紅燈的那顆『燈泡物件』拖進來！")]
    public GameObject redLightModel; // 👈 就是漏掉這超級關鍵的一行啦！

    // 每一幀檢查紅綠燈狀態
    void Update()
    {
        // 如果這個點是停止線，而且你有把紅燈模型拖給它
        if (isStopLine && redLightModel != null)
        {
            // 翻譯蒟蒻：如果那顆紅燈物件被打開了 (SetActive(true))，就代表現在是紅燈！
            currentIsRed = redLightModel.activeInHierarchy;
        }
    }

    // 畫黃線的視覺化功能
    private void OnDrawGizmos()
    {
        if (nextNodes == null) return;
        Gizmos.color = Color.yellow;
        foreach (TrafficNode node in nextNodes)
        {
            if (node != null)
            {
                // 畫一條線連接到下一個點
                Gizmos.DrawLine(transform.position, node.transform.position);
                // 在自己身上畫一個小圓圈代表節點
                Gizmos.DrawWireSphere(transform.position, 0.5f);
            }
        }
    }

    // 取得下一個節點的功能
    public TrafficNode GetNextNode()
    {
        if (nextNodes == null || nextNodes.Count == 0) return null; // 沒路了(死胡同)

        // 如果只有一條路，就走那一條
        if (nextNodes.Count == 1) return nextNodes[0];

        // 🎲 如果是十字路口 (有多條路)，就隨機選一條！
        int randomIndex = Random.Range(0, nextNodes.Count);
        return nextNodes[randomIndex];
    }
}