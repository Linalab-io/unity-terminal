using System;
using System.Collections.Generic;
using System.Text;
using UnityEditor;
using UnityEngine;

namespace Linalab.Terminal.Editor
{
    public struct TerminalSnapshot
    {
        public int VisibleRows;
        public int VisibleCols;
        public float CellWidth;
        public float CellHeight;
        public Color DefaultBackground;
        public Color DefaultForeground;
        public Color CursorColor;
        public List<TerminalRowSnapshot> Rows;
        public CursorSnapshot Cursor;
        public CompositionPreviewSnapshot CompositionPreview;
    }

    public struct TerminalRowSnapshot
    {
        public int RowIndex;
        public List<TerminalRunSnapshot> TextRuns;
        public List<TerminalBackgroundSnapshot> Backgrounds;
    }

    public struct TerminalRunSnapshot
    {
        public int StartCol;
        public int DisplayWidth;
        public string Text;
        public Color Foreground;
        public CellFlags Flags;
    }

    public struct TerminalBackgroundSnapshot
    {
        public int StartCol;
        public int DisplayWidth;
        public Color Color;
    }

    public struct CursorSnapshot
    {
        public bool Visible;
        public int Row;
        public int Col;
        public int DisplayWidth;
        public string Text;
    }

    public struct CompositionPreviewSnapshot
    {
        public bool Visible;
        public int Row;
        public int Col;
        public int DisplayWidth;
        public string Text;
    }

    public sealed class TerminalRenderer
    {
        public readonly struct SelectionRange
        {
            public readonly Vector2Int Start;
            public readonly Vector2Int End;

            public SelectionRange(Vector2Int start, Vector2Int end)
            {
                Start = start;
                End = end;
            }

            public bool Contains(int row, int col)
            {
                Normalize(out var normalizedStart, out var normalizedEnd);
                if (row < normalizedStart.y || row > normalizedEnd.y)
                {
                    return false;
                }

                if (normalizedStart.y == normalizedEnd.y)
                {
                    return col >= normalizedStart.x && col <= normalizedEnd.x;
                }

                if (row == normalizedStart.y)
                {
                    return col >= normalizedStart.x;
                }

                if (row == normalizedEnd.y)
                {
                    return col <= normalizedEnd.x;
                }

                return true;
            }

            public void Normalize(out Vector2Int normalizedStart, out Vector2Int normalizedEnd)
            {
                if (Start.y < End.y || (Start.y == End.y && Start.x <= End.x))
                {
                    normalizedStart = Start;
                    normalizedEnd = End;
                    return;
                }

                normalizedStart = End;
                normalizedEnd = Start;
            }
        }

        readonly ITerminalBuffer _buffer;
        GUIStyle _cellStyle;
        float _cellWidth;
        float _cellHeight;
        bool _cursorVisible;
        double _lastBlinkToggle;
        int _scrollbackOffset;
        SelectionRange? _selection;

        public TerminalRenderer(ITerminalBuffer buffer)
        {
            _buffer = buffer;
        }

        public int VisibleCols { get; private set; }
        public int VisibleRows { get; private set; }
        public float CellWidth => _cellWidth;
        public float CellHeight => _cellHeight;
        public bool HasSelection => _selection.HasValue;

        public void InvalidateStyle()
        {
            _cellStyle = null;
        }

        public bool CalculateGridSize(Rect area)
        {
            EnsureStyle();
            if (area.width < 1f || area.height < 1f)
            {
                return false;
            }

            var newVisibleCols = Mathf.Max(1, Mathf.FloorToInt(area.width / _cellWidth));
            var newVisibleRows = Mathf.Max(1, Mathf.FloorToInt(area.height / _cellHeight));
            var changed = newVisibleCols != VisibleCols || newVisibleRows != VisibleRows;
            VisibleCols = newVisibleCols;
            VisibleRows = newVisibleRows;
            return changed;
        }

