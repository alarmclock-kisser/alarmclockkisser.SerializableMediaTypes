using NAudio.Wave;
using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.Runtime.CompilerServices;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;
using SixLabors.ImageSharp.Processing;
using System.Security.Cryptography; 

namespace alarmclockkisser.SerializableMediaTypes
{
	public class AudioObj : IDisposable, INotifyPropertyChanged
	{
		public event PropertyChangedEventHandler? PropertyChanged;
		private SynchronizationContext? syncCtx;
		public event EventHandler? RemoveRequested;

		private void OnRemoveRequested()
		{
			var handler = RemoveRequested;
			if (handler == null)
			{
				return;
			}

			if (this.syncCtx != null && SynchronizationContext.Current != this.syncCtx)
			{
				this.syncCtx.Post(_ => handler(this, EventArgs.Empty), null);
			}
			else
			{
				handler(this, EventArgs.Empty);
			}
		}

		private void OnPropertyChanged([CallerMemberName] string? propertyName = null)
		{
			var handler = this.PropertyChanged;
			if (handler == null)
			{
				return;
			}

			// Auf UI-Thread mappen, falls vorhanden
			if (this.syncCtx != null && SynchronizationContext.Current != this.syncCtx)
			{
				this.syncCtx.Post(_ => handler(this, new PropertyChangedEventArgs(propertyName)), null);
			}
			else
			{
				handler(this, new PropertyChangedEventArgs(propertyName));
			}
		}

		public void BindToUiContext(SynchronizationContext? ctx = null)
		{
			this.syncCtx = ctx ?? SynchronizationContext.Current ?? this.syncCtx;
		}

		public Guid Id { get; private set; } = Guid.Empty;
		public DateTime CreatedAt { get; private set; } = DateTime.Now;
		public string FilePath { get; set; } = string.Empty;
		public string? ExportedFile { get; private set; } = null;
		public string Name => (this.IsProcessing ? "~Processing~ " : "") + (this.Playing ? "▶ " : (this.Paused ? "|| " : "")) + Path.GetFileNameWithoutExtension(this.FilePath);

		public float[] Data { get; private set; } = [];
		private int originalSampleRate = 0;
		public int SampleRate
		{
			get => this.originalSampleRate > 0 ? this.originalSampleRate : 44100;
			set
			{
				this.originalSampleRate = value > 0 ? value : 44100;
			}
		}
		public int Channels { get; private set; } = 0;
		public int BitDepth { get; private set; } = 0;
		public long Length => this.Data.LongLength;
		public double TotalSeconds => (this.SampleRate > 0 && this.Channels > 0) ? Math.Round((double) this.Length / (this.SampleRate * this.Channels), 3) : 0;
		public TimeSpan Duration => TimeSpan.FromSeconds(this.TotalSeconds);
		public float SizeInMb => this.Data.LongLength * sizeof(float) / (1024.0f * 1024.0f);

		private bool isProcessing;
		public bool IsProcessing
		{
			get => this.isProcessing;
			set
			{
				if (this.isProcessing == value)
				{
					return;
				}

				this.isProcessing = value;
				this.OnPropertyChanged();
				this.OnPropertyChanged(nameof(this.Name));
			}
		}

		public Dictionary<string, double> Metrics { get; private set; } = [];
			
		/*new Dictionary<string, double>
		{
			{ "Import", 0.0 },{ "Export", 0.0 },{ "Chunk", 0.0 },{ "Aggregate", 0.0 },
			{ "Normalize", 0.0 },{ "Level", 0.0 },{ "Push", 0.0 },{ "Pull", 0.0 },
			{ "FFT", 0.0 },{ "IFFT", 0.0 },{ "Stretch", 0.0 },
			{ "BeatScan", 0.0 },{ "TimingScan", 0.0 }
		};*/

		public double this[string metric]
		{
			get
			{
				// Find by tolower case
				if (this.Metrics.TryGetValue(metric, out double value))
				{
					return value;
				}
				else
				{
					var key = this.Metrics.Keys.FirstOrDefault(k => k.Equals(metric, StringComparison.OrdinalIgnoreCase));
					if (key != null)
					{
						return this.Metrics[key];
					}
					else
					{
						// If not found, return 0.0
						return 0.0;
					}
				}
			}
			set
			{
				// Find by tolower case
				if (this.Metrics.ContainsKey(metric))
				{
					this.Metrics[metric] = value;
				}
				else
				{
					var key = this.Metrics.Keys.FirstOrDefault(k => k.Equals(metric, StringComparison.OrdinalIgnoreCase));
					if (key != null)
					{
						this.Metrics[key] = value;
					}
					else
					{
						// Capitalize first letter and add to dictionary
						string capitalizedMetric = char.ToUpper(metric[0]) + metric.Substring(1).ToLowerInvariant();
						this.Metrics.Add(capitalizedMetric, value);
					}
				}
			}
		}

		public bool OnHost => this.Data.LongLength > 0 && this.Pointer == IntPtr.Zero;
		public bool OnDevice => this.Pointer != IntPtr.Zero;
		public IntPtr Pointer { get; set; } = IntPtr.Zero;
		public int ChunkSize { get; set; } = 0;
		public int OverlapSize { get; set; } = 0;
		public string Form { get; set; } = "f";
		public double StretchFactor { get; set; } = 1.0;

		public float Bpm { get; private set; } = 0.0f;
		public float Timing = 1.0f;
		public float ScannedBpm { get; set; } = 0.0f;
		public float ScannedTiming = 0.0f;

		private float currentVolume = 1.0f;
		public int Volume => (int) (this.currentVolume * 100.0f);
		private WaveOutEvent player = new();
		private WaveStream? waveStream;
		public object playerLock = new();

		public bool PlayerPlaying => this.player != null && this.player.PlaybackState == PlaybackState.Playing;
		private bool _playing;
		public bool Playing
		{
			get => this._playing;
			private set
			{
				if (this._playing == value)
				{
					return;
				}

				this._playing = value;
				this.OnPropertyChanged();
				this.OnPropertyChanged(nameof(this.Name));
			}
		}
		private bool _paused;
		public bool Paused
		{
			get => this._paused;
			private set
			{
				if (this._paused == value)
				{
					return;
				}

				this._paused = value;
				this.OnPropertyChanged();
				this.OnPropertyChanged(nameof(this.Name));
			}
		}

		private long position => (this.Paused || this.Playing) && this.waveStream != null ? (this.waveStream.Position / sizeof(float)) / this.Channels : 0;
		private double positionSeconds => this.SampleRate <= 0 ? 0 : (double) this.position / this.SampleRate;
		public TimeSpan CurrentTime => TimeSpan.FromSeconds(this.positionSeconds);

		public int SilenceStart { get; private set; } = 0;

		private CancellationTokenSource loopCts = new();
		private float currentLoopValue = 0.0f;
		private bool isLooping;
		private long loopStart = 0;
		public float Looping => this.isLooping ? this.currentLoopValue : 0;

		public bool RemoveAfterPlayback { get; set; } = false;

