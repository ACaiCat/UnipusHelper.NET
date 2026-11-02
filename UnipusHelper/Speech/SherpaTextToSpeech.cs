using System.Buffers.Binary;
using System.Diagnostics;
using SherpaOnnx;

namespace UnipusHelper.Speech;

public sealed record PcmAudio(byte[] Samples, int SampleRate)
{
    public TimeSpan Duration => TimeSpan.FromSeconds(Samples.Length / 2.0 / SampleRate);
    public byte[] ToWave()
    {
        var wave = new byte[44 + Samples.Length];
        "RIFF"u8.CopyTo(wave);
        BinaryPrimitives.WriteInt32LittleEndian(wave.AsSpan(4), wave.Length - 8);
        "WAVEfmt "u8.CopyTo(wave.AsSpan(8));
        BinaryPrimitives.WriteInt32LittleEndian(wave.AsSpan(16), 16);
        BinaryPrimitives.WriteInt16LittleEndian(wave.AsSpan(20), 1);
        BinaryPrimitives.WriteInt16LittleEndian(wave.AsSpan(22), 1);
        BinaryPrimitives.WriteInt32LittleEndian(wave.AsSpan(24), SampleRate);
        BinaryPrimitives.WriteInt32LittleEndian(wave.AsSpan(28), SampleRate * 2);
        BinaryPrimitives.WriteInt16LittleEndian(wave.AsSpan(32), 2);
        BinaryPrimitives.WriteInt16LittleEndian(wave.AsSpan(34), 16);
        "data"u8.CopyTo(wave.AsSpan(36));
        BinaryPrimitives.WriteInt32LittleEndian(wave.AsSpan(40), Samples.Length);
        Samples.CopyTo(wave, 44);
        return wave;
    }
}

public interface ITextToSpeech
{
    Task<PcmAudio> SynthesizeAsync(string text, CancellationToken ct, Action<string>? progress = null, float speed = 1.0f);
}

