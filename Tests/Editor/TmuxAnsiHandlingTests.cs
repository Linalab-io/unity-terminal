using System.Text;
using NUnit.Framework;
using UnityEngine;

namespace Linalab.Terminal.Editor.Tests
{
    public sealed class TmuxAnsiHandlingTests
    {
        [Test]
        public void DECSC_DECRC_RestoresCursorPosition_AndAttributes()
        {
            var buffer = new TerminalBuffer(4, 20, 0);
            var parser = new AnsiParser(buffer);

            parser.Feed("\x1b[31m");
            parser.Feed("AB");
            parser.Feed("\x1b" + "7");
            parser.Feed("\x1b[3;10H");
            parser.Feed("\x1b[32m");
            parser.Feed("X");
            parser.Feed("\x1b" + "8");
            parser.Feed("C");

            Assert.That(buffer.Cursor.Row, Is.EqualTo(0));
            Assert.That(buffer.Cursor.Col, Is.EqualTo(3));
            Assert.That(buffer.GetCell(0, 0).Codepoint, Is.EqualTo('A'));
            Assert.That(buffer.GetCell(0, 1).Codepoint, Is.EqualTo('B'));
            Assert.That(buffer.GetCell(0, 2).Codepoint, Is.EqualTo('C'));
            Assert.That(buffer.GetCell(2, 9).Codepoint, Is.EqualTo('X'));
            Assert.That(buffer.GetCell(0, 2).Foreground, Is.EqualTo(TerminalColor.Named(1)),
                "C should inherit restored foreground (red), not the green applied between save and restore");
            Assert.That(buffer.GetCell(2, 9).Foreground, Is.EqualTo(TerminalColor.Named(2)));
        }

        [Test]
        public void CSI_s_u_BehavesAsCursorSaveRestore()
        {
            var buffer = new TerminalBuffer(4, 20, 0);
            var parser = new AnsiParser(buffer);

            parser.Feed("hi");
            parser.Feed("\x1b[s");
            parser.Feed("\x1b[4;20H");
            parser.Feed("\x1b[u");
            parser.Feed("!");

            Assert.That(buffer.Cursor.Row, Is.EqualTo(0));
            Assert.That(buffer.Cursor.Col, Is.EqualTo(3));
            Assert.That(buffer.GetCell(0, 2).Codepoint, Is.EqualTo('!'));
        }

        [Test]
        public void DECRC_WithoutPriorSave_MovesCursorToHome()
        {
            var buffer = new TerminalBuffer(3, 10, 0);
            var parser = new AnsiParser(buffer);

            parser.Feed("\x1b[2;5H");
            parser.Feed("\x1b" + "8");

            Assert.That(buffer.Cursor.Row, Is.EqualTo(0));
            Assert.That(buffer.Cursor.Col, Is.EqualTo(0));
        }

        [Test]
        public void DECSTBM_SetsScrollRegion_AndMovesCursorHome()
        {
            var buffer = new TerminalBuffer(5, 10, 0);
            var parser = new AnsiParser(buffer);

            parser.Feed("\x1b[3;5H");
            parser.Feed("\x1b[2;4r");

            Assert.That(buffer.Cursor.Row, Is.EqualTo(0));
            Assert.That(buffer.Cursor.Col, Is.EqualTo(0));

            parser.Feed("\x1b[2;1H");
            for (var i = 0; i < 5; i++)
            {
                parser.Feed("\nZ");
            }

            Assert.That(buffer.GetCell(4, 0).Codepoint, Is.Not.EqualTo('Z'),
                "Row 4 should be outside the scroll region (bottom margin is 3)");
        }

        [Test]
        public void DECSTBM_EmptyParams_ResetsToFullScreen()
        {
            var buffer = new TerminalBuffer(5, 10, 0);
            var parser = new AnsiParser(buffer);

            parser.Feed("\x1b[2;4r");
            parser.Feed("\x1b[r");
            parser.Feed("\x1b[5;1H");
            parser.Feed("\nEND");

            Assert.That(buffer.Cursor.Row, Is.EqualTo(4));
        }

        [Test]
        public void ICH_InsertsBlankCellsAndShiftsRight()
        {
            var buffer = new TerminalBuffer(2, 8, 0);
            var parser = new AnsiParser(buffer);

            parser.Feed("ABCDEFG");
            parser.Feed("\x1b[1;3H");
            parser.Feed("\x1b[2@");

            Assert.That(ReadLine(buffer, 0, 8), Is.EqualTo("AB  CDEF"));
            Assert.That(buffer.Cursor.Col, Is.EqualTo(2));
        }

        [Test]
        public void ICH_ClampsAtRowEnd()
        {
            var buffer = new TerminalBuffer(2, 5, 0);
            var parser = new AnsiParser(buffer);

            parser.Feed("ABCDE");
            parser.Feed("\x1b[1;4H");
            parser.Feed("\x1b[10@");

            Assert.That(ReadLine(buffer, 0, 5), Is.EqualTo("ABC"));
        }

