using System.Collections.Generic;
using System.Text;
using NUnit.Framework;
using UnityEngine;

namespace Linalab.Terminal.Editor.Tests
{
    public sealed class TerminalCoverageTests
    {
        [Test]
        public void TerminalBuffer_ScrollbackRetainsNewestRows_UpToConfiguredLimit()
        {
            var buffer = new TerminalBuffer(2, 8, 2);

            WriteLine(buffer, "row1");
            WriteLine(buffer, "row2");
            WriteLine(buffer, "row3");
            WriteLine(buffer, "row4");

            Assert.That(buffer.ScrollbackCount, Is.EqualTo(2));
            Assert.That(ReadScrollbackLine(buffer, 0), Is.EqualTo("row2"));
            Assert.That(ReadScrollbackLine(buffer, 1), Is.EqualTo("row3"));
            Assert.That(ReadLine(buffer, 0), Is.EqualTo("row4"));
        }

        [Test]
        public void TerminalBuffer_InsertAndDeleteLines_AffectOnlyScrollRegion()
        {
            var buffer = new TerminalBuffer(5, 8, 10);
            WriteRow(buffer, 0, "top");
            WriteRow(buffer, 1, "one");
            WriteRow(buffer, 2, "two");
            WriteRow(buffer, 3, "three");
            WriteRow(buffer, 4, "bottom");
            buffer.SetScrollRegion(1, 3);

            buffer.MoveCursorTo(2, 0);
            buffer.InsertLines(1);

            Assert.That(ReadLine(buffer, 0), Is.EqualTo("top"));
            Assert.That(ReadLine(buffer, 1), Is.EqualTo("one"));
            Assert.That(ReadLine(buffer, 2), Is.EqualTo(string.Empty));
            Assert.That(ReadLine(buffer, 3), Is.EqualTo("two"));
            Assert.That(ReadLine(buffer, 4), Is.EqualTo("bottom"));

            buffer.DeleteLines(1);

            Assert.That(ReadLine(buffer, 0), Is.EqualTo("top"));
            Assert.That(ReadLine(buffer, 1), Is.EqualTo("one"));
            Assert.That(ReadLine(buffer, 2), Is.EqualTo("two"));
            Assert.That(ReadLine(buffer, 3), Is.EqualTo(string.Empty));
            Assert.That(ReadLine(buffer, 4), Is.EqualTo("bottom"));
        }

        [Test]
        public void AnsiParser_AppliesExtendedRgbAndIndexedSgrColors()
        {
            var buffer = new TerminalBuffer(2, 16, 0);
            var parser = new AnsiParser(buffer);

            parser.Feed("\x1b[38;2;12;34;56;48;5;196;1mA");

            var cell = buffer.GetCell(0, 0);
            Assert.That(cell.Codepoint, Is.EqualTo('A'));
            Assert.That(cell.Foreground, Is.EqualTo(TerminalColor.FromRgb(12, 34, 56)));
            Assert.That(cell.Background, Is.EqualTo(TerminalColor.Indexed(196)));
            Assert.That((cell.Flags & CellFlags.Bold) == CellFlags.Bold, Is.True);

            parser.Feed("\x1b[39;49;22mB");

            var resetCell = buffer.GetCell(0, 1);
            Assert.That(resetCell.Foreground, Is.EqualTo(TerminalColor.DefaultColor));
            Assert.That(resetCell.Background, Is.EqualTo(TerminalColor.DefaultColor));
            Assert.That(resetCell.Flags, Is.EqualTo(CellFlags.None));
        }

        [Test]
        public void AnsiParser_DeviceStatusReport_RespondsWithOneBasedCursorPosition()
        {
            var buffer = new TerminalBuffer(5, 10, 0);
            var parser = new AnsiParser(buffer);
            var responses = new List<string>();
            parser.ResponseCallback = responses.Add;

            buffer.MoveCursorTo(2, 4);
            parser.Feed("\x1b[6n");

            Assert.That(responses, Is.EqualTo(new[] { "\x1b[3;5R" }));
        }

        [Test]
        public void AnsiParser_OscAndDcsStrings_AreIgnoredUntilTerminator()
        {
            var buffer = new TerminalBuffer(2, 40, 0);
            var parser = new AnsiParser(buffer);

            parser.Feed("before");
            parser.Feed("\x1b]0;window title\x07");
            parser.Feed("\x1bPignored payload\x1b\\");
            parser.Feed("after");

            Assert.That(ReadLine(buffer, 0), Is.EqualTo("beforeafter"));
        }