		internal AudioObj(string filePath, string? overwriteName = null,  bool linearLoad = true)
		{
			this.Id = Guid.NewGuid();
			this.FilePath = filePath;
			this.syncCtx = SynchronizationContext.Current;
			this.ReadBpmTag();

			if (this.Data.LongLength <= 0 && linearLoad)
			{
				this.LoadAudioFile();
			}

			if (!string.IsNullOrEmpty(overwriteName))
			{
				this.FilePath = Path.Combine(Path.GetDirectoryName(filePath) ?? "", overwriteName);
			}
		}

		internal AudioObj(float[] samples, int sampleRate, int channels, int bitdepth)
		{
			this.Id = Guid.NewGuid();
			this.Data = samples ?? [];
			this.SampleRate = sampleRate > 0 ? sampleRate : 44100;
			this.Channels = channels > 0 ? channels : 2;
			this.BitDepth = bitdepth == 8 || bitdepth == 16 || bitdepth == 24 || bitdepth == 32 ? bitdepth : 32;
			this.syncCtx = SynchronizationContext.Current;
		}

		public static AudioObj? LoadFromFile(string filePath)
		{
			return new AudioObj(filePath, null, true);
		}

		public static async Task<AudioObj?> LoadFromFileAsync(string filePath)
		{
			var obj = await CreateAsync(filePath);

			return obj;
		}

		public void Dispose()
		{
			this.player?.Stop();
			this.Playing = false;

			this.Data = [];

			this.waveStream?.Dispose();
			this.waveStream = null;
			this.player?.Dispose();

			GC.SuppressFinalize(this);
		}

		public AudioObj Clone()
		{
			var clone = new AudioObj(this.Data.ToArray(), this.SampleRate, this.Channels, this.BitDepth)
			{
				FilePath = this.FilePath,
				Form = this.Form,
				StretchFactor = this.StretchFactor,
				Bpm = this.Bpm,
				Timing = this.Timing,
				ScannedBpm = this.ScannedBpm,
				ScannedTiming = this.ScannedTiming
			};

			return clone;
		}

		public async Task<AudioObj?> CloneAsync()
		{
			var clone = new AudioObj(this.Data.ToArray(), this.SampleRate, this.Channels, this.BitDepth)
			{
				FilePath = this.FilePath,
				Form = this.Form,
				StretchFactor = this.StretchFactor,
				Bpm = this.Bpm,
				Timing = this.Timing,
				ScannedBpm = this.ScannedBpm,
				ScannedTiming = this.ScannedTiming
			};
			return await Task.FromResult(clone);
		}

		public void LoadAudioFile()
		{
			if (string.IsNullOrEmpty(this.FilePath))
			{
				throw new FileNotFoundException("File path is empty");
			}

			Stopwatch sw = Stopwatch.StartNew();

			using AudioFileReader reader = new(this.FilePath);
			this.originalSampleRate = reader.WaveFormat.SampleRate;
			this.BitDepth = reader.WaveFormat.BitsPerSample;
			this.Channels = reader.WaveFormat.Channels;

			long numSamples = 0;
			try
			{
				if (reader.Length > 0 && reader.WaveFormat.BitsPerSample > 0)
				{
					numSamples = reader.Length / (reader.WaveFormat.BitsPerSample / 8);
				}
			}
			catch { numSamples = 0; }

			if (numSamples > 0)
			{
				try
				{
					float[] tmp = new float[numSamples];
					int read = reader.Read(tmp, 0, (int)numSamples);
					if (read != numSamples)
					{
						float[] resized = new float[read];
						Array.Copy(tmp, resized, read);
						this.Data = resized;
					}
					else
					{
						this.Data = tmp;
					}
				}
				catch
				{
					this.Data = ReadAllSamplesStreaming(reader).ToArray();
				}
			}
			else
			{
				this.Data = ReadAllSamplesStreaming(reader).ToArray();
			}

			sw.Stop();
			this["Import"] = sw.Elapsed.TotalMilliseconds;
			this.ReadBpmTag();
		}

		private static IEnumerable<float> ReadAllSamplesStreaming(AudioFileReader reader)
		{
			const int BlockSeconds = 1;
			int blockSize = reader.WaveFormat.SampleRate * reader.WaveFormat.Channels * BlockSeconds;
			float[] buffer = new float[blockSize];
			int read;
			while ((read = reader.Read(buffer, 0, buffer.Length)) > 0)
			{
				for (int i = 0; i < read; i++)
				{
					yield return buffer[i];
				}
			}
		}

		internal static async Task<AudioObj?> CreateAsync(string filePath)
		{
			if (string.IsNullOrEmpty(filePath) || !File.Exists(filePath))
			{
				return null;
			}

			var obj = new AudioObj(filePath, null, false);
			Stopwatch sw = Stopwatch.StartNew();

			try
			{
				using var reader = new AudioFileReader(filePath);
				obj.originalSampleRate = reader.WaveFormat.SampleRate;
				obj.Channels = reader.WaveFormat.Channels;
				obj.BitDepth = reader.WaveFormat.BitsPerSample;

				long numSamples = 0;
				try
				{
					if (reader.Length > 0 && reader.WaveFormat.BitsPerSample > 0)
					{
						numSamples = reader.Length / (reader.WaveFormat.BitsPerSample / 8);
					}
				}
				catch { numSamples = 0; }

				if (numSamples > 0)
				{
					float[] tmp = new float[numSamples];
					int read = reader.Read(tmp, 0, (int)numSamples);
					if (read != numSamples)
					{
						float[] resized = new float[read];
						Array.Copy(tmp, resized, read);
						obj.Data = resized;
					}
					else
					{
						obj.Data = tmp;
					}
				}
				else
				{
					obj.Data = ReadAllSamplesStreaming(reader).ToArray();
				}
			}
			catch
			{
				obj.Data = [];
			}

			sw.Stop();
			obj["import"] = sw.Elapsed.TotalMilliseconds;
			return await Task.FromResult(obj);
		}

		public float ReadBpmTag(string tag = "TBPM", bool set = true)
		{
			// Read bpm metadata if available
			float bpm = 0.0f;
			float roughBpm = 0.0f;

			try
			{
				if (!string.IsNullOrEmpty(this.FilePath) && File.Exists(this.FilePath))
				{
					using var file = TagLib.File.Create(this.FilePath);
					if (file.Tag.BeatsPerMinute > 0)
					{
						roughBpm = (float) file.Tag.BeatsPerMinute;
					}
					if (file.TagTypes.HasFlag(TagLib.TagTypes.Id3v2))
					{
						var id3v2Tag = (TagLib.Id3v2.Tag) file.GetTag(TagLib.TagTypes.Id3v2);

						var tagTextFrame = TagLib.Id3v2.TextInformationFrame.Get(id3v2Tag, tag, false);

						if (tagTextFrame != null && tagTextFrame.Text.Any())
						{
							string bpmString = tagTextFrame.Text.FirstOrDefault() ?? "0,0";
							if (!string.IsNullOrEmpty(bpmString))
							{
								bpmString = bpmString.Replace(',', '.');

								if (float.TryParse(bpmString, NumberStyles.Any, CultureInfo.InvariantCulture, out float parsedBpm))
								{
									bpm = parsedBpm;
								}
							}
						}
						else
						{
							bpm = 0.0f;
						}
					}
				}
			}
			catch (Exception ex)
			{
				Debug.WriteLine($"Fehler beim Lesen des Tags {tag.ToUpper()}: {ex.Message} ({ex.InnerException?.Message ?? " - "})");
			}

			// Take rough bpm if <= 0.0f
			if (bpm <= 0.0f && roughBpm > 0.0f)
			{
				Console.WriteLine($"No value found for '{tag.ToUpper()}', taking rough BPM value from legacy tag.");
				bpm = roughBpm;
			}

			if (set)
			{
				this.Bpm = bpm;
				if (this.Bpm <= 10)
				{
					this.ReadBpmTagLegacy();
				}
			}

			return bpm;
		}

