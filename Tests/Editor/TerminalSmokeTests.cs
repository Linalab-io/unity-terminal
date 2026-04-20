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
            bool originalTmuxAutoAttach = TerminalSettings.TmuxAutoAttach;
            TerminalSettings.TmuxAutoAttach = false;

            try
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

                    var snapshot = combined.ToString();
                    if (snapshot.Contains(plainMarker, StringComparison.Ordinal)
                        && snapshot.Contains(colorMarker, StringComparison.Ordinal))
                    {
                        break;
                    }

                    Thread.Sleep(50);
                }

                var output = combined.ToString();
                Assert.That(output, Does.Contain(plainMarker));
                Assert.That(output, Does.Contain(colorMarker));

                var line1 = ReadLine(buffer, 0, 80);
                var line2 = ReadLine(buffer, 1, 80);
                var line3 = ReadLine(buffer, 2, 80);

                var bufferHasPlainMarker = line1.Contains(plainMarker, StringComparison.Ordinal)
                    || line2.Contains(plainMarker, StringComparison.Ordinal)
                    || line3.Contains(plainMarker, StringComparison.Ordinal);
                var bufferHasColorMarker = line1.Contains(colorMarker, StringComparison.Ordinal)
                    || line2.Contains(colorMarker, StringComparison.Ordinal)
                    || line3.Contains(colorMarker, StringComparison.Ordinal);

                Assert.That(bufferHasPlainMarker, Is.True, $"Expected buffer lines to include {plainMarker}, but got: '{line1}' | '{line2}' | '{line3}'");
                Assert.That(bufferHasColorMarker, Is.True, $"Expected buffer lines to include {colorMarker}, but got: '{line1}' | '{line2}' | '{line3}'");
            }
            finally
            {
                TerminalSettings.TmuxAutoAttach = originalTmuxAutoAttach;
            }
        }

        [Test]
        public void ShellProcess_TmuxSessionExists_ReturnsFalse_ForInvalidNames()
        {
            Assert.That(ShellProcess.TmuxSessionExists(null), Is.False);
            Assert.That(ShellProcess.TmuxSessionExists(string.Empty), Is.False);
            Assert.That(ShellProcess.TmuxSessionExists("   "), Is.False);
        }

        [Test]
        public void ShellProcess_TmuxSessionExists_ReturnsFalse_ForUnlikelySessionName()
        {
            var missing = "unity-terminal-smoke-missing-" + Guid.NewGuid().ToString("N");
            Assert.That(ShellProcess.TmuxSessionExists(missing), Is.False);
        }

        [Test]
        public void ShellProcess_DetachLocalClient_IsSafeBeforeStart()
        {
            using var shell = new ShellProcess(ShellProcess.DetectShell());
            Assert.That(shell.IsRunning, Is.False);
            Assert.DoesNotThrow(() => shell.DetachLocalClient());
            Assert.That(shell.IsRunning, Is.False);
        }

        [Test]
        public void ShellProcess_DetachLocalClient_ReleasesClientAndAllowsBufferReuse()
        {
            bool originalTmuxAutoAttach = TerminalSettings.TmuxAutoAttach;
            TerminalSettings.TmuxAutoAttach = false;

            var buffer = new TerminalBuffer(24, 80, 200);
            var firstParser = new AnsiParser(buffer);
            ShellProcess firstShell = null;
            ShellProcess secondShell = null;

            try
            {
                firstShell = new ShellProcess(ShellProcess.DetectShell());
                firstParser.ResponseCallback = response => firstShell.Write(response);
                firstShell.Start(Application.dataPath.Replace("/Assets", string.Empty), 80, 24);
                Assert.That(firstShell.IsRunning, Is.True, "Shell process should start before detach test runs.");

                firstShell.DetachLocalClient();

                var detachDeadline = DateTime.UtcNow.AddSeconds(4);
                while (firstShell.IsRunning && DateTime.UtcNow < detachDeadline)
                {
                    Thread.Sleep(25);
                }

                Assert.That(firstShell.IsRunning, Is.False, "DetachLocalClient should release the local client process.");

                var secondParser = new AnsiParser(buffer);
                secondShell = new ShellProcess(ShellProcess.DetectShell());
                secondParser.ResponseCallback = response => secondShell.Write(response);
                secondShell.Start(Application.dataPath.Replace("/Assets", string.Empty), 80, 24);
                Assert.That(secondShell.IsRunning, Is.True, "A fresh shell should start on the same buffer after detach.");

                const string marker = "DETACH_REUSE_MARKER_789";
                secondShell.Write($"printf '{marker}\\r\\n'");
                secondShell.Write("\n");

                var combined = new StringBuilder();
                var deadline = DateTime.UtcNow.AddSeconds(8);
                while (DateTime.UtcNow < deadline)
                {
                    secondShell.DrainOutput(data =>
                    {
                        if (string.IsNullOrEmpty(data))
                        {
                            return;
                        }

                        combined.Append(data);
                        secondParser.Feed(data);
                    });

                    secondShell.DrainErrors(data =>
                    {
                        if (string.IsNullOrEmpty(data))
                        {
                            return;
                        }

                        combined.Append(data);
                        secondParser.Feed(data);
                    });

                    if (combined.ToString().Contains(marker, StringComparison.Ordinal))
                    {
                        break;
                    }

                    Thread.Sleep(50);
                }

                Assert.That(combined.ToString(), Does.Contain(marker));
            }
            finally
            {
                TerminalSettings.TmuxAutoAttach = originalTmuxAutoAttach;
                secondShell?.Dispose();
                firstShell?.Dispose();
            }
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

            var line = ReadLine(buffer, 0, 40);
            Assert.That(line, Is.EqualTo("beforeafter"));
            Assert.That(line, Does.Not.Contain("1u"));
            Assert.That(line, Does.Not.Contain("4;2m"));
            Assert.That(line, Does.Not.Contain("0 q"));
        }

        [Test]
        public void AnsiParser_TracksMouseReportingModes_AndEncoding()
        {
            var buffer = new TerminalBuffer(3, 40, 10);
            var parser = new AnsiParser(buffer);

            parser.Feed("\x1b[?1000h");
            parser.Feed("\x1b[?1006h");
            parser.Feed("\x1b[?1002h");
            parser.Feed("\x1b[?1003h");

            Assert.That(parser.MouseTrackingMode, Is.EqualTo(TerminalMouseTrackingMode.AnyMotion));
            Assert.That(parser.MouseEncoding, Is.EqualTo(TerminalMouseEncoding.Sgr));
            Assert.That(parser.IsMouseReportingEnabled, Is.True);

            parser.Feed("\x1b[?1003l");
            Assert.That(parser.MouseTrackingMode, Is.EqualTo(TerminalMouseTrackingMode.ButtonDrag));

            parser.Feed("\x1b[?1002l");
            Assert.That(parser.MouseTrackingMode, Is.EqualTo(TerminalMouseTrackingMode.ButtonPress));

            parser.Feed("\x1b[?1000l");
            parser.Feed("\x1b[?1006l");
            Assert.That(parser.MouseTrackingMode, Is.EqualTo(TerminalMouseTrackingMode.None));
            Assert.That(parser.MouseEncoding, Is.EqualTo(TerminalMouseEncoding.Default));
            Assert.That(parser.IsMouseReportingEnabled, Is.False);
        }

        [Test]
        public void AnsiParser_FullReset_ClearsMouseReportingState()
        {
            var buffer = new TerminalBuffer(3, 40, 10);
            var parser = new AnsiParser(buffer);

            parser.Feed("\x1b[?1000h");
            parser.Feed("\x1b[?1006h");
            parser.Feed("\x1b" + "c");

            Assert.That(parser.MouseTrackingMode, Is.EqualTo(TerminalMouseTrackingMode.None));
            Assert.That(parser.MouseEncoding, Is.EqualTo(TerminalMouseEncoding.Default));
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

        [TestCase('ㅇ', "ㅇ")]
        [TestCase('a', "a")]
        [TestCase('あ', "あ")]
        public void TerminalInputHandler_SuppressesPrintableCharactersDuringImeComposition(char character, string composition)
        {
            var evt = new Event
            {
                type = EventType.KeyDown,
                character = character,
                keyCode = KeyCode.None
            };

            Assert.That(TerminalInputHandler.TranslateKeyEvent(evt, composition), Is.Null);
        }

        [Test]
        public void TerminalInputHandler_PreservesControlLettersDuringImeComposition()
        {
            var evt = new Event
            {
                type = EventType.KeyDown,
                character = 'c',
                keyCode = KeyCode.C,
                control = true
            };

            Assert.That(TerminalInputHandler.TranslateKeyEvent(evt, "한"), Is.EqualTo("\x03"));
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

        [Test]
        public void TerminalInputHandler_SuppressesKeypadEnterDuringImeComposition()
        {
            var evt = new Event
            {
                type = EventType.KeyDown,
                character = '\n',
                keyCode = KeyCode.KeypadEnter
            };

            Assert.That(TerminalInputHandler.TranslateKeyEvent(evt, "한"), Is.Null);
        }

        [TestCase(RuntimePlatform.OSXEditor, KeyCode.V, true, false, true)]
        [TestCase(RuntimePlatform.OSXEditor, KeyCode.V, false, true, false)]
        [TestCase(RuntimePlatform.LinuxEditor, KeyCode.V, false, true, true)]
        [TestCase(RuntimePlatform.WindowsEditor, KeyCode.V, false, true, true)]
        [TestCase(RuntimePlatform.LinuxEditor, KeyCode.C, false, true, false)]
        public void TerminalInputHandler_DetectsPrimaryPasteShortcut(RuntimePlatform platform, KeyCode keyCode, bool command, bool control, bool expected)
        {
            Assert.That(TerminalInputHandler.IsPrimaryPasteShortcut(platform, keyCode, command, control), Is.EqualTo(expected));
        }

        [Test]
        public void TerminalInputHandler_BuildsDropInput_FromRelativePath()
        {
            var input = TerminalInputHandler.BuildDropInput("/Users/me/project", "Assets/Images/prompt.png");
            Assert.That(input, Is.EqualTo("@Assets/Images/prompt.png"));
        }

        [Test]
        public void TerminalInputHandler_BuildsDropInput_FromProjectAbsolutePath()
        {
            var input = TerminalInputHandler.BuildDropInput("/Users/me/project", "/Users/me/project/Assets/Images/prompt.png");
            Assert.That(input, Is.EqualTo("@Assets/Images/prompt.png"));
        }

        [Test]
        public void TerminalInputHandler_BuildsDropInput_FromExternalAbsolutePath()
        {
            var input = TerminalInputHandler.BuildDropInput("/Users/me/project", "/tmp/prompt.png");
            Assert.That(input, Is.EqualTo("@/tmp/prompt.png"));
        }

        [Test]
        public void TerminalInputHandler_BuildsDropInput_FromMultiplePaths()
        {
            var input = TerminalInputHandler.BuildDropInput(
                "/Users/me/project",
                new[]
                {
                    "Assets/Images/prompt.png",
                    "/Users/me/project/Assets/Folder",
                    "/tmp/reference.txt"
                });

            Assert.That(input, Is.EqualTo("@Assets/Images/prompt.png @Assets/Folder @/tmp/reference.txt"));
        }

        [Test]
        public void TerminalInputHandler_BuildsDropInput_FromFolderPath()
        {
            var input = TerminalInputHandler.BuildDropInput("/Users/me/project", "Assets/Folder");
            Assert.That(input, Is.EqualTo("@Assets/Folder"));
        }

        [Test]
        public void TerminalInputHandler_BuildsDropInput_SkipsBlankEntries()
        {
            var input = TerminalInputHandler.BuildDropInput("/Users/me/project", new[] { "", null, "Assets/Images/prompt.png" });
            Assert.That(input, Is.EqualTo("@Assets/Images/prompt.png"));
        }

        [TestCase("Paste", true)]
        [TestCase("Copy", false)]
        [TestCase(null, false)]
        public void TerminalInputHandler_RecognizesPasteCommand(string commandName, bool expected)
        {
            Assert.That(TerminalInputHandler.IsPasteCommand(commandName), Is.EqualTo(expected));
        }

        [Test]
        public void TerminalInputHandler_EncodesSgrMouseButtonEvents()
        {
            var cell = new Vector2Int(4, 1);

            Assert.That(
                TerminalInputHandler.TranslateMouseButtonEvent(TerminalMouseEncoding.Sgr, cell, 0, false, false, false, isRelease: false, isMotion: false),
                Is.EqualTo("\x1b[<0;5;2M"));
            Assert.That(
                TerminalInputHandler.TranslateMouseButtonEvent(TerminalMouseEncoding.Sgr, cell, 0, false, false, false, isRelease: true, isMotion: false),
                Is.EqualTo("\x1b[<0;5;2m"));
            Assert.That(
                TerminalInputHandler.TranslateMouseButtonEvent(TerminalMouseEncoding.Sgr, cell, 0, false, false, false, isRelease: false, isMotion: true),
                Is.EqualTo("\x1b[<32;5;2M"));
            Assert.That(
                TerminalInputHandler.TranslateMouseScrollEvent(TerminalMouseEncoding.Sgr, cell, false, false, false, scrollUp: true),
                Is.EqualTo("\x1b[<64;5;2M"));
        }

        [Test]
        public void TerminalInputHandler_EncodesLegacyMouseRelease()
        {
            var cell = new Vector2Int(4, 1);
            var encoded = TerminalInputHandler.TranslateMouseButtonEvent(TerminalMouseEncoding.Default, cell, 0, false, false, false, isRelease: true, isMotion: false);
            var expected = string.Concat("\x1b[M", ((char)(3 + 32)).ToString(), ((char)(5 + 32)).ToString(), ((char)(2 + 32)).ToString());
            Assert.That(encoded, Is.EqualTo(expected));
        }

        [Test]
        public void TerminalSettings_TogglesVerboseLogging_AndPersistsValue()
        {
            var original = TerminalSettings.VerboseLogging;

            try
            {
                TerminalSettings.VerboseLogging = false;
                Assert.That(TerminalSettings.VerboseLogging, Is.False);

                var toggledOn = TerminalSettings.ToggleVerboseLogging();
                Assert.That(toggledOn, Is.True);
                Assert.That(TerminalSettings.VerboseLogging, Is.True);

                var toggledOff = TerminalSettings.ToggleVerboseLogging();
                Assert.That(toggledOff, Is.False);
                Assert.That(TerminalSettings.VerboseLogging, Is.False);
            }
            finally
            {
                TerminalSettings.VerboseLogging = original;
            }
        }

        [Test]
        public void TerminalSettings_TogglesTmuxAutoAttach_AndPersistsValue()
        {
            var original = TerminalSettings.TmuxAutoAttach;

            try
            {
                TerminalSettings.TmuxAutoAttach = false;
                Assert.That(TerminalSettings.TmuxAutoAttach, Is.False);

                var toggledOn = TerminalSettings.ToggleTmuxAutoAttach();
                Assert.That(toggledOn, Is.True);
                Assert.That(TerminalSettings.TmuxAutoAttach, Is.True);

                var toggledOff = TerminalSettings.ToggleTmuxAutoAttach();
                Assert.That(toggledOff, Is.False);
                Assert.That(TerminalSettings.TmuxAutoAttach, Is.False);
            }
            finally
            {
                TerminalSettings.TmuxAutoAttach = original;
            }
        }

        [Test]
        public void TerminalSettings_BuildsStableTmuxSessionName_FromProjectRoot()
        {
            string first = TerminalSettings.BuildTmuxSessionName("/Users/me/projects/MyGame");
            string second = TerminalSettings.BuildTmuxSessionName("/Users/me/projects/MyGame");
            string differentRoot = TerminalSettings.BuildTmuxSessionName("/Users/me/projects/OtherGame");

            Assert.That(first, Is.EqualTo(second), "session name should be deterministic");
            Assert.That(first, Is.EqualTo("MyGame"));
            Assert.That(first, Does.Not.Contain("."));
            Assert.That(first, Does.Not.Contain(":"));
            Assert.That(first, Does.Not.Contain(" "));
            Assert.That(first, Is.Not.EqualTo(differentRoot));
        }

        [Test]
        public void TerminalSettings_BuildsFallbackTmuxSessionName_ForEmptyRoot()
        {
            string name = TerminalSettings.BuildTmuxSessionName(string.Empty);
            Assert.That(name, Is.EqualTo("unity-terminal"));
            Assert.That(name, Does.Not.Contain("."));
        }

        static void WriteText(TerminalBuffer buffer, string text)
        {
            foreach (var ch in text)
            {
                buffer.PutChar(ch);
            }
        }

        static string ReadLine(TerminalBuffer buffer, int row, int cols)
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
