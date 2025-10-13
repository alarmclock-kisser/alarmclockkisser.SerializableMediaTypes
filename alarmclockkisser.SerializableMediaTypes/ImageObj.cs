using SixLabors.ImageSharp;
using SixLabors.ImageSharp.Formats;
using SixLabors.ImageSharp.Formats.Bmp;
using SixLabors.ImageSharp.PixelFormats;
using SixLabors.ImageSharp.Processing;

namespace alarmclockkisser.SerializableMediaTypes
{
	public class ImageObj : IDisposable
	{
		public Guid Id { get; private set; }
		public DateTime CreatedAt { get; private set; } = DateTime.Now;

		public string FilePath { get; set; } = string.Empty;
		public string Name { get; set; } = string.Empty;

		public Image<Rgba32>? Img { get; set; } = null;
		public int Width { get; set; } = 0;
		public int Height { get; set; } = 0;
		public int Channels { get; set; } = 4;
		public int Bitdepth { get; set; } = 0;

		private long SizeInBytes => this.Width * this.Height * this.Channels * (this.Bitdepth / 8);
		public float SizeMb => this.SizeInBytes / (1024f * 1024f);
		public string DataType => "byte";
		public string DataStructure => "[]";
		public string Base64Image => this.AsBase64Image().Result;

		public long Pointer { get; set; } = IntPtr.Zero;
		public string PointerHex => this.Pointer == IntPtr.Zero ? "0" : this.Pointer.ToString("X");
		public string Meta { get; set; } = string.Empty;

		public bool OnHost => this.Pointer == IntPtr.Zero && this.Img != null;
		public bool OnDevice => this.Pointer != IntPtr.Zero && this.Img == null;

		public double ElapsedProcessingTime { get; set; } = 0.0;

		private Dictionary<string, double> metrics = [];
		public Dictionary<string, double> Metrics
		{
			get => this.metrics;
			set => this.metrics = value;
		}

		public Single ScalingFactor { get; set; }

		private readonly object lockObj = new();


		public ImageObj(string filePath)
		{
			this.Id = Guid.NewGuid();
			this.FilePath = filePath;
			this.Name = Path.GetFileNameWithoutExtension(filePath);

			try
			{
				this.Img = SixLabors.ImageSharp.Image.Load(filePath).CloneAs<Rgba32>();

				this.Img = this.Img?.CloneAs<Rgba32>();

				this.Width = this.Img?.Width ?? 0;
				this.Height = this.Img?.Height ?? 0;
				this.Channels = 4;
				this.Bitdepth = this.Img?.PixelType.BitsPerPixel ?? 0;
			}
			catch (Exception ex)
			{
				Console.WriteLine($"Error loading image {filePath}: {ex.Message}");
				this.Img = null;
			}
		}

		public ImageObj(int width, int height, string hexColor = "#00000000")
		{
			this.Id = Guid.NewGuid();
			this.Name = "image_" + this.Id.ToString();

			this.Width = width;
			this.Height = height;
			this.Channels = 4;
			this.Bitdepth = 32;

			this.FilePath = string.Empty;
			this.ScalingFactor = 1.0f;
			try
			{
				var color = SixLabors.ImageSharp.Color.ParseHex(hexColor);
				this.Img = new Image<Rgba32>(this.Width, this.Height, color);
			}
			catch (Exception ex)
			{
				Console.WriteLine($"Error creating image with size {width}x{height} and color {hexColor}: {ex.Message}");
				this.Img = null;
				this.Dispose();
			}
		}

		public ImageObj(Image<Rgba32> image, string name = "unknown", bool clone = false)
		{
			this.Id = Guid.NewGuid();
			this.Name = name;
			this.FilePath = string.Empty;
			if (image == null)
			{
				throw new ArgumentNullException(nameof(image), "Provided image is null.");
			}
			try
			{
				this.Img = clone ? image.CloneAs<Rgba32>() : image;
				this.Width = this.Img.Width;
				this.Height = this.Img.Height;
				this.Channels = 4;
				this.Bitdepth = this.Img.PixelType.BitsPerPixel;
			}
			catch (Exception ex)
			{
				Console.WriteLine($"Error creating ImageObj from provided image: {ex.Message}");
				this.Img = null;
			}
		}

