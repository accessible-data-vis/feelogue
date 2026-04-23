using System;
using System.Collections;
using System.Collections.Generic;
using UnityEngine;

/// <summary>
/// Manages streaming of full-frame image updates to the DotPad device.
/// Handles line-by-line transmission with ACK/retry logic.
/// </summary>
public class RTDStreamingController
{
    private const float SERIAL_DRAIN_TIMEOUT = 0.5f;
    private const float FIRST_LINE_TIMEOUT_MULTIPLIER = 3f;

    // ===== Dependencies =====
    private readonly MonoBehaviour _host;
    private readonly Action<byte[]> _sendData;
    private readonly Func<bool> _isSerialConnected;
    private readonly Func<int, int, byte[], byte[]> _buildGraphicLineCommand;

    // ===== State =====
    private List<byte[]> _linePackets;
    private int _nextLineToSend;
    private float _lineAckTimeout = 0.15f;
    private readonly int _maxLineRetries;
    private int _currentLineRetryCount;
    private Coroutine _lineTimeoutCoroutine;
    private Coroutine _drainCoroutine;
    private bool _isStreaming = false;
    private int _lastSentLine = 0;
    private int _waitingForAckLine = 0;
    private int _staleAckCount = 0;
    private int _futureAckCount = 0;

    // ===== Events =====
    public event Action StreamingCompleted = delegate { };
    public event Action StreamingFailed = delegate { };

    // ===== Properties =====
    public bool IsStreaming => _isStreaming;
    public int WaitingForAckLine => _waitingForAckLine;

    // ===== Constructor =====
    public RTDStreamingController(
        MonoBehaviour host,
        Action<byte[]> sendData,
        Func<bool> isSerialConnected,
        Func<int, int, byte[], byte[]> buildGraphicLineCommand,
        float ackTimeout,
        int maxLineRetries = 5)
    {
        _host = host;
        _sendData = sendData;
        _isSerialConnected = isSerialConnected;
        _buildGraphicLineCommand = buildGraphicLineCommand;
        _lineAckTimeout = ackTimeout;
        _maxLineRetries = maxLineRetries;
    }

    // ===== Public Methods =====

    /// <summary>
    /// Start streaming an image (builds all line packets and begins transmission).
    /// If already streaming, cancels the current stream first.
    /// </summary>
    public void StartStreaming(int[,] image)
    {
        if (_isStreaming)
            CancelStreaming();

        _isStreaming = true;
        _linePackets = BuildAllLinePackets(image);
        _nextLineToSend = 0;
        _currentLineRetryCount = 0;
        _staleAckCount = 0;
        _futureAckCount = 0;

        if (_lineTimeoutCoroutine != null)
        {
            _host.StopCoroutine(_lineTimeoutCoroutine);
            _lineTimeoutCoroutine = null;
        }

        // Start after drain
        _drainCoroutine = _host.StartCoroutine(BeginStreamAfterDrain());
    }

    /// <summary>
    /// Handle ACK for streaming mode.
    /// </summary>
    public void HandleStreamingAck(int ackedLine)
    {
        if (ackedLine == _waitingForAckLine)
        {
            if (_lineTimeoutCoroutine != null)
            {
                _host.StopCoroutine(_lineTimeoutCoroutine);
                _lineTimeoutCoroutine = null;
            }
            _currentLineRetryCount = 0;
            _nextLineToSend++;
            SendNextLine();
        }
        else
        {
            // Stale/unexpected ACK
            if (ackedLine < _waitingForAckLine)
                _staleAckCount++;
            else
                _futureAckCount++;
        }
    }

    /// <summary>
    /// Cancel streaming (stop coroutines, clear state).
    /// </summary>
    public void CancelStreaming()
    {
        if (_drainCoroutine != null)
        {
            _host.StopCoroutine(_drainCoroutine);
            _drainCoroutine = null;
        }
        if (_lineTimeoutCoroutine != null)
        {
            _host.StopCoroutine(_lineTimeoutCoroutine);
            _lineTimeoutCoroutine = null;
        }
        _isStreaming = false;
    }

    // ===== Internal Methods =====

