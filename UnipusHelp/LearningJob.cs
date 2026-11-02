using System.Diagnostics;
using UnipusHelp.Speech;

namespace UnipusHelp;

public sealed class LearningJob : IDisposable
{
    private readonly object sync = new();
    private readonly Queue<JobLog> logs = new();
    private readonly CancellationTokenSource stop = new();
    private readonly IUnipusApi client;
    private readonly User user;
    private readonly CourseChoice choice;
    private readonly bool demo;
    private readonly LearningOptions options;
    private readonly ITextToSpeech tts;
    private ISpeechEvaluation? evaluator;
    private readonly bool ownsEvaluator;
    private readonly Queue<long> submissions = new();
    private TaskCompletionSource? resume;
    private string state = "准备就绪";
    private string current = "";
    private int submitted, skipped;
    public Task? Execution { get; private set; }
    public string Label => $"{user.Name} / {choice.Label}";
    public string ShortLabel => $"{user.Name} · {choice.Resource.Name}";
    public string AccountName => user.Name;
    public string ResourceName => choice.Resource.Name;
    public bool IsSameTarget(User candidate, CourseChoice course) => user.OpenId == candidate.OpenId && choice.Resource.Id == course.Resource.Id;

    public LearningJob(IUnipusApi client, User user, CourseChoice choice, bool demo = false, LearningOptions? options = null, ITextToSpeech? tts = null, ISpeechEvaluation? evaluator = null)
    {
        this.client = client;
        this.user = user;
        this.choice = choice;
        this.demo = demo;
        this.options = options ?? new LearningOptions();
        this.tts = tts ?? SherpaTextToSpeech.Shared;
        this.evaluator = evaluator;
        ownsEvaluator = evaluator is null;
        client.OnRequest = Log;
    }

    public (string State, string Current, int Submitted, int Skipped, JobLog[] Logs) Snapshot()
    {
        lock (sync) return (state, current, submitted, skipped, logs.ToArray());
    }
    public void Start()
    {
        if (Execution is not null) return;
        Execution = Task.Run(RunAsync);
    }
    public void Pause(string reason = "用户暂停")
    {
        lock (sync)
        {
            if (Execution is { IsCompleted: true } || stop.IsCancellationRequested) return;
            resume ??= new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            state = "已暂停";
            Log(reason);
        }
    }
    public void Resume()
    {
        lock (sync)
        {
            if (resume is null) return;
            var signal = resume;
            resume = null;
            state = "运行中";
            signal.TrySetResult();
        }
    }
    public void Stop() => stop.Cancel();
    public void Log(string text) => AddLog(text, false);
    private void LogError(string text) => AddLog(text, true);
    private void AddLog(string text, bool isError)
    {
        lock (sync)
        {
            logs.Enqueue(new JobLog(DateTimeOffset.Now, text, isError));
            while (logs.Count > 160) logs.Dequeue();
        }
    }
    private void SetCurrent(string text) { lock (sync) current = text; }
    private async Task CheckpointAsync()
    {
        stop.Token.ThrowIfCancellationRequested();
        Task? wait;
        lock (sync) wait = resume?.Task;
        if (wait is not null) await wait.WaitAsync(stop.Token);
        stop.Token.ThrowIfCancellationRequested();
    }
    private async Task WaitAsync(TimeSpan delay, string reason)
    {
        var deadline = Stopwatch.GetTimestamp() + (long)(delay.TotalSeconds * Stopwatch.Frequency);
        while (true)
        {
            await CheckpointAsync();
            var remaining = TimeSpan.FromSeconds((deadline - Stopwatch.GetTimestamp()) / (double)Stopwatch.Frequency);
            if (remaining <= TimeSpan.Zero) break;
            SetCurrent($"{reason}，剩余 {Math.Ceiling(remaining.TotalSeconds)} 秒");
            await Task.Delay(remaining < TimeSpan.FromMilliseconds(200) ? remaining : TimeSpan.FromMilliseconds(200), stop.Token);
        }
    }

