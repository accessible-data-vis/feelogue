using System;
using System.Collections.Generic;
using UnityEngine;

/// <summary>
/// Manages image buffers, overlay state, braille pages, and overview layers.
/// Provides a centralized view of what should be displayed on the DotPad.
/// </summary>
public class RTDBufferManager
{
    // ===== Buffers =====
    private readonly int[,] _baseImage = new int[RTDConstants.PIXEL_ROWS, RTDConstants.PIXEL_COLS];
    private readonly sbyte[,] _overlay = new sbyte[RTDConstants.PIXEL_ROWS, RTDConstants.PIXEL_COLS];
    private int[,] _originalImage;
    private string _baseTitle = "";

    // ===== Braille State =====
    private List<string> _braillePages;
    private int _currentBraillePage;
    private string _lastBraille;
    private string _lastBrailleInput = "";

    // ===== Overview State =====
    private int _currentOverviewLayer = -1;

    // ===== Properties =====
    public int[,] BaseImage => _baseImage;
    public sbyte[,] Overlay => _overlay;
    public int[,] OriginalImage => _originalImage;
    public string BaseTitle => _baseTitle;
    public string LastBraille => _lastBraille;

    public int CurrentBraillePage => _currentBraillePage;
    public int TotalBraillePages => _braillePages?.Count ?? 0;
    public string CurrentBrailleHex => (_braillePages != null && _currentBraillePage < _braillePages.Count)
        ? _braillePages[_currentBraillePage] : null;

    public int CurrentOverviewLayer => _currentOverviewLayer;

    // ===== Buffer Operations =====

    /// <summary>
    /// Set the title for the current display.
    /// </summary>
    public void SetTitle(string title)
    {
        _baseTitle = title ?? "";
    }

    /// <summary>
    /// Store a snapshot of the current base image.
    /// </summary>
    public void CacheOriginalImage()
    {
        _originalImage = (int[,])_baseImage.Clone();
    }

    /// <summary>
    /// Restore the base image from the cached original.
    /// </summary>
    public void RestoreFromOriginal()
    {
        if (_originalImage == null)
            return;

        for (int y = 0; y < RTDConstants.PIXEL_ROWS; y++)
            for (int x = 0; x < RTDConstants.PIXEL_COLS; x++)
                _baseImage[y, x] = _originalImage[y, x];
    }

    /// <summary>
    /// Copy an entire image into the base buffer.
    /// </summary>
    public void SetImage(int[,] image)
    {
        if (image == null || image.GetLength(0) != RTDConstants.PIXEL_ROWS || image.GetLength(1) != RTDConstants.PIXEL_COLS)
        {
            Debug.LogError("[Buffer] Invalid image dimensions");
            return;
        }

        for (int y = 0; y < RTDConstants.PIXEL_ROWS; y++)
            for (int x = 0; x < RTDConstants.PIXEL_COLS; x++)
                _baseImage[y, x] = image[y, x];

        // Cache the original for refresh functionality
        CacheOriginalImage();
    }

    /// <summary>
    /// Set a single pixel in the base image.
    /// </summary>
    public void SetPixel(int y, int x, bool raised)
    {
        if (y >= 0 && y < RTDConstants.PIXEL_ROWS && x >= 0 && x < RTDConstants.PIXEL_COLS)
        {
            _baseImage[y, x] = raised ? 1 : 0;
        }
    }

    // ===== View Operations (base + overlay) =====

    /// <summary>
    /// Get the composed view bit (base + overlay) for a specific pixel.
    /// </summary>
    public bool GetViewBit(int y, int x)
    {
        if (y < 0 || y >= RTDConstants.PIXEL_ROWS || x < 0 || x >= RTDConstants.PIXEL_COLS)
            return false;

        sbyte o = _overlay[y, x];
        if (o == 1) return true;
        if (o == -1) return false;
        return _baseImage[y, x] > 0;
    }

    /// <summary>
    /// Pack a 2x4 cell from the view (base + overlay) into a byte.
    /// </summary>
    public byte PackCellFromView(int blockRow, int blockCol)
    {
        int baseY = blockRow * RTDConstants.CELL_HEIGHT;
        int baseX = blockCol * RTDConstants.CELL_WIDTH;
        int v = 0;
        if (GetViewBit(baseY + 0, baseX + 0)) v |= 1 << 0;
        if (GetViewBit(baseY + 1, baseX + 0)) v |= 1 << 1;
        if (GetViewBit(baseY + 2, baseX + 0)) v |= 1 << 2;
        if (GetViewBit(baseY + 3, baseX + 0)) v |= 1 << 3;
        if (GetViewBit(baseY + 0, baseX + 1)) v |= 1 << 4;
        if (GetViewBit(baseY + 1, baseX + 1)) v |= 1 << 5;
        if (GetViewBit(baseY + 2, baseX + 1)) v |= 1 << 6;
        if (GetViewBit(baseY + 3, baseX + 1)) v |= 1 << 7;
        return (byte)v;
    }

