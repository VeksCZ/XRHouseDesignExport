using System.Threading.Tasks;
using TMPro;
using UnityEngine;
using UnityEngine.UI;

/// <summary>
/// A brief branded splash shown right after Meta's own passthrough/loading environment settles - app name,
/// version and a progress bar that fades into the wrist menu. There's no real async work at startup worth
/// tracking yet (the scan itself only loads on demand, not eagerly at launch), so the bar fills over a fixed
/// short duration rather than faking progress for operations that don't exist - if a genuinely slow startup
/// step is ever added here, this is the place to drive the bar from it instead.
/// </summary>
public class SplashScreen : MonoBehaviour
{
    const float Duration = 2.0f;
    const float FadeOut = 0.4f;
    const float Distance = 1.0f;

    public async Task ShowAndWait()
    {
        var canvas = XRUi.CreateWorldCanvas("Splash", new Vector2(900f, 560f), 0.0009f);
        var root = canvas.transform;

        var cam = Camera.main;
        if (cam != null)
        {
            Vector3 fwd = Vector3.ProjectOnPlane(cam.transform.forward, Vector3.up);
            fwd = fwd.sqrMagnitude < 0.01f ? Vector3.forward : fwd.normalized;
            canvas.transform.SetPositionAndRotation(cam.transform.position + fwd * Distance, Quaternion.LookRotation(fwd, Vector3.up));
            canvas.worldCamera = cam;
        }

        XRUi.CreatePanel(root, "Background", XRUi.PanelColor, 0, 0, 900, 560);
        XRUi.CreateText(root, "Name", "XR House Export", 56, TextAlignmentOptions.Center, 0, 190, 900, 80, XRUi.TextColor, FontStyles.Bold);
        XRUi.CreateText(root, "Version", $"v{Application.version}  •  {VersionDisplay.BuildTime}", 26, TextAlignmentOptions.Center, 0, 280, 900, 40, XRUi.MutedText);

        const float barW = 500f, barX = (900f - barW) / 2f, barY = 380f;
        XRUi.CreatePanel(root, "BarBack", new Color(1, 1, 1, 0.12f), barX, barY, barW, 14);
        var barFill = XRUi.CreatePanel(root, "BarFill", XRUi.AccentText, barX, barY, 0, 14);

        float t = 0f;
        while (t < Duration)
        {
            t += Time.deltaTime;
            barFill.rectTransform.sizeDelta = new Vector2(barW * Mathf.Clamp01(t / Duration), 14f);
            await Task.Yield();
        }

        var cg = root.gameObject.AddComponent<CanvasGroup>();
        float ft = 0f;
        while (ft < FadeOut)
        {
            ft += Time.deltaTime;
            cg.alpha = 1f - Mathf.Clamp01(ft / FadeOut);
            await Task.Yield();
        }
        Destroy(canvas.gameObject);
    }
}
