using UnityEngine;
using TMPro;
public class VersionDisplay : MonoBehaviour {
    public static string BuildTime = "10.04. 14:24";
    public TextMeshProUGUI displayText;
    void Start() { if (displayText != null) displayText.text = "Verze: " + BuildTime; }
}