    /// <summary>
    /// Build line bytes from view for a specific line (1-based).
    /// </summary>
    public byte[] BuildLineBytesFromView(int line1Based)
    {
        var lineData = new byte[RTDConstants.CELLS_PER_LINE];
        int rowBlock = line1Based - 1;

        if (rowBlock < 0 || rowBlock >= RTDConstants.PIXEL_ROWS / RTDConstants.CELL_HEIGHT)
            return lineData;

        for (int colBlock = 0; colBlock < RTDConstants.CELLS_PER_LINE; colBlock++)
            lineData[colBlock] = PackCellFromView(rowBlock, colBlock);

        return lineData;
    }

    // ===== Braille Management =====

    /// <summary>
    /// Set braille text and generate pages.
    /// </summary>
    public void SetBrailleText(string text)
    {
        _lastBrailleInput = text ?? "";
        _braillePages = BrailleTranslator.ToPagedDotHex(text);
        _currentBraillePage = 0;

        if (_braillePages == null || _braillePages.Count == 0)
        {
            Debug.LogWarning("[Buffer] No braille to display. Input text was empty or invalid.");
            return;
        }

        _lastBraille = _braillePages[_currentBraillePage];
    }

    /// <summary>
    /// Re-translates the last input text with the current BrailleTranslator.Mode.
    /// Returns true if there was text to refresh.
    /// </summary>
    public bool RefreshBrailleText()
    {
        if (string.IsNullOrEmpty(_lastBrailleInput)) return false;
        SetBrailleText(_lastBrailleInput);
        return true;
    }

    /// <summary>
    /// Advance to next braille page.
    /// </summary>
    public bool NextBraillePage()
    {
        if (_braillePages != null && _currentBraillePage < _braillePages.Count - 1)
        {
            _currentBraillePage++;
            _lastBraille = _braillePages[_currentBraillePage];
            return true;
        }
        return false;
    }

    /// <summary>
    /// Go back to previous braille page.
    /// </summary>
    public bool PrevBraillePage()
    {
        if (_braillePages != null && _currentBraillePage > 0)
        {
            _currentBraillePage--;
            _lastBraille = _braillePages[_currentBraillePage];
            return true;
        }
        return false;
    }

    /// <summary>
    /// Set braille directly from hex string (bypasses translation).
    /// </summary>
    public void SetBrailleHex(string hexBraille)
    {
        if (hexBraille == null || hexBraille.Length != RTDConstants.TEXT_COLS * 2)
        {
            Debug.LogError($"[Buffer] Invalid braille hex length: expected {RTDConstants.TEXT_COLS * 2}, got {hexBraille?.Length}");
            return;
        }

        _lastBraille = hexBraille;
    }

    // ===== Hex Parsing =====

    /// <summary>
    /// Parse a braille hex string into a byte array.
    /// </summary>
    public static byte[] ParseBrailleHex(string hex)
    {
        var bytes = new byte[RTDConstants.TEXT_COLS];
        for (int i = 0; i < RTDConstants.TEXT_COLS; i++)
            bytes[i] = System.Convert.ToByte(hex.Substring(i * 2, 2), 16);
        return bytes;
    }

    // ===== Overview Layer Management =====

    /// <summary>
    /// Cycle to next overview layer.
    /// </summary>
    public void NextOverviewLayer(int maxLayers)
    {
        if (maxLayers <= 0) maxLayers = RTDConstants.MAX_OVERVIEW_LAYERS;
        _currentOverviewLayer = (_currentOverviewLayer + 1) % maxLayers;
        Debug.Log($"[Buffer] Switched to overview layer {_currentOverviewLayer}/{maxLayers}");
    }

    /// <summary>
    /// Cycle to previous overview layer.
    /// </summary>
    public void PrevOverviewLayer(int maxLayers)
    {
        if (maxLayers <= 0) maxLayers = RTDConstants.MAX_OVERVIEW_LAYERS;
        _currentOverviewLayer = (_currentOverviewLayer - 1 + maxLayers) % maxLayers;
        Debug.Log($"[Buffer] Switched to overview layer {_currentOverviewLayer}/{maxLayers}");
    }

    /// <summary>
    /// Set specific overview layer.
    /// </summary>
    public void SetOverviewLayer(int index)
    {
        _currentOverviewLayer = index;
    }
}