		public float ReadBpmTagLegacy()
		{
			// Read bpm metadata if available
			float bpm = 0.0f;

			try
			{
				if (!string.IsNullOrEmpty(this.FilePath) && File.Exists(this.FilePath))
				{
					using var file = TagLib.File.Create(this.FilePath);
					// Check for BPM in standard ID3v2 tag
					if (file.Tag.BeatsPerMinute > 0)
					{
						bpm = (float) file.Tag.BeatsPerMinute;
					}
					// Alternative für spezielle Tags (z.B. TBPM Frame)
					else if (file.TagTypes.HasFlag(TagLib.TagTypes.Id3v2))
					{
						var id3v2Tag = (TagLib.Id3v2.Tag) file.GetTag(TagLib.TagTypes.Id3v2);
						var bpmFrame = TagLib.Id3v2.UserTextInformationFrame.Get(id3v2Tag, "BPM", false);

						if (bpmFrame != null && float.TryParse(bpmFrame.Text.FirstOrDefault(), out float parsedBpm))
						{
							bpm = parsedBpm;
						}
					}
				}
			}
			catch (Exception ex)
			{
				Debug.WriteLine($"Fehler beim Lesen der BPM: {ex.Message}");
			}
			this.Bpm = bpm > 0 ? bpm / 100.0f : 0.0f;
			return this.Bpm;
		}

		public async Task UpdateBpm(float newValue = 0.0f, bool writeTag = false)
		{
			this.Bpm = newValue;

			if (writeTag)
			{
				await this.AddBpmTag(newValue, this.FilePath);
			}
		}

		public async Task<byte[]> GetBytesAsync()
		{
			int maxWorkers = Environment.ProcessorCount;

			if (this.Data == null || this.Data.Length == 0)
			{
				return [];
			}

			int bytesPerSample = this.BitDepth / 8;
			byte[] result = new byte[this.Data.Length * bytesPerSample];

			await Task.Run(() =>
			{
				var options = new ParallelOptions
				{
					MaxDegreeOfParallelism = maxWorkers
				};

				Parallel.For(0, this.Data.Length, options, i =>
				{
					float sample = this.Data[i];

					switch (this.BitDepth)
					{
						case 8:
							result[i] = (byte) (sample * 127f);
							break;

						case 16:
							short sample16 = (short) (sample * short.MaxValue);
							Span<byte> target16 = result.AsSpan(i * 2, 2);
							BitConverter.TryWriteBytes(target16, sample16);
							break;

						case 24:
							int sample24 = (int) (sample * 8_388_607f); // 2^23 - 1
							Span<byte> target24 = result.AsSpan(i * 3, 3);
							target24[0] = (byte) sample24;
							target24[1] = (byte) (sample24 >> 8);
							target24[2] = (byte) (sample24 >> 16);
							break;

						case 32:
							Span<byte> target32 = result.AsSpan(i * 4, 4);
							BitConverter.TryWriteBytes(target32, sample);
							break;
					}
				});
			});

			return result;
		}

		public byte[] GetBytes()
		{
			int maxWorkers = Environment.ProcessorCount;

			int bytesPerSample = this.BitDepth / 8;
			byte[] bytes = new byte[this.Data.Length * bytesPerSample];

			// Parallel options
			var parallelOptions = new ParallelOptions
			{
				MaxDegreeOfParallelism = maxWorkers
			};

			Parallel.For(0, this.Data.Length, parallelOptions, i =>
			{
				switch (this.BitDepth)
				{
					case 8:
						bytes[i] = (byte) (this.Data[i] * 127);
						break;
					case 16:
						short sample16 = (short) (this.Data[i] * short.MaxValue);
						Buffer.BlockCopy(BitConverter.GetBytes(sample16), 0, bytes, i * bytesPerSample, bytesPerSample);
						break;
					case 24:
						int sample24 = (int) (this.Data[i] * 8388607);
						Buffer.BlockCopy(BitConverter.GetBytes(sample24), 0, bytes, i * bytesPerSample, 3);
						break;
					case 32:
						Buffer.BlockCopy(BitConverter.GetBytes(this.Data[i]), 0, bytes, i * bytesPerSample, bytesPerSample);
						break;
				}
			});

			return bytes;
		}

		public async Task<IEnumerable<float[]>> GetChunks(int size = 2048, float overlap = 0.5f, bool keepData = false)
		{
			int maxWorkers = Environment.ProcessorCount;

			// Input Validation (sync part for fast fail)
			if (this.Data == null || this.Data.Length == 0)
			{
				return [];
			}

			if (size <= 0 || overlap < 0 || overlap >= 1)
			{
				return [];
			}

			// Calculate chunk metrics (sync)
			this.ChunkSize = size;
			this.OverlapSize = (int) (size * overlap);
			int step = size - this.OverlapSize;
			int numChunks = (this.Data.Length - size) / step + 1;

			// Prepare result array
			float[][] chunks = new float[numChunks][];

			await Task.Run(() =>
			{
				// Parallel processing with optimal worker count
				Parallel.For(0, numChunks, new ParallelOptions
				{
					MaxDegreeOfParallelism = maxWorkers
				}, i =>
				{
					int sourceOffset = i * step;
					float[] chunk = new float[size];
					Buffer.BlockCopy( // Faster than Array.Copy for float[]
						src: this.Data,
						srcOffset: sourceOffset * sizeof(float),
						dst: chunk,
						dstOffset: 0,
						count: size * sizeof(float));
					chunks[i] = chunk;
				});
			});

			// Cleanup if requested
			if (!keepData)
			{
				this.Data = [];
			}

			return chunks;
		}