    private async Task RunAsync()
    {
        lock (sync) state = resume is null ? "运行中" : "已暂停";
        try
        {
            var ct = stop.Token;
            if (!IsCourseOpen(choice, DateTimeOffset.UtcNow)) throw new InvalidOperationException("课程尚未开始、已结束或已归档。");
            SetCurrent("正在读取教程与必修策略");
            var resource = await client.GetResourceAsync(choice.Resource.Id, ct);
            var resourceId = resource.ResourceId;
            var courseId = resource.CourseInstanceId;
            var tree = await client.GetCourseTreeAsync(choice.Resource.InstanceId, ct);
            var nodes = Index(tree.Units, x => x.Id, x => x.Children);
            var required = await client.GetRequiredTasksAsync(new(choice.Resource.StrategyId, resourceId), ct);
            var strategies = required.ToDictionary(x => x.UnitId);
            var total = await GetProgressAsync(resourceId, ct);
            foreach (var unit in total.Units)
            {
                await CheckpointAsync();
                var unitId = unit.NodeId;
                if (unit.FinishProgress >= 100 || !strategies.TryGetValue(unitId, out var strategy)) continue;
                SetCurrent(unit.Caption + " " + unit.Name);
                var times = await client.GetTaskTimesAsync(new(courseId, unitId, user.OpenId), ct);
                var progress = await client.GetUnitProgressAsync(new(unitId, resourceId, user.AppUserId, user.SsoId), ct);
                var progressIndex = Index(progress, x => x.NodeId, x => x.Children);
                foreach (var taskId in strategy.RequiredTasks.Distinct())
                {
                    await CheckpointAsync();
                    if (!progressIndex.TryGetValue(taskId, out var taskProgress)) { Skip(taskId, "缺少任务进度"); continue; }
                    if (taskProgress.FinishProgress >= 100) continue;
                    if (!nodes.TryGetValue(taskId, out var node)) { Skip(taskId, "课程目录未找到任务"); continue; }
                    var types = node.Types;
                    if (types.Count == 0) { Skip(taskId, "缺少题型"); continue; }
                    var type = types[0];
                    if (types.Any(t => !Protocol.Supported.Contains(t))) { Skip(taskId, "不支持的题型 " + string.Join(", ", types.Where(t => !Protocol.Supported.Contains(t)))); continue; }
                    if (!times.TryGetValue(taskId, out var timing)) { Skip(taskId, "缺少任务开放时间"); continue; }
                    if (!IsTaskOpen(timing, strategy, DateTimeOffset.UtcNow)) { Skip(taskId, "不在开放时间内"); continue; }
                    await ProcessTaskAsync(courseId, taskId, node, timing, strategy, ct);
                }
            }
            await CheckpointAsync();
            var final = await GetProgressAsync(resourceId, ct);
            var percentage = final.FinishProgress;
            SetCurrent($"服务端必修进度：{percentage:0.##}% · 本次提交 {submitted} · 跳过 {skipped}");
            lock (sync) state = percentage >= 100 ? "已完成" : "执行结束，仍有未完成任务";
            Log(current);
        }
        catch (OperationCanceledException) when (stop.IsCancellationRequested)
        {
            lock (sync) state = "已停止";
            Log("任务已停止；已发出的提交可能已被服务端处理。");
        }
        catch (Exception ex)
        {
            lock (sync) state = "出错";
            SetCurrent(ex.Message);
            LogError(ex is OperationCanceledException ? "请求超时，请检查网络后重新添加任务。" : ex.Message);
        }
    }

    private Task<CourseProgress> GetProgressAsync(string resourceId, CancellationToken ct) => client.GetCourseProgressAsync(new(resourceId, user.AppUserId), ct);

