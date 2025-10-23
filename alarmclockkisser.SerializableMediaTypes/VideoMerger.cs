using alarmclockkisser.SerializableMediaTypes;
using NAudio.Wave;
using SharpAvi.Output;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO.Pipes;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading.Tasks;
using Xabe.FFmpeg;

namespace alarmclockkisser.SerializableMediaTypes
{
	public class VideoMerger
	{
		public readonly AudioCollection audioCollection;
		public readonly ImageCollection imageCollection;

		public AudioObj? CurrentAudio { get; private set; } = null;

		public IEnumerable<float[]> AudioChunks { get; private set; } = [];
		public long FramesCount => this.imageCollection.Images.LongCount();

		public double FramesPerSecond { get; private set; } = 20.0;




		public VideoMerger(AudioCollection audioCollection, ImageCollection imageCollection)
		{
			this.audioCollection = audioCollection;
			this.imageCollection = imageCollection;
		}


		public async Task LoadAudioAsync(string filePath)
		{
			this.CurrentAudio = await this.audioCollection.ImportAsync(filePath);
		}

		public async Task AddFrameToCollectionAsync(byte[] frameBytes, long frameId, string contentType = "image/png")
		{
			var img = await ImageObj.FromBytesAsync(frameBytes, frameId.ToString(), contentType);
			if (img == null)
			{
				Console.WriteLine("ImageObj was null after getting FromBytesAsync(), bytes: " + frameBytes.LongLength + ", id: " + frameId + $" ({contentType})");
				return;
			}

			this.imageCollection.Add(img);
		}





		// Static ImageObj[] + AudioObj -> Video Task<string?>  (async + parallel)
		public static async Task<string?> MergeImagesAndAudioToMp4Async(IEnumerable<byte[]> imageRgbas, int width, int height, AudioObj audioObj, double? fps = 30.0)
		{
			string format = "mp4";
			var imageList = imageRgbas.ToList();
			if (imageList.LongCount() <= 0 || audioObj == null || width <= 0 || height <= 0)
			{
				return null;
			}

			// 0. AudioObj as wav export to safe temp path
			string tempAudioPath = Path.GetTempPath();
			string? audioFile = await AudioExporter.ExportWavAsync(audioObj, tempAudioPath, 24);
			if (audioFile == null || !File.Exists(audioFile))
			{
				return null;
			}

			// 1. Framerate berechnen
			double frameRate = fps ?? (double) imageList.Count / audioObj.Duration.TotalSeconds;

			// 2. Output-Pfad generieren
			string outputFileName = $"visualizer_output_{DateTime.Now:yyyyMMddHHmmss}.{format}";
			string outputFilePath = Path.Combine(Path.GetTempPath(), outputFileName);

			// 3. FFmpeg-Argumente für die Konvertierung erstellen
			// Wichtig: Wir definieren den Input als Pipe; die Audio-Input-Option muss vor den Encoder-/Output-Optionen stehen,
			// weil ffmpeg Optionen, die zwischen Inputs stehen, als Optionen für das nächste Input interpretiert.
			string videoInputArgs = $"-f rawvideo -pix_fmt rgba -s {width}x{height} -r {frameRate.ToString(CultureInfo.InvariantCulture)} -i pipe:0";
			string audioInputArg = $"-i \"{audioFile}\"";

			// Versuche zuerst NVENC, falls Fehler bzw. Encoder nicht verfügbar ist, fällt auf libx264 zurück.
			string[] codecCandidates = ["h264_nvenc", "libx264"];

			// 4. FFmpeg-Prozess vorbereiten und starten (ggf. mehrfach versuchen)
			var ffmpegDir = FFmpeg.ExecutablesPath ?? string.Empty;
			var ffmpegExe = RuntimeInformation.IsOSPlatform(OSPlatform.Windows) ? "ffmpeg.exe" : "ffmpeg";
			var ffmpegPath = Path.Combine(ffmpegDir, ffmpegExe);

			foreach (var codec in codecCandidates)
			{
				// Codec-spezifische Optionen (nvenc und libx264 haben unterschiedliche empfohlene Flags)
				string codecSpecificArgs = codec == "h264_nvenc"
					? "-preset fast -qp 20"
					: "-preset fast -crf 23"; // libx264: bessere Standard-Optionen

				string outputOptions = $"-c:v {codec} {codecSpecificArgs} -c:a aac -b:a 256k -pix_fmt yuv420p -y \"{outputFilePath}\"";

				// WICHTIG: Audio-Input muss vor den Output/Codec-Optionen stehen
				string fullArguments = $"{videoInputArgs} {audioInputArg} {outputOptions}";

				var startInfo = new ProcessStartInfo(ffmpegPath)
				{
					Arguments = fullArguments,
					UseShellExecute = false,
					CreateNoWindow = true,
					RedirectStandardInput = true,
					RedirectStandardError = true
				};

				using (var process = new Process { StartInfo = startInfo })
				{
					Console.WriteLine("Trying to write MP4 using: " + codec + " (" + codecSpecificArgs + ")");
					try
					{
						process.Start();

						// Beginne sofort, StandardError asynchron zu lesen
						var errorReaderTask = Task.Run(async () =>
						{
							try { return await process.StandardError.ReadToEndAsync(); }
							catch { return string.Empty; }
						});

						// Schreibe die Frames in stdin
						var frameWritingTask = Task.Run(async () =>
						{
							try
							{
								var inputStream = process.StandardInput.BaseStream;
								foreach (var imgRgba in imageList)
								{
									if (process.HasExited)
									{
										break;
									}

									try
									{
										await inputStream.WriteAsync(imgRgba.AsMemory(), CancellationToken.None);
									}
									catch (IOException)
									{
										// Pipe geschlossen -> niemals weiter schreiben
										break;
									}
								}
							}
							finally
							{
								try { process.StandardInput.Close(); } catch { }
							}
						});

						await frameWritingTask;
						await process.WaitForExitAsync();

						var stderr = await errorReaderTask;

						if (process.ExitCode == 0)
						{
							// Erfolg
							return File.Exists(outputFilePath) ? outputFilePath : null;
						}
						else
						{
							// Bei Encoder-spezifischen Fehlern: versuche nächsten Kandidaten
							Console.WriteLine($"FFmpeg failed with codec={codec}, exit={process.ExitCode}");
							Console.WriteLine(stderr ?? string.Empty);

							// Wenn Fehler nicht encoder-spezifisch ist, brechen wir ggf. ab.
							// Prüfe typische Hinweise, die auf fehlenden Encoder hinweisen.
							if (codec == "h264_nvenc" &&
								(stderr != null && (stderr.Contains("Unknown encoder", StringComparison.OrdinalIgnoreCase)
									|| stderr.Contains("Unknown decoder", StringComparison.OrdinalIgnoreCase)
									|| stderr.Contains("nvenc", StringComparison.OrdinalIgnoreCase)
									|| stderr.Contains("Device or resource busy", StringComparison.OrdinalIgnoreCase)
									|| stderr.Contains("Requested codec not found", StringComparison.OrdinalIgnoreCase))))
							{
								// Weiter zur nächsten Iteration (libx264)
								continue;
							}

							// Andernfalls Abbruch und null zurückgeben
							return null;
						}
					}
					catch (Exception ex)
					{
						Console.WriteLine("Failed to start or run ffmpeg: " + ex.Message);
						// Bei Startfehlern nicht weiter versuchen
						return null;
					}
				}
			}

			// Alle Versuche fehlgeschlagen
			Console.WriteLine("All FFmpeg codec attempts failed for MP4 export.");
			return null;
		}