		public ImageObj(IEnumerable<byte> rawPixelData, int width, int height, string name = "UnbenanntesBild")
		{
			this.Id = Guid.NewGuid();
			this.Name = name;
			this.FilePath = string.Empty;

			try
			{
				this.Img = SixLabors.ImageSharp.Image.LoadPixelData<Rgba32>(rawPixelData.ToArray(), width, height);

				this.Width = this.Img.Width;
				this.Height = this.Img.Height;
				this.Channels = 4;
				this.Bitdepth = this.Img.PixelType.BitsPerPixel;
			}
			catch (Exception ex)
			{
				Console.WriteLine($"Fehler beim Erstellen des Bildes aus rohen Daten: {ex.Message}");
				this.Img = null;
			}
		}

		public async Task<string> AsBase64Image(string format = "bmp")
		{
			if (this.Img == null)
			{
				return string.Empty;
			}

			try
			{
				using var imgClone = this.Img.CloneAs<Rgba32>();
				using var ms = new MemoryStream();
				IImageEncoder encoder = format.ToLower() switch
				{
					"png" => new SixLabors.ImageSharp.Formats.Png.PngEncoder(),
					"jpeg" or "jpg" => new SixLabors.ImageSharp.Formats.Jpeg.JpegEncoder(),
					"gif" => new SixLabors.ImageSharp.Formats.Gif.GifEncoder(),
					_ => new BmpEncoder()
				};

				await imgClone.SaveAsync(ms, encoder);
				return Convert.ToBase64String(ms.ToArray());
			}
			catch (Exception ex)
			{
				Console.WriteLine($"Base64 conversion error: {ex}");
				return string.Empty;
			}
		}

		public async Task<IEnumerable<byte>> GetBytes(bool keepImage = false)
		{
			if (this.Img == null)
			{
				return [];
			}

			Image<Rgba32> imgClone;

			lock (this.lockObj)
			{
				imgClone = this.Img.CloneAs<Rgba32>();
			}

			int bytesPerPixel = this.Img.PixelType.BitsPerPixel / 8;
			long totalBytes = this.Width * this.Height * bytesPerPixel;

			byte[] bytes = new byte[totalBytes];

			await Task.Run(() =>
			{
				imgClone.CopyPixelDataTo(bytes);
			});

			if (!keepImage)
			{
				this.Img.Dispose();
				this.Img = null;
			}

			return bytes.AsEnumerable();
		}

		public async Task<Image<Rgba32>?> SetImageAsync(IEnumerable<byte> bytes, bool keepPointer = false)
		{
			if (this.Img != null)
			{
				this.Img.Dispose();
				this.Img = null;
			}

			Image<Rgba32>? img = null;

			try
			{
				img = await Task.Run(() =>
				{
					return SixLabors.ImageSharp.Image.LoadPixelData<Rgba32>(bytes.ToArray(), this.Width, this.Height);
				});

				// Lock
				lock (this.lockObj)
				{
					this.Img = img;
				}
			}
			catch (Exception ex)
			{
				Console.WriteLine($"Error setting image from bytes: {ex.Message}");
				this.Img = null;
				return null;
			}
			finally
			{
				if (!keepPointer)
				{
					this.Pointer = IntPtr.Zero;
				}

				await Task.Yield();
			}

			return img;
		}

		public void SetImage(Image<Rgba32> image, bool clone = false)
		{
			this.Img?.Dispose();
			this.Img = clone ? image.CloneAs<Rgba32>() : image;
		}

		public void Dispose()
		{
			if (this.Img != null)
			{
				this.Img.Dispose();
				this.Img = null;
			}

			this.Pointer = IntPtr.Zero;

			GC.SuppressFinalize(this);
		}

