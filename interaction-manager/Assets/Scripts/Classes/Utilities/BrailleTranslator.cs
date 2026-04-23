using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Text;
using UnityEngine;

public static class BrailleTranslator
{

    public enum BrailleMode { RawDotBytes, LibLouisGrade1, LibLouisGrade2 }
    public static BrailleMode Mode { get; set; } = BrailleMode.RawDotBytes;

    private const byte CapitalSignByte = 0b00100000;
    private const string CapitalSignUnicode = "⠠";
    private const byte NumberSignByte  = 0b00111100;
    private const string NumberSignUnicode = "⠼";
    private static readonly Dictionary<char, string> asciiToUnicodeBraille = new()
    {
        ['1'] = "⠁",
        ['2'] = "⠃",
        ['3'] = "⠉",
        ['4'] = "⠙",
        ['5'] = "⠑",
        ['6'] = "⠋",
        ['7'] = "⠛",
        ['8'] = "⠓",
        ['9'] = "⠊",
        ['0'] = "⠚",
        [' '] = "⠀",
        ['a'] = "⠁",
        ['b'] = "⠃",
        ['c'] = "⠉",
        ['d'] = "⠙",
        ['e'] = "⠑",
        ['f'] = "⠋",
        ['g'] = "⠛",
        ['h'] = "⠓",
        ['i'] = "⠊",
        ['j'] = "⠚",
        ['k'] = "⠅",
        ['l'] = "⠇",
        ['m'] = "⠍",
        ['n'] = "⠝",
        ['o'] = "⠕",
        ['p'] = "⠏",
        ['q'] = "⠟",
        ['r'] = "⠗",
        ['s'] = "⠎",
        ['t'] = "⠞",
        ['u'] = "⠥",
        ['v'] = "⠧",
        ['w'] = "⠺",
        ['x'] = "⠭",
        ['y'] = "⠽",
        ['z'] = "⠵",
        [','] = "⠂",
        [';'] = "⠆",
        [':'] = "⠒",
        ['.'] = "⠲",
        ['!'] = "⠖",
        ['?'] = "⠦",
        ['\''] = "⠄",
        ['-'] = "⠤",
        ['"'] = "⠶",
        ['('] = "⠐⠣",
        [')'] = "⠐⠜"
    };

    private static readonly Dictionary<char, byte> asciiBrailleToDotByte = new()
    {
        ['a'] = 0b00000001, // 1
        ['b'] = 0b00000011, // 1-2
        ['c'] = 0b00001001, // 1-4
        ['d'] = 0b00011001, // 1-4-5
        ['e'] = 0b00010001, // 1-5
        ['f'] = 0b00001011, // 1-2-4
        ['g'] = 0b00011011, // 1-2-4-5
        ['h'] = 0b00010011, // 1-2-5
        ['i'] = 0b00001010, // 2-4
        ['j'] = 0b00011010, // 2-4-5

        ['k'] = 0b00000101, // 1-3
        ['l'] = 0b00000111, // 1-2-3
        ['m'] = 0b00001101, // 1-3-4
        ['n'] = 0b00011101, // 1-3-4-5
        ['o'] = 0b00010101, // 1-3-5
        ['p'] = 0b00001111, // 1-2-3-4
        ['q'] = 0b00011111, // 1-2-3-4-5
        ['r'] = 0b00010111, // 1-2-3-5
        ['s'] = 0b00001110, // 2-3-4
        ['t'] = 0b00011110, // 2-3-4-5

        ['u'] = 0b00100101, // 1-3-6
        ['v'] = 0b00100111, // 1-2-3-6
        ['w'] = 0b00111010, // 2-4-5-6
        ['x'] = 0b00101101, // 1-3-4-6
        ['y'] = 0b00111101, // 1-3-4-5-6
        ['z'] = 0b00110101, // 1-3-5-6

        [' '] = 0b00000000, // space
        [','] = 0b00000010, // 2
        [';'] = 0b00000110, // 2-3
        [':'] = 0b00010010, // 2-5
        ['.'] = 0b00110010, // 2-5-6
        ['!'] = 0b00010110, // 2-3-5
        ['?'] = 0b00100110, // 2-3-6
        ['\'']= 0b00000100, // 3
        ['-'] = 0b00100100, // 3-6
        ['"'] = 0b00110110, // 2-3-5-6
    };

