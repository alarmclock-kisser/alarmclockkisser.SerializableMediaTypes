using System.Diagnostics;
using NAudio.Wave;

namespace alarmclockkisser.SerializableMediaTypes
{
    public static class AudioExporter
    {
		public static async Task<string?> ExportMp3Async(AudioObj audio, string outDir, int bitrate = 192, int? maxDegreeOfParallelism = null)
		{
			// Verify audio
			if (audio.Data.LongLength <= 0 || audio.SampleRate <= 0 || audio.BitDepth <= 0 || audio.Channels <= 0)
			{
				return null;
			}

			// Get export dir
			if (!Directory.Exists(outDir))
			{
				return "Out directory does not exist: " + outDir;
			}

			string outFile = Path.Combine(outDir, $"{audio.Name} [{audio.Bpm:F1}] {bitrate}kBit.mp3");

			Stopwatch sw = Stopwatch.StartNew();

			await Task.Run(() =>
			{
				// Konvertiere float[] Samples zu short[] PCM-Daten
				short[] pcm = new short[audio.Data.Length];

				var parallelOptions = maxDegreeOfParallelism.HasValue
					? new ParallelOptions { MaxDegreeOfParallelism = maxDegreeOfParallelism.Value }
					: new ParallelOptions();

				Parallel.For(0, audio.Data.Length, parallelOptions, i =>
				{
					float sample = Math.Clamp(audio.Data[i], -1.0f, 1.0f);
					pcm[i] = (short) (sample * short.MaxValue);
				});

				byte[] pcmBytes = new byte[pcm.Length * sizeof(short)];
				Buffer.BlockCopy(pcm, 0, pcmBytes, 0, pcmBytes.Length);

				var waveFormat = new WaveFormat(audio.SampleRate, audio.Channels);
				using var ms = new MemoryStream();
				using var writer = new NAudio.Lame.LameMP3FileWriter(ms, waveFormat, bitrate);

				writer.Write(pcmBytes, 0, pcmBytes.Length);
				writer.Flush();
				File.WriteAllBytes(outFile, ms.ToArray());
			});

			sw.Stop();
			audio["mp3Export"] = sw.Elapsed.TotalMilliseconds;

			return outFile;
		}

		public static async Task<string?> ExportWavAsync(AudioObj audio, string outPath, int bitDepth)
		{
			if (audio.Data == null || audio.Data.Length == 0)
			{
				return "AudioObj data was null or empty";
			}

			string baseFileName = $"{audio.Name.Replace("▶ ", "").Replace("|| ", "")}{(audio.Bpm > 10 ? (" [" + audio.Bpm.ToString("F1", System.Globalization.CultureInfo.InvariantCulture) + "]") : "")}".Trim();
			string finalPath = Path.Combine(outPath, $"{baseFileName}.wav");

			if (!Directory.Exists(outPath))
			{
				return "Out directory does not exist: " + outPath;
			}

			Stopwatch sw = Stopwatch.StartNew();

			try
			{
				await Task.Run(() =>
				{
					using var writer = new WaveFileWriter(finalPath, new WaveFormat(audio.SampleRate, bitDepth, audio.Channels));

					// Konvertierung und Schreiben
					if (bitDepth == 32)
					{
						writer.WriteSamples(audio.Data, 0, audio.Data.Length);
					}
					else if (bitDepth == 16)
					{
						short[] samples16Bit = new short[audio.Data.Length];
						for (int i = 0; i < audio.Data.Length; i++)
						{
							samples16Bit[i] = (short) (audio.Data[i] * short.MaxValue);
						}
						writer.WriteSamples(samples16Bit, 0, samples16Bit.Length);
					}
					else if (bitDepth == 8)
					{
						byte[] samples8Bit = new byte[audio.Data.Length];
						for (int i = 0; i < audio.Data.Length; i++)
						{
							// 8-Bit-PCM ist vorzeichenlos, 0 ist die Nulllinie
							samples8Bit[i] = (byte) ((audio.Data[i] + 1.0f) * 127.5f);
						}
						writer.Write(samples8Bit, 0, samples8Bit.Length);
					}
					else if (bitDepth == 24)
					{
						byte[] samples24Bit = new byte[audio.Data.Length * 3];
						int byteIndex = 0;
						for (int i = 0; i < audio.Data.Length; i++)
						{
							// Konvertiere Float zu 24-Bit-Integer und schreibe es als 3 Bytes
							int value = (int) (audio.Data[i] * 8388607.0f); // 2^23 - 1
							samples24Bit[byteIndex++] = (byte) (value);
							samples24Bit[byteIndex++] = (byte) (value >> 8);
							samples24Bit[byteIndex++] = (byte) (value >> 16);
						}
						writer.Write(samples24Bit, 0, samples24Bit.Length);
					}
					else
					{
						throw new ArgumentException("Ungültige Bit-Tiefe. Unterstützte Werte sind 8, 16, 24 und 32.");
					}
				});

				sw.Stop();
				audio["wavExport"] = sw.Elapsed.TotalMilliseconds;

				return finalPath;
			}
			catch (Exception ex)
			{
				Debug.WriteLine($"Audio export failed: {ex.Message}");
				return null;
			}
		}


	}
}