		public async Task<string> Export(string filePath = "", string format = "bmp")
		{
			if (this.Img == null)
			{
				return string.Empty;
			}

			// Fallback to Bmp
			IImageEncoder encoder = format.ToLower() switch
			{
				"png" => new SixLabors.ImageSharp.Formats.Png.PngEncoder(),
				"jpeg" or "jpg" => new SixLabors.ImageSharp.Formats.Jpeg.JpegEncoder(),
				"gif" => new SixLabors.ImageSharp.Formats.Gif.GifEncoder(),
				// Default to BMP if no valid format is provided + set format to bmp
				_ => new BmpEncoder()
			};

			// Determine file extension based on format
			string extension = format.ToLower() switch
			{
				"png" => "png",
				"jpeg" or "jpg" => "jpg",
				"gif" => "gif",
				_ => "bmp"
			};

			if (string.IsNullOrEmpty(filePath))
			{
				filePath = Path.Combine(Path.GetTempPath(), $"{this.Name}_{DateTime.Now:yyyyMMdd_HHmmss}.{extension}");
			}

			try
			{
				// Clone img in a thread-safe manner
				Image<Rgba32> clone = this.Img.CloneAs<Rgba32>();

				// Use the clone in an async context
				using (clone)
				{
					// Create the directory if it doesn't exist
					var directory = Path.GetDirectoryName(filePath);
					if (!string.IsNullOrEmpty(directory) && !Directory.Exists(directory))
					{
						Directory.CreateDirectory(directory);
					}

					// Save asynchronously with proper disposal
					await using var fileStream = new FileStream(filePath, FileMode.Create, FileAccess.Write, FileShare.None, bufferSize: 4096, useAsync: true);
					await clone.SaveAsync(fileStream, encoder);
				}

				return filePath;
			}
			catch (Exception ex)
			{
				Console.WriteLine($"Error exporting image: {ex.Message}");
				return string.Empty;
			}
		}

		public async Task<byte[]> GetImageAsFileFormatAsync(IImageEncoder? encoder = null)
		{
			if (this.Img == null)
			{
				return [];
			}

			encoder ??= new BmpEncoder();
			using MemoryStream ms = new();
			await this.Img.SaveAsync(ms, encoder);
			return ms.ToArray();
		}

		public override string ToString()
		{
			return $"{this.Width}x{this.Height} px, {this.Channels} ch., {this.Bitdepth} Bits";
		}

		public static async Task<ImageObj?> FromBytesAsync(byte[] bytes, string name, string contentType)
		{
			try
			{
				var img = await Task.Run(() =>
				{
					return SixLabors.ImageSharp.Image.Load<Rgba32>(bytes);
				});

				return new ImageObj(img, name, false);
			}
			catch (Exception ex)
			{
				Console.WriteLine($"Error creating ImageObj from bytes: {ex.Message}");
				return null;
			}
		}

		public async Task<ImageObj?> CloneAsync()
		{
			if (this.Img == null)
			{
				return null;
			}
			Image<Rgba32> imgClone;
			lock (this.lockObj)
			{
				imgClone = this.Img.CloneAs<Rgba32>();
			}
			return await Task.FromResult(new ImageObj(imgClone, this.Name, false));
		}

		public async Task<ImageObj?> ResizeAsync(int newWidth, int newHeight, bool keepAspectRatio = true, bool createNewImageObj = false)
		{
			if (this.Img == null)
			{
				return null;
			}

			Image<Rgba32> imgClone;
			lock (this.lockObj)
			{
				imgClone = this.Img.CloneAs<Rgba32>();
			}
			try
			{
				float absoluteScale = Math.Max(newWidth / (float) this.Width, newHeight / (float) this.Height);

				if (keepAspectRatio)
				{
					var aspectRatio = (float) this.Width / this.Height;
					if (newWidth / (float) newHeight > aspectRatio)
					{
						newWidth = (int) (newHeight * aspectRatio);
					}
					else
					{
						newHeight = (int) (newWidth / aspectRatio);
					}
				}
				else
				{
					float maxScale = Math.Max(newWidth / (float) this.Width, newHeight / (float) this.Height);
					absoluteScale = maxScale;
				}

				imgClone.Mutate(x => x.Resize(newWidth, newHeight));

				if (createNewImageObj)
				{
					return await Task.FromResult(new ImageObj(imgClone, this.Name + "_resized_" + $"{absoluteScale:F2}" + "", false));
				}
				else
				{
					this.Img.Dispose();
					this.Img = imgClone;
					this.Width = newWidth;
					this.Height = newHeight;
					this.ScalingFactor = absoluteScale;
					return this;
				}
			}
			catch (Exception ex)
			{
				Console.WriteLine($"Error resizing image: {ex.Message}");
				return null;
			}
		}

		public async Task<ImageObj?> ResizeAsync(float scale, bool createNewImageObj = false)
		{
			if (this.Img == null)
			{
				return null;
			}
			
			try
			{
				return await this.ResizeAsync((int)(this.Width * scale), (int)(this.Height * scale), true, createNewImageObj);
			}
			catch (Exception ex)
			{
				Console.WriteLine($"Error resizing image: {ex.Message}");
				return null;
			}
		}
	}
}
