using System.Diagnostics;
using System.IO;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;

namespace XamlAnimatedGif.PlaybackTest;

public partial class App : Application {
	private readonly StringBuilder log = new();
	private int errors;

	private async void Application_Startup(object sender, StartupEventArgs e) {
		DispatcherUnhandledException += (_, args) => {
			errors++;
			Write($"UNHANDLED {args.Exception.GetType().Name}: {args.Exception.Message}");
			args.Handled = true;
		};

		string root = @"C:\Users\RainbowWolfer\AppData\Local\YiffBrowser\MediaCache";
		List<string> files = Directory.EnumerateFiles(root, "*.gif", SearchOption.AllDirectories)
			.Select(path => new FileInfo(path))
			.Where(info => info.Length > 0)
			.OrderBy(info => info.Length)
			.Select(info => info.FullName)
			.ToList();
		Write($"gifs={files.Count}");
		if (files.Count < 12) {
			Write("RESULT fail not enough gifs");
			Shutdown(1);
			return;
		}

		Window window = new() {
			Title = "GIF crowd",
			Width = 1100,
			Height = 760,
			Background = Brushes.Black,
		};
		Grid host = new();
		window.Content = host;
		window.Show();

		string referencePath = files[Math.Min(8, files.Count - 1)];
		string largestPath = files[^1];
		await CompareFrame0(host, referencePath);
		await CompareFrame0(host, largestPath);
		HashSet<ulong> reference = await RecordFrames(host, referencePath);
		Write($"FRAMES {Path.GetFileName(referencePath)} signatures={reference.Count}");
		WatchResult solo = await PlayPage(host, [referencePath], TimeSpan.FromSeconds(3), "solo", referencePath, null);
		Write($"SOLO advances={solo.PrimaryAdvances:0.0}/s distinct={solo.Signatures.Count} errors={errors}");

		List<string> page = files.Take(11).Append(referencePath).ToList();
		WatchResult crowd = await PlayPage(host, page, TimeSpan.FromSeconds(4), "page-12", referencePath, reference);
		Write($"PAGE12 playing={crowd.Playing}/{page.Count} slowest={crowd.SlowestAdvances:0.0}/s primary={crowd.PrimaryAdvances:0.0}/s misses={crowd.Misses}/{crowd.Samples}");

		List<string> tabA = files.Skip(12).Take(10).ToList();
		List<string> tabB = files.Skip(22).Take(8).Append(referencePath).ToList();
		WatchResult tabs = await PlayTabs(host, tabA, tabB, TimeSpan.FromSeconds(4), referencePath, reference);
		Write($"TABS a={tabs.PlayingA}/{tabA.Count} b={tabs.PlayingB}/{tabB.Count} primary={tabs.PrimaryAdvances:0.0}/s misses={tabs.Misses}/{tabs.Samples}");

		bool rateOk = solo.PrimaryAdvances < 1 || crowd.PrimaryAdvances >= solo.PrimaryAdvances * 0.55;
		bool pageOk = crowd.Playing == page.Count && crowd.SlowestAdvances > 0;
		bool tabsOk = tabs.PlayingA == tabA.Count && tabs.PlayingB == tabB.Count;
		bool pictureOk = crowd.Samples == 0 || crowd.Misses * 5 <= crowd.Samples;
		bool tabsPictureOk = tabs.Samples == 0 || tabs.Misses * 5 <= tabs.Samples;
		bool pass = errors == 0 && rateOk && pageOk && tabsOk && pictureOk && tabsPictureOk && reference.Count > 0;
		Write($"RESULT pass={pass} rateOk={rateOk} pageOk={pageOk} tabsOk={tabsOk} pictureOk={pictureOk} tabsPictureOk={tabsPictureOk} errors={errors}");
		File.WriteAllText(Path.Combine(AppContext.BaseDirectory, "playback-test.log"), log.ToString());
		Shutdown(pass ? 0 : 1);
	}

