using UnityEngine;
using TMPro;
public class VersionDisplay : MonoBehaviour {
    public static string BuildTime = "17.04. 09:57";
    public TextMeshProUGUI displayText;
    void Start() { if (displayText != null) displayText.text = "Version: " + BuildTime; }
}