		public async Task<IEnumerable<byte[]>> GetChunksBytes(int size = 2048, float overlap = 0.5f, bool keepData = false)
		{
			// Same as GetChunks but returns byte arrays as chunks

			int maxWorkers = Environment.ProcessorCount;

			// Validierung
			if (this.Data == null || this.Data.Length == 0)
			{
				return [];
			}
			if (size <= 0 || overlap < 0 || overlap >= 1)
			{
				return [];
			}
			if (this.BitDepth != 8 && this.BitDepth != 16 && this.BitDepth != 24 && this.BitDepth != 32)
			{
				// Fallback: interpretiere als 32-bit float
				this.BitDepth = 32;
			}

			this.ChunkSize = size;
			this.OverlapSize = (int) (size * overlap);
			int step = size - this.OverlapSize;
			if (step <= 0 || this.Data.Length < size)
			{
				return [];
			}

			int numChunks = (this.Data.Length - size) / step + 1;
			int bytesPerSample = this.BitDepth / 8;

			byte[][] chunks = new byte[numChunks][];

			await Task.Run(() =>
			{
				var options = new ParallelOptions
				{
					MaxDegreeOfParallelism = maxWorkers
				};

				Parallel.For(0, numChunks, options, chunkIndex =>
				{
					int sourceOffset = chunkIndex * step;
					byte[] chunkBytes = new byte[size * bytesPerSample];

					switch (this.BitDepth)
					{
						case 8:
							for (int j = 0; j < size; j++)
							{
								float sample = this.Data[sourceOffset + j];
								chunkBytes[j] = (byte) (Math.Clamp(sample, -1f, 1f) * 127f);
							}
							break;

						case 16:
							for (int j = 0; j < size; j++)
							{
								float sample = this.Data[sourceOffset + j];
								short s16 = (short) (Math.Clamp(sample, -1f, 1f) * short.MaxValue);
								int o = j * 2;
								chunkBytes[o] = (byte) s16;
								chunkBytes[o + 1] = (byte) (s16 >> 8);
							}
							break;

						case 24:
							for (int j = 0; j < size; j++)
							{
								float sample = this.Data[sourceOffset + j];
								int s24 = (int) (Math.Clamp(sample, -1f, 1f) * 8_388_607f); // 2^23 - 1
								int o = j * 3;
								chunkBytes[o] = (byte) s24;
								chunkBytes[o + 1] = (byte) (s24 >> 8);
								chunkBytes[o + 2] = (byte) (s24 >> 16);
							}
							break;

						case 32:
							for (int j = 0; j < size; j++)
							{
								float sample = this.Data[sourceOffset + j];
								int o = j * 4;
								BitConverter.TryWriteBytes(new Span<byte>(chunkBytes, o, 4), sample);
							}
							break;
					}

					chunks[chunkIndex] = chunkBytes;
				});
			});

			if (!keepData)
			{
				this.Data = [];
			}

			return chunks;
		}

		public async Task AggregateStretchedChunks(IEnumerable<float[]> chunks, bool keepPointer = false)
		{
			int maxWorkers = Environment.ProcessorCount;

			if (chunks == null || !chunks.Any())
			{
				return;
			}

			// Pointer
			this.Pointer = keepPointer ? this.Pointer : IntPtr.Zero;

			// Pre-calculate all values that don't change
			double stretchFactor = this.StretchFactor;
			int chunkSize = this.ChunkSize;
			int overlapSize = this.OverlapSize;
			int originalHopSize = chunkSize - overlapSize;
			int stretchedHopSize = (int)Math.Round(originalHopSize * stretchFactor);
			int outputLength = (chunks.Count() - 1) * stretchedHopSize + chunkSize;

			// Create window function (cosine window)
			double[] window = await Task.Run(() =>
				Enumerable.Range(0, chunkSize)
						  .Select(i => 0.5 * (1.0 - Math.Cos(2.0 * Math.PI * i / (chunkSize - 1))))
						  .ToArray()
			).ConfigureAwait(false);

			// Logging: Chunks-Infos
			var chunkList = chunks.ToList();
			Debug.WriteLine($"[AggregateStretchedChunks] Chunks: {chunkList.Count}, ChunkSize: {chunkSize}, OutputLength: {outputLength}");
			for (int c = 0; c < Math.Min(3, chunkList.Count); c++)
			{
				var arr = chunkList[c];
				Debug.WriteLine($"Chunk[{c}] Length: {arr.Length}, Min: {arr.Min()}, Max: {arr.Max()}, First10: {string.Join(", ", arr.Take(10))}");
			}

			// Initialize accumulators in parallel
			double[] outputAccumulator = new double[outputLength];
			double[] weightSum = new double[outputLength];

			await Task.Run(() =>
			{
				var parallelOptions = new ParallelOptions
				{
					MaxDegreeOfParallelism = maxWorkers
				};

				// Phase 1: Process chunks in parallel
				Parallel.For(0, chunkList.Count, parallelOptions, chunkIndex =>
				{
					var chunk = chunkList[chunkIndex];
					int offset = chunkIndex * stretchedHopSize;

					for (int j = 0; j < Math.Min(chunkSize, chunk.Length); j++)
					{
						int idx = offset + j;
						if (idx >= outputLength)
						{
							break;
						}

						double windowedSample = chunk[j] * window[j];

						// Using Interlocked for thread-safe accumulation
						Interlocked.Exchange(ref outputAccumulator[idx], outputAccumulator[idx] + windowedSample);
						Interlocked.Exchange(ref weightSum[idx], weightSum[idx] + window[j]);
					}
				});

				// Phase 2: Normalize results
				float[] finalOutput = new float[outputLength];
				Parallel.For(0, outputLength, parallelOptions, i =>
				{
					finalOutput[i] = weightSum[i] > 1e-6
						? (float)(outputAccumulator[i] / weightSum[i])
						: 0.0f;
				});

				// Final assignment (thread-safe)
				this.Data = finalOutput;
			}).ConfigureAwait(true);

			// Logging: Output-Infos
			Debug.WriteLine($"[AggregateStretchedChunks] Output Min: {this.Data.Min()}, Max: {this.Data.Max()}, First10: {string.Join(", ", this.Data.Take(10))}");
		}


		// Data accessors
		public void VoidData()
		{
			this.Data = [];
			GC.Collect();
		}

		public long SetData(float[] data)
		{
			this.Data = data;
			return this.Data.LongLength;
		}


		// Playback
		public void SetVolume(float volume, bool smoothTransition = false)
		{
			volume = Math.Clamp(volume, 0.0f, 1.0f);

			lock (this.playerLock)
			{
				if (this.player == null)
				{
					return;
				}

				if (smoothTransition && this.player.PlaybackState == PlaybackState.Playing)
				{
					// Sanfte Übergänge (optional)
					Task.Run(async () =>
					{
						float startVolume = this.currentVolume;
						float duration = 0.2f; // 200ms Übergang
						int steps = 10;

						for (int i = 0; i <= steps; i++)
						{
							float t = i / (float) steps;
							float v = startVolume + (volume - startVolume) * t;

							lock (this.playerLock)
							{
								this.player.Volume = v;
							}

							await Task.Delay((int) (duration * 1000 / steps));
						}
						this.currentVolume = volume;
					});
				}
				else
				{
					// Direkte Änderung
					this.player.Volume = volume;
					this.currentVolume = volume;
				}
			}
		}