	private async Task<WatchResult> PlayPage(Grid host, IReadOnlyList<string> paths, TimeSpan duration, string name, string watchPath, HashSet<ulong>? expected) {
		host.Children.Clear();
		UniformGrid grid = new() { Columns = 4 };
		host.Children.Add(grid);
		List<Image> images = paths.Select(path => CreateImage(path)).ToList();
		foreach (Image image in images) {
			grid.Children.Add(image);
		}
		await WaitForAnimators(images);
		return await Watch(images, images.Where(image => (string)image.Tag == watchPath).ToList(), duration, name, expected);
	}

	private async Task<WatchResult> PlayTabs(Grid host, IReadOnlyList<string> pageA, IReadOnlyList<string> pageB, TimeSpan duration, string watchPath, HashSet<ulong>? expected) {
		host.Children.Clear();
		DockPanel dock = new();
		host.Children.Add(dock);
		TabControl tabs = new() { Height = 32 };
		DockPanel.SetDock(tabs, Dock.Top);
		dock.Children.Add(tabs);

		UniformGrid gridA = new() { Columns = 5 };
		UniformGrid gridB = new() { Columns = 4 };
		Grid pages = new();
		pages.Children.Add(gridA);
		pages.Children.Add(gridB);
		dock.Children.Add(pages);

		List<Image> imagesA = pageA.Select(path => CreateImage(path)).ToList();
		List<Image> imagesB = pageB.Select(path => CreateImage(path)).ToList();
		foreach (Image image in imagesA) gridA.Children.Add(image);
		foreach (Image image in imagesB) gridB.Children.Add(image);

		tabs.Items.Add(new TabItem { Header = $"A ({imagesA.Count})", Content = new Border() });
		tabs.Items.Add(new TabItem { Header = $"B ({imagesB.Count})", Content = new Border() });
		tabs.SelectionChanged += (_, _) => {
			gridA.Visibility = tabs.SelectedIndex == 0 ? Visibility.Visible : Visibility.Collapsed;
			gridB.Visibility = tabs.SelectedIndex == 1 ? Visibility.Visible : Visibility.Collapsed;
		};
		gridB.Visibility = Visibility.Collapsed;

		List<Image> all = [.. imagesA, .. imagesB];
		await WaitForAnimators(all);
		WatchResult result = await Watch(all, all.Where(image => (string)image.Tag == watchPath).ToList(), duration, "tabs", expected, () => {
			tabs.SelectedIndex = 1;
			gridA.Visibility = Visibility.Collapsed;
			gridB.Visibility = Visibility.Visible;
		});
		result.PlayingA = imagesA.Count(image => result.Advances.TryGetValue(image, out int count) && count > 0);
		result.PlayingB = imagesB.Count(image => result.Advances.TryGetValue(image, out int count) && count > 0);
		await Dispatcher.InvokeAsync(() => tabs.SelectedIndex = 1);
		await Task.Delay(400);
		return result;
	}

	private Image CreateImage(string path) {
		Image image = new() {
			Width = 180,
			Height = 180,
			Stretch = Stretch.Uniform,
			Tag = path,
			Margin = new Thickness(4),
		};
		AnimationBehavior.SetAutoStart(image, true);
		AnimationBehavior.SetRepeatBehavior(image, System.Windows.Media.Animation.RepeatBehavior.Forever);
		AnimationBehavior.AddErrorHandler(image, (_, args) => {
			errors++;
			Write($"ERROR {Path.GetFileName(path)} {args.Kind}: {args.Exception.GetType().Name} {args.Exception.Message}");
		});
		byte[] bytes = File.ReadAllBytes(path);
		AnimationBehavior.SetSourceStream(image, new MemoryStream(bytes, writable: false));
		return image;
	}

	private async Task WaitForAnimators(IReadOnlyList<Image> images) {
		Stopwatch sw = Stopwatch.StartNew();
		while (sw.Elapsed < TimeSpan.FromSeconds(8)) {
			if (images.All(image => AnimationBehavior.GetAnimator(image) != null)) {
				return;
			}
			await Task.Delay(50);
		}
		int ready = images.Count(image => AnimationBehavior.GetAnimator(image) != null);
		Write($"load timeout {ready}/{images.Count}");
	}

