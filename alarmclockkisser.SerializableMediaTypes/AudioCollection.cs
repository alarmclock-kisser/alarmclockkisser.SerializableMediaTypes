using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;
using System.Collections.Concurrent;
using System.Drawing;

namespace alarmclockkisser.SerializableMediaTypes
{
	public class AudioCollection
	{
		public ConcurrentDictionary<Guid, AudioObj> tracks { get; private set; } = [];
		public IReadOnlyList<AudioObj> Tracks => this.tracks.Values.OrderBy(t => t.CreatedAt).ToList();

		public string ResourcesPath { get; set; } = Path.GetFullPath(Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "..", "..", "..", "..", "alarmclockkisser.SerializableMediaTypes", "Resources"));

		public int Count => this.Tracks.Count;
		public string[] Entries => this.Tracks.Select(t => t.Name).ToArray();
		public string[] Playing => this.tracks.Values.Where(t => t.Playing).Select(t => t.Name).ToArray();

		public int MaxTracks { get; set; } = 1;

		public AudioObj? this[Guid guid]
		{
			get => this.tracks.TryGetValue(guid, out var obj) ? obj : null;
		}

		public AudioObj? this[string name]
		{
			get => this.tracks.Values.FirstOrDefault(t => t.Name.Equals(name, StringComparison.CurrentCultureIgnoreCase));
		}

		public AudioObj? this[int index]
		{
			get => index >= 0 && index < this.Count ? this.tracks.Values.ElementAt(index) : null;
		}

		public AudioObj? this[IntPtr pointer]
		{
			get => pointer != IntPtr.Zero ? this.tracks.Values.FirstOrDefault(t => t.Pointer == pointer) : null;
		}

		public AudioCollection()
		{
		}

		public AudioCollection(int? maxracks = 1)
		{
			this.MaxTracks = maxracks ?? 1;

			if (!Directory.Exists(this.ResourcesPath))
			{
				this.ResourcesPath = Path.Combine(AppContext.BaseDirectory, "Resources");
				if (!Directory.Exists(this.ResourcesPath))
				{
					Console.WriteLine($"AudioCollection: Resources directory not found at '{this.ResourcesPath}'.");
				}
				else
				{
					this.ResourcesPath = Path.GetFullPath(this.ResourcesPath);
					Console.WriteLine($"AudioCollection: Using Resources directory at '{this.ResourcesPath}'.");
				}
			}
		}

		public void SetRemoveAfterPlayback(bool remove)
		{
			foreach (var track in this.tracks.Values)
			{
				track.RemoveAfterPlayback = remove;
			}
		}

		private bool ExistsByFileName(string filePath)
		{
			var name = Path.GetFileName(filePath);
			return this.tracks.Values.Any(t => string.Equals(Path.GetFileName(t.FilePath), name, StringComparison.OrdinalIgnoreCase));
		}

		private AudioObj? FindByHash(string? hash)
		{
			if (string.IsNullOrWhiteSpace(hash))
			{
				return null;
			}

			return this.tracks.Values.FirstOrDefault(t => string.Equals(t.ContentHash, hash, StringComparison.OrdinalIgnoreCase));
		}

		public async Task<AudioObj?> ImportAsync(string filePath, string? overwriteName = null, bool linearLoad = true)
		{
			if (!File.Exists(filePath))
			{
				return null;
			}

			// Compute hash early for dedupe
			string? hash = AudioObj.ComputeFileHash(filePath);
			var existingByHash = this.FindByHash(hash);
			if (existingByHash != null)
			{
				return existingByHash; // Already imported exact same content
			}

			// Also check by filename (helps when hash could not be computed)
			if (hash == null && this.ExistsByFileName(filePath))
			{
				return this.tracks.Values.First(t => string.Equals(Path.GetFileName(t.FilePath), Path.GetFileName(filePath), StringComparison.OrdinalIgnoreCase));
			}

			AudioObj? obj = null;
			if (linearLoad)
			{
				await Task.Run(() => { obj = new AudioObj(filePath, overwriteName, true); });
			}
			else
			{
				obj = await AudioObj.CreateAsync(filePath);
			}
			if (obj == null)
			{
				return null;
			}

			obj.ContentHash = hash ?? AudioObj.ComputeFileHash(filePath);

			if (!this.tracks.TryAdd(obj.Id, obj))
			{
				obj.Dispose();
				return null;
			}

			obj.RemoveRequested += async (_, __) => { await this.RemoveAsync(obj); };

			return obj;
		}

