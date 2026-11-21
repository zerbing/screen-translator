using ScreenTranslator;

internal static class OcrWorkerTests
{
    internal static async Task<int> RunAsync()
    {
        var checks = 0;
        void Check(bool passed, string description)
        {
            Interlocked.Increment(ref checks);
            if (!passed) throw new InvalidOperationException("OCR 工作单元测试失败：" + description);
        }

        var firstStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseFirst = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var advanced = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var readers = new[] { new Reader(), new Reader() };
        var prepared = new List<(int Index, Bitmap Image)>();
        var preparing = 0;
        var active = 0;
        var maximum = 0;
        var running = ScreenOcr.RunOcrWorkersAsync(Enumerable.Range(0, 8).ToArray(), readers, index =>
        {
            Check(Interlocked.Increment(ref preparing) == 1, "图像准备串行访问源图");
            try
            {
                var image = new Bitmap(4, 4);
                image.SetPixel(0, 0, Color.FromArgb(index, 0, 0));
                prepared.Add((index, image));
                Thread.Yield();
                return image;
            }
            finally { Interlocked.Decrement(ref preparing); }
        }, async (reader, index, image) =>
        {
            if (Interlocked.Exchange(ref reader.Busy, 1) != 0)
                throw new InvalidOperationException("一个识别器被多个工作单元同时使用。");
            var count = Interlocked.Increment(ref active);
            lock (readers) maximum = Math.Max(count, maximum);
            try
            {
                if (index == 0)
                {
                    firstStarted.SetResult();
                    await releaseFirst.Task;
                }
                if (index >= 2) advanced.TrySetResult();
                return image.GetPixel(0, 0).R;
            }
            finally
            {
                Interlocked.Decrement(ref active);
                Volatile.Write(ref reader.Busy, 0);
            }
        }, default);
        try
        {
            await firstStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
            await advanced.Task.WaitAsync(TimeSpan.FromSeconds(5));
            Check(!running.IsCompleted, "慢任务运行期间其他工作单元可以领取下一项");
        }
        finally { releaseFirst.TrySetResult(); }
        var results = await running;
        Check(results.Select(value => (int)value).SequenceEqual(Enumerable.Range(0, 8)), "乱序完成后仍按原始顺序返回结果");
        Check(prepared.Select(item => item.Index).SequenceEqual(Enumerable.Range(0, 8)), "每个图像只准备一次");
        Check(maximum <= readers.Length, "并发读取数量受识别器数量限制");
        Check(prepared.All(item => IsDisposed(item.Image)), "完成后释放所有工作图像");

        foreach (var cancel in new[] { true, false })
        {
            using var cancellation = new CancellationTokenSource();
            var nativeStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var releaseNative = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var interrupted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            Bitmap? nativeImage = null;
            var stopped = ScreenOcr.RunOcrWorkersAsync(new[] { 0, 1, 2 }, readers, index =>
            {
                if (index == 1)
                {
                    nativeStarted.Task.WaitAsync(TimeSpan.FromSeconds(5)).GetAwaiter().GetResult();
                    if (cancel) cancellation.Cancel();
                    interrupted.SetResult();
                    if (!cancel) throw new InvalidDataException("test preparation failure");
                }
                return new Bitmap(4, 4);
            }, async (reader, index, image) =>
            {
                nativeImage = image;
                nativeStarted.SetResult();
                await releaseNative.Task;
                return image.GetPixel(0, 0).ToArgb();
            }, cancellation.Token);
            try
            {
                await interrupted.Task.WaitAsync(TimeSpan.FromSeconds(5));
                await Task.WhenAny(stopped, Task.Delay(20));
                Check(!stopped.IsCompleted, $"{(cancel ? "取消" : "准备失败")}后等待已启动的原生读取");
                Check(nativeImage is not null && !IsDisposed(nativeImage), "读取结束前保留其独占图像");
            }
            finally { releaseNative.TrySetResult(); }
            Exception? failure = null;
            try { await stopped; }
            catch (Exception error) { failure = error; }
            Check(cancel ? failure is OperationCanceledException : failure is InvalidDataException, "等待结束后保留原始取消或错误");
            Check(nativeImage is not null && IsDisposed(nativeImage), "中断后的原生读取完成后释放图像");
        }

        Console.WriteLine($"OCR 工作单元测试通过：{checks} 项。");
        return 0;
    }

    private static bool IsDisposed(Bitmap image)
    {
        try { image.GetPixel(0, 0); return false; }
        catch (ArgumentException) { return true; }
    }

    private sealed class Reader { internal int Busy; }
}