    /// <summary>
    /// Maps liblouis BRF output characters to braille dot bytes.
    /// Derived from us-table.dis (the default liblouis display table):
    ///   display &lt;char&gt; &lt;dots&gt; means that braille cell &lt;dots&gt; is rendered as &lt;char&gt; in BRF output.
    /// Letters a-z are output as-is; special cells use the chars below.
    /// </summary>
    private static readonly Dictionary<char, byte> brlAsciiToByte = new()
    {
        [' '] = 0x00,
        // digits (display 0 356 … display 9 35)
        ['0'] = 0x34, // dots 3-5-6
        ['1'] = 0x02, // dot 2
        ['2'] = 0x06, // dots 2-3
        ['3'] = 0x12, // dots 2-5
        ['4'] = 0x32, // dots 2-5-6
        ['5'] = 0x22, // dots 2-6
        ['6'] = 0x16, // dots 2-3-5
        ['7'] = 0x36, // dots 2-3-5-6
        ['8'] = 0x26, // dots 2-3-6
        ['9'] = 0x14, // dots 3-5
        // letters (standard braille, same bit patterns as asciiBrailleToDotByte)
        ['a'] = 0x01, ['b'] = 0x03, ['c'] = 0x09, ['d'] = 0x19, ['e'] = 0x11,
        ['f'] = 0x0B, ['g'] = 0x1B, ['h'] = 0x13, ['i'] = 0x0A, ['j'] = 0x1A,
        ['k'] = 0x05, ['l'] = 0x07, ['m'] = 0x0D, ['n'] = 0x1D, ['o'] = 0x15,
        ['p'] = 0x0F, ['q'] = 0x1F, ['r'] = 0x17, ['s'] = 0x0E, ['t'] = 0x1E,
        ['u'] = 0x25, ['v'] = 0x27, ['w'] = 0x3A, ['x'] = 0x2D, ['y'] = 0x3D, ['z'] = 0x35,
        // indicators and punctuation (from display table)
        ['#'] = 0x3C, // dots 3-4-5-6  — number indicator
        [','] = 0x20, // dot 6         — capital indicator (single letter)
        ['.'] = 0x28, // dots 4-6
        ['-'] = 0x24, // dots 3-6
        ['\'']= 0x04, // dot 3
        ['"'] = 0x10, // dot 5
        [';'] = 0x30, // dots 5-6
        [':'] = 0x31, // dots 1-5-6
        ['!'] = 0x2E, // dots 2-3-4-6
        ['('] = 0x37, // dots 1-2-3-5-6
        [')'] = 0x3E, // dots 2-3-4-5-6
        ['?'] = 0x39, // dots 1-4-5-6
        ['/'] = 0x0C, // dots 3-4
        ['+'] = 0x2C, // dots 3-4-6
        ['&'] = 0x2F, // dots 1-2-3-4-6
        ['$'] = 0x2B, // dots 1-2-4-6
        ['%'] = 0x29, // dots 1-4-6
        ['*'] = 0x21, // dots 1-6
        ['='] = 0x3F, // dots 1-2-3-4-5-6
        ['<'] = 0x23, // dots 1-2-6
        ['>'] = 0x1C, // dots 3-4-5
        ['~'] = 0x18, // dots 4-5
        ['`'] = 0x08, // dot 4
        ['_'] = 0x38, // dots 4-5-6
        ['{'] = 0x2A, // dots 2-4-6
        ['}'] = 0x3B, // dots 1-2-4-5-6
        ['|'] = 0x33, // dots 1-2-5-6
    };

    public static string ConvertToUnicodeBraille(string inputText)
    {
        string ascii = TranslateWithLou(inputText);
        return AsciiToUnicodeWithCase(ascii, inputText);
    }