		public async Task RemoveAsync(AudioObj? obj)
		{
			if (obj == null)
			{
				return;
			}

			// Entfernen aus Dictionary (threadsafe)
			if (this.tracks.TryRemove(obj.Id, out var removed))
			{
				removed.Dispose();
			}
			await Task.CompletedTask;
		}

		public void StopAll(bool remove = false)
		{
			foreach (var track in this.tracks.Values.ToList())
			{
				bool wasPlaying = track.Playing;
				track.Stop();

				if (remove && wasPlaying)
				{
					if (this.tracks.TryRemove(track.Id, out var t))
					{
						t?.Dispose();
					}
				}
			}
		}

		public void SetMasterVolume(float percentage)
		{
			percentage = Math.Clamp(percentage, 0.0f, 1.0f);

			foreach (var track in this.tracks.Values)
			{
				int volume = (int)(track.Volume * percentage);
				track.SetVolume(volume);
			}
		}

		public async Task DisposeAsync()
		{
			var items = this.tracks.Values.ToList();
			this.tracks.Clear();

			foreach (var track in items)
			{
				track.Dispose();
			}
			await Task.CompletedTask;
		}

		public static async Task<AudioObj?> LevelAudioFileAsync(string filePath, float duration = 1.0f, float normalize = 1.0f)
		{
			AudioObj? obj = await AudioObj.CreateAsync(filePath);
			if (obj == null)
			{
				return null;
			}

			await obj.Level(duration, normalize);

			return obj;
		}

		public async Task<int> EnforceTracksLimit(int? limitOverride = null)
					{
			int limit = limitOverride ?? this.MaxTracks;
			if (limit <= 0)
			{
				return 0;
			}

			int removed = 0;
			while (this.Count > limit)
			{
				var oldest = this.tracks.Values.OrderBy(t => t.CreatedAt).FirstOrDefault();
				if (oldest != null)
				{
					await this.RemoveAsync(oldest);
					removed++;
				}
				else
				{
					break;
				}
			}

			return removed;
		}

		public void Clear()
		{
			foreach (var track in this.tracks.Values.ToList())
			{
				if (this.tracks.TryRemove(track.Id, out var t))
				{
					t?.Dispose();
				}
			}
		}

		public static async Task<AudioObj?> CreateFromDataAsync(byte[] samples, int sampleRate, int channels, int bitdepth)
		{
			if (samples.LongLength <= 0 || sampleRate <= 0 || channels <= 0)
			{
				return null;
			}

			// Unterstützte Bit-Tiefen (unkomprimiertes PCM oder 32-bit Float)
			if (bitdepth is not (8 or 16 or 24 or 32))
			{
				bitdepth = 16; // Fallback
			}

			int processorCount = Environment.ProcessorCount;
			float[] floats;
			int sampleCount;

			switch (bitdepth)
			{
				case 8:
					// 8-bit PCM ist gewöhnlich unsigned (0..255) → map nach -1..1
					sampleCount = samples.Length;
					floats = new float[sampleCount];
					Parallel.For(0, sampleCount, new ParallelOptions { MaxDegreeOfParallelism = processorCount }, i =>
					{
						floats[i] = (samples[i] / 255f) * 2f - 1f;
					});
					break;

				case 16:
					sampleCount = samples.Length / 2;
					floats = new float[sampleCount];
					Parallel.For(0, sampleCount, new ParallelOptions { MaxDegreeOfParallelism = processorCount }, i =>
					{
						int bi = i * 2;
						short v = (short) (samples[bi] | (samples[bi + 1] << 8)); // little endian
						floats[i] = v / 32768f;
					});
					break;

				case 24:
					sampleCount = samples.Length / 3;
					floats = new float[sampleCount];
					Parallel.For(0, sampleCount, new ParallelOptions { MaxDegreeOfParallelism = processorCount }, i =>
					{
						int bi = i * 3;
						int raw = samples[bi] | (samples[bi + 1] << 8) | (samples[bi + 2] << 16);
						// Sign-Extend 24-bit
						if ((raw & 0x00800000) != 0)
						{
							raw |= unchecked((int) 0xFF000000);
						}
						floats[i] = raw / 8388608f; // 2^23
					});
					break;

				case 32:
					sampleCount = samples.Length / 4;
					floats = new float[sampleCount];
					Parallel.For(0, sampleCount, new ParallelOptions { MaxDegreeOfParallelism = processorCount }, i =>
					{
						int bi = i * 4;
						// Versuche: behandle als IEEE 754 Float (typisch bei 32-bit float WAV)
						floats[i] = BitConverter.ToSingle(samples, bi);
					});
					break;

				default:
					return null;
			}

			// Reihenfolge bleibt erhalten (keine Race Conditions, da jeder Index exklusiv beschrieben wird)
			var obj = new AudioObj(floats, sampleRate, channels, bitdepth);

			await Task.Yield();
			return obj;
		}

