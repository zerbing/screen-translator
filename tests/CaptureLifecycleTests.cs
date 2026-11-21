using System.Reflection;
using System.Runtime.InteropServices;
using ScreenTranslator;
using Vortice.Direct3D;
using Vortice.Direct3D11;
using Vortice.DXGI;

internal static class CaptureLifecycleTests
{
    internal static int Run()
    {
        var checks = 0;
        void Check(bool passed, string description)
        {
            checks++;
            if (!passed)
                throw new InvalidOperationException("截屏生命周期测试失败：" + description);
        }

        // WARP exercises real D3D mapping/unmapping without changing display
        // modes, starting a game, or depending on the installed GPU driver.
        D3D11.D3D11CreateDevice(null, DriverType.Warp, DeviceCreationFlags.BgraSupport,
            [FeatureLevel.Level_11_0], out var device, out _, out var context).CheckError();
        using (device)
        using (context)
        {
            var gains = Enumerable.Repeat(1f, 0x7c00).ToArray();
            gains[0] = 0;
            var pixels = new ushort[] { HalfBits(1), HalfBits(.5f), HalfBits(.25f), HalfBits(1) };
            var pointer = Marshal.AllocHGlobal(pixels.Length * sizeof(ushort));
            try
            {
                Marshal.Copy(Array.ConvertAll(pixels, value => unchecked((short)value)), 0, pointer, pixels.Length);
                var description = new Texture2DDescription(Format.R16G16B16A16_Float, 1, 1, 1, 1,
                    BindFlags.None, ResourceUsage.Staging, CpuAccessFlags.Read);
                using var staging = device.CreateTexture2D(description, new SubresourceData(pointer, 8));
                using (var bitmap = HdrScreenCapture.ReadStaging(context, staging, new Size(1, 1), gains, default))
                    Check(bitmap?.GetPixel(0, 0).ToArgb() == Color.FromArgb(255, 255, 188, 137).ToArgb(),
                        "FP16 读回保留 RGB 通道及 sRGB 转换");

                using var cancelled = new CancellationTokenSource();
                cancelled.Cancel();
                var rejected = false;
                try { using var ignored = HdrScreenCapture.ReadStaging(context, staging, new Size(1, 1), gains, cancelled.Token); }
                catch (OperationCanceledException) { rejected = true; }
                Check(rejected, "取消的 GPU 读回立即停止");
                using var retry = HdrScreenCapture.ReadStaging(context, staging, new Size(1, 1), gains, default);
                Check(retry?.GetPixel(0, 0).R == 255, "取消后纹理仍可重新映射读取");

                var conversionFailed = false;
                try { using var ignored = HdrScreenCapture.ReadStaging(context, staging, new Size(1, 1), [], default); }
                catch (IndexOutOfRangeException) { conversionFailed = true; }
                Check(conversionFailed, "覆盖映射成功后转换失败的清理路径");
                using var recovered = HdrScreenCapture.ReadStaging(context, staging, new Size(1, 1), gains, default);
                Check(recovered?.GetPixel(0, 0).G == 188, "转换异常后解除纹理映射");
            }
            finally { Marshal.FreeHGlobal(pointer); }
        }

        Exception? uiError = null;
        var ui = new Thread(() =>
        {
            try
            {
                using (var target = new Form())
                using (var popup = new TranslationPopup())
                {
                    var oldHandle = target.Handle;
                    target.Dispose();
                    popup.Present("Rendering window closed.", "游戏窗口已经关闭。", null,
                        RectangleF.Empty, new Point(100, 100), oldHandle);
                    Check(Desktop.IsWindowVisible(popup.Handle), "目标窗口销毁后浮窗仍可独立测量和显示文字");
                    popup.Dismiss();
                }

                using var app = new TranslatorApp();
                Field<System.Windows.Forms.Timer>(app, "timer").Stop();
                using var cancellation = new CancellationTokenSource();
                var first = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                var last = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                SetField(app, "pending", cancellation);
                SetField(app, "activeRequest", Task.WhenAll(first.Task, last.Task));
                var exits = 0;
                app.ThreadExit += (_, _) => exits++;
                app.ExitThread();
                app.ExitThread();
                Check(cancellation.IsCancellationRequested, "退出先取消正在进行的识别");
                Check(exits == 0, "后台工作未完成时保留消息循环");
                first.SetResult();
                Application.DoEvents();
                Check(exits == 0, "一项工作完成后仍等待剩余工作");
                last.SetResult();
                var deadline = Environment.TickCount64 + 5000;
                while (exits == 0 && Environment.TickCount64 < deadline)
                {
                    Application.DoEvents();
                    Thread.Sleep(1);
                }
                Check(exits == 1, "全部工作完成后只退出一次");
            }
            catch (Exception error) { uiError = error; }
        }) { IsBackground = true };
        ui.SetApartmentState(ApartmentState.STA);
        ui.Start();
        if (!ui.Join(TimeSpan.FromSeconds(30)))
            throw new TimeoutException("截屏生命周期测试的 UI 线程未能退出。");
        if (uiError is not null)
            throw new InvalidOperationException("截屏生命周期 UI 测试失败。", uiError);

        Console.WriteLine($"截屏生命周期测试通过：{checks} 项。");
        return 0;
    }

    private static ushort HalfBits(float value) => BitConverter.HalfToUInt16Bits((Half)value);
    private static T Field<T>(object target, string name) => (T)target.GetType()
        .GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(target)!;
    private static void SetField(object target, string name, object value) => target.GetType()
        .GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(target, value);
}