		public async Task Play(CancellationToken cancellationToken, Action? onPlaybackStopped = null, float? initialVolume = null)
		{
			this.Playing = true;
			this.Paused = false;
			initialVolume ??= this.Volume / 100f;

			bool paused = false;
			if (this.player != null && this.player.PlaybackState != PlaybackState.Paused)
			{
				this.player?.Stop();
				this.player?.Dispose();
				this.waveStream?.Dispose();
				this.waveStream = null;
				this.Paused = false;
			}
			else
			{
				paused = true;
				this.Paused = false;
			}

			if (this.Data == null || this.Data.Length == 0)
			{
				this.Playing = false;
				return;
			}

			try
			{
				this.player = new WaveOutEvent { Volume = initialVolume ?? 1.0f, DesiredLatency = 100 };

				byte[] bytes = await this.GetBytesAsync();
				var waveFormat = WaveFormat.CreateIeeeFloatWaveFormat(this.SampleRate, this.Channels);
				var memoryStream = new MemoryStream(bytes);
				var audioStream = new RawSourceWaveStream(memoryStream, waveFormat);

				this.player.PlaybackStopped += (sender, args) =>
				{
					try
					{
						onPlaybackStopped?.Invoke();
					}
					finally
					{
						this.Playing = false;
						this.Paused = false;
						this.waveStream = null;
						audioStream.Dispose();
						memoryStream.Dispose();
						this.player?.Dispose();

						// NEU: nach natürlichem Ende ggf. entfernen
						if (this.RemoveAfterPlayback)
						{
							this.OnRemoveRequested();
						}
					}
				};

				using (cancellationToken.Register(() => this.player?.Stop()))
				{
					this.waveStream = audioStream;
					this.player.Init(audioStream);

					_ = Task.Run(() =>
					{
						try
						{
							if (paused)
							{
								// ggf. Position wiederherstellen
							}
							this.player.Play();

							while (this.player.PlaybackState == PlaybackState.Playing)
							{
								cancellationToken.ThrowIfCancellationRequested();
								Thread.Sleep(50);
							}
						}
						catch (OperationCanceledException)
						{
						}
						catch (Exception ex)
						{
							Debug.WriteLine($"Playback error: {ex.Message}");
						}
					}, cancellationToken);
				}
			}
			catch (OperationCanceledException)
			{
				Debug.WriteLine("Playback preparation was canceled");
				this.Playing = false;
			}
			catch (Exception ex)
			{
				Debug.WriteLine($"Playback initialization failed: {ex.Message}");
				this.player?.Dispose();
				this.Playing = false;
				throw;
			}
		}

		public async Task Pause()
		{
			if (this.player == null)
			{
				return;
			}

			if (this.player.PlaybackState == PlaybackState.Playing)
			{
				this.Playing = false;
				this.Paused = true;
				await Task.Run(this.player.Pause);
			}
			else if (this.player.PlaybackState == PlaybackState.Paused)
			{
				this.Playing = true;
				this.Paused = false;
				await Task.Run(this.player.Play);
			}

		}

		public void Stop()
		{
			this.Playing = false;
			this.Paused = false;
			this.waveStream?.Dispose();
			this.waveStream = null;
			this.player.Stop();

			// NEU: bei Stop ebenfalls entfernen, falls gewünscht
			if (this.RemoveAfterPlayback)
			{
				this.OnRemoveRequested();
			}
		}

		public async Task Normalize(float maxAmplitude = 1.0f)
		{
			this.isProcessing = true;
			int maxWorkers = Environment.ProcessorCount;

			if (this.Data == null || this.Data.Length == 0)
			{
				return;
			}

			var parallelOptions = new ParallelOptions
			{
				MaxDegreeOfParallelism = maxWorkers
			};

			this.IsProcessing = true;
			Stopwatch sw = Stopwatch.StartNew();

			// Phase 1: Find global maximum (parallel + async)
			float globalMax = await Task.Run(() =>
			{
				float max = 0f;
				Parallel.For(0, this.Data.Length, parallelOptions,
					() => 0f,
					(i, _, localMax) => Math.Max(Math.Abs(this.Data[i]), localMax),
					localMax => { lock (this) { max = Math.Max(max, localMax); } }
				);
				return max;
			}).ConfigureAwait(false);

			if (globalMax == 0f)
			{
				return;
			}

			// Phase 2: Apply scaling (parallel + async)
			float scale = maxAmplitude / globalMax;
			await Task.Run(() =>
			{
				Parallel.For(0, this.Data.Length, parallelOptions, i =>
				{
					this.Data[i] *= scale;
				});
			}).ConfigureAwait(false);

			this.IsProcessing = false;
			sw.Stop();
			this["normalize"] = sw.Elapsed.TotalMilliseconds;
		}

		public async Task Level(float duration = 1.0f, float average = 1.0f)
		{
			int maxWorkers = Environment.ProcessorCount;

			this.isProcessing = true;

			// Validate input
			duration = Math.Clamp(duration, 0.1f, 600.0f);

			if (this.Data == null || this.Data.Length == 0)
			{
				return;
			}

			this.IsProcessing = true;

			// Calculate number of samples to process
			int samplesPerSecond = (int) (this.SampleRate * this.Channels);
			int totalSamples = (int) (duration * samplesPerSecond);
			if (totalSamples <= 0 || totalSamples > this.Data.Length)
			{
				return;
			}

			// Calculate the average level over the specified duration
			float averageLevel = average * await Task.Run(() =>
			{
				float sum = 0f;
				int count = 0;
				Parallel.For(0, totalSamples, new ParallelOptions { MaxDegreeOfParallelism = maxWorkers }, i =>
				{
					if (i < this.Data.Length)
					{
						sum += Math.Abs(this.Data[i]);
						count++;
					}
				});
				return count > 0 ? sum / count : 0f;
			}).ConfigureAwait(false);
			if (averageLevel <= 0f)
			{
				return;
			}

			// Within the specified duration, normalize the audio data to the average level
			await Task.Run(() =>
			{
				Parallel.For(0, totalSamples, new ParallelOptions { MaxDegreeOfParallelism = maxWorkers }, i =>
				{
					if (i < this.Data.Length)
					{
						this.Data[i] = (this.Data[i] / Math.Abs(this.Data[i])) * averageLevel;
					}
				});
			}).ConfigureAwait(false);

			this.IsProcessing = false;

			// Optionally, normalize the entire audio data to ensure no clipping occurs
			await this.Normalize(maxAmplitude: 1.0f).ConfigureAwait(false);
		}