        [Test]
        public void TerminalInputHandler_TranslatesNavigationAndFunctionKeys()
        {
            Assert.That(TerminalInputHandler.TranslateKeyEvent('\0', KeyCode.UpArrow, false, false, string.Empty), Is.EqualTo("\x1b[A"));
            Assert.That(TerminalInputHandler.TranslateKeyEvent('\0', KeyCode.UpArrow, false, true, string.Empty), Is.EqualTo("\x1b[1;2A"));
            Assert.That(TerminalInputHandler.TranslateKeyEvent('\0', KeyCode.UpArrow, true, false, string.Empty), Is.EqualTo("\x1b[1;5A"));
            Assert.That(TerminalInputHandler.TranslateKeyEvent('\0', KeyCode.F5, false, false, string.Empty), Is.EqualTo("\x1b[15~"));
            Assert.That(TerminalInputHandler.TranslateKeyEvent('\r', KeyCode.Return, false, false, string.Empty), Is.EqualTo("\r"));
            Assert.That(TerminalInputHandler.TranslateKeyEvent('\b', KeyCode.Backspace, false, false, string.Empty), Is.EqualTo("\x7f"));
        }

        [Test]
        public void AnsiPalette_BuildDefaultPalette_ProvidesStandardAndExtendedColors()
        {
            var palette = AnsiPalette.BuildDefaultPalette();

            Assert.That(palette, Has.Length.EqualTo(256));
            Assert.That(palette[0], Is.EqualTo(new Color(0f, 0f, 0f, 1f)));
            Assert.That(palette[15], Is.EqualTo(new Color(1f, 1f, 1f, 1f)));
            Assert.That(TerminalColor.FromRgb(255, 128, 0).ToUnityColor(palette, Color.magenta), Is.EqualTo(new Color(1f, 128f / 255f, 0f, 1f)));
            Assert.That(TerminalColor.Named(250).ToUnityColor(palette, Color.magenta), Is.EqualTo(palette[250]));
            Assert.That(TerminalColor.Named(255).ToUnityColor(new Color[16], Color.magenta), Is.EqualTo(Color.magenta));
        }

        static void WriteRow(TerminalBuffer buffer, int row, string text)
        {
            buffer.MoveCursorTo(row, 0);
            foreach (var ch in text)
            {
                buffer.PutChar(ch);
            }
        }

        static void WriteLine(TerminalBuffer buffer, string text)
        {
            foreach (var ch in text)
            {
                buffer.PutChar(ch);
            }

            buffer.NewLine();
            buffer.CarriageReturn();
        }

        static string ReadLine(TerminalBuffer buffer, int row)
        {
            var builder = new StringBuilder(buffer.Cols);
            for (var col = 0; col < buffer.Cols; col++)
            {
                var cell = buffer.GetCell(row, col);
                builder.Append(cell.Codepoint == '\0' ? ' ' : cell.Codepoint);
            }

            return builder.ToString().TrimEnd();
        }

        static string ReadScrollbackLine(TerminalBuffer buffer, int row)
        {
            var builder = new StringBuilder(buffer.Cols);
            for (var col = 0; col < buffer.Cols; col++)
            {
                var cell = buffer.GetScrollbackCell(row, col);
                builder.Append(cell.Codepoint == '\0' ? ' ' : cell.Codepoint);
            }

            return builder.ToString().TrimEnd();
        }

        [Test]
        public void TerminalRenderer_SetSelection_TracksSelectionRange()
        {
            var buffer = new TerminalBuffer(5, 20, 10);
            var renderer = new TerminalRenderer(buffer);

            Assert.That(renderer.HasSelection, Is.False);

            renderer.SetSelection(new Vector2Int(2, 1), new Vector2Int(5, 3));
            Assert.That(renderer.HasSelection, Is.True);

            var text = renderer.GetSelectedText();
            Assert.That(text, Is.Not.Null);

            renderer.ClearSelection();
            Assert.That(renderer.HasSelection, Is.False);
        }