        public void AdjustScroll(int delta)
        {
            _scrollbackOffset = Mathf.Clamp(_scrollbackOffset - delta, 0, _buffer.ScrollbackCount);
        }

        public void ScrollToBottom()
        {
            _scrollbackOffset = 0;
        }

        public void SetSelection(Vector2Int start, Vector2Int end)
        {
            _selection = new SelectionRange(start, end);
        }

        public void ClearSelection()
        {
            _selection = null;
        }

        public bool TryGetCellPosition(Rect area, Vector2 mousePosition, out Vector2Int position)
        {
            EnsureStyle();

            if (!area.Contains(mousePosition))
            {
                position = default;
                return false;
            }

            var col = Mathf.Clamp(Mathf.FloorToInt((mousePosition.x - area.x) / _cellWidth), 0, Mathf.Max(0, VisibleCols - 1));
            var row = Mathf.Clamp(Mathf.FloorToInt((mousePosition.y - area.y) / _cellHeight), 0, Mathf.Max(0, VisibleRows - 1));
            position = new Vector2Int(col, row);
            return true;
        }

        public string GetSelectedText()
        {
            if (!_selection.HasValue)
            {
                return string.Empty;
            }

            _selection.Value.Normalize(out var start, out var end);
            var builder = new StringBuilder();

            for (var row = start.y; row <= end.y; row++)
            {
                var startCol = row == start.y ? start.x : 0;
                var endCol = row == end.y ? end.x : Mathf.Max(0, VisibleCols - 1);
                builder.Append(GetSelectedRowText(row, startCol, endCol));

                if (row < end.y)
                {
                    builder.Append('\n');
                }
            }

            return builder.ToString();
        }

        public Font GetFont()
        {
            EnsureStyle();
            return _cellStyle?.font ?? GetFallbackFont();
        }

        public bool TryGetInputCursor(out CursorSnapshot cursor)
        {
            EnsureStyle();
            if (_scrollbackOffset != 0)
            {
                cursor = new CursorSnapshot { Visible = false };
                return false;
            }

            cursor = BuildCursorSnapshot(ignoreBlink: true);
            return cursor.Visible;
        }

        public TerminalSnapshot BuildSnapshot(string compositionString)
        {
            EnsureStyle();
            TerminalTheme theme = TerminalThemeResolver.GetCurrentTheme();
            
            var snapshot = new TerminalSnapshot
            {
                VisibleRows = VisibleRows,
                VisibleCols = VisibleCols,
                CellWidth = _cellWidth,
                CellHeight = _cellHeight,
                DefaultBackground = Opaque(theme.DefaultBackground),
                DefaultForeground = Opaque(theme.DefaultForeground),
                CursorColor = Opaque(theme.CursorColor),
                Rows = new List<TerminalRowSnapshot>(),
                Cursor = new CursorSnapshot { Visible = false },
                CompositionPreview = new CompositionPreviewSnapshot { Visible = false }
            };

            var rows = Mathf.Min(VisibleRows, _buffer.Rows);
            var cols = Mathf.Min(VisibleCols, _buffer.Cols);

            var now = EditorApplication.timeSinceStartup;
            if (now - _lastBlinkToggle > TerminalSettings.CursorBlinkRate)
            {
                _cursorVisible = !_cursorVisible;
                _lastBlinkToggle = now;
            }

            for (var row = 0; row < rows; row++)
            {
                snapshot.Rows.Add(BuildRowSnapshot(row, cols, theme));
            }

            if (_scrollbackOffset == 0)
            {
                if (!string.IsNullOrEmpty(compositionString))
                {
                    snapshot.CompositionPreview = BuildCompositionPreviewSnapshot(compositionString);
                }
                else
                {
                    snapshot.Cursor = BuildCursorSnapshot();
                }
            }

            return snapshot;
        }