    private async Task ProcessTaskAsync(string courseId, string taskId, CourseNode node, TaskTime timing, UnitStrategy strategy, CancellationToken ct)
    {
        var name = node.Name;
        var type = node.Types[0];
        SetCurrent($"{name} ({type})");
        var multiple = (node.Types.Count > 1 || node.QuestionCount > 1) && !node.Types.All(Protocol.SpeechTypes.Contains);
        SubmitRequest request;
        if (multiple)
        {
            try { request = await PrepareGroupAsync(courseId, taskId, node, ct); }
            catch (InvalidDataException ex) { Skip(taskId, ex.Message); return; }
            catch (NotSupportedException ex) { Skip(taskId, ex.Message); return; }
        }
        else
        {
            if (type == "discussion")
            {
                if (string.IsNullOrWhiteSpace(options.DiscussionReply)) { Skip(taskId, "讨论回复留空"); return; }
                await ReplyToDiscussionAsync(courseId, taskId, name, timing, strategy, ct);
            }
            QuestionAnswer? answer = null;
            IReadOnlyList<RecordedQuestion>? recordings = null;
            if (Protocol.SpeechTypes.Contains(type))
            {
                var questions = await client.GetSpeechQuestionsAsync(new(courseId, taskId, user.OpenId), ct);
                if (questions.Any(q => q.Parts.Any(p => p.Kind == SpeechKind.PersonalStatement)) && string.IsNullOrWhiteSpace(options.OralScript))
                { Skip(taskId, "开放式口语题需要填写自定义口语稿"); return; }
                recordings = await RecordSpeechAsync(courseId, questions, ct);
            }
            else if (!Protocol.Preset.Contains(type))
            {
                var answers = await client.GetAnswersAsync(new(courseId, taskId, user.OpenId), ct);
                if (answers.Count != 1) { Skip(taskId, "答案数量与单题任务不符"); return; }
                answer = answers[0];
            }
            request = new SubmitRequest(courseId, taskId, user.OpenId, type, answer) { Recordings = recordings };
        }
        try { Protocol.ValidateSubmit(request); }
        catch (InvalidDataException ex) { Skip(taskId, ex.Message); return; }
        for (var attempt = 0; attempt < 6; attempt++)
        {
            await CheckpointAsync();
            while (submissions.TryPeek(out var old) && Stopwatch.GetElapsedTime(old).TotalSeconds >= 60) submissions.Dequeue();
            if (submissions.Count >= 5)
                await WaitAsync(TimeSpan.FromSeconds(60) - Stopwatch.GetElapsedTime(submissions.Peek()), "每分钟提交上限，等待");
            await CheckpointAsync();
            if (!IsCourseOpen(choice, DateTimeOffset.UtcNow) || !IsTaskOpen(timing, strategy, DateTimeOffset.UtcNow))
            { Skip(taskId, "等待期间已超过开放时间"); return; }
            SetCurrent($"正在提交 {name}");
            submissions.Enqueue(Stopwatch.GetTimestamp());
            var response = await client.SubmitAsync(request, ct);
            if (response.IsRateLimited)
            {
                Log("服务端限流，等待两分钟后重试。");
                await WaitAsync(demo ? TimeSpan.FromMilliseconds(500) : TimeSpan.FromMinutes(2), "服务端限流");
                continue;
            }
            lock (sync) submitted++;
            var shown = response.RealScore ?? response.Score;
            Log($"已提交：{name} · 得分 {shown?.ToString("0.##") ?? "未返回"}{(response.Passed == false ? " · 未通过" : "")}");
            // Subjective tasks (short-answer and friends) report score_avg 0 even when the server
            // accepted the answer and passed the task; only the server verdict should pause the job.
            var passed = response.Passed ?? (response.Score is null || double.IsNaN(response.Score.Value) ? null : response.Score > 0);
            if ((multiple || (!Protocol.Preset.Contains(type) && type != "oral-personal-state")) && passed == false)
            {
                Pause($"任务 {taskId} 未通过服务端校验，请检查后使用“继续”或“停止”。");
            }
            await CheckpointAsync();
            if (demo) await WaitAsync(TimeSpan.FromMilliseconds(750), "演示任务间隔");
            return;
        }
        throw new InvalidOperationException("服务端连续限流，已停止本任务，请稍后重试。");
    }

    private async Task<SubmitRequest> PrepareGroupAsync(string courseId, string taskId, CourseNode node, CancellationToken ct)
    {
        var scope = new GetAnswersRequest(courseId, taskId, user.OpenId);
        var questions = await client.GetTaskQuestionsAsync(scope, ct);
        if (questions.Count == 0 || (node.QuestionCount > 0 && questions.Count != node.QuestionCount) ||
            questions.Any(q => string.IsNullOrWhiteSpace(q.InstanceId)) || questions.Select(q => q.InstanceId).Distinct().Count() != questions.Count)
            throw new InvalidDataException("题目数量或 ID 与任务不符，未提交整组任务。");
        foreach (var question in questions)
        {
            if (!Protocol.Supported.Contains(question.Type) || Protocol.Preset.Contains(question.Type))
                throw new NotSupportedException("多题目任务包含尚未支持的题型：" + question.Type);
            if (question.Speech?.Parts.Any(p => p.Kind == SpeechKind.PersonalStatement) == true && string.IsNullOrWhiteSpace(options.OralScript))
                throw new InvalidDataException("开放式口语题需要填写自定义口语稿");
        }
        var answers = questions.Any(q => q.Speech is null) ? await client.GetAnswersAsync(scope, ct) : [];
        if (answers.Select(a => a.InstanceId).Distinct().Count() != answers.Count ||
            answers.Any(a => !questions.Any(q => q.InstanceId == a.InstanceId)))
            throw new InvalidDataException("答案 ID 重复或不属于本任务，未提交整组任务。");
        var prepared = new Dictionary<string, QuestionSubmission>(StringComparer.Ordinal);
        // Validate every text answer before synthesizing or uploading any speech.
        foreach (var question in questions.Where(q => q.Speech is null))
        {
            var answer = answers.SingleOrDefault(a => a.InstanceId == question.InstanceId)
                ?? throw new InvalidDataException("任务中有题目缺少答案，未提交整组任务。");
            Protocol.ValidateSubmit(new(courseId, taskId, user.OpenId, question.Type, answer));
            prepared.Add(question.InstanceId, new(question.Type, answer));
        }
        foreach (var question in questions.Where(q => q.Speech is not null))
        {
            var recorded = await RecordSpeechAsync(courseId, [question.Speech!], ct);
            prepared.Add(question.InstanceId, new(question.Type, Recording: recorded.Single()));
        }
        return new SubmitRequest(courseId, taskId, user.OpenId, node.Types[0], null)
        { Questions = questions.Select(q => prepared[q.InstanceId]).ToArray() };
    }

    private void Skip(string taskId, string reason)
    {
        lock (sync) skipped++;
        Log($"跳过 {taskId}：{reason}");
    }

    private async Task<IReadOnlyList<RecordedQuestion>> RecordSpeechAsync(string courseId, IReadOnlyList<SpeechQuestion> questions, CancellationToken ct)
    {
        if (questions.Count == 0) throw new InvalidDataException("没有读取到口语题目。");
        var result = new List<RecordedQuestion>();
        foreach (var question in questions)
        {
            var recordings = new List<SpeechRecording>();
            for (var index = 0; index < question.Parts.Count; index++)
            {
                await CheckpointAsync();
                var part = question.Parts[index];
                var text = part.Kind == SpeechKind.PersonalStatement ? options.OralScript.Trim() : part.ReferenceText;
                if (string.IsNullOrWhiteSpace(text)) throw new InvalidDataException("口语朗读文本为空，未提交任务。");
                var audio = await tts.SynthesizeAsync(text, ct, SetCurrent);
                if (question.Type == "video-dub" && part.MaximumMilliseconds > 0)
                {
                    var speed = 1.0f;
                    for (var retry = 0; retry < 3 && audio.Duration.TotalMilliseconds > part.MaximumMilliseconds && speed < 2.0f; retry++)
                    {
                        await CheckpointAsync();
                        speed = Math.Min(2.0f, speed * (float)(audio.Duration.TotalMilliseconds / part.MaximumMilliseconds) * 1.05f);
                        SetCurrent($"正在调整配音语速（{speed:0.##} 倍）");
                        audio = await tts.SynthesizeAsync(text, ct, SetCurrent, speed);
                    }
                }
                if (part.MaximumMilliseconds > 0 && audio.Duration.TotalMilliseconds > part.MaximumMilliseconds)
                    throw new InvalidDataException(question.Type == "video-dub" ? "视频配音音频超过片段时限，已阻止提交。" : "口语稿合成音频超过本题录音时限，请缩短口语稿。");
                await CheckpointAsync();
                SetCurrent($"正在评测口语 {index + 1}/{question.Parts.Count}");
                evaluator ??= new SpeechEvaluationClient();
                var evaluation = await evaluator.EvaluateAsync(new(user.OpenId, choice.ClassId, text, audio, part.Kind), ct);
                await CheckpointAsync();
                var saved = await client.UploadRecordingAsync(new(evaluation.SourceUrl, courseId, user.OpenId), ct);
                recordings.Add(new SpeechRecording(saved.Url, text, part.Kind, evaluation));
                Log($"口语 {index + 1}/{question.Parts.Count} 已保存 · 真实评分 {evaluation.Score?.ToString() ?? "不适用（开放式口述）"}");
            }
            result.Add(new RecordedQuestion(question.InstanceId, question.Type, recordings, question.Versions) { ReplyType = question.ReplyType });
        }
        return result;
    }

    private async Task ReplyToDiscussionAsync(string courseId, string taskId, string name, TaskTime timing, UnitStrategy strategy, CancellationToken ct)
    {
        var scope = new DiscussionScope(courseId, taskId, choice.ClassId, choice.CurriculaId);
        await CheckpointAsync();
        var page = await client.GetDiscussionTopicsAsync(new(scope), ct);
        if (page.Topics.Count == 0)
        {
            await CheckDiscussionWriteAsync();
            var title = string.IsNullOrWhiteSpace(name) ? "Discussion" : name;
            await client.CreateDiscussionTopicAsync(new(scope, title, title), ct);
            page = await client.GetDiscussionTopicsAsync(new(scope), ct);
        }
        var topic = page.Topics.FirstOrDefault(x => x.IsOwner) ?? page.Topics.FirstOrDefault()
            ?? throw new InvalidDataException("未找到可回复的讨论主题，未标记任务完成。");
        if (await HasOwnReplyAsync(topic.Id))
        {
            Log($"讨论 {name} 已有本人回复，跳过重复发帖。");
            return;
        }
        await CheckDiscussionWriteAsync();
        var result = await client.PostDiscussionReplyAsync(new(topic.Id, options.DiscussionReply), ct);
        if (result.AlreadyExists && !await HasOwnReplyAsync(topic.Id))
            throw new InvalidDataException("服务端提示重复回复，但未确认本人回复；未标记任务完成。");
        Log($"已发表讨论回复：{name}");

        async Task CheckDiscussionWriteAsync()
        {
            await CheckpointAsync();
            if (!IsCourseOpen(choice, DateTimeOffset.UtcNow) || !IsTaskOpen(timing, strategy, DateTimeOffset.UtcNow))
                throw new InvalidOperationException("讨论任务已超过开放时间，未发表回复。");
        }
        async Task<bool> HasOwnReplyAsync(long topicId)
        {
            const int pageSize = 20;
            for (var number = 1; number <= 50; number++)
            {
                await CheckpointAsync();
                var replies = await client.GetDiscussionRepliesAsync(new(topicId, number, pageSize), ct);
                if (replies.Replies.Any(x => x.IsOwner)) return true;
                if (replies.Replies.Count == 0 || number * pageSize >= replies.Total) return false;
            }
            throw new InvalidOperationException("讨论回复过多，无法确认是否已有回复，已停止以避免重复发帖。");
        }
    }
    public static Dictionary<string, T> Index<T>(IEnumerable<T> root, Func<T, string> getId, Func<T, IReadOnlyList<T>> getChildren)
    {
        var result = new Dictionary<string, T>(StringComparer.Ordinal);
        void Visit(T node)
        {
            var id = getId(node);
            if (!string.IsNullOrEmpty(id)) result[id] = node;
            foreach (var child in getChildren(node)) Visit(child);
        }
        foreach (var node in root) Visit(node);
        return result;
    }
    public static bool IsCourseOpen(CourseChoice course, DateTimeOffset now) => !course.Archived && InWindow(course.Start, course.End, now.ToUnixTimeMilliseconds());
    public static bool IsTaskOpen(TaskTime task, UnitStrategy unit, DateTimeOffset now) =>
        InWindow(task.Start, task.End, now.ToUnixTimeSeconds()) &&
        InWindow(unit.StudyStartTime, unit.StudyEndTime, now.ToUnixTimeMilliseconds());
    private static bool InWindow(long start, long end, long now) => (start == 0 || now >= start) && (end == 0 || now <= end);
    public void Dispose() { client.Dispose(); if (ownsEvaluator) evaluator?.Dispose(); stop.Dispose(); }
}