    private static string AsciiToUnicodeWithCase(string ascii, string original)
    {
        var result = new StringBuilder();
        bool inNumber = false;
        int len = Math.Min(ascii.Length, original.Length);
        for (int i = 0; i < len; i++)
        {
            char ac = ascii[i];
            char oc = original[i];
            if (char.IsWhiteSpace(ac)) { result.Append("⠀"); inNumber = false; continue; }
            if (char.IsDigit(ac))
            {
                if (!inNumber) { result.Append("⠼"); inNumber = true; }
                if (asciiToUnicodeBraille.TryGetValue(ac, out var d)) result.Append(d);
                else result.Append("⠀");
                continue;
            }
            inNumber = false;
            if (char.IsUpper(oc)) result.Append(CapitalSignUnicode);
            char lower = char.ToLowerInvariant(ac);
            if (asciiToUnicodeBraille.TryGetValue(lower, out var braille)) result.Append(braille);
            else result.Append("⠀");
        }
        return result.ToString();
    }

    private static string TranslateWithLou(string inputText)
    {
        var psi = new ProcessStartInfo
        {
            FileName = "/opt/homebrew/bin/lou_translate",
            Arguments = "en-us-g1.ctb",
            EnvironmentVariables = { ["LOUIS_TABLEPATH"] = "/opt/homebrew/share/liblouis/tables" },
            RedirectStandardInput  = true,
            RedirectStandardOutput = true,
            RedirectStandardError  = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };
        using var process = Process.Start(psi);
        process.StandardInput.WriteLine(inputText);
        process.StandardInput.Close();
        string output = process.StandardOutput.ReadToEnd();
        string error = process.StandardError.ReadToEnd();
        process.WaitForExit();
        if (!string.IsNullOrWhiteSpace(error)) UnityEngine.Debug.LogWarning("Liblouis stderr: " + error);
        return output.Trim();
    }

    public static List<string> ToPagedDotHex(string text, int cellsPerPage = 20)
    {
        if (Mode == BrailleMode.LibLouisGrade1 || Mode == BrailleMode.LibLouisGrade2)
        {
            string table = Mode == BrailleMode.LibLouisGrade1 ? "en-ueb-g1.ctb" : "en-ueb-g2.ctb";
            return TranslateViaLiblouis(text, table, cellsPerPage);
        }

        // 1) break into "words" or runs of whitespace
        var tokens = System.Text.RegularExpressions
            .Regex.Split(text, @"(\s+)")
            .Where(t => t.Length > 0)
            .ToList();

        var pages = new List<List<byte>>();
        var current = new List<byte>();

        foreach (var tok in tokens)
        {
            var raw = RawDotBytes(tok, cellsPerPage);

            // if a single token overflows one page, chunk it
            if (raw.Count > cellsPerPage)
            {
                for (int i = 0; i < raw.Count; i += cellsPerPage)
                    pages.Add(raw.Skip(i).Take(cellsPerPage).ToList());
                current.Clear();
                continue;
            }

            // else, can we append it to current?
            if (current.Count + raw.Count <= cellsPerPage)
            {
                current.AddRange(raw);
            }
            else
            {
                // push current, start a fresh page
                pages.Add(current);
                current = new List<byte>(raw);
            }
        }

        if (current.Count > 0)
            pages.Add(current);

        // finally pad & hex‐encode each
        return pages
            .Select(p => PadAndHex(p, cellsPerPage))
            .ToList();
    }

    // Pull out the raw byte sequence for one token (no padding)
    private static List<byte> RawDotBytes(string text, int maxCells)
    {
        var bytes = new List<byte>();
        int used = 0;
        bool inNumber = false;

        foreach (char c in text)
        {

            // 1) digits get a number‐sign once, then the a–j patterns
            if (char.IsDigit(c))
            {
                if (!inNumber)
                {
                    // emit the "number sign" (dots 3-4-5-6)
                    if (used + 1 > maxCells) break;
                    bytes.Add(NumberSignByte);
                    used++;
                    inNumber = true;
                }
                // now map '1'→a, '2'→b, … '0'→j
                char letter = (c == '0') ? 'j' : (char)('a' + (c - '1'));
                if (used + 1 > maxCells) break;
                bytes.Add(asciiBrailleToDotByte[letter]);
                used++;
                continue;
            }

            // any non-digit breaks number-mode
            inNumber = false;

            // 2) uppercase gets your capital sign + letter
            if (char.IsUpper(c))
            {
                if (used + 2 > maxCells) break;
                bytes.Add(CapitalSignByte);

                if (asciiBrailleToDotByte.TryGetValue(char.ToLowerInvariant(c), out var b))
                    bytes.Add(b);
                else
                    bytes.Add(0x00);
                used += 2;
            }
            else // 3) everything else you already support
            {
                if (used + 1 > maxCells) break;

                if (asciiBrailleToDotByte.TryGetValue(c, out var b))
                    bytes.Add(b);
                else
                    bytes.Add(0x00);
                used += 1;
            }
        }
        return bytes;
    }

