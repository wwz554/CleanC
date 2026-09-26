using System.Diagnostics;
using System.Runtime.InteropServices.WindowsRuntime;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;

namespace CleanC.App;

/// <summary>Animated particle styling around an ordinary camera-readable QR code.</summary>
internal sealed class ConstellationQrCard : Grid
{
    const int PixelSize = 720;
    readonly StandardParticleQrRenderer renderer;
    readonly byte[] pixels = new byte[PixelSize * PixelSize * 4];
    readonly WriteableBitmap bitmap = new(PixelSize, PixelSize);
    readonly Stopwatch clock = new();
    double lastFrame = -1;
    bool subscribed;

    public ConstellationQrCard(string url)
    {
        renderer = new StandardParticleQrRenderer(url);
        Width = 308; Height = 308;
        HorizontalAlignment = HorizontalAlignment.Center;
        Background = null;
        Children.Add(new Image { Source = bitmap, Stretch = Stretch.Uniform });
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(this, "手机相机或微信可识别的动态粒子样式二维码");
        Loaded += (_, _) => Start();
        Unloaded += (_, _) => Stop();
    }

    void Start()
    {
        if (subscribed) return;
        Draw(0);
        if (!new Windows.UI.ViewManagement.UISettings().AnimationsEnabled) return;
        clock.Restart(); lastFrame = -1;
        CompositionTarget.Rendering += OnRendering;
        subscribed = true;
    }

    void Stop()
    {
        if (subscribed) CompositionTarget.Rendering -= OnRendering;
        subscribed = false; clock.Stop();
    }

    void OnRendering(object? sender, object args)
    {
        var now = clock.Elapsed.TotalSeconds;
        if (now - lastFrame < 1d / 12) return;
        lastFrame = now;
        Draw(now);
    }

    void Draw(double seconds)
    {
        renderer.Render(pixels, PixelSize, seconds, Ui.Dark);
        using var stream = bitmap.PixelBuffer.AsStream();
        stream.Write(pixels);
        bitmap.Invalidate();
    }
}
