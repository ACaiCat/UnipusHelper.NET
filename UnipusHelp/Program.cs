using System.Runtime.CompilerServices;
using System.Text;

namespace UnipusHelp;

public static class Program
{
    public static async Task<int> Main(string[] args)
    {
        Console.OutputEncoding = Encoding.UTF8;
        Console.InputEncoding = Encoding.UTF8;
        if (args.Contains("--speech-test"))
        {
            Console.Write("用户 OpenID：");
            var userId = Console.ReadLine() ?? "";
            try
            {
                using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(5));
                var audio = await Speech.SherpaTextToSpeech.Shared.SynthesizeAsync("The Analects of Confucius", timeout.Token, Console.WriteLine);
                using var evaluator = new Speech.SpeechEvaluationClient();
                var kind = args.Contains("--personal") ? Speech.SpeechKind.PersonalStatement : Speech.SpeechKind.Sentence;
                var result = await evaluator.EvaluateAsync(new(userId, "", "The Analects of Confucius", audio, kind), timeout.Token);
                Console.WriteLine($"真实评测成功：总分 {result.Score}，准确度 {result.Accuracy}，完整度 {result.Completeness}，流利度 {result.Fluency}；未提交课程答案。");
                return 0;
            }
            catch (Exception ex) { Console.Error.WriteLine(ex.Message); return 1; }
        }
        if (args.Contains("--tts-test"))
        {
            var index = Array.IndexOf(args, "--tts-test");
            var output = index + 1 < args.Length ? args[index + 1] : "tts-test.wav";
            try
            {
                var audio = await Speech.SherpaTextToSpeech.Shared.SynthesizeAsync("The Analects of Confucius.", CancellationToken.None, Console.WriteLine);
                await File.WriteAllBytesAsync(output, audio.ToWave());
                Console.WriteLine($"TTS 成功：{audio.SampleRate} Hz，{audio.Duration.TotalSeconds:0.00} 秒，{Path.GetFullPath(output)}");
                return 0;
            }
            catch (Exception ex) { Console.Error.WriteLine(ex.Message); return 1; }
        }
        if (args.Contains("--diagnose")) return await Diagnostics.RunAsync();
        if (args.Contains("--help") || args.Contains("-h"))
        {
            Console.WriteLine("UnipusHelp.NET\n  无参数          终端任务管理器\n  --proxy URL     HTTP / SOCKS 代理\n  --demo          离线演示界面\n  --demo --run    无交互离线验证\n  --version       版本信息\nTab 切换控件，方向键选择，Enter 确认，Esc 关闭窗口。");
            return 0;
        }
        if (args.Contains("--version"))
        {
            Console.WriteLine($"UnipusHelp.NET 1.0.0 | Native AOT: {!RuntimeFeature.IsDynamicCodeSupported}");
            return 0;
        }
        var demo = args.Contains("--demo");
        var batch = args.Contains("--run");
        var smoke = args.Contains("--ui-smoke");
        string? proxy = null;
        for (var i = 0; i < args.Length; i++)
        {
            if (args[i] is "--demo" or "--run" or "--ui-smoke") continue;
            if (args[i] == "--proxy" && i + 1 < args.Length && Uri.TryCreate(args[++i], UriKind.Absolute, out var uri) && uri.Scheme is "http" or "https" or "socks4" or "socks4a" or "socks5")
            { proxy = uri.ToString(); continue; }
            Console.Error.WriteLine("参数无效，使用 --help 查看用法。");
            return 2;
        }
        if ((batch || smoke) && !demo) { Console.Error.WriteLine("--run 和 --ui-smoke 仅用于离线演示。"); return 2; }
        var jobs = new List<LearningJob>();
        using var exit = new CancellationTokenSource();
        ConsoleCancelEventHandler cancel = (_, e) => { e.Cancel = true; exit.Cancel(); };
        Console.CancelKeyPress += cancel;
        try
        {
            if (demo)
            {
                var api = new UnipusClient(handler: new DemoHandler { ChoiceType = "multichoice", ExtraType = "discussion" });
                var user = await AuthenticateAsync(api, new("demo", "demo"), exit.Token);
                var courses = await api.GetCoursesAsync(exit.Token);
                jobs.Add(new LearningJob(api, user, courses[0], demo: true));
            }
            if (batch)
            {
                jobs[0].Start();
                await PrintRunAsync(jobs[0], exit.Token);
                return jobs[0].Snapshot().State == "出错" ? 1 : 0;
            }
            if (Console.IsInputRedirected || Console.IsOutputRedirected)
            {
                Console.Error.WriteLine("请在交互式终端打开。离线验证可使用 --demo --run。");
                return 2;
            }
            var previousContext = SynchronizationContext.Current;
            try { TuiApplication.Run(jobs, proxy, exit.Token, smoke, demo); }
            finally { SynchronizationContext.SetSynchronizationContext(previousContext); }
            return 0;
        }
        catch (OperationCanceledException) { return 130; }
        catch (Exception ex) { Console.Error.WriteLine(ex.Message); return 1; }
        finally
        {
            foreach (var job in jobs) job.Stop();
            await Task.WhenAll(jobs.Select(x => x.Execution ?? Task.CompletedTask)).ConfigureAwait(false);
            foreach (var job in jobs) job.Dispose();
            Console.CancelKeyPress -= cancel;
        }
    }

    public static async Task<User> AuthenticateAsync(IUnipusApi api, LoginRequest request, CancellationToken ct)
    {
        var login = await api.LoginAsync(request, ct);
        var info = await api.GetUserInfoAsync(ct);
        return new User(info.Name, info.School, login.OpenId, info.AppUserId, info.SsoId);
    }

    private static async Task PrintRunAsync(LearningJob job, CancellationToken ct)
    {
        var printed = 0;
        while (true)
        {
            var snapshot = job.Snapshot();
            foreach (var log in snapshot.Logs.Skip(printed)) Console.WriteLine(log);
            printed = snapshot.Logs.Length;
            if (job.Execution?.IsCompleted == true) { Console.WriteLine(snapshot.State + " · " + snapshot.Current); return; }
            await Task.Delay(100, ct);
        }
    }
}
