using System.IO;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;

namespace SoriLab.App;

public static class Program
{
    [STAThread]
    public static int Main(string[] args)
    {
        try
        {
            var app = new Application { ShutdownMode = ShutdownMode.OnMainWindowClose };
            var window = new StudioWindow();
            app.MainWindow = window;
            if (args.Length > 0 && args[0] == "--close-test")
            {
                window.ShowInTaskbar = false;
                window.WindowStartupLocation = WindowStartupLocation.Manual;
                window.Left = -20000; window.Top = -20000;
                window.Loaded += (_, _) => window.Dispatcher.BeginInvoke(new Action(window.Close));
            }
            else if (args.Length >= 3 && args[0] == "--smoke-test")
            {
                window.ShowInTaskbar = false;
                window.WindowStartupLocation = WindowStartupLocation.Manual;
                window.Left = -20000;
                window.Top = -20000;
                window.Loaded += async (_, _) =>
                {
                    try
                    {
                        await window.LoadFileAsync(args[1]);
                        await window.RunInteractionChecksAsync(args[2]);
                        if (args.Length >= 4 && args[3] == "--small") { window.Width = 1180; window.Height = 800; }
                        await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
                        window.UpdateLayout();
                        SaveScreenshot(window, args[2]);
                        File.WriteAllText(args[2] + ".status", window.SmokeTestStatus);
                        app.Shutdown(0);
                    }
                    catch (Exception ex)
                    {
                        File.WriteAllText(args[2] + ".error", ex.ToString());
                        app.Shutdown(1);
                    }
                };
            }
            else if (args.Length == 1 && File.Exists(args[0]))
            {
                window.Loaded += async (_, _) => await window.OpenDocumentAsync(args[0]);
            }
            return app.Run(window);
        }
        catch (Exception ex)
        {
            MessageBox.Show("프로그램을 시작하지 못했습니다.\n" + ex.Message, "소리공방", MessageBoxButton.OK, MessageBoxImage.Error);
            return 1;
        }
    }

    internal static void SaveScreenshot(Window window, string path)
    {
        window.UpdateLayout();
        var root = (FrameworkElement)window.Content;
        var bitmap = new RenderTargetBitmap((int)Math.Ceiling(root.ActualWidth + root.Margin.Left + root.Margin.Right), (int)Math.Ceiling(root.ActualHeight + root.Margin.Top + root.Margin.Bottom), 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(window);
        var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using var output = File.Create(path); encoder.Save(output);
    }
}
