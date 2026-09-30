using UnityEngine;

/// <summary>Switches on detailed logging per area. Optional: without it, only Info lines show.</summary>
public class LogSettings : MonoBehaviour
{
    [Header("Detailed logging (Info lines always show)")]
    [SerializeField] private bool agent;
    [SerializeField] private bool presentation;
    [SerializeField] private bool speech;
    [SerializeField] private bool buttons;
    [SerializeField] private bool touch;
    [SerializeField] private bool chart;
    [Tooltip("One line per pin or node on every redraw")]
    [SerializeField] private bool render;
    [SerializeField] private bool braille;
    [SerializeField] private bool device;
    [SerializeField] private bool setup;

    private void OnEnable() => Apply();
    private void OnValidate() => Apply();

    private void Apply()
    {
        AppLog.SetDetail(LogArea.Agent, agent);
        AppLog.SetDetail(LogArea.Presentation, presentation);
        AppLog.SetDetail(LogArea.Speech, speech);
        AppLog.SetDetail(LogArea.Buttons, buttons);
        AppLog.SetDetail(LogArea.Touch, touch);
        AppLog.SetDetail(LogArea.Chart, chart);
        AppLog.SetDetail(LogArea.Render, render);
        AppLog.SetDetail(LogArea.Braille, braille);
        AppLog.SetDetail(LogArea.Device, device);
        AppLog.SetDetail(LogArea.Setup, setup);
    }
}