		// Export
		public async Task<string?> Export(string outPath = "", string? outFile = null)
		{
			if (File.Exists(outFile))
			{
				// If outPath is a file, use its directory
				outPath = Path.GetDirectoryName(outFile) ?? string.Empty;
			}

			if (string.IsNullOrEmpty(outPath))
			{
				outPath = Path.GetTempPath();
			}

			string baseFileName = $"{this.Name.Replace("▶ ", "").Replace("|| ", "")}{(this.Bpm > 10 ? (" [" + this.Bpm.ToString("F1", CultureInfo.InvariantCulture) + "]") : "")}".Trim();

			// Validate and prepare output directory
			outPath = (await this.PrepareOutputPath(outPath, baseFileName)) ?? Path.GetTempPath();
			if (string.IsNullOrEmpty(outPath))
			{
				return null;
			}

			Stopwatch sw = Stopwatch.StartNew();

			try
			{
				// Process audio data in parallel
				byte[] bytes = await this.GetBytesAsync();

				var waveFormat = WaveFormat.CreateIeeeFloatWaveFormat(
					this.SampleRate,
					this.Channels);

				await using (var memoryStream = new MemoryStream(bytes))
				await using (var audioStream = new RawSourceWaveStream(memoryStream, waveFormat))
				await using (var fileStream = new FileStream(
					outPath,
					FileMode.Create,
					FileAccess.Write,
					FileShare.None,
					bufferSize: 4096,
					useAsync: true))
				{
					await Task.Run(() =>
					{
						WaveFileWriter.WriteWavFileToStream(fileStream, audioStream);
					});
				}

				// Add BPM metadata if needed
				if (this.Bpm > 0.0f)
				{
					await this.AddBpmTag(this.Bpm, outPath)
						.ConfigureAwait(false);
				}

				this.ExportedFile = outPath;

                sw.Stop();
                this["wavExport"] = sw.Elapsed.TotalMilliseconds;

                return outPath;
			}
			catch (Exception ex)
			{
				Debug.WriteLine($"Audio export failed: {ex.Message}");
				return null;
			}
		}

		public string GetMetricsString()
		{
			string metrics = string.Empty;
			foreach (var kvp in this.Metrics)
			{
				metrics += $"{kvp.Key}: {kvp.Value:F2} ms" + Environment.NewLine;
			}

			return metrics.TrimEnd();
		}

		private async Task<string?> PrepareOutputPath(string outPath, string baseFileName)
		{
			// Check directory existence asynchronously
			if (!string.IsNullOrEmpty(outPath))
			{
				var dirExists = await Task.Run(() => Directory.Exists(outPath))
										.ConfigureAwait(false);
				if (!dirExists)
				{
					outPath = Path.GetDirectoryName(outPath) ?? string.Empty;
				}
			}

			// Fallback to temp directory if needed
			if (string.IsNullOrEmpty(outPath) ||
				!await Task.Run(() => Directory.Exists(outPath))
						  .ConfigureAwait(false))
			{
				outPath = Path.Combine(
					Path.GetTempPath(),
					$"{this.Name}_[{this.Bpm:F2}].wav");
			}

			// Build final file path
			if (Path.HasExtension(outPath))
			{
				return outPath;
			}

			return Path.Combine(outPath, $"{baseFileName}.wav");
		}

		public async Task AddBpmTag(float bpm = 0.0f, string filePath = "")
		{
			if (string.IsNullOrEmpty(filePath))
			{
				filePath = this.FilePath;
			}

			if (bpm <= 0)
			{
				bpm = this.Bpm;
			}

			if (!File.Exists(filePath))
			{
				Debug.WriteLine($"File not found for BPM tag writing: {filePath}");
				return;
			}

			try
			{
				await Task.Run(() =>
				{
					// Create or open the file using TagLib
					using var file = TagLib.File.Create(filePath);
					// Set BPM tag
					file.Tag.BeatsPerMinute = (uint) (bpm < 1000 ? bpm * 100 : bpm);

					// Add or update TBPM frame
					var id3v2Tag = (TagLib.Id3v2.Tag) file.GetTag(TagLib.TagTypes.Id3v2);
					var bpmFrame = TagLib.Id3v2.TextInformationFrame.Get(id3v2Tag, "TBPM", true);
					if (bpmFrame == null)
					{
						bpmFrame = new TagLib.Id3v2.TextInformationFrame("TBPM");
						id3v2Tag.AddFrame(bpmFrame);
					}
					bpmFrame.Text = [bpm.ToString(CultureInfo.InvariantCulture)];

					// Save changes
					file.Save();
				}).ConfigureAwait(false);
			}
			catch (Exception ex)
			{
				Debug.WriteLine($"BPM tag writing failed: {ex.Message}");
			}
		}

		public override string ToString()
		{
			return this.Name;
		}

		public void JumpByBeats(int beats)
		{
			if (beats == 0 || this.player == null || this.waveStream == null ||
				this.player.PlaybackState != PlaybackState.Playing)
			{
				return;
			}

			try
			{
				// Try to get the lock with timeout to avoid deadlocks
				if (Monitor.TryEnter(this.playerLock, TimeSpan.FromMilliseconds(100)))
				{
					try
					{
						// Calculate jump
						float bpm = Math.Max(this.Bpm, 60.0f);
						int bytesPerSecond = this.waveStream.WaveFormat.AverageBytesPerSecond;
						int bytesPerBeat = (int) (bytesPerSecond * 60.0f / bpm);
						int jumpBytes = bytesPerBeat * beats;

						// Calculate and set new position
						long newPosition = this.waveStream.Position + jumpBytes;
						newPosition = Math.Max(0, Math.Min(newPosition, this.waveStream.Length));
						this.waveStream.Position = newPosition;
						this.loopStart = this.waveStream.Position;

						// Restart looping if it was active
						if (!this.loopCts.IsCancellationRequested)
						{
							_ = this.StartLoop(this.currentLoopValue); // Fire-and-forget restart
						}
					}
					finally
					{
						Monitor.Exit(this.playerLock);
					}
				}
				else
				{
					Debug.WriteLine("JumpByBeats: Could not acquire lock, operation skipped");
				}
			}
			catch (Exception ex)
			{
				Debug.WriteLine($"JumpByBeats error: {ex.Message}");
			}
		}

		public async Task StartLoop(float loopValue)
		{
			this.StopLoop();

			if (loopValue == 0 || this.isLooping || this.player?.PlaybackState != PlaybackState.Playing)
			{
				return;
			}

			this.currentLoopValue = loopValue;
			this.isLooping = true;
			this.loopCts = new CancellationTokenSource();

			try
			{
				await Task.Run(async () =>
				{
					// Vorab-Berechnungen
					int bytesPerSecond = 0;
					int loopBytes = 0;
					bool isForwardLoop = loopValue > 0;

					lock (this.playerLock)
					{
						if (this.waveStream != null)
						{
							bytesPerSecond = this.waveStream.WaveFormat.AverageBytesPerSecond;
							float bpm = Math.Max(this.Bpm, 60.0f);
							loopBytes = (int) ((bytesPerSecond * 60.0f / bpm) * Math.Abs(loopValue));
						}
					}

					while (!this.loopCts.IsCancellationRequested &&
						   this.player?.PlaybackState == PlaybackState.Playing)
					{
						long loopEndPos;

						// 1. Loop-Bereich festlegen
						lock (this.playerLock)
						{
							if (this.waveStream == null)
							{
								return;
							}

							this.loopStart = this.waveStream.Position;
							loopEndPos = isForwardLoop
								? Math.Min(this.loopStart + loopBytes, this.waveStream.Length)
								: Math.Max(0, this.loopStart - loopBytes);
						}

						// 2. Normal abspielen lassen bis zum Endpunkt
						bool reachedEnd = false;
						while (!reachedEnd &&
							   !this.loopCts.IsCancellationRequested &&
							   this.player?.PlaybackState == PlaybackState.Playing)
						{
							await Task.Delay(50); // Check-Intervall

							lock (this.playerLock)
							{
								if (this.waveStream == null)
								{
									return;
								}

								long currentPos = this.waveStream.Position;
								reachedEnd = isForwardLoop
									? currentPos >= loopEndPos
									: currentPos <= loopEndPos;
							}
						}

						// 3. Nur zurückspringen wenn natürlich beendet
						if (reachedEnd && !this.loopCts.IsCancellationRequested)
						{
							lock (this.playerLock)
							{
								if (this.waveStream != null && isForwardLoop)
								{
									this.waveStream.Position = this.loopStart;
								}
							}
						}
						else
						{
							break; // Bei Abbruch raus
						}
					}
				}, this.loopCts.Token);
			}
			catch (OperationCanceledException)
			{
				/* Expected */
			}
			finally
			{
				this.isLooping = false;
			}
		}