        [Test]
        public void TerminalRenderer_GetSelectedText_ExtractsMultiRowSelection()
        {
            var buffer = new TerminalBuffer(3, 10, 0);
            WriteRow(buffer, 0, "HelloWorld");
            WriteRow(buffer, 1, "TestLineXX");
            WriteRow(buffer, 2, "ThirdRowYY");

            var renderer = new TerminalRenderer(buffer);
            renderer.CalculateGridSize(new Rect(0f, 0f, 800f, 600f));

            var startCell = new Vector2Int(2, 0);
            var endCell = new Vector2Int(7, 1);
            renderer.SetSelection(startCell, endCell);
            var selectedText = renderer.GetSelectedText();

            Assert.That(selectedText, Does.Contain("lloWorld"));
            Assert.That(selectedText, Does.Contain("TestLine"));
        }

        [Test]
        public void AnsiParser_MouseTrackingMode_IsEnabledByAppropriateSequences()
        {
            var buffer = new TerminalBuffer(3, 40, 10);
            var parser = new AnsiParser(buffer);

            Assert.That(parser.IsMouseReportingEnabled, Is.False);
            Assert.That(parser.MouseTrackingMode, Is.EqualTo(TerminalMouseTrackingMode.None));

            parser.Feed("\x1b[?1000h");
            Assert.That(parser.IsMouseReportingEnabled, Is.True);
            Assert.That(parser.MouseTrackingMode, Is.EqualTo(TerminalMouseTrackingMode.ButtonPress));

            parser.Feed("\x1b[?1002h");
            Assert.That(parser.MouseTrackingMode, Is.EqualTo(TerminalMouseTrackingMode.ButtonDrag));

            parser.Feed("\x1b[?1003h");
            Assert.That(parser.MouseTrackingMode, Is.EqualTo(TerminalMouseTrackingMode.AnyMotion));

            parser.Feed("\x1b[?1003l");
            parser.Feed("\x1b[?1002l");
            parser.Feed("\x1b[?1000l");
            Assert.That(parser.IsMouseReportingEnabled, Is.False);
            Assert.That(parser.MouseTrackingMode, Is.EqualTo(TerminalMouseTrackingMode.None));
        }

        [Test]
        public void TerminalInputHandler_MouseEvents_EncodeSgrButtonPress()
        {
            var cell = new Vector2Int(5, 10);
            var pressSgr = TerminalInputHandler.TranslateMouseButtonEvent(
                TerminalMouseEncoding.Sgr, cell, 0, false, false, false, isRelease: false, isMotion: false);
            Assert.That(pressSgr, Is.EqualTo("\x1b[<0;6;11M"));
        }

        [Test]
        public void TerminalInputHandler_MouseEvents_EncodeSgrButtonRelease()
        {
            var cell = new Vector2Int(5, 10);
            var releaseSgr = TerminalInputHandler.TranslateMouseButtonEvent(
                TerminalMouseEncoding.Sgr, cell, 0, false, false, false, isRelease: true, isMotion: false);
            Assert.That(releaseSgr, Is.EqualTo("\x1b[<0;6;11m"));
        }

        [Test]
        public void TerminalInputHandler_MouseEvents_EncodeSgrWithShiftModifier()
        {
            var cell = new Vector2Int(5, 10);
            var shiftPress = TerminalInputHandler.TranslateMouseButtonEvent(
                TerminalMouseEncoding.Sgr, cell, 0, true, false, false, isRelease: false, isMotion: false);
            Assert.That(shiftPress, Is.EqualTo("\x1b[<4;6;11M"));
        }

        [Test]
        public void TerminalInputHandler_MouseEvents_EncodeLegacyFormat()
        {
            var cell = new Vector2Int(5, 10);
            var legacyPress = TerminalInputHandler.TranslateMouseButtonEvent(
                TerminalMouseEncoding.Default, cell, 0, false, false, false, isRelease: false, isMotion: false);
            Assert.That(legacyPress, Is.Not.Null);
            Assert.That(legacyPress.StartsWith("\x1b[M"), Is.True);
        }

        [Test]
        public void TerminalRenderer_TryGetCellPosition_MapsTopLeftToOriginCell()
        {
            var buffer = new TerminalBuffer(10, 40, 0);
            var renderer = new TerminalRenderer(buffer);
            var area = new Rect(0f, 0f, 400f, 200f);
            renderer.CalculateGridSize(area);

            Assert.That(renderer.VisibleCols, Is.GreaterThan(0));
            Assert.That(renderer.VisibleRows, Is.GreaterThan(0));

            var success = renderer.TryGetCellPosition(area, new Vector2(0f, 0f), out var topLeft);
            Assert.That(success, Is.True);
            Assert.That(topLeft.x, Is.EqualTo(0));
            Assert.That(topLeft.y, Is.EqualTo(0));
        }
    }
}