public sealed class SherpaTextToSpeech : ITextToSpeech
{
    public static SherpaTextToSpeech Shared { get; } = new();
    private const string ModelName = "vits-piper-en_US-amy-low";
    private const string ModelFile = "en_US-amy-low.onnx";
    private static readonly HttpClient download = new() { Timeout = TimeSpan.FromMinutes(10) };
    private readonly SemaphoreSlim gate = new(1, 1);
    private OfflineTts? engine;
    private static string BundledModelDirectory => Path.Combine(AppContext.BaseDirectory, "tts", ModelName);
    private static string CachedModelDirectory => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "UnipusHelper.NET", "tts", ModelName);
    public static string ModelDirectory => IsModelAvailable(BundledModelDirectory) ? BundledModelDirectory : CachedModelDirectory;

    private static bool IsModelAvailable(string directory) =>
        File.Exists(Path.Combine(directory, ModelFile)) && File.Exists(Path.Combine(directory, "tokens.txt")) &&
        Directory.Exists(Path.Combine(directory, "espeak-ng-data"));

    public async Task<PcmAudio> SynthesizeAsync(string text, CancellationToken ct, Action<string>? progress = null, float speed = 1.0f)
    {
        if (string.IsNullOrWhiteSpace(text)) throw new ArgumentException("口语稿不能为空。", nameof(text));
        if (!float.IsFinite(speed) || speed <= 0) throw new ArgumentOutOfRangeException(nameof(speed));
        await gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (engine is null)
            {
                await EnsureModelAsync(ct, progress).ConfigureAwait(false);
                progress?.Invoke(IsModelAvailable(BundledModelDirectory) ? "正在加载语音模型（随包模型）" : "正在加载语音模型（本地缓存）");
                var config = new OfflineTtsConfig();
                config.Model.Vits.Model = Path.Combine(ModelDirectory, ModelFile);
                config.Model.Vits.Tokens = Path.Combine(ModelDirectory, "tokens.txt");
                config.Model.Vits.DataDir = Path.Combine(ModelDirectory, "espeak-ng-data");
                config.Model.Provider = "cpu";
                config.Model.NumThreads = Math.Clamp(Environment.ProcessorCount / 2, 1, 4);
                config.Model.Debug = 0;
                engine = new OfflineTts(config);
                if (engine.SampleRate <= 0) throw new InvalidOperationException("语音模型加载失败。");
            }
            progress?.Invoke("正在生成口语音频");
            return await Task.Run(() =>
            {
                ct.ThrowIfCancellationRequested();
                var generated = engine.GenerateWithCallback(text, speed, 0, (_, _) => ct.IsCancellationRequested ? 0 : 1);
                try
                {
                    ct.ThrowIfCancellationRequested();
                    if (generated.NumSamples == 0) throw new InvalidOperationException("TTS 未生成音频，请检查口语稿内容。");
                    return ConvertToPcm(generated.Samples, generated.SampleRate);
                }
                finally { generated.Dispose(); }
            }, ct).ConfigureAwait(false);
        }
        finally { gate.Release(); }
    }

    public static PcmAudio ConvertToPcm(float[] samples, int sourceRate)
    {
        const int targetRate = 16000;
        if (sourceRate <= 0) throw new ArgumentOutOfRangeException(nameof(sourceRate));
        var length = checked((int)Math.Round(samples.Length * (double)targetRate / sourceRate));
        var pcm = new byte[checked(length * 2)];
        for (var i = 0; i < length; i++)
        {
            var position = i * (double)sourceRate / targetRate;
            var left = Math.Min((int)position, samples.Length - 1);
            var right = Math.Min(left + 1, samples.Length - 1);
            var value = samples[left] + (samples[right] - samples[left]) * (position - left);
            var sample = (short)Math.Clamp(Math.Round(value * 32767), short.MinValue, short.MaxValue);
            BinaryPrimitives.WriteInt16LittleEndian(pcm.AsSpan(i * 2), sample);
        }
        return new PcmAudio(pcm, targetRate);
    }

    private static async Task EnsureModelAsync(CancellationToken ct, Action<string>? progress)
    {
        if (IsModelAvailable(BundledModelDirectory)) return;
        Directory.CreateDirectory(ModelDirectory);
        if (!IsModelAvailable(ModelDirectory))
        {
            var staging = Path.Combine(ModelDirectory, ".download-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(staging);
            try
            {
                var archive = Path.Combine(staging, "model.tar.bz2");
                await DownloadAsync($"https://github.com/k2-fsa/sherpa-onnx/releases/download/tts-models/{ModelName}.tar.bz2", archive, "正在下载语音模型", ct, progress).ConfigureAwait(false);
                var start = new ProcessStartInfo("tar") { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
                foreach (var arg in new[] { "-xf", archive, "-C", staging }) start.ArgumentList.Add(arg);
                using var process = Process.Start(start) ?? throw new IOException("无法启动模型解压工具 tar。");
                var stderr = process.StandardError.ReadToEndAsync(ct);
                var stdout = process.StandardOutput.ReadToEndAsync(ct);
                try { await process.WaitForExitAsync(ct).ConfigureAwait(false); }
                catch { if (!process.HasExited) process.Kill(entireProcessTree: true); throw; }
                await stdout.ConfigureAwait(false);
                var error = await stderr.ConfigureAwait(false);
                if (process.ExitCode != 0) throw new IOException("解压语音模型失败：" + error);
                foreach (var name in new[] { ModelFile, "tokens.txt" })
                    File.Move(Path.Combine(staging, ModelName, name), Path.Combine(ModelDirectory, name), overwrite: true);
                Directory.Move(Path.Combine(staging, ModelName, "espeak-ng-data"), Path.Combine(ModelDirectory, "espeak-ng-data"));
            }
            finally { Directory.Delete(staging, recursive: true); }
        }
    }

    private static async Task DownloadAsync(string url, string path, string description, CancellationToken ct, Action<string>? progress)
    {
        using var response = await download.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
        await using var input = await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
        await using var output = File.Create(path);
        var buffer = new byte[81920];
        long total = 0, lastReported = -1;
        while (true)
        {
            var count = await input.ReadAsync(buffer, ct).ConfigureAwait(false);
            if (count == 0) break;
            await output.WriteAsync(buffer.AsMemory(0, count), ct).ConfigureAwait(false);
            total += count;
            var megabytes = total / (1024 * 1024);
            if (megabytes != lastReported) { progress?.Invoke($"{description}（{megabytes} MB）"); lastReported = megabytes; }
        }
    }
}