    private static string PadAndHex(List<byte> page, int cellsPerPage)
    {
        while (page.Count < cellsPerPage)
            page.Add(0x00);
        return BitConverter.ToString(page.ToArray()).Replace("-", "").ToLower();
    }

    /// <summary>
    /// Translates text via liblouis (forward translation) and pages the result into
    /// cellsPerPage-cell hex strings. BRF output is decoded using brlAsciiToByte.
    /// </summary>
    private static List<string> TranslateViaLiblouis(string text, string table, int cellsPerPage)
    {
        var psi = new ProcessStartInfo
        {
            FileName = "/opt/homebrew/bin/lou_translate",
            Arguments = table,
            EnvironmentVariables = { ["LOUIS_TABLEPATH"] = "/opt/homebrew/share/liblouis/tables" },
            RedirectStandardInput  = true,
            RedirectStandardOutput = true,
            RedirectStandardError  = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };

        string brf;
        using (var process = Process.Start(psi))
        {
            process.StandardInput.WriteLine(text);
            process.StandardInput.Close();
            // lou_translate wraps output at ~40 cells; strip newlines so cells flow continuously
            brf = process.StandardOutput.ReadToEnd().Replace("\r", "").Replace("\n", "").Trim();
            string err = process.StandardError.ReadToEnd();
            process.WaitForExit();
            if (!string.IsNullOrWhiteSpace(err))
                UnityEngine.Debug.LogWarning("[Liblouis] stderr: " + err);
        }

        // Word-wrap: split BRF at spaces, decode each token, pack into pages
        var pages = new List<string>();
        var current = new List<byte>();
        var allBytes = new List<byte>(); // for logging only

        foreach (var tok in brf.Split(' '))
        {
            if (tok.Length == 0) continue;
            var tokenBytes = BrfToBytes(tok);

            if (current.Count == 0)
            {
                current.AddRange(tokenBytes);
                allBytes.AddRange(tokenBytes);
            }
            else if (current.Count + 1 + tokenBytes.Count <= cellsPerPage)
            {
                current.Add(0x00);
                current.AddRange(tokenBytes);
                allBytes.Add(0x00);
                allBytes.AddRange(tokenBytes);
            }
            else
            {
                pages.Add(PadAndHex(current, cellsPerPage));
                current = new List<byte>(tokenBytes);
                allBytes.Add(0x00);
                allBytes.AddRange(tokenBytes);
            }
        }
        if (current.Count > 0)
            pages.Add(PadAndHex(current, cellsPerPage));
        if (pages.Count == 0)
            pages.Add(PadAndHex(new List<byte>(), cellsPerPage));

        string unicode = new string(allBytes.Select(b => (char)(0x2800 + b)).ToArray());
        UnityEngine.Debug.Log($"[Braille] {text}\n  BRF:     {brf}\n  Unicode: {unicode}");

        return pages;
    }

    /// <summary>
    /// Converts a liblouis BRF output string to braille dot bytes using the
    /// us-table.dis display mapping.
    /// </summary>
    private static List<byte> BrfToBytes(string brf)
    {
        var bytes = new List<byte>();
        foreach (char c in brf)
        {
            if (char.IsUpper(c))
            {
                // Liblouis outputs capital letters as uppercase ASCII rather than
                // using the comma indicator + lowercase. Emit capital sign + letter.
                bytes.Add(CapitalSignByte);
                if (brlAsciiToByte.TryGetValue(char.ToLowerInvariant(c), out byte lb))
                    bytes.Add(lb);
            }
            else if (brlAsciiToByte.TryGetValue(c, out byte b))
                bytes.Add(b);
            else
                bytes.Add(0x00); // unknown char → blank cell
        }
        return bytes;
    }
}