	private async Task CompareFrame0(Grid host, string path) {
		byte[] bytes = File.ReadAllBytes(path);
		GifBitmapDecoder decoder = new(new MemoryStream(bytes, writable: false), BitmapCreateOptions.PreservePixelFormat, BitmapCacheOption.OnLoad);
		FormatConvertedBitmap converted = new(decoder.Frames[0], PixelFormats.Bgra32, null, 0);
		int width = converted.PixelWidth;
		int height = converted.PixelHeight;
		int stride = width * 4;
		byte[] wpf = new byte[height * stride];
		converted.CopyPixels(wpf, stride, 0);

		host.Children.Clear();
		Image image = new() { Width = 320, Height = 320, Stretch = Stretch.Uniform };
		AnimationBehavior.SetAutoStart(image, false);
		AnimationBehavior.SetRepeatBehavior(image, System.Windows.Media.Animation.RepeatBehavior.Forever);
		AnimationBehavior.AddErrorHandler(image, (_, args) => {
			errors++;
			Write($"ERROR wpf {args.Kind}: {args.Exception.GetType().Name} {args.Exception.Message}");
		});
		AnimationBehavior.SetSourceStream(image, new MemoryStream(bytes, writable: false));
		host.Children.Add(image);
		await WaitForAnimators([image]);
		Animator? animator = AnimationBehavior.GetAnimator(image);
		Stopwatch ready = Stopwatch.StartNew();
		while (ready.Elapsed < TimeSpan.FromSeconds(5) && (animator?.IsPaused != true || image.Source is not BitmapSource)) {
			await Task.Delay(20);
		}

		if (image.Source is not BitmapSource source) {
			errors++;
			Write($"WPF {Path.GetFileName(path)} missing frame");
			return;
		}

		byte[] ours = new byte[height * stride];
		source.CopyPixels(ours, stride, 0);
		int pixels = width * height;
		int mismatch = 0;
		int opaque = 0;
		for (int i = 0; i < pixels; i++) {
			int o = i * 4;
			if (wpf[o + 3] != 255 && ours[o + 3] != 255)
				continue;
			opaque++;
			if (wpf[o] != ours[o] || wpf[o + 1] != ours[o + 1] || wpf[o + 2] != ours[o + 2] || wpf[o + 3] != ours[o + 3])
				mismatch++;
		}
		Write($"WPF {Path.GetFileName(path)} {width}x{height} opaque={opaque} mismatch={mismatch}");
		if (opaque > 0 && mismatch * 100 > opaque)
			errors++;
	}

	private async Task<HashSet<ulong>> RecordFrames(Grid host, string path) {
		host.Children.Clear();
		Image image = new() {
			Width = 320,
			Height = 320,
			Stretch = Stretch.Uniform,
			Tag = path,
		};
		AnimationBehavior.SetAutoStart(image, false);
		AnimationBehavior.SetRepeatBehavior(image, System.Windows.Media.Animation.RepeatBehavior.Forever);
		AnimationBehavior.AddErrorHandler(image, (_, args) => {
			errors++;
			Write($"ERROR record {args.Kind}: {args.Exception.GetType().Name} {args.Exception.Message}");
		});
		byte[] bytes = File.ReadAllBytes(path);
		AnimationBehavior.SetSourceStream(image, new MemoryStream(bytes, writable: false));
		host.Children.Add(image);
		await WaitForAnimators([image]);
		Animator? animator = AnimationBehavior.GetAnimator(image);
		Stopwatch ready = Stopwatch.StartNew();
		while (ready.Elapsed < TimeSpan.FromSeconds(5) && (animator?.IsPaused != true || image.Source is not BitmapSource)) {
			await Task.Delay(20);
		}

		HashSet<ulong> signatures = [];
		if (image.Source is BitmapSource first && first.PixelWidth > 0) {
			signatures.Add(Signature(first));
		}

		System.Reflection.MethodInfo? render = typeof(Animator).GetMethod("RenderAndPublishAsync", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic);
		if (animator == null || render == null) {
			Write("record missing animator");
			return signatures;
		}

		for (int frame = 1; frame < animator.FrameCount; frame++) {
			Task task = (Task)render.Invoke(animator, [frame, CancellationToken.None, true])!;
			await task;
			if (image.Source is BitmapSource source && source.PixelWidth > 0) {
				signatures.Add(Signature(source));
			}
		}

		Write($"recorded {Path.GetFileName(path)} frames={animator.FrameCount} signatures={signatures.Count}");
		return signatures;
	}

