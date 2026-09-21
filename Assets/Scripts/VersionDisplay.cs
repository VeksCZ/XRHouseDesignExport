using TMPro;
using UnityEngine;

public class VersionDisplay : MonoBehaviour
{
    static string buildTime;

    /// <summary>Build timestamp, written to Resources/BuildInfo.txt by the editor build menu before each player build.</summary>
    public static string BuildTime
    {
        get
        {
            if (buildTime == null)
            {
                var asset = Resources.Load<TextAsset>("BuildInfo");
                buildTime = asset != null ? asset.text.Trim() : "dev";
            }
            return buildTime;
        }
    }

    public TextMeshProUGUI displayText;

    void Start()
    {
        if (displayText != null) displayText.text = "Version: " + BuildTime;
    }
}
