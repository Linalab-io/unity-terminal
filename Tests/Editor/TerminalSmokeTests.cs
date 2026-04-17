using System;
using System.Text;
using System.Threading;
using NUnit.Framework;
using UnityEngine;

namespace Linalab.Terminal.Editor.Tests
{
    public sealed class TerminalSmokeTests
    {
        [Test]
        public void ShellProcess_ParsesShellOutput_IntoTerminalBuffer()
        {
            var buffer = new TerminalBuffer(24, 80, 200);
            var parser = new AnsiParser(buffer);
            using var shell = new ShellProcess(ShellProcess.DetectShell());
            parser.ResponseCallback = response => shell.Write(response);

            shell.Start(Application.dataPath.Replace("/Assets", string.Empty), 80, 24);
            Assert.That(shell.IsRunning, Is.True, "Shell process should start for smoke test.");

            const string plainMarker = "TERM_SMOKE_OK_123";
            const string colorMarker = "RED_MARKER_456";

            shell.Write($"printf '{plainMarker}\\r\\n'");
            shell.Write("\n");
            shell.Write($"printf '\\033[31m{colorMarker}\\033[0m\\r\\n'");
            shell.Write("\n");

            var combined = new StringBuilder();
            var deadline = DateTime.UtcNow.AddSeconds(8);
            while (DateTime.UtcNow < deadline)
            {
                shell.DrainOutput(data =>
                {
                    if (string.IsNullOrEmpty(data))
                    {
                        return;
                    }

                    combined.Append(data);
                    parser.Feed(data);
                });

                shell.DrainErrors(data =>
                {
                    if (string.IsNullOrEmpty(data))
                    {
                        return;
                    }

                    combined.Append(data);
                    parser.Feed(data);
                });

                string snapshot = combined.ToString();
                if (snapshot.Contains(plainMarker, StringComparison.Ordinal)
                    && snapshot.Contains(colorMarker, StringComparison.Ordinal))
                {
                    break;
                }

                Thread.Sleep(50);
            }

            string output = combined.ToString();
            Assert.That(output, Does.Contain(plainMarker));
            Assert.That(output, Does.Contain(colorMarker));

            string line1 = ReadLine(buffer, 0, 80);
            string line2 = ReadLine(buffer, 1, 80);
            string line3 = ReadLine(buffer, 2, 80);

            bool bufferHasPlainMarker = line1.Contains(plainMarker, StringComparison.Ordinal)
                || line2.Contains(plainMarker, StringComparison.Ordinal)
                || line3.Contains(plainMarker, StringComparison.Ordinal);
            bool bufferHasColorMarker = line1.Contains(colorMarker, StringComparison.Ordinal)
                || line2.Contains(colorMarker, StringComparison.Ordinal)
                || line3.Contains(colorMarker, StringComparison.Ordinal);

            Assert.That(bufferHasPlainMarker, Is.True, $"Expected buffer lines to include {plainMarker}, but got: '{line1}' | '{line2}' | '{line3}'");
            Assert.That(bufferHasColorMarker, Is.True, $"Expected buffer lines to include {colorMarker}, but got: '{line1}' | '{line2}' | '{line3}'");
        }

        [Test]
        public void TerminalBuffer_Resize_PreservesVisibleContent_AndClampsCursor()
        {
            var buffer = new TerminalBuffer(3, 5, 10);
            WriteText(buffer, "ABCDE");
            buffer.MoveCursorTo(1, 0);
            WriteText(buffer, "12345");
            buffer.MoveCursorTo(2, 4);

            buffer.Resize(2, 3);

            Assert.That(buffer.Rows, Is.EqualTo(2));
            Assert.That(buffer.Cols, Is.EqualTo(3));
            Assert.That(ReadLine(buffer, 0, 3), Is.EqualTo("ABC"));
            Assert.That(ReadLine(buffer, 1, 3), Is.EqualTo("123"));
            Assert.That(buffer.Cursor.Row, Is.EqualTo(1));
            Assert.That(buffer.Cursor.Col, Is.EqualTo(2));
            Assert.That(buffer.Cursor.PendingWrap, Is.False);

            buffer.Resize(4, 6);

            Assert.That(buffer.Rows, Is.EqualTo(4));
            Assert.That(buffer.Cols, Is.EqualTo(6));
            Assert.That(ReadLine(buffer, 0, 6), Is.EqualTo("ABC"));
            Assert.That(ReadLine(buffer, 1, 6), Is.EqualTo("123"));
            Assert.That(ReadLine(buffer, 2, 6), Is.EqualTo(string.Empty));
            Assert.That(ReadLine(buffer, 3, 6), Is.EqualTo(string.Empty));
            Assert.That(buffer.GetCell(0, 3).Codepoint, Is.Not.EqualTo('D'));
            Assert.That(buffer.GetCell(1, 3).Codepoint, Is.Not.EqualTo('4'));
        }

        [Test]
        public void AnsiParser_IgnoresPrivateCsiSequences_WithoutLeakingArtifacts()
        {
            var buffer = new TerminalBuffer(3, 40, 10);
            var parser = new AnsiParser(buffer);

            parser.Feed("before");
            parser.Feed("\x1b[>1u");
            parser.Feed("\x1b[>4;2m");
            parser.Feed("\x1b[0 q");
            parser.Feed("after");

            string line = ReadLine(buffer, 0, 40);
            Assert.That(line, Is.EqualTo("beforeafter"));
            Assert.That(line, Does.Not.Contain("1u"));
            Assert.That(line, Does.Not.Contain("4;2m"));
            Assert.That(line, Does.Not.Contain("0 q"));
        }

        [TestCase('한')]
        [TestCase('あ')]
        [TestCase('中')]
        public void TerminalBuffer_CjkCharacters_ConsumeTwoColumns(char character)
        {
            var buffer = new TerminalBuffer(2, 8, 0);

            buffer.PutChar(character);

            Assert.That(buffer.Cursor.Col, Is.EqualTo(2));
            Assert.That(buffer.GetCell(0, 0).Codepoint, Is.EqualTo(character));
            Assert.That(buffer.GetCell(0, 1).Codepoint, Is.EqualTo('\0'));
        }

        [TestCase('한')]
        [TestCase('あ')]
        [TestCase('中')]
        public void TerminalInputHandler_TranslatesCommittedCjkCharacters(char character)
        {
            var evt = new Event
            {
                type = EventType.KeyDown,
                character = character,
                keyCode = KeyCode.None
            };

            Assert.That(TerminalInputHandler.TranslateKeyEvent(evt), Is.EqualTo(character.ToString()));
        }

        [Test]
        public void TerminalInputHandler_SuppressesEnterDuringImeComposition()
        {
            var evt = new Event
            {
                type = EventType.KeyDown,
                character = '\r',
                keyCode = KeyCode.Return
            };

            Assert.That(TerminalInputHandler.TranslateKeyEvent(evt, "한"), Is.Null);
        }

        static void WriteText(TerminalBuffer buffer, string text)
        {
            foreach (char ch in text)
            {
                buffer.PutChar(ch);
            }
        }

        static string ReadLine(TerminalBuffer buffer, int row, int cols)
        {
            var builder = new StringBuilder(cols);
            for (int col = 0; col < cols; col++)
            {
                var cell = buffer.GetCell(row, col);
                builder.Append(cell.Codepoint == '\0' ? ' ' : cell.Codepoint);
            }

            return builder.ToString().TrimEnd();
        }
    }
}
