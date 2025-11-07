using System;
using System.Collections.Generic;
using System.IO;
using System.Media;
using System.Speech.Synthesis;
using System.Threading;
using System.Threading.Tasks;

namespace BARS.Util
{
    public static class AudioHandler
    {
        private static readonly Logger logger = new Logger("AudioHandler");
        private static readonly SemaphoreSlim speechSemaphore = new SemaphoreSlim(1, 1);

        public static Task SpeakAsync(string text)
        {
            if (string.IsNullOrWhiteSpace(text))
            {
                return Task.CompletedTask;
            }

            return Task.Run(async () =>
            {
                await speechSemaphore.WaitAsync().ConfigureAwait(false);
                try
                {
                    using (var synthesizer = CreateSynthesizer())
                    {
                        synthesizer.Speak(text);
                    }
                }
                catch (Exception ex)
                {
                    logger.Error($"Speech synthesis failed: {ex.Message}");
                }
                finally
                {
                    speechSemaphore.Release();
                }
            });
        }

        public static Task AnnounceStopbarViolation(string airport, string runwayIdent, string stopbarLabel, string controllerId)
        {
            string message;
            string runwaySpeech = ConvertRunwayIdentToSpeech(runwayIdent);

            if (!string.IsNullOrWhiteSpace(runwaySpeech))
            {
                message = $"warning runway {runwaySpeech} stop bar violation";
            }
            else
            {
                string speechAirport = PrepareSegment(airport, "Unknown airport");
                string speechStopbar = PrepareSegment(stopbarLabel, "stop bar");
                message = $"{speechAirport} stop bar {speechStopbar} violation detected.";
            }

            return PlayAlertThenSpeakAsync(message);
        }

        private static Task PlayAlertThenSpeakAsync(string message)
        {
            if (string.IsNullOrWhiteSpace(message))
            {
                return Task.CompletedTask;
            }

            return Task.Run(async () =>
            {
                await speechSemaphore.WaitAsync().ConfigureAwait(false);
                try
                {
                    await PlayAlertToneAsync().ConfigureAwait(false);

                    using (var synthesizer = CreateSynthesizer())
                    {
                        synthesizer.Speak(message);
                    }
                }
                catch (Exception ex)
                {
                    logger.Error($"Audio alert failed: {ex.Message}");
                }
                finally
                {
                    speechSemaphore.Release();
                }
            });
        }

        private static string ConvertRunwayIdentToSpeech(string ident)
        {
            if (string.IsNullOrWhiteSpace(ident))
            {
                return null;
            }

            ident = ident.Trim();
            int index = 0;
            var parts = new List<string>();

            while (index < ident.Length && char.IsDigit(ident[index]))
            {
                int digit = ident[index] - '0';
                if (digit < 0 || digit > 9)
                {
                    return null;
                }
                parts.Add(DigitWords[digit]);
                index++;
            }

            if (parts.Count == 0)
            {
                return null;
            }

            if (index < ident.Length)
            {
                string suffix = ident.Substring(index).Trim().ToUpperInvariant();
                switch (suffix)
                {
                    case "L":
                        parts.Add("left");
                        break;
                    case "R":
                        parts.Add("right");
                        break;
                    case "C":
                        parts.Add("center");
                        break;
                    case "LR":
                        parts.Add("left");
                        parts.Add("right");
                        break;
                    default:
                        break;
                }
            }

            return string.Join(" ", parts);
        }

        private static SpeechSynthesizer CreateSynthesizer()
        {
            var synthesizer = new SpeechSynthesizer();
            synthesizer.SetOutputToDefaultAudioDevice();
            synthesizer.Rate = 0;
            synthesizer.Volume = TryGetVolume();
            return synthesizer;
        }

        private static async Task PlayAlertToneAsync()
        {
            try
            {
                string baseFolder = GetProgramFolder();
                if (string.IsNullOrWhiteSpace(baseFolder))
                {
                    return;
                }

                string wavPath = Path.Combine(baseFolder, "wav", "P2.wav");
                if (!File.Exists(wavPath))
                {
                    logger.Log($"Alert tone missing at {wavPath}");
                    return;
                }

                using (var player = new SoundPlayer(wavPath))
                {
                    player.Load();
                    player.Play();
                    await Task.Delay(2000).ConfigureAwait(false);
                    player.Stop();
                }
            }
            catch (Exception ex)
            {
                logger.Error($"Failed to play alert tone: {ex.Message}");
            }
        }

        private static string PrepareSegment(string value, string fallback)
        {
            if (string.IsNullOrWhiteSpace(value))
            {
                return fallback;
            }

            return value.Replace('_', ' ').Replace('-', ' ');
        }

        private static readonly string[] DigitWords = new[]
        {
            "zero",
            "one",
            "two",
            "three",
            "four",
            "five",
            "six",
            "seven",
            "eight",
            "nine"
        };

        private static int TryGetVolume()
        {
            try
            {
                return MapVolume(vatsys.AFV.OuputVolume);
            }
            catch
            {
                return 100;
            }
        }

        private static int MapVolume(float raw)
        {
            if (float.IsNaN(raw) || float.IsInfinity(raw))
            {
                raw = 1f;
            }

            if (raw < 0f)
            {
                raw = 0f;
            }
            else if (raw > 1f)
            {
                raw = 1f;
            }

            double scaled = Math.Round(raw * 100.0, MidpointRounding.AwayFromZero);
            if (scaled < 0d)
            {
                scaled = 0d;
            }
            else if (scaled > 100d)
            {
                scaled = 100d;
            }
            return (int)scaled;
        }

        public static string GetProgramFolder()
        {
            return new FileInfo(AppDomain.CurrentDomain.BaseDirectory).Directory.Parent.FullName;
        }
    }
}