        void EnsureStyle()
        {
            if (_cellStyle != null)
            {
                return;
            }

            GUIStyle baseStyle = CreateTerminalBaseStyle();
            Font font = CreateMonospaceFont(TerminalSettings.GetEffectiveFontFamily(), TerminalSettings.FontSize) ?? GetFallbackFont();

            _cellStyle = new GUIStyle(baseStyle)
            {
                font = font,
                fontSize = TerminalSettings.FontSize,
                alignment = TextAnchor.UpperLeft,
                padding = new RectOffset(0, 0, 0, 0),
                margin = new RectOffset(0, 0, 0, 0),
                border = new RectOffset(0, 0, 0, 0),
                wordWrap = false,
                clipping = TextClipping.Clip,
                richText = false,
                stretchWidth = false,
                stretchHeight = false,
                contentOffset = Vector2.zero
            };

            const string CellWidthProbeCharacters = "MW@#%&_gjy|/\\[]{}()";
            const string CellHeightProbeCharacters = "MW@#%&_gjy|/\\[]{}()한あ中█\uE0B0\uE0B1\uE0A0";

            float maxNormalizedWidth = 0f;
            float maxHeight = Mathf.Max(1f, _cellStyle.lineHeight);

            for (int i = 0; i < CellWidthProbeCharacters.Length; i++)
            {
                char probeCharacter = CellWidthProbeCharacters[i];
                var probeContent = new GUIContent(probeCharacter.ToString());
                Vector2 probeSize = _cellStyle.CalcSize(probeContent);
                maxNormalizedWidth = Mathf.Max(maxNormalizedWidth, probeSize.x);
            }

            for (int i = 0; i < CellHeightProbeCharacters.Length; i++)
            {
                char probeCharacter = CellHeightProbeCharacters[i];
                var probeContent = new GUIContent(probeCharacter.ToString());
                Vector2 probeSize = _cellStyle.CalcSize(probeContent);
                maxHeight = Mathf.Max(maxHeight, probeSize.y);
            }

            float pixelsPerPoint = Mathf.Max(1f, EditorGUIUtility.pixelsPerPoint);
            float horizontalPadding = 1f / pixelsPerPoint;
            float verticalPadding = 2f / pixelsPerPoint;
            _cellWidth = Mathf.Max(1f, SnapToPixel(maxNormalizedWidth + horizontalPadding));
            _cellHeight = Mathf.Max(1f, SnapToPixel(maxHeight + verticalPadding));
        }

        static float SnapToPixel(float value)
        {
            float pixelsPerPoint = EditorGUIUtility.pixelsPerPoint;
            if (pixelsPerPoint <= 0f)
            {
                return value;
            }

            return Mathf.Round(value * pixelsPerPoint) / pixelsPerPoint;
        }

        static GUIStyle CreateTerminalBaseStyle()
        {
            var style = new GUIStyle
            {
                alignment = TextAnchor.UpperLeft,
                padding = new RectOffset(0, 0, 0, 0),
                margin = new RectOffset(0, 0, 0, 0),
                border = new RectOffset(0, 0, 0, 0),
                clipping = TextClipping.Clip,
                wordWrap = false,
                richText = false,
                stretchWidth = false,
                stretchHeight = false,
                contentOffset = Vector2.zero,
                imagePosition = ImagePosition.TextOnly
            };

            style.normal.background = null;
            style.hover.background = null;
            style.focused.background = null;
            style.active.background = null;
            style.onNormal.background = null;
            style.onHover.background = null;
            style.onFocused.background = null;
            style.onActive.background = null;
            style.normal.textColor = Color.white;
            style.hover.textColor = Color.white;
            style.focused.textColor = Color.white;
            style.active.textColor = Color.white;
            style.onNormal.textColor = Color.white;
            style.onHover.textColor = Color.white;
            style.onFocused.textColor = Color.white;
            style.onActive.textColor = Color.white;
            return style;
        }

        static Font GetFallbackFont()
        {
            try
            {
                var monospaceFallback = Font.CreateDynamicFontFromOSFont(GetPlatformPreferredFontFamilies(), TerminalSettings.FontSize);
                if (monospaceFallback != null)
                {
                    return monospaceFallback;
                }
            }
            catch
            {
            }

            return Resources.GetBuiltinResource<Font>("LegacyRuntime.ttf");
        }