		public static async Task<string?> MergeFramesAndAudioToAviAsync(IEnumerable<byte[]> framesRgba, int width, int height, AudioObj audioObj, double? fps = null, int quality = 70,  string? outputFilePath = null, IProgress<double>? progress = null, CancellationToken ct = default)
		{
			// Validierung
			if (audioObj == null || audioObj.Data == null || audioObj.Data.LongLength <= 0)
			{
				return null;
			}

			var audioSamples = audioObj.Data;
			int sampleRate = audioObj.SampleRate > 0 ? audioObj.SampleRate : 44100;
			int channels = audioObj.Channels > 0 ? audioObj.Channels : 2;
			var frames = framesRgba?.ToList() ?? new List<byte[]>();
			if (frames.Count == 0 || audioSamples == null || audioSamples.Length == 0 || width <= 0 || height <= 0 || sampleRate <= 0 || channels <= 0)
			{
				return null;
			}

			double frameRate = fps ?? Math.Max(1.0, frames.Count / Math.Max(0.0001, (double) audioSamples.Length / (sampleRate * channels)));
			if (frameRate <= 0)
			{
				frameRate = 30.0;
			}

			// Pfade
			string tmpDir = Path.Combine(Path.GetTempPath(), "lcws_sharpavi");
			Directory.CreateDirectory(tmpDir);
			if (string.IsNullOrWhiteSpace(outputFilePath))
			{
				outputFilePath = Path.Combine(tmpDir, $"visualizer_{DateTime.Now:yyyyMMddHHmmssfff}.avi");
			}

			// Vorverarbeitung: RGBA -> BGR24 (MJPEG erwartet 24bpp BGR)
			int frameRgbLen = width * height * 3;
			var bgrFrames = new byte[frames.Count][];
			// Parallel konvertieren
			await Task.Run(() =>
			{
				Parallel.For(0, frames.Count, new ParallelOptions { CancellationToken = ct }, i =>
				{
					ct.ThrowIfCancellationRequested();
					var src = frames[i];
					if (src == null || src.LongLength < width * height * 4)
					{
						// Fallback: leeres schwarzes Frame
						bgrFrames[i] = new byte[frameRgbLen];
						return;
					}
					var dst = new byte[frameRgbLen];
					int dstIdx = 0;
					for (int p = 0, s = 0; p < width * height; p++, s += 4)
					{
						// RGBA -> BGR
						byte r = src[s + 0];
						byte g = src[s + 1];
						byte b = src[s + 2];
						dst[dstIdx++] = b;
						dst[dstIdx++] = g;
						dst[dstIdx++] = r;
					}
					bgrFrames[i] = dst;
				});
			}, ct).ConfigureAwait(false);

			// Audio: float[] -> PCM16 interleaved bytes
			int totalSamples = audioSamples.Length;
			var audioBytes = new byte[totalSamples * 2];
			for (int i = 0; i < totalSamples; i++)
			{
				float s = audioSamples[i];
				if (s > 1.0f)
				{
					s = 1.0f;
				}

				if (s < -1.0f)
				{
					s = -1.0f;
				}

				short v = (short) Math.Round(s * short.MaxValue);
				int idx = i * 2;
				audioBytes[idx] = (byte) (v & 0xff);
				audioBytes[idx + 1] = (byte) ((v >> 8) & 0xff);
			}

			// Samples per frame (may be fractional)
			double samplesPerFrameD = (sampleRate * (double) channels) / frameRate;

			// SharpAvi writer
			try
			{
				// FramesPerSecond in AviWriter is int; store rounded fps in writer. Use fractional timing via audio interleaving.
				int fpsInt = Math.Max(1, (int) Math.Round(frameRate));

				using (var writer = new AviWriter(outputFilePath)
				{
					FramesPerSecond = fpsInt,
				})
				{
					// Video stream (MJPEG)
					var videoStream = writer.AddVideoStream(width, height, SharpAvi.BitsPerPixel.Bpp24);

					// Audio stream (PCM 16)
					// Signature differs between versions; this common overload exists in many versions:
					// AddAudioStream(samplesPerSecond, channels, bitsPerSample)
					var audioStream = writer.AddAudioStream(sampleRate, channels, 16);

					// Schreiben: für jeden Frame Video schreiben und passenden Audio-Block schreiben
					int frameCount = bgrFrames.Length;
					long writtenAudioBytes = 0;
					long totalAudioBytes = audioBytes.Length;

					for (int i = 0; i < frameCount; i++)
					{
						ct.ThrowIfCancellationRequested();

						// Video: WriteFrame expects RGB24/BGR24 raw bytes
						videoStream.WriteFrame(true, bgrFrames[i], 0, bgrFrames[i].Length);

						// Berechne wie viele Audio-Bytes diesem Frame entsprechen
						// samplesPerFrameD ist samples (including channel count) per frame
						int samplesThisFrame = (int) Math.Round(samplesPerFrameD);
						int bytesThisFrame = samplesThisFrame * 2; // 2 bytes per sample (PCM16)
						int remaining = (int) Math.Max(0, totalAudioBytes - writtenAudioBytes);
						if (remaining > 0)
						{
							int toWrite = Math.Min(bytesThisFrame, remaining);
							if (toWrite > 0)
							{
								audioStream.WriteBlock(audioBytes, (int) writtenAudioBytes, toWrite);
								writtenAudioBytes += toWrite;
							}
						}

						// Progress
						progress?.Report((i + 1) / (double) frameCount);
						await Task.Yield();
					}

					// Falls noch Audio übrig ist, anhängen
					if (writtenAudioBytes < totalAudioBytes)
					{
						audioStream.WriteBlock(audioBytes, (int) writtenAudioBytes, (int) (totalAudioBytes - writtenAudioBytes));
						writtenAudioBytes = totalAudioBytes;
					}

					// Dispose writer -> Datei abschließen
				}

				// Cleanup optional: keine temporären Dateien erstellt außer output
				return File.Exists(outputFilePath) ? outputFilePath : null;
			}
			catch (OperationCanceledException)
			{
				// Abbruch
				try { if (File.Exists(outputFilePath)) { File.Delete(outputFilePath); } } catch { }
				return null;
			}
			catch (Exception ex)
			{
				Console.WriteLine("SharpAvi write failed: " + ex.Message);
				try { if (File.Exists(outputFilePath)) { File.Delete(outputFilePath); } } catch { }
				return null;
			}
		}
	}
}