        [Test]
        public void Cursor_Visibility_TogglesViaDECSetReset25()
        {
            var buffer = new TerminalBuffer(2, 10, 0);
            var parser = new AnsiParser(buffer);

            parser.Feed("\x1b[?25l");
            Assert.That(buffer.Cursor.Visible, Is.False);

            parser.Feed("\x1b[?25h");
            Assert.That(buffer.Cursor.Visible, Is.True);
        }

        [Test]
        public void AltScreen_DecPrivateModes_ConsumedWithoutLeakingArtifacts()
        {
            var buffer = new TerminalBuffer(2, 20, 0);
            var parser = new AnsiParser(buffer);

            parser.Feed("before");
            parser.Feed("\x1b[?1049h");
            parser.Feed("\x1b[?2004h");
            parser.Feed("\x1b[?7h");
            parser.Feed("\x1b[?1h");
            parser.Feed("\x1b[?12l");
            parser.Feed("\x1b[?1049l");
            parser.Feed("after");

            Assert.That(ReadLine(buffer, 0, 20), Is.EqualTo("beforeafter"));
        }

        [Test]
        public void ColonSeparatedSgr_38_5_ParsesAs256Color()
        {
            var buffer = new TerminalBuffer(2, 10, 0);
            var parser = new AnsiParser(buffer);

            parser.Feed("\x1b[38:5:214mX");

            Assert.That(buffer.GetCell(0, 0).Codepoint, Is.EqualTo('X'));
            Assert.That(buffer.GetCell(0, 0).Foreground, Is.EqualTo(TerminalColor.Indexed(214)));
        }

        [Test]
        public void ColonSeparatedSgr_48_2_ParsesAsTruecolorBackground()
        {
            var buffer = new TerminalBuffer(2, 10, 0);
            var parser = new AnsiParser(buffer);

            parser.Feed("\x1b[48:2:10:20:30mY");

            Assert.That(buffer.GetCell(0, 0).Codepoint, Is.EqualTo('Y'));
            Assert.That(buffer.GetCell(0, 0).Background, Is.EqualTo(TerminalColor.FromRgb(10, 20, 30)));
        }

        [Test]
        public void SemicolonSeparatedSgr_StillWorksAfterColonSupport()
        {
            var buffer = new TerminalBuffer(2, 10, 0);
            var parser = new AnsiParser(buffer);

            parser.Feed("\x1b[38;5;196mZ");

            Assert.That(buffer.GetCell(0, 0).Foreground, Is.EqualTo(TerminalColor.Indexed(196)));
        }

        [Test]
        public void Keypad_ModesAreSilentlyAccepted()
        {
            var buffer = new TerminalBuffer(2, 20, 0);
            var parser = new AnsiParser(buffer);

            parser.Feed("before");
            parser.Feed("\x1b=");
            parser.Feed("\x1b>");
            parser.Feed("after");

            Assert.That(ReadLine(buffer, 0, 20), Is.EqualTo("beforeafter"));
        }

        [Test]
        public void LineAttributes_HashDoubleHeightEscape_IsSilentlyConsumed()
        {
            var buffer = new TerminalBuffer(2, 20, 0);
            var parser = new AnsiParser(buffer);

            parser.Feed("A");
            parser.Feed("\x1b#3");
            parser.Feed("B");

            Assert.That(ReadLine(buffer, 0, 20), Is.EqualTo("AB"));
        }

        [Test]
        public void DeviceAttributes_RespondsWithVt100Identifier()
        {
            var buffer = new TerminalBuffer(2, 10, 0);
            var parser = new AnsiParser(buffer);
            string response = null;
            parser.ResponseCallback = reply => response = reply;

            parser.Feed("\x1b[c");

            Assert.That(response, Is.EqualTo("\x1b[?1;2c"));
        }

        [Test]
        public void ScrollUp_CSI_S_MovesContentUp()
        {
            var buffer = new TerminalBuffer(3, 5, 0);
            var parser = new AnsiParser(buffer);

            parser.Feed("aaaaa\r\nbbbbb\r\nccccc");
            parser.Feed("\x1b[S");

            Assert.That(ReadLine(buffer, 0, 5), Is.EqualTo("bbbbb"));
            Assert.That(ReadLine(buffer, 1, 5), Is.EqualTo("ccccc"));
            Assert.That(ReadLine(buffer, 2, 5), Is.EqualTo(string.Empty));
        }

        [Test]
        public void ScrollDown_CSI_T_MovesContentDown()
        {
            var buffer = new TerminalBuffer(3, 5, 0);
            var parser = new AnsiParser(buffer);

            parser.Feed("aaaaa\r\nbbbbb\r\nccccc");
            parser.Feed("\x1b[T");

            Assert.That(ReadLine(buffer, 0, 5), Is.EqualTo(string.Empty));
            Assert.That(ReadLine(buffer, 1, 5), Is.EqualTo("aaaaa"));
            Assert.That(ReadLine(buffer, 2, 5), Is.EqualTo("bbbbb"));
        }

        static string ReadLine(ITerminalBuffer buffer, int row, int cols)
        {
            var builder = new StringBuilder(cols);
            for (var col = 0; col < cols; col++)
            {
                var cell = buffer.GetCell(row, col);
                builder.Append(cell.Codepoint == '\0' ? ' ' : cell.Codepoint);
            }

            return builder.ToString().TrimEnd();
        }
    }
}