        static Font CreateMonospaceFont(string preferredFontFamily, int fontSize)
        {
            var fontNames = ResolveFontCandidates(preferredFontFamily);

            return Font.CreateDynamicFontFromOSFont(fontNames, fontSize);
        }

        static string[] ResolveFontCandidates(string preferredFontFamily)
        {
            var preferredFamilies = GetPlatformPreferredFontFamilies();

            var installedFonts = Font.GetOSInstalledFontNames();
            var installedFontSet = new HashSet<string>(installedFonts, StringComparer.OrdinalIgnoreCase);
            var candidates = new List<string>(preferredFamilies.Length + 1);

            AddInstalledFont(candidates, installedFontSet, preferredFontFamily);
            for (var i = 0; i < preferredFamilies.Length; i++)
            {
                AddInstalledFont(candidates, installedFontSet, preferredFamilies[i]);
            }

            if (candidates.Count == 0)
            {
                return preferredFamilies;
            }

            return candidates.ToArray();
        }

        static string[] GetPlatformPreferredFontFamilies()
        {
            return Application.platform == RuntimePlatform.WindowsEditor
                ? new[]
                {
                    "Consolas",
                    "Courier New",
                    "Lucida Console",
                    "CaskaydiaCove Nerd Font Mono",
                    "JetBrainsMono Nerd Font Mono",
                    "FiraCode Nerd Font Mono",
                    "Hack Nerd Font Mono"
                }
                : new[]
                {
                    "SF Mono",
                    "Menlo",
                    "Monaco",
                    "Courier New",
                    "Courier",
                    "Andale Mono",
                    "MesloLGS NF",
                    "MesloLGSNerdFontMono",
                    "MesloLGS Nerd Font Mono",
                    "JetBrainsMono Nerd Font Mono",
                    "JetBrainsMono NF",
                    "Hack Nerd Font Mono",
                    "HackNerdFontMono",
                    "FiraCodeNerdFontMono",
                    "FiraCode Nerd Font Mono",
                    "CaskaydiaCove Nerd Font Mono",
                    "CaskaydiaCove NF"
                };
        }

        static void AddInstalledFont(List<string> candidates, HashSet<string> installedFontSet, string fontName)
        {
            if (string.IsNullOrWhiteSpace(fontName) || !installedFontSet.Contains(fontName))
            {
                return;
            }

            if (!candidates.Contains(fontName))
            {
                candidates.Add(fontName);
            }
        }

