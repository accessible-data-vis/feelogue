using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using Newtonsoft.Json.Linq;
using UnityEngine;

/// <summary>
/// Manages text chunking for long agent responses.
/// Splits text into sentences and provides navigation (next/previous).
/// </summary>
public class RTDTextChunkingController
{
    // ===== Dependencies =====
    private readonly InterfaceTextToSpeech _textToSpeech;
    private readonly InterfaceRTDUpdater _rtdUpdater;
    private readonly SpeechSettings _speechSettings;
    private readonly Action<JObject, string, bool> _onNodePulsingRequested;

    // ===== State =====
    private bool _chunkModeEnabled = true;
    private List<string> _chunks = new List<string>();
    private int _currentChunkIndex = 0;
    private JObject _lastAgentResponseJson;

    // ===== Constants =====

    // Pass 1: split before "N. Capital" when preceded by sentence-ending punctuation or colon
    private static readonly Regex ListItemRegex = new Regex(@"(?<=[.:?!])\s+(?=\d+\.\s+[A-Z])", RegexOptions.Compiled);

    // Pass 2: split on sentence boundaries within a chunk (leading list number already stripped)
    private static readonly Regex SentenceRegex = new Regex(@"(?<=[a-zA-Z%][.?!])\s+(?=[A-Z(])|(?<=\d+\.)\s+(?=[A-Z(])", RegexOptions.Compiled);

    // Detects a leading list number at the start of a chunk, e.g. "6. "
    private static readonly Regex LeadingListNumberRegex = new Regex(@"^\d+\.\s+", RegexOptions.Compiled);

    // ===== Constructor =====
    public RTDTextChunkingController(
        InterfaceTextToSpeech textToSpeech,
        InterfaceRTDUpdater rtdUpdater,
        SpeechSettings speechSettings,
        Action<JObject, string, bool> onNodePulsingRequested)
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
        UnityEngine.Debug.Log($"Chunk mode: {status}");
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
            _chunks = ChunkBySentence(responseText);
            _currentChunkIndex = 0;

            if (_chunks.Count == 0)
            {
                // Fallback: nothing to chunk
                _textToSpeech.ConvertTextToSpeech(responseText, _speechSettings, onComplete);
                _rtdUpdater.DisplayBrailleLabel(responseText);
                _onNodePulsingRequested?.Invoke(_lastAgentResponseJson, responseText, false);
                return;
            }

            PlayChunk(_lastAgentResponseJson, _chunks[_currentChunkIndex], onComplete);
        }
        else
        {
            _textToSpeech.ConvertTextToSpeech(responseText, _speechSettings, onComplete);
            _rtdUpdater.DisplayBrailleLabel(responseText);
            _onNodePulsingRequested?.Invoke(_lastAgentResponseJson, responseText, false);
        }
    }

    /// <summary>
    /// Advance to the next chunk (called when user presses button).
    /// </summary>
    public void AdvanceToNextChunk()
    {
        if (_chunks == null || _chunks.Count == 0) return;

        if (_currentChunkIndex < _chunks.Count - 1)
        {
            _currentChunkIndex++;
            PlayChunk(_lastAgentResponseJson, _chunks[_currentChunkIndex]);
        }
        else
        {
            UnityEngine.Debug.Log("Already at last chunk...");
        }
    }

    /// <summary>
    /// Step back to the previous chunk (called when user presses button).
    /// </summary>
    public void StepBackInChunk()
    {
        if (_chunks == null || _chunks.Count == 0) return;

        if (_currentChunkIndex > 0)
        {
            _currentChunkIndex--;
            PlayChunk(_lastAgentResponseJson, _chunks[_currentChunkIndex]);
        }
        else
        {
            UnityEngine.Debug.Log("Already at first chunk...");
        }
    }

    // ===== Private Methods =====

    /// <summary>
    /// Play a single chunk with TTS and update display.
    /// NOTE: Don't call RefreshScreen() here because it displays BaseTitle on the braille line,
    /// causing a flash before the chunk text appears. Instead, clear agent highlights and display the chunk directly.
    ///
    /// PERFORMANCE NOTE: Chunk highlights may appear slower and cause pin buzzing compared to navigation
    /// because ShowShape() uses enableTail=true (keeps pins raised when touched) whereas navigation uses
    /// enableTail=false. The tail coroutine repeatedly sends commands to maintain highlights, which causes
    /// physical pin buzzing. Trade-off: enableTail=true allows tactile exploration without pins staying down
    /// when touched, but causes buzzing. To eliminate buzzing, ShowShape would need enableTail=false, but
    /// then touched pins wouldn't pop back up during exploration.
    /// </summary>
    private void PlayChunk(JObject json, string chunk, Action onComplete = null)
    {
        _rtdUpdater.StopPulsePins();
        _rtdUpdater.ClearHighlights("agent");
        _rtdUpdater.DisplayBrailleLabel(chunk);

        UnityEngine.Debug.Log($"Calling HandleNodePulsing for chunk {_currentChunkIndex}: '{chunk}'");
        _onNodePulsingRequested?.Invoke(json, chunk, true);

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
    }

    /// <summary>
    /// Split text into chunks using a two-pass approach:
    /// Pass 1 splits on numbered list item boundaries (e.g. "1. Sentence"),
    /// Pass 2 splits on sentence boundaries within each resulting part.
    /// Stripping the leading list number before Pass 2 prevents false splits
    /// on the item number itself, then reattaches it to the first sentence.
    /// </summary>
    private List<string> ChunkBySentence(string text)
    {
        text = (text ?? string.Empty).Trim();

        // Pass 1: split on numbered list item boundaries
        var pass1 = ListItemRegex.Split(text)
            .Select(s => s.Trim())
            .Where(s => s.Length > 0)
            .ToList();

        // Pass 2: sentence-split within each part
        var result = new List<string>();
        foreach (var part in pass1)
        {
            var leadingMatch = LeadingListNumberRegex.Match(part);
            string prefix = leadingMatch.Success ? leadingMatch.Value : string.Empty;
            string body   = leadingMatch.Success ? part.Substring(leadingMatch.Length) : part;

            var sentences = SentenceRegex.Split(body)
                .Select(s => s.Trim())
                .Where(s => s.Length > 0)
                .ToList();

            if (sentences.Count == 0)
            {
                result.Add(part);
            }
            else
            {
                result.Add(prefix + sentences[0]);
                result.AddRange(sentences.Skip(1));
            }
        }

        UnityEngine.Debug.Log($"Chunk count: {result.Count}");
        for (int i = 0; i < result.Count; i++)
            UnityEngine.Debug.Log($"Chunk {i}: '{result[i]}' (length: {result[i].Length})");

        return result;
    }
}
