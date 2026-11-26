using System.IO;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;

namespace XamlAnimatedGif.PlaybackTest;

public partial class App : Application {
	private const string GifPath = @"C:\Users\RainbowWolfer\AppData\Local\YiffBrowser\MediaCache\e621\5965623\sample-4aa8157a0f82e6d861960f3a.gif";

	private readonly StringBuilder log = new();
	private int ticks;
	private int started;
	private int errors;
	private int pixelChanges;
	private int frameAdvances;
	private int lastFrame = -1;
	private byte[]? lastPixels;
	private Image? image;
	private DispatcherTimer? timer;

	private void Application_Startup(object sender, StartupEventArgs e) {
		DispatcherUnhandledException += (_, args) => {
			Write($"UNHANDLED {args.Exception}");
			args.Handled = true;
			Finish(2);
		};

		AppDomain.CurrentDomain.FirstChanceException += (_, args) => {
			if (args.Exception is OperationCanceledException or TaskCanceledException) {
				return;
			}

			string type = args.Exception.GetType().FullName ?? args.Exception.GetType().Name;
			if (type.Contains("XamlAnimatedGif") || type.Contains("IOException") || type.Contains("InvalidOperation") || type.Contains("Argument") || type.Contains("EndOfStream") || type.Contains("IndexOutOfRange") || type.Contains("NullReference")) {
				Write($"FIRST {args.Exception.GetType().Name}: {args.Exception.Message}");
			}
		};

		Write($"file exists={File.Exists(GifPath)} length={(File.Exists(GifPath) ? new FileInfo(GifPath).Length : 0)}");

		image = new Image {
			Width = 480,
			Height = 480,
			Stretch = Stretch.Uniform,
		};
		AnimationBehavior.SetAutoStart(image, true);
		AnimationBehavior.SetRepeatBehavior(image, System.Windows.Media.Animation.RepeatBehavior.Forever);
		AnimationBehavior.AddErrorHandler(image, (_, args) => {
			errors++;
			Write($"ERROR {args.Kind}: {args.Exception}");
		});
		AnimationBehavior.AddAnimationStartedHandler(image, (_, _) => {
			started++;
			Write("STARTED");
		});
		AnimationBehavior.AddLoadedHandler(image, (_, _) => {
			Animator? animator = AnimationBehavior.GetAnimator(image);
			Write($"LOADED animator={animator != null} frames={animator?.FrameCount}");
		});

		Window window = new() {
			Title = "GIF playback test",
			Width = 520,
			Height = 560,
			Content = image,
		};
		window.Show();

		byte[] bytes = File.ReadAllBytes(GifPath);
		MemoryStream stream = new(bytes, writable: false);
		Write($"stream length={stream.Length} header={BitConverter.ToString(bytes, 0, 6)}");
		AnimationBehavior.SetSourceStream(image, stream);

		timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(500) };
		timer.Tick += (_, _) => Sample();
		timer.Start();
	}

	private void Sample() {
		ticks++;
		if (image?.Source is not BitmapSource source) {
			Write($"tick {ticks}: source={image?.Source?.GetType().Name ?? "null"}");
		} else {
			byte[] pixels = new byte[16];
			try {
				int width = Math.Min(4, source.PixelWidth);
				int height = Math.Min(1, source.PixelHeight);
				if (width > 0 && source.PixelHeight > 0) {
					source.CopyPixels(new System.Windows.Int32Rect(0, 0, width, Math.Max(1, height)), pixels, width * 4, 0);
				}
			} catch (Exception ex) {
				Write($"tick {ticks}: copy failed {ex.GetType().Name}: {ex.Message}");
				pixels = [];
			}

			bool changed = lastPixels == null || !pixels.AsSpan().SequenceEqual(lastPixels);
			if (changed && lastPixels != null) {
				pixelChanges++;
			}

			lastPixels = pixels;
			Animator? animator = AnimationBehavior.GetAnimator(image);
			int frame = animator?.CurrentFrameIndex ?? -1;
			if (lastFrame >= 0 && frame != lastFrame) {
				frameAdvances++;
			}

			lastFrame = frame;
			Write($"tick {ticks}: {source.PixelWidth}x{source.PixelHeight} frame={frame} paused={animator?.IsPaused} complete={animator?.IsComplete} changed={changed} pixels={BitConverter.ToString(pixels)}");
		}

		if (ticks >= 12) {
			Finish(frameAdvances > 0 || pixelChanges > 0 ? 0 : 1);
		}
	}

	private void Finish(int code) {
		timer?.Stop();
		Write($"RESULT started={started} errors={errors} pixelChanges={pixelChanges} frameAdvances={frameAdvances} code={code}");
		string path = Path.Combine(AppContext.BaseDirectory, "playback-test.log");
		File.WriteAllText(path, log.ToString());
		Console.WriteLine(log.ToString());
		Shutdown(code);
	}

	private void Write(string line) {
		string text = $"{DateTime.Now:HH:mm:ss.fff} {line}";
		log.AppendLine(text);
		Console.WriteLine(text);
	}
}
