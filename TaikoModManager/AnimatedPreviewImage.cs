using System;
using System.Collections.Generic;
using System.IO;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using ImageMagick;

namespace TaikoModManager
{
    /// <summary>
    /// Displays remote preview images, including multi-frame WebP and APNG files.
    /// Static images use the same control so preview layout is consistent.
    /// </summary>
    public sealed class AnimatedPreviewImage : Image
    {
        public static readonly DependencyProperty SourceUrlProperty = DependencyProperty.Register(
            nameof(SourceUrl), typeof(string), typeof(AnimatedPreviewImage),
            new PropertyMetadata(string.Empty, OnSourceUrlChanged));

        private readonly DispatcherTimer _animationTimer;
        private CancellationTokenSource? _loadCancellation;
        private IReadOnlyList<PreviewFrame> _frames = Array.Empty<PreviewFrame>();
        private int _frameIndex;

        public AnimatedPreviewImage()
        {
            _animationTimer = new DispatcherTimer(DispatcherPriority.Render)
            {
                Interval = TimeSpan.FromMilliseconds(100)
            };
            _animationTimer.Tick += AnimationTimer_Tick;
            Loaded += (_, _) =>
            {
                if (Source == null && !string.IsNullOrWhiteSpace(SourceUrl))
                {
                    LoadPreview(SourceUrl);
                }
            };
            Unloaded += (_, _) => StopLoading();
        }

        public string SourceUrl
        {
            get => (string)GetValue(SourceUrlProperty);
            set => SetValue(SourceUrlProperty, value);
        }

        private static void OnSourceUrlChanged(DependencyObject dependencyObject, DependencyPropertyChangedEventArgs args)
        {
            ((AnimatedPreviewImage)dependencyObject).LoadPreview((string?)args.NewValue);
        }

        private void LoadPreview(string? url)
        {
            StopLoading();
            Source = null;
            _frames = Array.Empty<PreviewFrame>();
            _frameIndex = 0;

            bool isLocalFile = File.Exists(url);
            Uri? uri = null;
            if (!isLocalFile && (!Uri.TryCreate(url, UriKind.Absolute, out uri) || uri.Scheme != Uri.UriSchemeHttps))
            {
                return;
            }

            _loadCancellation = new CancellationTokenSource();
            _ = LoadPreviewAsync(isLocalFile ? url! : null, uri, _loadCancellation.Token);
        }

        private async Task LoadPreviewAsync(string? localPath, Uri? remoteUri, CancellationToken cancellationToken)
        {
            try
            {
                byte[] imageBytes;
                if (localPath != null)
                {
                    imageBytes = await File.ReadAllBytesAsync(localPath, cancellationToken);
                }
                else
                {
                    using var client = new HttpClient();
                    client.DefaultRequestHeaders.UserAgent.ParseAdd("TaikoModManager/1.0");
                    imageBytes = await client.GetByteArrayAsync(remoteUri!, cancellationToken);
                }
                IReadOnlyList<PreviewFrame> frames = await Task.Run(() => DecodeFrames(imageBytes, cancellationToken), cancellationToken);

                if (cancellationToken.IsCancellationRequested || frames.Count == 0)
                {
                    return;
                }

                _frames = frames;
                _frameIndex = 0;
                Source = frames[0].Image;
                if (frames.Count > 1)
                {
                    _animationTimer.Interval = frames[0].Delay;
                    _animationTimer.Start();
                }
            }
            catch (OperationCanceledException)
            {
                // The selected list item changed before its image finished loading.
            }
            catch
            {
                // A broken or unsupported preview should never affect browsing mods.
            }
        }

        private static IReadOnlyList<PreviewFrame> DecodeFrames(byte[] imageBytes, CancellationToken cancellationToken)
        {
            var frames = new List<PreviewFrame>();
            using var images = new MagickImageCollection();
            if (IsAnimatedPng(imageBytes))
            {
                // ImageMagick otherwise uses the normal PNG decoder and only exposes
                // the default image. APNG has its own decoder name.
                images.Read(imageBytes, new MagickReadSettings { Format = MagickFormat.APng });
            }
            else
            {
                images.Read(imageBytes);
            }

            // APNG and WebP commonly store delta frames. Coalesce turns them into
            // complete canvas frames before WPF displays them one at a time.
            images.Coalesce();
            foreach (MagickImage frame in images)
            {
                cancellationToken.ThrowIfCancellationRequested();
                byte[] png = frame.ToByteArray(MagickFormat.Png);
                using var stream = new MemoryStream(png);
                var bitmap = new BitmapImage();
                bitmap.BeginInit();
                bitmap.CacheOption = BitmapCacheOption.OnLoad;
                bitmap.StreamSource = stream;
                bitmap.EndInit();
                bitmap.Freeze();

                int ticksPerSecond = Math.Max(1, frame.AnimationTicksPerSecond);
                int delayMilliseconds = Math.Max(20, (int)Math.Round(frame.AnimationDelay * 1000d / ticksPerSecond));
                frames.Add(new PreviewFrame(bitmap, TimeSpan.FromMilliseconds(delayMilliseconds)));
            }
            return frames;
        }

        private static bool IsAnimatedPng(byte[] imageBytes)
        {
            if (imageBytes.Length < 20 || imageBytes[0] != 137 || imageBytes[1] != 80 ||
                imageBytes[2] != 78 || imageBytes[3] != 71 || imageBytes[4] != 13 ||
                imageBytes[5] != 10 || imageBytes[6] != 26 || imageBytes[7] != 10)
            {
                return false;
            }

            int offset = 8;
            while (offset <= imageBytes.Length - 12)
            {
                uint length = ((uint)imageBytes[offset] << 24) |
                              ((uint)imageBytes[offset + 1] << 16) |
                              ((uint)imageBytes[offset + 2] << 8) |
                              imageBytes[offset + 3];
                if (length > int.MaxValue || offset + 12L + length > imageBytes.Length)
                {
                    return false;
                }

                if (imageBytes[offset + 4] == (byte)'a' && imageBytes[offset + 5] == (byte)'c' &&
                    imageBytes[offset + 6] == (byte)'T' && imageBytes[offset + 7] == (byte)'L')
                {
                    return true;
                }

                offset += checked((int)length + 12);
            }

            return false;
        }

        private void AnimationTimer_Tick(object? sender, EventArgs e)
        {
            if (_frames.Count < 2)
            {
                _animationTimer.Stop();
                return;
            }

            _frameIndex = (_frameIndex + 1) % _frames.Count;
            PreviewFrame frame = _frames[_frameIndex];
            Source = frame.Image;
            _animationTimer.Interval = frame.Delay;
        }

        private void StopLoading()
        {
            _animationTimer.Stop();
            _loadCancellation?.Cancel();
            _loadCancellation?.Dispose();
            _loadCancellation = null;
        }

        private sealed record PreviewFrame(BitmapImage Image, TimeSpan Delay);
    }
}
