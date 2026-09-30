using System;
using System.Collections.Generic;
using System.Linq;
using Newtonsoft.Json.Linq;
using UnityEngine;

/// <summary>
/// Plays an agent reply chunk by chunk. The agent splits the reply into sentences
/// ("chunks") and highlight nodes refer to them by index. A reply without chunks is
/// played as one chunk with every node highlighted.
/// </summary>
public class RTDTextChunkingController
{
    // ===== Dependencies =====
    private readonly InterfaceTextToSpeech _textToSpeech;
    private readonly InterfaceRTDUpdater _rtdUpdater;
    private readonly SpeechSettings _speechSettings;
    // (json, chunk text, chunk index); index is -1 when nodes have no chunk
    private readonly Action<JObject, string, int> _onNodePulsingRequested;

    // ===== State =====
    private bool _chunkModeEnabled = true;
    private List<string> _chunks = new List<string>();
    private int _currentChunkIndex = 0;
    private bool _hasChunkAssignments = false;
    private JObject _lastAgentResponseJson;

    // ===== Constructor =====
    public RTDTextChunkingController(
        InterfaceTextToSpeech textToSpeech,
        InterfaceRTDUpdater rtdUpdater,
        SpeechSettings speechSettings,
        Action<JObject, string, int> onNodePulsingRequested)
    {
        _textToSpeech = textToSpeech;
        _rtdUpdater = rtdUpdater;
        _speechSettings = speechSettings;
        _onNodePulsingRequested = onNodePulsingRequested;
    }

    // ===== Public API =====

    /// <summary>
    /// Toggle chunk mode on/off.
    /// </summary>
    public void ToggleChunkMode()
    {
        _chunkModeEnabled = !_chunkModeEnabled;
        string status = _chunkModeEnabled ? "on" : "off";
        AppLog.Info(LogArea.Setup, $"Chunk mode: {status}");
        _textToSpeech.ConvertTextToSpeech($"Chunk mode {status}", _speechSettings, null);
    }

    /// <summary>
    /// Check if chunk mode is currently enabled.
    /// </summary>
    public bool IsChunkModeEnabled()
    {
        return _chunkModeEnabled;
    }

    /// <summary>
    /// Process text with chunking if enabled, otherwise speak entire text.
    /// </summary>
    public void ProcessText(JObject agentResponseJson, string responseText, Action onComplete = null)
    {
        _lastAgentResponseJson = agentResponseJson;

        if (IsChunkModeEnabled())
        {
            var transmitted = ReadTransmittedChunks(agentResponseJson);
            _hasChunkAssignments = transmitted != null;
            // No chunks in the reply: one chunk, all nodes highlighted
            _chunks = transmitted ?? new List<string> { responseText };
            _currentChunkIndex = 0;

            AppLog.Detail(LogArea.Agent, $"Chunk count: {_chunks.Count} (transmitted: {_hasChunkAssignments})");

            PlayChunk(_lastAgentResponseJson, _chunks[_currentChunkIndex], onComplete);
        }
        else
        {
            // Chunk mode off: one chunk of this reply, so stepping can't replay an
            // earlier reply's chunks.
            _chunks = new List<string> { responseText };
            _currentChunkIndex = 0;
            _hasChunkAssignments = false;

            _textToSpeech.ConvertTextToSpeech(responseText, _speechSettings, onComplete);
            _rtdUpdater.DisplayBrailleLabel(responseText, BrailleLineOwner.Answer);
            _onNodePulsingRequested?.Invoke(_lastAgentResponseJson, responseText, -1);
        }
    }

    /// <summary>The agent's sentence split of the reply, or null if it has none.</summary>
    private static List<string> ReadTransmittedChunks(JObject json)
    {
        if (json?["agent_response_for_user"]?["chunks"] is not JArray arr) return null;

        // Nodes refer to chunks by index, so keep every entry as sent.
        var chunks = arr.Select(t => t?.ToString() ?? string.Empty).ToList();
        return chunks.Any(s => !string.IsNullOrWhiteSpace(s)) ? chunks : null;
    }

    /// <summary>Play the next chunk; false at the last one.</summary>
    public bool AdvanceToNextChunk()
    {
        if (_chunks == null || _chunks.Count == 0) return false;

        if (_currentChunkIndex < _chunks.Count - 1)
        {
            _currentChunkIndex++;
            PlayChunk(_lastAgentResponseJson, _chunks[_currentChunkIndex]);
            return true;
        }
        AppLog.Detail(LogArea.Agent, "Already at last chunk...");
        return false;
    }

    /// <summary>Play the previous chunk from its first braille line; false at the first one.</summary>
    public bool StepBackInChunk()
    {
        if (_chunks == null || _chunks.Count == 0) return false;

        if (_currentChunkIndex > 0)
        {
            _currentChunkIndex--;
            PlayChunk(_lastAgentResponseJson, _chunks[_currentChunkIndex]);
            return true;
        }
        AppLog.Detail(LogArea.Agent, "Already at first chunk...");
        return false;
    }

    // ===== Private Methods =====

    /// <summary>
    /// Speak one chunk, show it on the braille line and highlight its nodes. Avoids
    /// RefreshScreen, which would flash the chart title first. Agent highlights keep a
    /// tail so touched pins pop back up, at the cost of some pin buzzing.
    /// </summary>
    private void PlayChunk(JObject json, string chunk, Action onComplete = null)
    {
        _rtdUpdater.StopAgentHighlights();
        _rtdUpdater.DisplayBrailleLabel(chunk, BrailleLineOwner.Answer);

        AppLog.Detail(LogArea.Agent, $"Calling HandleNodePulsing for chunk {_currentChunkIndex}: '{chunk}'");
        _onNodePulsingRequested?.Invoke(json, chunk, _hasChunkAssignments ? _currentChunkIndex : -1);

        bool isMultiChunk = _chunks != null && _chunks.Count > 1;
        bool isFirst = _currentChunkIndex == 0;
        bool isLast = _chunks == null || _currentChunkIndex == _chunks.Count - 1;

        if (isMultiChunk && isFirst && isLast)
        {
            _textToSpeech.ConvertTextToSpeech($"First... {chunk}", _speechSettings, () =>
            {
                _textToSpeech.ConvertTextToSpeech("End.", _speechSettings, onComplete);
            });
        }
        else if (isMultiChunk && isFirst)
        {
            _textToSpeech.ConvertTextToSpeech($"First... {chunk}", _speechSettings, null);
        }
        else if (isMultiChunk && isLast)
        {
            _textToSpeech.ConvertTextToSpeech(chunk, _speechSettings, () =>
            {
                _textToSpeech.ConvertTextToSpeech("End.", _speechSettings, onComplete);
            });
        }
        else
        {
            _textToSpeech.ConvertTextToSpeech(chunk, _speechSettings, isLast ? onComplete : null);
        }

        // Prepare the next chunk's audio while this one plays.
        if (_chunks != null && _currentChunkIndex + 1 < _chunks.Count)
            _textToSpeech.Prefetch(_chunks[_currentChunkIndex + 1], _speechSettings);
    }

}