		public void StopLoop()
		{
			this.loopCts?.Cancel();
			this.isLooping = false;
			this.currentLoopValue = 0;
		}

		public double GetTime(long position = -1)
		{
			if (position < 0)
			{
				position = this.position;
			}

			if (this.SampleRate <= 0 || this.Channels <= 0)
			{
				return 0.0;
			}

			// Berechne die Zeit in Sekunden
			double timeInSeconds = (double) position / (this.SampleRate * this.Channels);
			return timeInSeconds;
		}

		public async Task<float[]> ConvertToMono(bool set = false)
		{
			int maxWorkers = Environment.ProcessorCount;

			if (this.Data == null || this.Data.Length == 0 || this.Channels <= 0)
			{
				return [];
			}

			// Calculate the number of samples in mono
			int monoSampleCount = this.Data.Length / this.Channels;
			float[] monoData = new float[monoSampleCount];

			await Task.Run(() =>
			{
				Parallel.For(0, monoSampleCount, new ParallelOptions
				{
					MaxDegreeOfParallelism = maxWorkers
				}, i =>
				{
					float sum = 0.0f;
					for (int channel = 0; channel < this.Channels; channel++)
					{
						sum += this.Data[i * this.Channels + channel];
					}
					monoData[i] = sum / this.Channels;
				});
			});

			if (set)
			{
				this.Data = monoData;
				this.Channels = 1;
			}

			return monoData;
		}

		public async Task<float[][]> SplitData(int chunkSize = 65536)
		{
			int maxWorkers = Environment.ProcessorCount;

			if (this.Data == null || this.Data.Length == 0)
			{
				return [];
			}

			// Validate chunk size
			chunkSize = Math.Max(1, chunkSize);

			int totalChunks = (int) Math.Ceiling((double) this.Data.Length / chunkSize);
			float[][] chunks = new float[totalChunks][];

			await Task.Run(() =>
			{
				Parallel.For(0, totalChunks, new ParallelOptions
				{
					MaxDegreeOfParallelism = maxWorkers
				}, i =>
				{
					int start = i * chunkSize;
					int length = Math.Min(chunkSize, this.Data.Length - start);
					chunks[i] = new float[chunkSize];
					Array.Copy(this.Data, start, chunks[i], 0, length);
				});
			});

			return chunks;
		}



		public async Task<float[]> GetCurrentWindow(int windowSize = 65536, int lookingRange = 2, bool mono = false, bool lookBackwards = false)
		{
			if (this.Data == null || this.Data.Length == 0 || this.SampleRate <= 0 || this.Channels <= 0)
			{
				return [];
			}

			// Parameter normalisieren
			windowSize = Math.Max(1, windowSize);
			lookingRange = Math.Max(1, lookingRange);
			// Für spätere FFTs oft sinnvoll: auf Potenz von 2 schnappen
			windowSize = (int) Math.Pow(2, Math.Ceiling(Math.Log(windowSize, 2)));

			// Position in Frames (position ist bereits frame-basiert)
			long posFrames = this.position;

			// Fensterbreite in Frames: um pos herum jeweils die Hälfte
			int halfWindowFrames = (windowSize * lookingRange) / 2;
			int fullWindowFrames = halfWindowFrames * 2;
			if (fullWindowFrames <= 0)
			{
				return [];
			}

			if (mono)
			{
				// Mono-Daten (Frames == Samples)
				float[] data = await this.ConvertToMono(false);
				if (data.Length == 0)
				{
					return [];
				}

				long startFrame = posFrames - (lookBackwards ? halfWindowFrames : 0);
				long endFrameExclusive = startFrame + fullWindowFrames;

				// Out-of-bounds vermeiden, verschieben
				while (endFrameExclusive > data.Length)
				{
					startFrame -= windowSize;
					endFrameExclusive -= windowSize;
				}

				while (startFrame < 0)
				{
					startFrame += windowSize;
					endFrameExclusive += windowSize;
				}

				if (endFrameExclusive > data.LongLength)
				{
					return [];
				}

				float[] current = new float[fullWindowFrames];
				await Task.Run(() => Array.Copy(data, (int) startFrame, current, 0, fullWindowFrames));
				return current;
			}
			else
			{
				// Interleaved Mehrkanal-Daten (Floats)
				float[] data = this.Data;

				long startFloatIndex = (posFrames - (lookBackwards ? halfWindowFrames : 0)) * this.Channels;
				long endFloatIndexExclusive = startFloatIndex + ((long) fullWindowFrames * this.Channels);

				while (endFloatIndexExclusive > data.Length)
				{
					startFloatIndex -= windowSize * this.Channels;
					endFloatIndexExclusive -= windowSize * this.Channels;
				}

				while (startFloatIndex < 0)
				{
					startFloatIndex += windowSize * this.Channels;
					endFloatIndexExclusive += windowSize * this.Channels;
				}

				if (endFloatIndexExclusive > data.LongLength || startFloatIndex < 0)
				{
					Debug.WriteLine("GetCurrentWindow: Out of bounds access prevented.");
					// Out-of-bounds, verschieben
					return [];
				}

				int lengthFloats = fullWindowFrames * this.Channels;
				float[] current = new float[lengthFloats];
				await Task.Run(() => Array.Copy(data, (int) startFloatIndex, current, 0, lengthFloats));
				return current;
			}
		}

		public async Task<string?> GetWaveformBase64Async(int width = 800, int height = 200, int samplesPerPixel = 0, float offsetProgress = 0.0f)
		{
			if (this.Data == null || this.Data.Length == 0 || this.SampleRate <= 0 || this.Channels <= 0)
			{
				return null;
			}

			long offsetValue = this.Length > 0 ? (long) (this.Length * Math.Clamp(offsetProgress, 0.0f, 1.0f)) : 0;

			// Calculate bitsPerPixel required for full waveform
			long totalSamples = this.Data.LongLength;
			if (samplesPerPixel <= 0)
			{
				samplesPerPixel = (int) Math.Max(1, totalSamples / width);
			}
			samplesPerPixel = Math.Max(1, samplesPerPixel);

			var waveformImg = await this.GetWaveformImageSimpleBase64Async(this.Data, width, height, samplesPerPixel, 1.0f, offsetValue, false, System.Drawing.Color.BlueViolet, System.Drawing.Color.White, true);

			return waveformImg;
		}

