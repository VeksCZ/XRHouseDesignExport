using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Rendering.Universal;
using UnityEngine.XR;

/// <summary>
/// Fixed, crisp rendering for a scene made of large flat-coloured surfaces, where any loss of resolution shows as
/// big stair-stepped edges: OVRManager's dynamic resolution (could drop to 0.7 / 1.6 of the eye buffer under load)
/// and dynamic foveated rendering (blocky periphery) are switched off, MSAA 4x and a fixed render scale are set.
/// Logs the resulting values so a screenshot can be matched to the actual settings.
/// </summary>
public class RenderQuality : MonoBehaviour
{
    public XRMenu uiLog;
    public float renderScale = 1.4f;
    public int msaa = 4;

    void Start()
    {
        var mgr = OVRManager.instance;
        if (mgr != null) mgr.enableDynamicResolution = false;
        OVRManager.useDynamicFoveatedRendering = false;
        OVRManager.foveatedRenderingLevel = OVRManager.FoveatedRenderingLevel.Off;

        // Not in the Editor: there the URP asset change would be saved into the project.
        if (!Application.isEditor && GraphicsSettings.currentRenderPipeline is UniversalRenderPipelineAsset urp)
        {
            urp.msaaSampleCount = msaa;
            urp.renderScale = renderScale;
        }
        XRSettings.renderViewportScale = 1f;

        var asset = GraphicsSettings.currentRenderPipeline as UniversalRenderPipelineAsset;
        string info = $"Render: scale {(asset ? asset.renderScale : -1):0.00}, MSAA {(asset ? asset.msaaSampleCount : -1)}x, " +
                      $"eye tex {XRSettings.eyeTextureWidth}x{XRSettings.eyeTextureHeight} (x{XRSettings.eyeTextureResolutionScale:0.00}), " +
                      $"viewport {XRSettings.renderViewportScale:0.00}, foveation {OVRManager.foveatedRenderingLevel}";
        Debug.Log("[RenderQuality] " + info);
        uiLog?.AddLog(info);
    }
}