        TerminalRowSnapshot BuildRowSnapshot(int row, int cols, TerminalTheme theme)
        {
            var rowSnapshot = new TerminalRowSnapshot
            {
                RowIndex = row,
                TextRuns = new List<TerminalRunSnapshot>(),
                Backgrounds = new List<TerminalBackgroundSnapshot>()
            };

            var displayRow = row;
            var isScrollback = false;
            var scrollbackRow = -1;

            if (_scrollbackOffset > 0)
            {
                var scrollbackRowsVisible = Mathf.Min(_scrollbackOffset, VisibleRows);
                if (row < scrollbackRowsVisible)
                {
                    isScrollback = true;
                    scrollbackRow = _buffer.ScrollbackCount - _scrollbackOffset + row;
                    if (scrollbackRow < 0)
                    {
                        return rowSnapshot;
                    }
                }
                else
                {
                    displayRow = row - scrollbackRowsVisible;
                    if (displayRow >= _buffer.Rows)
                    {
                        return rowSnapshot;
                    }
                }
            }

            var runBuilder = new StringBuilder(cols);
            var runStartCol = 0;
            Color runForeground = Opaque(theme.DefaultForeground);
            CellFlags runFlags = CellFlags.None;
            var hasRun = false;

            var bgStartCol = 0;
            var bgDisplayWidth = 0;
            Color bgRunColor = Opaque(theme.DefaultBackground);
            var hasBgRun = false;

            for (var col = 0; col < cols; col++)
            {
                TerminalCell cell = isScrollback
                    ? _buffer.GetScrollbackCell(scrollbackRow, col)
                    : _buffer.GetCell(displayRow, col);

                bool isContinuation = IsContinuationCell(cell);
                bool isWideLead = !isContinuation && IsWideLeadCell(isScrollback, scrollbackRow, displayRow, col, cols);

                if (isContinuation)
                {
                    FlushTextRun(rowSnapshot, runBuilder, runStartCol, runForeground, runFlags, ref hasRun);
                    continue;
                }

                bool boldBrightens = (cell.Flags & CellFlags.Bold) != 0;
                Color bgColor = Opaque(cell.Background.ToUnityColor(theme.Palette, theme.DefaultBackground));
                Color fgColor = Opaque(ResolveForegroundWithBoldBright(cell.Foreground, boldBrightens, theme));
                if ((cell.Flags & CellFlags.Inverse) != 0)
                {
                    fgColor = Opaque(cell.Background.ToUnityColor(theme.Palette, theme.DefaultBackground));
                    bgColor = Opaque(ResolveForegroundWithBoldBright(cell.Foreground, boldBrightens, theme));
                }

                if ((cell.Flags & CellFlags.Dim) != 0)
                {
                    fgColor.r *= 0.6f;
                    fgColor.g *= 0.6f;
                    fgColor.b *= 0.6f;
                }

                bool isSelected = _selection.HasValue && _selection.Value.Contains(row, col);
                if (isWideLead && _selection.HasValue && col + 1 < cols && _selection.Value.Contains(row, col + 1))
                {
                    isSelected = true;
                }

                if (isSelected)
                {
                    bgColor = new Color(0.33f, 0.52f, 0.88f, 1f);
                    fgColor = Color.white;
                }

                if (bgColor != Opaque(theme.DefaultBackground))
                {
                    if (!hasBgRun)
                    {
                        hasBgRun = true;
                        bgStartCol = col;
                        bgDisplayWidth = isWideLead ? 2 : 1;
                        bgRunColor = bgColor;
                    }
                    else if (bgRunColor == bgColor && bgStartCol + bgDisplayWidth == col)
                    {
                        bgDisplayWidth += isWideLead ? 2 : 1;
                    }
                    else
                    {
                        rowSnapshot.Backgrounds.Add(new TerminalBackgroundSnapshot
                        {
                            StartCol = bgStartCol,
                            DisplayWidth = bgDisplayWidth,
                            Color = bgRunColor
                        });
                        bgStartCol = col;
                        bgDisplayWidth = isWideLead ? 2 : 1;
                        bgRunColor = bgColor;
                    }
                }
                else
                {
                    if (hasBgRun)
                    {
                        rowSnapshot.Backgrounds.Add(new TerminalBackgroundSnapshot
                        {
                            StartCol = bgStartCol,
                            DisplayWidth = bgDisplayWidth,
                            Color = bgRunColor
                        });
                        hasBgRun = false;
                    }
                }

                if (cell.Codepoint == ' ')
                {
                    FlushTextRun(rowSnapshot, runBuilder, runStartCol, runForeground, runFlags, ref hasRun);
                    continue;
                }

                if (isWideLead)
                {
                    FlushTextRun(rowSnapshot, runBuilder, runStartCol, runForeground, runFlags, ref hasRun);

                    rowSnapshot.TextRuns.Add(new TerminalRunSnapshot
                    {
                        StartCol = col,
                        DisplayWidth = 2,
                        Text = cell.Codepoint.ToString(),
                        Foreground = fgColor,
                        Flags = cell.Flags
                    });
                    continue;
                }

                if (!hasRun)
                {
                    hasRun = true;
                    runStartCol = col;
                    runForeground = fgColor;
                    runFlags = cell.Flags;
                    runBuilder.Clear();
                }
                else if (runForeground != fgColor || runFlags != cell.Flags)
                {
                    FlushTextRun(rowSnapshot, runBuilder, runStartCol, runForeground, runFlags, ref hasRun);
                    runStartCol = col;
                    runForeground = fgColor;
                    runFlags = cell.Flags;
                    hasRun = true;
                }

                runBuilder.Append(cell.Codepoint);
            }

            FlushTextRun(rowSnapshot, runBuilder, runStartCol, runForeground, runFlags, ref hasRun);

            if (hasBgRun)
            {
                rowSnapshot.Backgrounds.Add(new TerminalBackgroundSnapshot
                {
                    StartCol = bgStartCol,
                    DisplayWidth = bgDisplayWidth,
                    Color = bgRunColor
                });
            }

            return rowSnapshot;
        }