		public async Task<IEnumerable<AudioObj>> LoadFromResources(string? resourcesPath = null)
		{
			resourcesPath ??= this.ResourcesPath;
			if (!Directory.Exists(resourcesPath))
			{
				Console.WriteLine("AudioCollection: Resources directory not found at '" + resourcesPath + "'.");
				return [];
			}

			var supportedExtensions = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { ".wav", ".mp3", ".flac"};
			var files = Directory.GetFiles(resourcesPath, "*.*", SearchOption.AllDirectories)
				.Where(f => supportedExtensions.Contains(Path.GetExtension(f)))
				.ToList();

			var imported = new List<AudioObj>();
			foreach (var file in files)
			{
				var obj = await this.ImportAsync(file, linearLoad: false);
				if (obj != null)
				{
					imported.Add(obj);
				}
			}

			return imported;
		}



		public static async Task<string?> GenerateWaveformFromBytesAsBase64Async(IEnumerable<byte> samples, int sampeRate, int channels, int bitDepth,
			string? offsetString = null, int samplesPerPixel = 256, int width = 800, int height = 200,
			string? graphColorHex = "#FFFFFF", string? backgroundColorHex = "#000000", string imageBase64Format = "jpg")
		{
			// Basisvalidierung
			if (sampeRate <= 0 || channels <= 0 || bitDepth <= 0)
			{
				return null;
			}

			samplesPerPixel = Math.Clamp(samplesPerPixel, 1, 65536);
			width = Math.Clamp(width, 1, 8192);
			height = Math.Clamp(height, 1, 4096);
			imageBase64Format = imageBase64Format.ToLowerInvariant().Trim().Trim('.');
			if (!ImageCollection.SupportedFormats.Contains(imageBase64Format))
			{
				imageBase64Format = "jpg";
			}

			graphColorHex = string.IsNullOrWhiteSpace(graphColorHex) ? "#FFFFFF" : "#" + graphColorHex.Replace("#", "");
			backgroundColorHex = string.IsNullOrWhiteSpace(backgroundColorHex) ? "#000000" : "#" + backgroundColorHex.Replace("#", "");

			long offsetFrames = 0;
			if (!string.IsNullOrWhiteSpace(offsetString) && long.TryParse(offsetString, out var parsed) && parsed > 0)
			{
				offsetFrames = parsed;
			}

			// Materialisieren (wir brauchen zufälligen Zugriff)
			byte[] data = samples as byte[] ?? samples.ToArray();
			if (data.Length == 0)
			{
				return null;
			}

			int bytesPerSample = bitDepth / 8;
			if (bytesPerSample != 1 && bytesPerSample != 2 && bytesPerSample != 3 && bytesPerSample != 4)
			{
				return null;
			}

			long totalSamples = data.LongLength / bytesPerSample;
			long totalFrames = totalSamples / channels;
			if (totalFrames <= 0)
			{
				return null;
			}

			if (offsetFrames >= totalFrames)
			{
				offsetFrames = 0;
			}

			// Benötigte Frame-Anzahl für das Bild
			long framesNeeded = (long) width * samplesPerPixel;
			long framesAvailableFromOffset = totalFrames - offsetFrames;
			long framesToUse = Math.Min(framesNeeded, framesAvailableFromOffset);
			if (framesToUse <= 0)
			{
				return null;
			}

			// Ziel: pro X-Säule min/max Amplitude bestimmen
			// Wir normalisieren alle Kanäle zu mono über Peak (max abs) oder Mittelwert – hier: Peak (robuster)
			float[] columnMin = new float[width];
			float[] columnMax = new float[width];
			for (int i = 0; i < width; i++)
			{
				columnMin[i] = 1f;
				columnMax[i] = -1f;
			}

			// Performance: berechne direkte Start-Byteposition
			long startFrame = offsetFrames;
			long startSampleIndex = startFrame * channels;
			long startByteIndex = startSampleIndex * bytesPerSample;

			// Clamp Ende
			long lastFrameExclusive = startFrame + framesToUse;
			long lastSampleExclusive = lastFrameExclusive * channels;
			long lastByteExclusive = Math.Min(lastSampleExclusive * bytesPerSample, data.LongLength);

			// Fix: Verschiebe die Verwendung von ReadOnlySpan<byte> in den synchronen Task.Run-Block
			int logicalProc = Environment.ProcessorCount;
			int columns = width;
			int chunkColumns = Math.Max(1, columns / logicalProc);

			await Task.Run(() =>
			{
				// Kopiere die Daten in ein Array, das in der Lambda verwendet werden kann
				byte[] localData = data;

				static float ReadSample(byte[] s, int bitDepth, long byteIndex)
				{
					switch (bitDepth)
					{
						case 8:
							sbyte v8 = unchecked((sbyte) s[(int) byteIndex]);
							return v8 / 128f;
						case 16:
							{
								short v16 = (short) (s[(int) byteIndex] | (s[(int) (byteIndex + 1)] << 8));
								return v16 / 32768f;
							}
						case 24:
							{
								int b0 = s[(int) byteIndex];
								int b1 = s[(int) (byteIndex + 1)];
								int b2 = s[(int) (byteIndex + 2)];
								int raw = (b0) | (b1 << 8) | (b2 << 16);
								if ((raw & 0x00800000) != 0)
								{
									raw |= unchecked((int) 0xFF000000);
								}
								return raw / 8388608f;
							}
						case 32:
							{
								int i = s[(int) byteIndex] | (s[(int) (byteIndex + 1)] << 8) | (s[(int) (byteIndex + 2)] << 16) | (s[(int) (byteIndex + 3)] << 24);
								return BitConverter.Int32BitsToSingle(i);
							}
						default:
							return 0f;
					}
				}

				Parallel.For(0, columns, new ParallelOptions { MaxDegreeOfParallelism = logicalProc }, x =>
				{
					long frameStart = startFrame + (long) x * samplesPerPixel;
					if (frameStart >= lastFrameExclusive)
					{
						return;
					}
					long frameEnd = Math.Min(frameStart + samplesPerPixel, lastFrameExclusive);

					float localMin = 1f;
					float localMax = -1f;

					for (long f = frameStart; f < frameEnd; f++)
					{
						long baseSample = f * channels;
						float peak = 0f;
						for (int c = 0; c < channels; c++)
						{
							long sampleIndex = baseSample + c;
							long bIndex = sampleIndex * bytesPerSample;
							if (bIndex + bytesPerSample > lastByteExclusive)
							{
								break;
							}
							float val = ReadSample(localData, bitDepth, bIndex); // Verwende das lokale Array
							float abs = Math.Abs(val);
							if (abs > peak)
							{
								peak = abs;
							}
						}

						if (peak > localMax)
						{
							localMax = peak;
						}

						if (-peak < localMin)
						{
							localMin = -peak;
						}
					}

					columnMin[x] = localMin;
					columnMax[x] = localMax;
				});
			});

			// Bild zeichnen
			var fg = System.Drawing.ColorTranslator.FromHtml(graphColorHex);
			var bg = System.Drawing.ColorTranslator.FromHtml(backgroundColorHex);

			using var image = new SixLabors.ImageSharp.Image<Rgba32>(width, height, new Rgba32(bg.R, bg.G, bg.B, 255));
			Rgba32 lineColor = new Rgba32(fg.R, fg.G, fg.B, 255);
			float half = height / 2f - 0.5f;

			// Schnelles Zeichnen: direkte Row-Zugriffe
			for (int x = 0; x < width; x++)
			{
				float minVal = columnMin[x]; // -1..0
				float maxVal = columnMax[x]; // 0..1

				int yMid = (int) Math.Round(half);
				int yMin = (int) Math.Round(half - (minVal * -half)); // minVal ist negativ
				int yMax = (int) Math.Round(half - (maxVal * half));

				if (yMax > yMin)
				{
					(yMax, yMin) = (yMin, yMax);
				}
				yMin = Math.Clamp(yMin, 0, height - 1);
				yMax = Math.Clamp(yMax, 0, height - 1);

				image.ProcessPixelRows(accessor =>
				{
					for (int y = yMax; y <= yMin; y++)
					{
						accessor.GetRowSpan(y)[x] = lineColor;
					}
				});
			}

			// Export als Base64
			using var ms = new MemoryStream();
			if (imageBase64Format == "png")
			{
				await image.SaveAsPngAsync(ms);
			}
			else if (imageBase64Format == "bmp")
			{
				await image.SaveAsBmpAsync(ms);
			}
			else if (imageBase64Format == "gif")
			{
				await image.SaveAsGifAsync(ms);
			}
			else if (imageBase64Format == "jpg" || imageBase64Format == "jpeg")
			{
				await image.SaveAsJpegAsync(ms);
			}
			else
			{
				await image.SaveAsJpegAsync(ms);
			}

			string b64 = Convert.ToBase64String(ms.ToArray());
			return b64;
		}



	}
}
