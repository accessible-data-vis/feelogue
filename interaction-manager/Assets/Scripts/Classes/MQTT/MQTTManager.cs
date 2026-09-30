using System;
using UnityEngine;

public class MQTTManager : MonoBehaviour, InterfaceMQTTManager
{
    [Header("MQTT Clients")]
    [SerializeField] public MQTTReceiver localMQTT;   // assign in Inspector
    [SerializeField] public MQTTReceiver remoteMQTT;  // assign in Inspector
    public static bool usingRemote { get; private set; } = false;
    public event Action<string, string> MessageReceived;
    public event Action MQTTConnected;  // Fired when MQTT successfully connects

    void Start()
    {
        // Local broker (localhost:1883) by default; the GUI toggle switches to remote.
        // Set here as well because statics survive domain reloads when those are off.
        usingRemote = false;
        remoteMQTT.enabled = false;
        remoteMQTT.Disconnect();

        localMQTT.enabled = true;
        localMQTT.Connect();
        localMQTT.MessageReceived += OnMessageReceived;
        localMQTT.Connected += OnMQTTConnected;
    }

    private void OnMQTTConnected()
    {
        AppLog.Info(LogArea.Setup, $"MQTT Connected [{(usingRemote ? "REMOTE" : "LOCAL")}]");
        MQTTConnected?.Invoke();
    }

    public void ToggleMQTTSource()
    {
        usingRemote = !usingRemote;

        if (usingRemote)
        {
            localMQTT.MessageReceived -= OnMessageReceived;
            localMQTT.Connected -= OnMQTTConnected;
            localMQTT.Disconnect();
            localMQTT.enabled = false;

            remoteMQTT.enabled = true;
            remoteMQTT.Connect();
            remoteMQTT.MessageReceived += OnMessageReceived;
            remoteMQTT.Connected += OnMQTTConnected;
            AppLog.Info(LogArea.Setup, "Switched to REMOTE MQTT");
        }
        else
        {
            remoteMQTT.MessageReceived -= OnMessageReceived;
            remoteMQTT.Connected -= OnMQTTConnected;
            remoteMQTT.Disconnect();
            remoteMQTT.enabled = false;

            localMQTT.enabled = true;
            localMQTT.Connect();
            localMQTT.MessageReceived += OnMessageReceived;
            localMQTT.Connected += OnMQTTConnected;
            AppLog.Info(LogArea.Setup, "Switched to LOCAL MQTT");
        }
    }

    public MQTTReceiver GetActiveMQTTInstance()
    {
        return usingRemote ? remoteMQTT : localMQTT;
    }

    public bool IsUsingRemote()
    {
        return usingRemote;
    }

    public void PublishInteraction(string json)
    {
        var client = usingRemote ? remoteMQTT : localMQTT;
        client.PublishInteraction(json);
    }

    public void PublishChart(string chartData)
    {
        var client = usingRemote ? remoteMQTT : localMQTT;
        client.PublishChart(chartData);
    }

    public void OnMessageReceived(string topic, string payload)
    {
        AppLog.Detail(LogArea.Agent, $"MQTT RECEIVED [{(usingRemote ? "REMOTE" : "LOCAL")}]: {topic} -> {payload}");
        // Only raise if the sender is the current active one
        if ((usingRemote && remoteMQTT.enabled) || (!usingRemote && localMQTT.enabled))
            MessageReceived?.Invoke(topic, payload);
    }
}