        static void FlushTextRun(TerminalRowSnapshot rowSnapshot, StringBuilder runBuilder, int runStartCol, Color runForeground, CellFlags runFlags, ref bool hasRun)
        {
            if (!hasRun)
            {
                return;
            }

            rowSnapshot.TextRuns.Add(new TerminalRunSnapshot
            {
                StartCol = runStartCol,
                DisplayWidth = runBuilder.Length,
                Text = runBuilder.ToString(),
                Foreground = runForeground,
                Flags = runFlags
            });
            hasRun = false;
            runBuilder.Clear();
        }

        string GetSelectedRowText(int displayRow, int startCol, int endCol)
        {
            if (!TryMapDisplayRow(displayRow, out var isScrollback, out var mappedRow))
            {
                return string.Empty;
            }

            var builder = new StringBuilder(Mathf.Max(0, endCol - startCol + 1));
            var maxCol = Mathf.Min(endCol, Mathf.Max(0, _buffer.Cols - 1));
            for (var col = startCol; col <= maxCol; col++)
            {
                TerminalCell cell = isScrollback
                    ? _buffer.GetScrollbackCell(mappedRow, col)
                    : _buffer.GetCell(mappedRow, col);
                if (cell.Codepoint == '\0')
                {
                    continue;
                }

                builder.Append(cell.Codepoint);
            }

            return builder.ToString().TrimEnd(' ');
        }

        bool TryMapDisplayRow(int displayRow, out bool isScrollback, out int mappedRow)
        {
            if (_scrollbackOffset > 0)
            {
                var scrollbackRowsVisible = Mathf.Min(_scrollbackOffset, VisibleRows);
                if (displayRow < scrollbackRowsVisible)
                {
                    isScrollback = true;
                    mappedRow = _buffer.ScrollbackCount - _scrollbackOffset + displayRow;
                    return mappedRow >= 0 && mappedRow < _buffer.ScrollbackCount;
                }

                mappedRow = displayRow - scrollbackRowsVisible;
                isScrollback = false;
                return mappedRow >= 0 && mappedRow < _buffer.Rows;
            }

            isScrollback = false;
            mappedRow = displayRow;
            return mappedRow >= 0 && mappedRow < _buffer.Rows;
        }

        CursorSnapshot BuildCursorSnapshot(bool ignoreBlink = false)
        {
            var cursor = _buffer.Cursor;
            if (!cursor.Visible || (!ignoreBlink && !_cursorVisible))
            {
                return new CursorSnapshot { Visible = false };
            }

            if (cursor.Row < 0 || cursor.Row >= VisibleRows || cursor.Col < 0 || cursor.Col >= VisibleCols)
            {
                return new CursorSnapshot { Visible = false };
            }

            int anchorCol = cursor.Col;
            int cursorWidth = 1;
            var cell = _buffer.GetCell(cursor.Row, cursor.Col);
            if (IsContinuationCell(cell))
            {
                int leadCol = cursor.Col - 1;
                if (leadCol < 0 || !IsWideLeadCell(false, -1, cursor.Row, leadCol, VisibleCols))
                {
                    return new CursorSnapshot { Visible = false };
                }

                anchorCol = leadCol;
                cursorWidth = 2;
                cell = _buffer.GetCell(cursor.Row, leadCol);
            }

            return new CursorSnapshot
            {
                Visible = true,
                Row = cursor.Row,
                Col = anchorCol,
                DisplayWidth = cursorWidth,
                Text = cell.Codepoint == ' ' ? string.Empty : cell.Codepoint.ToString()
            };
        }

