using UnityEngine;
using TMPro;
public class VersionDisplay : MonoBehaviour {
    public static string BuildTime = "13.04. 20:42";
    public TextMeshProUGUI displayText;
    void Start() { if (displayText != null) displayText.text = "Verze: " + BuildTime; }
}