    private IEnumerator BeginStreamAfterDrain()
    {
        // Wait for a quiet window to let stale ACKs drain
        float start = Time.realtimeSinceStartup;
        while (_isSerialConnected() && Time.realtimeSinceStartup - start < SERIAL_DRAIN_TIMEOUT)
        {
            yield return null;
        }

        _drainCoroutine = null;

        if (!_isSerialConnected())
        {
            Debug.LogWarning("[Streaming] Serial disconnected during drain; aborting stream.");
            _isStreaming = false;
            StreamingFailed?.Invoke();
            yield break;
        }

        SendNextLine();
    }

    private void SendNextLine()
    {
        if (_nextLineToSend >= _linePackets.Count)
        {
            Debug.Log("All lines sent and ACKed.");
            if (_staleAckCount > 0 || _futureAckCount > 0)
                Debug.Log($"ACK summary: stale={_staleAckCount}, future={_futureAckCount}");
            _isStreaming = false;
            StreamingCompleted?.Invoke();
            return;
        }

        _lastSentLine = _nextLineToSend + 1;
        _waitingForAckLine = _lastSentLine;
        _sendData(_linePackets[_nextLineToSend]);

        if (_lineTimeoutCoroutine != null)
            _host.StopCoroutine(_lineTimeoutCoroutine);
        _lineTimeoutCoroutine = _host.StartCoroutine(LineAckTimeout());
    }

    private IEnumerator LineAckTimeout()
    {
        float t = (_nextLineToSend == 0) ? _lineAckTimeout * FIRST_LINE_TIMEOUT_MULTIPLIER : _lineAckTimeout;
        yield return new WaitForSeconds(t);

        _currentLineRetryCount++;

        if (_currentLineRetryCount >= _maxLineRetries)
        {
            Debug.LogError($"[Streaming] Line {_nextLineToSend + 1} failed after {_maxLineRetries} attempts. Aborting stream.");
            _isStreaming = false;
            StreamingFailed?.Invoke();
            yield break;
        }

        Debug.LogWarning($"[Streaming] Line {_nextLineToSend + 1} ACK timed out (attempt {_currentLineRetryCount}/{_maxLineRetries})—resending.");
        SendNextLine();
    }

    private List<byte[]> BuildAllLinePackets(int[,] image)
    {
        // Pack the pixel grid into Braille cells (CELL_HEIGHT x CELL_WIDTH pixels each),
        // then wrap each line's cells into a framed packet.
        byte[] dtm = new byte[RTDConstants.NUM_LINES * RTDConstants.CELLS_PER_LINE];
        for (int cell = 0; cell < dtm.Length; cell++)
        {
            int rowBlock = cell / RTDConstants.CELLS_PER_LINE;
            int colBlock = cell % RTDConstants.CELLS_PER_LINE;
            var block = new bool[RTDConstants.CELL_HEIGHT, RTDConstants.CELL_WIDTH];
            int baseRow = rowBlock * RTDConstants.CELL_HEIGHT;
            int baseCol = colBlock * RTDConstants.CELL_WIDTH;
            for (int r = 0; r < RTDConstants.CELL_HEIGHT; r++)
                for (int c = 0; c < RTDConstants.CELL_WIDTH; c++)
                    block[r, c] = image[baseRow + r, baseCol + c] > 0;
            dtm[cell] = PackCell(block);
        }

        var packets = new List<byte[]>();
        for (int line = 1; line <= RTDConstants.NUM_LINES; line++)
        {
            var lineData = new byte[RTDConstants.CELLS_PER_LINE];
            Array.Copy(dtm, (line - 1) * RTDConstants.CELLS_PER_LINE, lineData, 0, RTDConstants.CELLS_PER_LINE);
            packets.Add(_buildGraphicLineCommand(line, 0, lineData));
        }
        return packets;
    }

    private byte PackCell(bool[,] b)
    {
        int val = 0;
        if (b[0, 0]) val |= 1 << 0;
        if (b[1, 0]) val |= 1 << 1;
        if (b[2, 0]) val |= 1 << 2;
        if (b[3, 0]) val |= 1 << 3;
        if (b[0, 1]) val |= 1 << 4;
        if (b[1, 1]) val |= 1 << 5;
        if (b[2, 1]) val |= 1 << 6;
        if (b[3, 1]) val |= 1 << 7;
        return (byte)val;
    }
}