	private async Task<WatchResult> Watch(IReadOnlyList<Image> images, IReadOnlyList<Image> watched, TimeSpan duration, string name, HashSet<ulong>? expected, Action? halfway = null) {
		Dictionary<Image, int> last = images.ToDictionary(image => image, _ => -1);
		Dictionary<Image, int> advances = images.ToDictionary(image => image, _ => 0);
		HashSet<ulong> signatures = [];
		int misses = 0;
		int samples = 0;
		bool switched = halfway == null;
		Stopwatch sw = Stopwatch.StartNew();
		while (sw.Elapsed < duration) {
			await Task.Delay(50);
			if (!switched && sw.Elapsed > duration / 2) {
				switched = true;
				halfway?.Invoke();
			}
			foreach (Image image in images) {
				int frame = AnimationBehavior.GetAnimator(image)?.CurrentFrameIndex ?? -1;
				if (last[image] >= 0 && frame != last[image]) {
					advances[image]++;
				}
				last[image] = frame;
			}

			foreach (Image image in watched) {
				if (image.Source is not BitmapSource source || source.PixelWidth <= 0 || source.PixelHeight <= 0) {
					continue;
				}
				ulong signature = Signature(source);
				signatures.Add(signature);
				samples++;
				if (expected != null && expected.Count > 0 && !expected.Contains(signature)) {
					misses++;
				}
			}
		}

		int playing = advances.Values.Count(count => count > 0);
		double seconds = Math.Max(sw.Elapsed.TotalSeconds, 0.001);
		double slowest = images.Count == 0 ? 0 : advances.Values.Min() / seconds;
		double primary = watched.Count == 0 ? 0 : watched.Max(image => advances[image]) / seconds;
		Write($"{name} playing={playing}/{images.Count} primary={primary:0.0}/s slowest={slowest:0.0}/s signatures={signatures.Count} misses={misses}/{samples}");
		return new WatchResult(playing, primary, slowest, signatures, misses, samples, advances);
	}

	private static ulong Signature(BitmapSource source) {
		ulong hash = 14695981039346656037UL;
		byte[] pixel = new byte[4];
		int width = source.PixelWidth;
		int height = source.PixelHeight;
		for (int gy = 0; gy < 6; gy++) {
			for (int gx = 0; gx < 6; gx++) {
				int x = Math.Clamp((gx * 2 + 1) * (width - 1) / 12, 0, width - 1);
				int y = Math.Clamp((gy * 2 + 1) * (height - 1) / 12, 0, height - 1);
				source.CopyPixels(new Int32Rect(x, y, 1, 1), pixel, 4, 0);
				hash ^= (ulong)(pixel[0] + (pixel[1] << 8) + (pixel[2] << 16) + (pixel[3] << 24));
				hash *= 1099511628211UL;
			}
		}
		return hash;
	}

	private void Write(string line) {
		string text = $"{DateTime.Now:HH:mm:ss.fff} {line}";
		log.AppendLine(text);
		Console.WriteLine(text);
	}

	private sealed class WatchResult {
		public WatchResult(int playing, double primaryAdvances, double slowestAdvances, HashSet<ulong> signatures, int misses, int samples, Dictionary<Image, int> advances) {
			Playing = playing;
			PrimaryAdvances = primaryAdvances;
			SlowestAdvances = slowestAdvances;
			Signatures = signatures;
			Misses = misses;
			Samples = samples;
			Advances = advances;
		}

		public int Playing { get; }
		public int PlayingA { get; set; }
		public int PlayingB { get; set; }
		public double PrimaryAdvances { get; }
		public double SlowestAdvances { get; }
		public HashSet<ulong> Signatures { get; }
		public int Misses { get; }
		public int Samples { get; }
		public Dictionary<Image, int> Advances { get; }
	}
}