        CompositionPreviewSnapshot BuildCompositionPreviewSnapshot(string compositionString)
        {
            var cursor = _buffer.Cursor;
            if (cursor.Row < 0 || cursor.Row >= VisibleRows || cursor.Col < 0 || cursor.Col >= VisibleCols)
            {
                return new CompositionPreviewSnapshot { Visible = false };
            }

            int anchorCol = cursor.Col;
            int displayWidth = 0;
            for (int i = 0; i < compositionString.Length; i++)
            {
                displayWidth += GetDisplayWidth(compositionString[i]);
            }

            if (displayWidth <= 0)
            {
                return new CompositionPreviewSnapshot { Visible = false };
            }

            return new CompositionPreviewSnapshot
            {
                Visible = true,
                Row = cursor.Row,
                Col = anchorCol,
                DisplayWidth = displayWidth,
                Text = compositionString
            };
        }

        bool IsWideLeadCell(bool isScrollback, int scrollbackRow, int displayRow, int col, int cols)
        {
            if (col < 0 || col + 1 >= cols)
            {
                return false;
            }

            TerminalCell cell = isScrollback
                ? _buffer.GetScrollbackCell(scrollbackRow, col)
                : _buffer.GetCell(displayRow, col);

            return !IsContinuationCell(cell)
                && GetDisplayWidth(cell.Codepoint) > 1
                && IsValidContinuationCell(isScrollback, scrollbackRow, displayRow, col + 1, cols);
        }

        bool IsValidContinuationCell(bool isScrollback, int scrollbackRow, int displayRow, int col, int cols)
        {
            if (col <= 0 || col >= cols)
            {
                return false;
            }

            TerminalCell cell = isScrollback
                ? _buffer.GetScrollbackCell(scrollbackRow, col)
                : _buffer.GetCell(displayRow, col);

            TerminalCell leftCell = isScrollback
                ? _buffer.GetScrollbackCell(scrollbackRow, col - 1)
                : _buffer.GetCell(displayRow, col - 1);

            return IsContinuationCell(cell)
                && !IsContinuationCell(leftCell)
                && GetDisplayWidth(leftCell.Codepoint) > 1;
        }

        static bool IsContinuationCell(TerminalCell cell)
        {
            return cell.Codepoint == '\0';
        }

        static int GetDisplayWidth(char character)
        {
            int codepoint = character;

            return codepoint switch
            {
                >= 0x1100 and <= 0x115F => 2,
                >= 0x2329 and <= 0x232A => 2,
                >= 0x2E80 and <= 0xA4CF => 2,
                >= 0xAC00 and <= 0xD7A3 => 2,
                >= 0xF900 and <= 0xFAFF => 2,
                >= 0xFE10 and <= 0xFE19 => 2,
                >= 0xFE30 and <= 0xFE6F => 2,
                >= 0xFF00 and <= 0xFF60 => 2,
                >= 0xFFE0 and <= 0xFFE6 => 2,
                _ => 1
            };
        }

        static Color Opaque(Color color)
        {
            color.a = 1f;
            return color;
        }

        static Color ResolveForegroundWithBoldBright(TerminalColor foreground, bool bold, TerminalTheme theme)
        {
            if (bold && foreground.ColorKind == TerminalColor.Kind.Named && foreground.R < 8)
            {
                byte brightIndex = (byte)(foreground.R + 8);
                return TerminalColor.Named(brightIndex).ToUnityColor(theme.Palette, theme.DefaultForeground);
            }

            return foreground.ToUnityColor(theme.Palette, theme.DefaultForeground);
        }
    }
}
