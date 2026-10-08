using UnityEngine;

public class PathAutoLinker : MonoBehaviour
{
    [ContextMenu("🔄 一鍵反轉整條道路方向")]
    public void ReversePath()
    {
        // 抓出這個資料夾底下的所有 Node
        TrafficNode[] allNodes = GetComponentsInChildren<TrafficNode>();

        // 先把所有人現在的連線全部剪斷清空
        foreach (TrafficNode node in allNodes)
        {
            node.nextNodes.Clear();
        }

        // 倒過來重新連線！(讓最後一個點，連回前一個點)
        for (int i = allNodes.Length - 1; i > 0; i--)
        {
            allNodes[i].nextNodes.Add(allNodes[i - 1]);
        }

        Debug.Log("✅ 整條馬路已經成功反轉！");
    }
}