		public async Task<string?> GetWaveformImageSimpleBase64Async(float[]? data, int width = 720, int height = 480,
	int samplesPerPixel = 128, float amplifier = 1.0f, long offset = 0, bool drawChannelsSeparately = false,
	System.Drawing.Color? graphColor = null, System.Drawing.Color? backgroundColor = null, bool smoothEdges = true)
		{
			int maxWorkers = Environment.ProcessorCount;

			// Normalize dimensions
			width = Math.Max(1, width);
			height = Math.Max(1, height);
			offset = offset == 0 ? this.position * this.Channels : Math.Max(0, offset);

			// Colors
			graphColor ??= System.Drawing.Color.BlueViolet;
			backgroundColor ??= System.Drawing.Color.White;

			// Use ImageSharp image (RGBA32)
			using var image = new Image<Rgba32>(width, height, new Rgba32(backgroundColor.Value.R, backgroundColor.Value.G, backgroundColor.Value.B, backgroundColor.Value.A));

			data ??= this.Data;
			if (data == null || data.LongLength <= 0)
			{
				return null;
			}

			if (data.Length <= offset)
			{
				offset = 0;
			}

			if (maxWorkers <= 0)
			{
				maxWorkers = Environment.ProcessorCount;
			}
			maxWorkers = Math.Min(maxWorkers, 16);

			long totalSamples = Math.Min(data.Length - offset, (long) width * samplesPerPixel);
			if (totalSamples <= 0)
			{
				return null;
			}

			// Copy relevant portion
			float[] samples = new float[totalSamples];
			Array.Copy(data, offset, samples, 0, totalSamples);

			// If requested draw channels separately and there are multiple channels:
			bool separate = drawChannelsSeparately && this.Channels > 1;

			await Task.Run(() =>
			{
				Rgba32 lineColor = new(graphColor.Value.R, graphColor.Value.G, graphColor.Value.B, graphColor.Value.A);
				int bytesPerWorker = Math.Max(1, width / maxWorkers);

				if (!separate)
				{
					// Original behaviour: treat samples as raw floats (possibly interleaved) and draw one waveform
					Parallel.For(0, maxWorkers, worker =>
					{
						int xStart = worker * bytesPerWorker;
						int xEnd = worker == maxWorkers - 1 ? width : xStart + bytesPerWorker;

						for (int x = xStart; x < xEnd; x++)
						{
							int sampleIndex = x * samplesPerPixel;
							if (sampleIndex >= samples.Length)
							{
								break;
							}

							float maxPeak = 0.0f;
							float minPeak = 0.0f;

							int startSample = sampleIndex;
							int endSample = Math.Min(startSample + samplesPerPixel, samples.Length);
							for (int s = startSample; s < endSample; s++)
							{
								float v = samples[s];
								if (v > maxPeak) maxPeak = v;
								if (v < minPeak) minPeak = v;
							}

							int yMax = (int) (((maxPeak * amplifier) + 1.0f) / 2.0f * height);
							int yMin = (int) (((minPeak * amplifier) + 1.0f) / 2.0f * height);
							yMax = Math.Clamp(yMax, 0, height - 1);
							yMin = Math.Clamp(yMin, 0, height - 1);
							if (yMin > yMax) (yMin, yMax) = (yMax, yMin);

							for (int y = yMin; y <= yMax; y++)
							{
								image.ProcessPixelRows(accessor =>
								{
									var row = accessor.GetRowSpan(y);
									row[x] = lineColor;
								});
							}
						}
					});
				}
				else
				{
					// Draw each channel in its own vertical band stacked top-to-bottom.
					int channels = this.Channels;
					int channelHeight = Math.Max(1, height / channels);

					// total frames available in copied samples (interleaved)
					int totalFrames = (int) (samples.Length / channels);
					if (totalFrames <= 0)
					{
						return;
					}

					// Interpret samplesPerPixel as floats; convert to frames per pixel for multi-channel drawing
					int framesPerPixel = Math.Max(1, samplesPerPixel / channels);

					Parallel.For(0, maxWorkers, worker =>
					{
						int xStart = worker * bytesPerWorker;
						int xEnd = worker == maxWorkers - 1 ? width : xStart + bytesPerWorker;

						for (int x = xStart; x < xEnd; x++)
						{
							int frameIndex = x * framesPerPixel;
							if (frameIndex >= totalFrames)
							{
								break;
							}

							// For each channel compute min/max within the frame window and draw into its band
							for (int ch = 0; ch < channels; ch++)
							{
								float maxPeak = float.MinValue;
								float minPeak = float.MaxValue;

								int startFrame = frameIndex;
								int endFrame = Math.Min(frameIndex + framesPerPixel, totalFrames);

								for (int f = startFrame; f < endFrame; f++)
								{
									int idx = f * channels + ch;
									if (idx >= samples.Length) break;
									float v = samples[idx];
									if (v > maxPeak) maxPeak = v;
									if (v < minPeak) minPeak = v;
								}

								// If no samples in window fallback to zero
								if (maxPeak == float.MinValue) maxPeak = 0f;
								if (minPeak == float.MaxValue) minPeak = 0f;

								int top = ch * channelHeight;
								int h = channelHeight;
								// Map peaks into channel band
								int yMax = top + (int) (((maxPeak * amplifier) + 1.0f) / 2.0f * (h - 1));
								int yMin = top + (int) (((minPeak * amplifier) + 1.0f) / 2.0f * (h - 1));
								yMax = Math.Clamp(yMax, top, top + h - 1);
								yMin = Math.Clamp(yMin, top, top + h - 1);
								if (yMin > yMax) (yMin, yMax) = (yMax, yMin);

								for (int y = yMin; y <= yMax; y++)
								{
									image.ProcessPixelRows(accessor =>
									{
										var row = accessor.GetRowSpan(y);
										row[x] = lineColor;
									});
								}
							}
						}
					});
				}

				// Optionally smooth edges (simple vertical blur pass)
				if (smoothEdges)
				{
					image.Mutate(ctx => ctx.BoxBlur(1));
				}
			});

			// Encode to PNG and return Base64
			using var ms = new MemoryStream();
			await image.SaveAsPngAsync(ms);
			var b64 = Convert.ToBase64String(ms.ToArray());
			return b64;
		}

		public string? ContentHash { get; internal set; } // SHA256 of original file
		public string ErrorMessage { get; set; } = string.Empty;
		public double? LastExecutionTime { get; set; } = null;

		internal static string? ComputeFileHash(string filePath)
		{
			try
			{
				if (string.IsNullOrWhiteSpace(filePath) || !File.Exists(filePath))
				{
					return null;
				}

				using var sha = SHA256.Create();
				using var fs = File.OpenRead(filePath);
				var hash = sha.ComputeHash(fs);
				return BitConverter.ToString(hash).Replace("-", string.Empty);
			}
			catch
			{
				return null;
			}
		}
	}
}
