
using System.IO.Ports;
using UnityEngine;

public class ArduinoTrafficLightAutoSync : MonoBehaviour
{
    [Header("Serial Port")]
    public string portName = "COM5";
    public int baudRate = 9600;

    [Header("左路口代表燈")]
    public GameObject leftEW;
    public GameObject leftNS;

    [Header("右路口代表燈")]
    public GameObject rightEW;
    public GameObject rightNS;

    private SerialPort serial;
    private string lastMsg = "";

    private bool leftEmergency = false;
    private bool rightEmergency = false;

    private string leftEWOverride = "R";
    private string leftNSOverride = "R";
    private string rightEWOverride = "R";
    private string rightNSOverride = "R";

    void Start()
    {
        try
        {
            serial = new SerialPort(portName, baudRate);
            serial.ReadTimeout = 50;
            serial.WriteTimeout = 50;
            serial.Open();

            Debug.Log("Arduino Connected : " + portName);
        }
        catch
        {
            Debug.LogError("Arduino 連線失敗，確認 COM Port");
        }
    }

    void Update()
    {
        if (serial == null || !serial.IsOpen)
            return;

        string lEW = leftEmergency ? leftEWOverride : GetLightState(leftEW);
        string lNS = leftEmergency ? leftNSOverride : GetLightState(leftNS);

        string rEW = rightEmergency ? rightEWOverride : GetLightState(rightEW);
        string rNS = rightEmergency ? rightNSOverride : GetLightState(rightNS);

        string msg =
            "L_EW_" + lEW + "," +
            "L_NS_" + lNS + "," +
            "R_EW_" + rEW + "," +
            "R_NS_" + rNS;

        if (msg != lastMsg)
        {
            lastMsg = msg;
            Debug.Log("Arduino Sync : " + msg);
            serial.WriteLine(msg);
        }
    }

    public void LeftEmergency(string ewState, string nsState)
    {
        leftEmergency = true;
        leftEWOverride = ewState;
        leftNSOverride = nsState;
    }

    public void RightEmergency(string ewState, string nsState)
    {
        rightEmergency = true;
        rightEWOverride = ewState;
        rightNSOverride = nsState;
    }

    public void ClearLeftEmergency()
    {
        leftEmergency = false;
        lastMsg = "";
    }

    public void ClearRightEmergency()
    {
        rightEmergency = false;
        lastMsg = "";
    }

    public void ClearAllEmergency()
    {
        leftEmergency = false;
        rightEmergency = false;
        lastMsg = "";
    }

    string GetLightState(GameObject root)
    {
        if (root == null)
            return "R";

        float redPower = GetLightPower(root, "60_light_auto_red", "60_light_auto_red_2");
        float yellowPower = GetLightPower(root, "61_light_auto_yellow", "61_light_auto_yellow_2");
        float greenPower = GetLightPower(root, "62_light_auto_green", "62_light_auto_green_2");

        if (yellowPower > redPower && yellowPower > greenPower)
            return "Y";

        if (greenPower > redPower && greenPower > yellowPower)
            return "G";

        return "R";
    }

    float GetLightPower(GameObject root, string name1, string name2)
    {
        Renderer[] renderers = root.GetComponentsInChildren<Renderer>(true);
        float maxPower = 0f;

        foreach (Renderer r in renderers)
        {
            string objName = r.gameObject.name.ToLower();

            if (objName != name1 && objName != name2)
                continue;

            if (!r.gameObject.activeInHierarchy || !r.enabled)
                continue;

            Material mat = r.material;
            float power = 0f;

            if (mat.HasProperty("_EmissionColor"))
            {
                Color emission = mat.GetColor("_EmissionColor");
                power += emission.r + emission.g + emission.b;
            }

            if (mat.HasProperty("_Color"))
            {
                Color color = mat.GetColor("_Color");
                power += color.r + color.g + color.b;
            }

            if (power > maxPower)
                maxPower = power;
        }

        return maxPower;
    }

    void OnApplicationQuit()
    {
        if (serial != null && serial.IsOpen)
        {
            serial.WriteLine("STOP");
            serial.Close();
        }
    }
}