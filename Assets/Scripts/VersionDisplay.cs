using UnityEngine;
using TMPro;
public class VersionDisplay : MonoBehaviour {
    public static string BuildTime = "25.04. 20:07:53";
    public TextMeshProUGUI displayText;
    void Start() { if (displayText != null) displayText.text = "Version: " + BuildTime; }
}