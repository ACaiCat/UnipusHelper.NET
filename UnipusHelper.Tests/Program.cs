using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using UnipusHelper;
using UnipusHelper.Speech;
using App = UnipusHelper.Program;

var passed = 0;
await Test("Captcha login retries only 1506 and carries verification fields", async () =>
{
    using var handler = new CaptchaLoginHandler();
    using var api = new UnipusClient(handler: handler);
    var solves = 0;
    api.SolveLoginCaptchaAsync = _ =>
    {
        solves++;
        return Task.FromResult(new JsonObject { ["captchaCode"] = "Ab12", ["encodeCaptha"] = "encoded", ["username"] = "must-not-override" });
    };
    var login = await api.LoginAsync(new("test", "secret"), default);
    Equal("openid", login.OpenId);
    Equal(1, solves);
    Equal(2, handler.Bodies.Count);
    Equal("test", handler.Bodies[1]["username"].Text());
    Equal("secret", handler.Bodies[1]["password"].Text());
    Equal("Ab12", handler.Bodies[1]["captchaCode"].Text());
    Equal("encoded", handler.Bodies[1]["encodeCaptha"].Text());
    using var wrongPassword = new CaptchaLoginHandler { ErrorCode = 1001 };
    using var rejected = new UnipusClient(handler: wrongPassword) { SolveLoginCaptchaAsync = _ => throw new Exception("Should not prompt") };
    await ThrowsAsync<UnipusApiException>(() => rejected.LoginAsync(new("test", "bad"), default));
    using var repeated = new CaptchaLoginHandler { AlwaysFail = true };
    using var limited = new UnipusClient(handler: repeated) { SolveLoginCaptchaAsync = api.SolveLoginCaptchaAsync };
    await ThrowsAsync<UnipusApiException>(() => limited.LoginAsync(new("test", "bad"), default));
    Equal(4, repeated.Bodies.Count);
    using var cancelled = new UnipusClient(handler: new CaptchaLoginHandler())
    {
        SolveLoginCaptchaAsync = _ => Task.FromCanceled<JsonObject>(new CancellationToken(true))
    };
    await ThrowsAsync<OperationCanceledException>(() => cancelled.LoginAsync(new("test", "secret"), default));
});
await Test("Browser captcha protects callbacks, proxies fixed routes and returns user result", async () =>
{
    using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
    var opened = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
    var proxied = new List<string>();
    var solve = LoginCaptchaBrowser.SolveAsync((route, body, _) =>
    {
        proxied.Add(route);
        return Task.FromResult<JsonNode>(new JsonObject { ["code"] = "0" });
    }, url => opened.SetResult(url), timeout.Token, _ => { });
    var url = await opened.Task;
    using var browser = new HttpClient();
    var page = await browser.GetStringAsync(url, timeout.Token);
    Assert(page.Contains("ssoImgCaptcha"), "image fallback missing");
    using var forbidden = await browser.PostAsync(url + "complete", new StringContent("{}"), timeout.Token);
    Equal(System.Net.HttpStatusCode.Forbidden, forbidden.StatusCode);
    using var register = new HttpRequestMessage(HttpMethod.Get, url + "register");
    register.Headers.Referrer = new Uri(url);
    using var registered = await browser.SendAsync(register, timeout.Token);
    registered.EnsureSuccessStatusCode();
    Equal("sso/5.0/sso/register", proxied.Single());
    using var image = new HttpRequestMessage(HttpMethod.Post, new Uri(new Uri(url), "/sso/4.0/sso/cip/image_captcha2"));
    image.Headers.Referrer = new Uri(url);
    image.Content = new StringContent("{\"param\":\"encrypted\"}", Encoding.UTF8, "application/json");
    using var imageResponse = await browser.SendAsync(image, timeout.Token);
    imageResponse.EnsureSuccessStatusCode();
    Equal("sso/4.0/sso/cip/image_captcha2", proxied.Last());
    using var callback = new HttpRequestMessage(HttpMethod.Post, url + "complete");
    callback.Headers.Referrer = new Uri(url);
    callback.Headers.Add("Origin", new Uri(url).GetLeftPart(UriPartial.Authority));
    callback.Content = new StringContent("{\"challenge\":\"c\",\"validate\":\"v\",\"seccode\":\"s\"}", Encoding.UTF8, "application/json");
    using var done = await browser.SendAsync(callback, timeout.Token);
    done.EnsureSuccessStatusCode();
    var solution = await solve;
    Equal("v", solution["validate"].Text());
    using var cancellation = new CancellationTokenSource();
    var waiting = LoginCaptchaBrowser.SolveAsync((_, _, _) => throw new Exception(), _ => { }, cancellation.Token, _ => { });
    cancellation.Cancel();
    await ThrowsAsync<OperationCanceledException>(async () => await waiting);
});
await Test("AES ECB / zero padding / plaintext passthrough", () =>
{
    const string plain = "[{\"id\":1,\"text\":\"中文\"}]";
    var bytes = Encoding.UTF8.GetBytes(plain);
    Array.Resize(ref bytes, ((bytes.Length + 15) / 16) * 16);
    using var aes = Aes.Create();
    aes.Key = Encoding.UTF8.GetBytes("1a2b3c4d20250627");
    var encrypted = aes.EncryptEcb(bytes, PaddingMode.None);
    Equal(plain, Protocol.Decrypt("unipus." + Convert.ToHexString(encrypted), "20250627"));
    Equal(plain, Protocol.Decrypt(plain, ""));
    Throws<FormatException>(() => Protocol.Decrypt("unipus.not-hex", "20250627"));
    return Task.CompletedTask;
});
await Test("JWT claims, millisecond expiry and HMAC signature", () =>
{
    var now = DateTimeOffset.FromUnixTimeMilliseconds(1700000000000);
    var token = Protocol.AuthToken("test-openid", now).Split('.');
    byte[] Decode(string s) => Convert.FromBase64String(s.Replace('-', '+').Replace('_', '/').PadRight((s.Length + 3) / 4 * 4, '='));
    var claims = Json.Parse(Encoding.UTF8.GetString(Decode(token[1])));
    Equal("test-openid", claims["open_id"].Text());
    Equal(1731536000000L, claims["exp"].Integer());
    Equal("edx.unipus.cn", claims["aud"].Text());
    var expected = HMACSHA256.HashData(Encoding.UTF8.GetBytes("a824b379f126b8b7aa5e33dee83fb0a05aa7462c"), Encoding.UTF8.GetBytes(token[0] + "." + token[1]));
    Assert(CryptographicOperations.FixedTimeEquals(expected, Decode(token[2])), "JWT signature differs");
    return Task.CompletedTask;
});
await Test("HTML, entities, unsupported characters and type aliases", () =>
{
    Equal("Hello & world !", Protocol.CleanText("<p>Hello&nbsp;&amp; <b>world</b></p><script>bad()</script>!😀"));
    Equal("short-answer", Protocol.NormalizeType("SHORT_ANSWER"));
    Equal("multi-file-upload", Protocol.NormalizeType("multiFileUpload"));
    return Task.CompletedTask;
});
await Test("Open windows use correct units and inclusive boundaries", () =>
{
    var now = DateTimeOffset.FromUnixTimeSeconds(1700000000);
    var unit = new UnitStrategy("unit", [], now.ToUnixTimeMilliseconds(), now.ToUnixTimeMilliseconds() + 1000);
    Assert(LearningJob.IsTaskOpen(new(now.ToUnixTimeSeconds(), now.ToUnixTimeSeconds() + 1), unit, now), "boundary should be open");
    Assert(!LearningJob.IsTaskOpen(new(0, now.ToUnixTimeSeconds() - 1), unit, now), "expired task should close");
    Assert(!LearningJob.IsTaskOpen(new(now.ToUnixTimeSeconds() + 1, 0), unit, now), "future task should close");
    return Task.CompletedTask;
});
await Test("Typed RPC methods map course, strategies, timings and answer models", async () =>
{
    using IUnipusApi api = new UnipusClient(handler: new DemoHandler());
    var user = await App.AuthenticateAsync(api, new("test", "password"), default);
    Equal("demo-openid", user.OpenId);
    var courses = await api.GetCoursesAsync(default);
    Equal("20000000001", courses[0].Resource.Id);
    var resource = await api.GetResourceAsync(courses[0].Resource.Id, default);
    var tree = await api.GetCourseTreeAsync(resource.CourseInstanceId, default);
    Equal(4, tree.Units[0].Children.Count);
    var strategy = await api.GetRequiredTasksAsync(new(123, resource.ResourceId), default);
    Equal(4, strategy[0].RequiredTasks.Count);
    var times = await api.GetTaskTimesAsync(new(resource.CourseInstanceId, "unit-1", user.OpenId), default);
    Equal(new TaskTime(0, 0), times["task-read"]);
    var answers = await api.GetAnswersAsync(new(resource.CourseInstanceId, "task-choice", user.OpenId), default);
    Equal("9876543210", answers[0].InstanceId);
    Equal("A", answers[0].Answers[0][0]);
    var info = await api.GetTaskInfoAsync(new(resource.CourseInstanceId, "task-choice", "1", user.OpenId), default);
    Equal(1, info.Questions[0].QuestionType);
});
await Test("Course progress accepts absent or null percentages and preserves recorded values", async () =>
{
    foreach (var total in new[] { "{}", "{\"finishProgress\":null}", "{\"finishProgress\":20.9}" })
    {
        using var api = new UnipusClient(handler: new DemoHandler
        {
            FailurePath = "totalAndUnitSituation",
            FailureBody = $$$"""{"code":1,"value":{"totalDetail":{{{total}}},"unitList":[{"nodeId":"new-unit"},{"nodeId":"null-unit","finishProgress":null},{"nodeId":"done-unit","finishProgress":100}]}}"""
        });
        var progress = await api.GetCourseProgressAsync(new("resource", "user"), default);
        Equal(total.Contains("20.9") ? 20.9 : 0, progress.FinishProgress);
        Equal(0d, progress.Units[0].FinishProgress);
        Equal(0d, progress.Units[1].FinishProgress);
        Equal(100d, progress.Units[2].FinishProgress);
    }
});
await Test("Empty total detail allows a new course job to process its tasks", async () =>
{
    var handler = new DemoHandler
    {
        FailurePath = "totalAndUnitSituation",
        FailureBody = """{"code":1,"value":{"totalDetail":{},"unitList":[{"nodeId":"unit-1","caption":"Unit 1","name":"Learning English"}]}}"""
    };
    using var job = await CreateJob(handler);
    job.Start();
    await job.Execution!.WaitAsync(TimeSpan.FromSeconds(10));
    Equal(3, handler.SubmitCalls);
    Equal("执行结束，仍有未完成任务", job.Snapshot().State);
    Assert(job.Snapshot().Current.Contains("0%"), "missing server progress must not imply completion");
});
await Test("Submission wire payload, preset request and empty-answer rejection", async () =>
{
    var handler = new DemoHandler();
    using IUnipusApi api = new UnipusClient(handler: handler);
    var answer = new QuestionAnswer("12345678901", [["<b>A</b>"]], ["<p>Short answer</p>"], "<p>Essay</p>");
    await api.SubmitAsync(new("course", "task-choice", "openid", "single-choice", answer), default);
    var wire = handler.SubmittedBodies[0];
    Equal(1L, wire["submitType"].Integer());
    Equal("12345678901", wire["quesDatas"]![0]!["instanceId"].Text());
    var inner = wire["quesDatas"]![0]!.Need("answer").Unwrap();
    Equal("A", inner["children"]![0]!["value"]![0].Text());
    Equal(1, wire.Need("thirdPartyJudges").Unwrap().Array().Count);
    await api.SubmitAsync(new("course", "task-read", "openid", "rich-text-read", null), default);
    Equal(0, handler.SubmittedBodies[1].Need("quesDatas").Array().Count);
    Equal(2L, handler.SubmittedBodies[1]["submitType"].Integer());
    Equal("Short answer", Protocol.CleanText(Protocol.ExtractAnswers(answer, "short-answer")[0][0]));
    Equal("Essay", Protocol.CleanText(Protocol.ExtractAnswers(answer, "writing")[0][0]));
    await ThrowsAsync<InvalidDataException>(() => api.SubmitAsync(new("c", "t", "o", "single-choice", answer with { Answers = [] }), default));
    await ThrowsAsync<NotSupportedException>(() => api.SubmitAsync(new("c", "t", "o", "multi-file-upload", null), default));
    Equal(2, handler.SubmitCalls);
});
await Test("Full run skips unsupported tasks and reports incomplete server progress", async () =>
{
    var handler = new DemoHandler();
    using var job = await CreateJob(handler);
    job.Start();
    await job.Execution!.WaitAsync(TimeSpan.FromSeconds(10));
    var state = job.Snapshot();
    Equal(3, state.Submitted);
    Equal(1, state.Skipped);
    Equal("执行结束，仍有未完成任务", state.State);
    Assert(state.Current.Contains("75%"), "server progress must not be replaced by local completed count");
});
await Test("Pause blocks submissions, resume continues, stop cancels", async () =>
{
    var handler = new DemoHandler();
    using var job = await CreateJob(handler);
    job.Start();
    job.Pause();
    await Task.Delay(300);
    Equal(0, handler.SubmitCalls);
    job.Resume();
    await Until(() => handler.SubmitCalls > 0);
    job.Stop();
    await job.Execution!.WaitAsync(TimeSpan.FromSeconds(2));
    Equal("已停止", job.Snapshot().State);
});
await Test("Zero score pauses before the next task; stopping unblocks it", async () =>
{
    var handler = new DemoHandler { ZeroScore = true };
    using var job = await CreateJob(handler);
    job.Start();
    await Until(() => job.Snapshot().State == "已暂停");
    Equal(2, handler.SubmitCalls);
    await Task.Delay(200);
    Equal(2, handler.SubmitCalls);
    job.Stop();
    await job.Execution!.WaitAsync(TimeSpan.FromSeconds(2));
    Equal("已停止", job.Snapshot().State);
});
await Test("Zero average with a passing verdict does not pause (subjective scoring)", async () =>
{
    var handler = new DemoHandler { ZeroScore = true, ZeroAverageButPass = true };
    using var job = await CreateJob(handler);
    job.Start();
    await job.Execution!.WaitAsync(TimeSpan.FromSeconds(10));
    Equal(3, handler.SubmitCalls);
    Assert(job.Snapshot().State != "已暂停", "a passing server verdict must not be overridden by score_avg");
    Assert(job.Snapshot().Logs.Any(x => x.Message.Contains("得分 100")), "real_score_pct should be shown as the score");
});
await Test("Empty answer is skipped without submitting an empty payload", async () =>
{
    var handler = new DemoHandler { EmptyAnswer = true };
    using var job = await CreateJob(handler);
    job.Start();
    await job.Execution!.WaitAsync(TimeSpan.FromSeconds(10));
    Equal(1, handler.SubmitCalls);
    Equal(3, job.Snapshot().Skipped);
});
await Test("Server rate limit retries; transport failure never repeats POST", async () =>
{
    var handler = new DemoHandler { RateLimitResponses = 1 };
    using (var job = await CreateJob(handler))
    {
        job.Start();
        await job.Execution!.WaitAsync(TimeSpan.FromSeconds(10));
        Equal(4, handler.SubmitCalls);
        Equal(3, job.Snapshot().Submitted);
    }
    var broken = new DemoHandler { FailSubmit = true };
    using (var job = await CreateJob(broken))
    {
        job.Start();
        await job.Execution!.WaitAsync(TimeSpan.FromSeconds(5));
        Equal(1, broken.SubmitCalls);
        Equal("出错", job.Snapshot().State);
    }
});
await Test("Runtime API errors preserve operation, code, message and error severity", async () =>
{
    var handler = new DemoHandler { FailurePath = "courseStudyStrategy/detail", FailureBody = """{"code":2,"msg":"","message":"任务不可访问"}""" };
    using var job = await CreateJob(handler);
    job.Start();
    await job.Execution!.WaitAsync(TimeSpan.FromSeconds(5));
    var snapshot = job.Snapshot();
    Equal("出错", snapshot.State);
    Assert(snapshot.Current.Contains("读取必修策略失败（2）：任务不可访问"), "missing endpoint context or server message");
    Assert(snapshot.Logs.Last().IsError, "error severity must reach the TUI");
    Equal(0, handler.SubmitCalls);
});
await Test("Catalog preserves colon/plus and uses list instance ID instead of detail ID", async () =>
{
    var handler = new DemoHandler { EnforceCatalogPath = true, DetailInstanceId = "course-v2:detail+different+2026" };
    using var job = await CreateJob(handler);
    job.Start();
    await job.Execution!.WaitAsync(TimeSpan.FromSeconds(10));
    Equal("执行结束，仍有未完成任务", job.Snapshot().State);
    Equal(3, handler.SubmitCalls);
});
await Test("Jobs do not capture the UI synchronization context", async () =>
{
    using var job = await CreateJob(new DemoHandler());
    var previous = SynchronizationContext.Current;
    try
    {
        SynchronizationContext.SetSynchronizationContext(new RejectingSynchronizationContext());
        job.Start();
    }
    finally { SynchronizationContext.SetSynchronizationContext(previous); }
    await job.Execution!.WaitAsync(TimeSpan.FromSeconds(10));
    Equal(3, job.Snapshot().Submitted);
});
await Test("Merged multichoice behavior preserves all selected answers", async () =>
{
    var handler = new DemoHandler { ChoiceType = "multichoice" };
    using var job = await CreateJob(handler);
    job.Start();
    await job.Execution!.WaitAsync(TimeSpan.FromSeconds(10));
    Equal(3, job.Snapshot().Submitted);
    var body = handler.SubmittedBodies.Single(x => x["groupId"].Text() == "task-choice");
    var values = body["quesDatas"]![0]!.Need("answer").Unwrap()["children"]![0]!.Need("value").Array();
    Equal(2, values.Count);
    Equal("A", values[0].Text());
    Equal("C", values[1].Text());
    Equal("A,C", body.Need("thirdPartyJudges").Unwrap()[0]!["value"].Text());
});
await Test("Discussion uses the configured reply and posts before marking complete", async () =>
{
    var handler = new DemoHandler { ExtraType = "discussion" };
    using var job = await CreateJob(handler, new LearningOptions("My discussion reply."));
    job.Start();
    await job.Execution!.WaitAsync(TimeSpan.FromSeconds(10));
    Equal("已完成", job.Snapshot().State);
    Equal(4, job.Snapshot().Submitted);
    Equal("My discussion reply.", handler.DiscussionReplies.Single());
    Equal(1, handler.DiscussionPostCalls);
    var body = handler.SubmittedBodies.Last();
    Equal("task-discussion", body["groupId"].Text());
    Equal(2L, body["submitType"].Integer());
    Equal(0, body.Need("quesDatas").Array().Count);
    Assert(handler.RequestOrder.FindIndex(x => x.EndsWith("/ureply/add")) < handler.RequestOrder.FindLastIndex(x => x.EndsWith("/newExploration/submit")), "completion must follow the reply");
    Equal("demo-class", handler.DiscussionRequests[0]["classId"].Text());
    Equal("123", handler.DiscussionRequests[0]["curriculaId"].Text());
});
await Test("Discussion uses custom text and creates a missing topic", async () =>
{
    var handler = new DemoHandler { ExtraType = "discussion", NoDiscussionTopic = true };
    const string reply = "My reply & 中文 <literal>";
    using var job = await CreateJob(handler, new LearningOptions(reply));
    job.Start();
    await job.Execution!.WaitAsync(TimeSpan.FromSeconds(10));
    Equal("已完成", job.Snapshot().State);
    Equal(reply, handler.DiscussionReplies.Single());
    Equal(1, handler.CreatedTopics);
});
await Test("Discussion checks later reply pages and does not repost", async () =>
{
    var handler = new DemoHandler { ExtraType = "discussion", AlreadyReplied = true, OwnReplyPage = 2 };
    using var job = await CreateJob(handler, new LearningOptions("My discussion reply."));
    job.Start();
    await job.Execution!.WaitAsync(TimeSpan.FromSeconds(10));
    Equal("已完成", job.Snapshot().State);
    Equal(0, handler.DiscussionPostCalls);
    Assert(handler.DiscussionRequests.Any(x => x["pageNum"].Integer() == 2), "later reply pages were not checked");
});
await Test("Failed discussion posting does not mark the task complete or retry the POST", async () =>
{
    var handler = new DemoHandler { ExtraType = "discussion", FailDiscussionReply = true };
    using var job = await CreateJob(handler, new LearningOptions("My discussion reply."));
    job.Start();
    await job.Execution!.WaitAsync(TimeSpan.FromSeconds(10));
    Equal("出错", job.Snapshot().State);
    Equal(1, handler.DiscussionPostCalls);
    Equal(3, handler.SubmitCalls);
    Assert(!handler.SubmittedBodies.Any(x => x["groupId"].Text() == "task-discussion"), "failed discussion must remain incomplete");
});
await Test("Default and whitespace discussion replies skip without posting or marking complete", async () =>
{
    Equal("", new LearningOptions().DiscussionReply);
    foreach (var options in new[] { new LearningOptions(), new LearningOptions("   ") })
    {
        var handler = new DemoHandler { ExtraType = "discussion" };
        using var job = await CreateJob(handler, options);
        job.Start();
        await job.Execution!.WaitAsync(TimeSpan.FromSeconds(10));
        Equal(3, handler.SubmitCalls);
        Equal(1, job.Snapshot().Skipped);
        Equal(0, handler.DiscussionPostCalls);
        Equal(0, handler.CreatedTopics);
        Assert(!handler.RequestOrder.Any(x => x.StartsWith("/api/bbs/")), "blank reply should skip all discussion operations");
    }
});
await Test("Speech PCM conversion and WAV header preserve duration", () =>
{
    var pcm = SherpaTextToSpeech.ConvertToPcm(Enumerable.Repeat(0.5f, 22050).ToArray(), 22050);
    Equal(16000, pcm.SampleRate);
    Equal(32000, pcm.Samples.Length);
    Equal(1.0, pcm.Duration.TotalSeconds);
    var wav = pcm.ToWave();
    Equal("RIFF", Encoding.ASCII.GetString(wav, 0, 4));
    Equal("WAVE", Encoding.ASCII.GetString(wav, 8, 4));
    Equal(32044, wav.Length);
    return Task.CompletedTask;
});
await Test("Speech assessment keeps real scores and permits unscored personal ASR", () =>
{
    var response = Json.Parse("""{"code":0,"finalResult":{"url":"https://audio.example/test.mp3","result":{"total":36,"accuracy":19,"completeness":25,"fluency":{"overall":90},"details":[{"word":"the","score":88}]}}}""");
    var parsed = SpeechEvaluationClient.ParseResult(response, SpeechKind.Sentence);
    Equal(36d, parsed.Score!.Value);
    Equal(88d, parsed.Details[0].Score);
    response["finalResult"]!["result"]!.AsObject().Remove("total");
    Equal<double?>(null, SpeechEvaluationClient.ParseResult(response, SpeechKind.PersonalStatement).Score);
    Throws<InvalidDataException>(() => SpeechEvaluationClient.ParseResult(response, SpeechKind.Sentence));
    Equal(9, SpeechEvaluationClient.QuestionType(SpeechKind.PersonalStatement));
    return Task.CompletedTask;
});
await Test("Multiple speech children synthesize, assess, archive and submit real records", async () =>
{
    var handler = new DemoHandler { ExtraType = "oral-sentence" };
    var speech = new FakeSpeech();
    using var job = await CreateJob(handler, tts: speech, evaluator: speech);
    job.Start();
    await job.Execution!.WaitAsync(TimeSpan.FromSeconds(10));
    Equal("已完成", job.Snapshot().State);
    Equal(2, speech.Texts.Count);
    Equal(2, handler.UploadedRecordings);
    var body = handler.SubmittedBodies.Last();
    var children = body["quesDatas"]![0]!.Need("answer").Unwrap().Need("children").Array();
    Equal(2, children.Count);
    var record = children[0]!.Need("record");
    Equal(36d, record["score"].Number());
    Equal(0.36, record["specific_scores"]!["total"].Number());
    Equal(0.9, record["specific_scores"]!["fluency"].Number());
    Equal(88d, record["recordDetail"]!["details"]![0]!["score"].Number());
    Equal("https://course.media.unipus.cn/test/recording-1.mp3", record["url"].Text());
    var judges = body.Need("thirdPartyJudges").Unwrap().Array();
    Equal(2, judges.Count);
    Equal("basic-oral", judges[0]!["question_type"].Text());
    Equal(138359L, judges[0]!["versions"]!["course"].Integer());
});
await Test("Personal statements use only custom script; missing script skips without audio or submit", async () =>
{
    foreach (var script in new[] { "", "My own oral script." })
    {
        var handler = new DemoHandler { ExtraType = "oral-personal-state", SpeechPartCount = 1 };
        var speech = new FakeSpeech();
        using var job = await CreateJob(handler, new LearningOptions(OralScript: script), speech, speech);
        job.Start();
        await job.Execution!.WaitAsync(TimeSpan.FromSeconds(10));
        Equal(script.Length == 0 ? 3 : 4, handler.SubmitCalls);
        Equal(script.Length == 0 ? 0 : 1, speech.Texts.Count);
        if (script.Length > 0)
        {
            Equal(script, speech.Texts.Single());
            var judge = handler.SubmittedBodies.Last().Need("thirdPartyJudges").Unwrap()[0]!;
            Equal("oral-personal-state", judge["question_type"].Text());
            Equal("oral-personal-state", judge["reply_type"].Text());
        }
    }
});
await Test("Failed speech assessment never submits incomplete recordings", async () =>
{
    var handler = new DemoHandler { ExtraType = "oral-sentence" };
    var speech = new FakeSpeech { Fail = true };
    using var job = await CreateJob(handler, tts: speech, evaluator: speech);
    job.Start();
    await job.Execution!.WaitAsync(TimeSpan.FromSeconds(10));
    Equal("出错", job.Snapshot().State);
    Equal(3, handler.SubmitCalls);
    Equal(0, handler.UploadedRecordings);
    Assert(job.Snapshot().Logs.Any(x => x.IsError), "speech failure must be red");
});
await Test("Short-answer children may omit answers and use child analysis", async () =>
{
    const string fixture = """[{"id":"short-instance","answer":"{\"children\":[{},{}]}","analysis":"{\"children\":[{\"analysis\":\"<p>First reference.</p>\"},{\"analysis\":\"Second reference.\"}],\"analysis\":\"Writing reference.\"}"}]""";
    var handler = new DemoHandler { ChoiceType = "short-answer", AnswerDataOverride = fixture };
    using var job = await CreateJob(handler);
    job.Start();
    await job.Execution!.WaitAsync(TimeSpan.FromSeconds(10));
    Equal(3, handler.SubmitCalls);
    Assert(job.Snapshot().State != "出错", "missing unused answers must not fail the job");
    var body = handler.SubmittedBodies.Single(x => x["groupId"].Text() == "task-choice");
    var children = body["quesDatas"]![0]!.Need("answer").Unwrap().Need("children").Array();
    Equal(2, children.Count);
    Equal("First reference.", children[0]!["value"]![0].Text());
    Equal("Second reference.", children[1]!["value"]![0].Text());
    using var api = new UnipusClient(handler: new DemoHandler { AnswerDataOverride = fixture });
    var parsed = (await api.GetAnswersAsync(new("course", "task", "user"), default)).Single();
    Throws<InvalidDataException>(() => Protocol.ValidateSubmit(new("course", "task", "user", "single-choice", parsed)));
});
await Test("Multiple questions match answers by ID and use each question type", async () =>
{
    const string content = """[{"id":"sequence-id","content":{"type":"sequence","replyType":"sequence","children":[{},{}]}},{"id":"short-id","content":"{\"type\":\"basic\",\"replyType\":\"text-area\",\"children\":[{}]}"}]""";
    const string answers = """[{"id":"short-id","answer":{"children":[{}]},"analysis":{"analysis":"Essay","children":[{"analysis":"Short reference"}]}},{"id":"sequence-id","answer":{"children":[{"answers":["B"]},{"answers":["A"]}]},"analysis":{"analysis":"Essay"}}]""";
    var handler = new DemoHandler { ChoiceType = "sequence,short-answer", ChoiceQuestionCount = 2, ContentDataOverride = content, AnswerDataOverride = answers };
    using var job = await CreateJob(handler);
    job.Start();
    await job.Execution!.WaitAsync(TimeSpan.FromSeconds(10));
    Equal(2, handler.SubmitCalls);
    var body = handler.SubmittedBodies.Single(x => x["groupId"].Text() == "task-choice");
    Equal(2, body.Need("quesDatas").Array().Count);
    Equal("sequence-id", body["quesDatas"]![0]!["instanceId"].Text());
    Equal("short-id", body["quesDatas"]![1]!["instanceId"].Text());
    Equal("Short reference", body["quesDatas"]![1]!.Need("answer").Unwrap()["children"]![0]!["value"]![0].Text());
    Equal(3, body.Need("isCompleted").Array().Count);
    Equal(3, body.Need("thirdPartyJudges").Unwrap().Array().Count);
});
await Test("Mixed text and oral questions submit one complete group in content order", async () =>
{
    const string content = """[{"id":"oral-id","content":{"type":"basic-oral","replyType":"record","children":[{"rule":{"text":"Hello world."},"children":[]}]}},{"id":"text-id","content":{"type":"basic-scoop-content","replyType":"fillblank","children":[{}]}}]""";
    const string answers = """[{"id":"text-id","answer":{"children":[{"answers":["hello"]}]},"analysis":{"analysis":"Essay"}},{"id":"oral-id","answer":{"children":[{}]},"analysis":{"analysis":"Essay"}}]""";
    var handler = new DemoHandler { ChoiceType = "oral-sentence,basic-scoop-content", ChoiceQuestionCount = 2, ContentDataOverride = content, AnswerDataOverride = answers };
    var speech = new FakeSpeech();
    using var job = await CreateJob(handler, tts: speech, evaluator: speech);
    job.Start();
    await job.Execution!.WaitAsync(TimeSpan.FromSeconds(10));
    Equal(2, handler.SubmitCalls);
    Equal(1, handler.UploadedRecordings);
    var body = handler.SubmittedBodies.Single(x => x["groupId"].Text() == "task-choice");
    Equal("oral-id", body["quesDatas"]![0]!["instanceId"].Text());
    Equal("text-id", body["quesDatas"]![1]!["instanceId"].Text());
    var judges = body.Need("thirdPartyJudges").Unwrap().Array();
    Equal("basic-oral", judges[0]!["question_type"].Text());
    Equal("basic", judges[1]!["question_type"].Text());
    Equal(138359L, judges[0]!["versions"]!["course"].Integer());
});
await Test("Incomplete, duplicate, empty or unsupported groups never submit or record partially", async () =>
{
    const string validContent = """[{"id":"first","content":{"type":"basic-oral","replyType":"record","children":[{"rule":{"text":"Hello world."},"children":[]}]}},{"id":"second","content":{"type":"basic","replyType":"multichoice","children":[{}]}}]""";
    foreach (var (content, answers, count) in new[]
    {
        (validContent, "[]", 2),
        (validContent, """[{"id":"second","answer":{"children":[{"answers":[""]}]}}]""", 2),
        (validContent, """[{"id":"second","answer":{"children":[{"answers":["A"]}]}},{"id":"second","answer":{"children":[{"answers":["B"]}]}}]""", 2),
        (validContent, "[]", 3),
        (validContent.Replace("multichoice", "multiFileUpload"), "[]", 2),
        (validContent.Replace("second", "first"), "[]", 2)
    })
    {
        var handler = new DemoHandler { ChoiceType = "oral-sentence,multichoice", ChoiceQuestionCount = count, ContentDataOverride = content, AnswerDataOverride = answers };
        var speech = new FakeSpeech();
        using var job = await CreateJob(handler, tts: speech, evaluator: speech);
        job.Start();
        await job.Execution!.WaitAsync(TimeSpan.FromSeconds(10));
        Equal(1, handler.SubmitCalls); // Only the separate reading task can complete.
        Equal(0, speech.Texts.Count);
        Equal(0, handler.UploadedRecordings);
        Assert(job.Snapshot().State != "出错", "invalid group should be skipped");
    }
});
await Test("Group API validates every member before POST, including same-type questions", async () =>
{
    var handler = new DemoHandler();
    using var api = new UnipusClient(handler: handler);
    var a = new QuestionAnswer("one", [["A", "C"]], [], "");
    var b = new QuestionAnswer("two", [["B"]], [], "");
    var request = new SubmitRequest("c", "t", "o", "multichoice", null)
    { Questions = [new("multichoice", a), new("multichoice", b)] };
    await api.SubmitAsync(request, default);
    Equal(2, handler.SubmittedBodies[0].Need("quesDatas").Array().Count);
    await ThrowsAsync<InvalidDataException>(() => api.SubmitAsync(request with { Questions = [new("multichoice", a), new("multichoice", a)] }, default));
    await ThrowsAsync<InvalidDataException>(() => api.SubmitAsync(request with { Questions = [new("multichoice", a), new("single-choice", b with { Answers = [] })] }, default));
    await ThrowsAsync<InvalidDataException>(() => api.SubmitAsync(request with { Questions = [] }, default));
    Equal(1, handler.SubmitCalls);
});

const string dubFixture = """[{"id":"dub-instance","content":{"type":"video-dub","category":7,"contents":[{"type":2,"path":"https://media.example/video.mp4","points":[{"start":0.2,"end":3.2,"text":"<p>Hello world.</p>"},{"start":4,"end":9.024,"text":"A longer reference."}]}],"children":[{"type":"video-dub","rule":{"text":"Hello world."},"config":{"recordTime":3000},"children":[]},{"type":"video-dub","contents":[{"type":5,"text":"<p>This is a longer reference sentence with more than fifteen words and it must still use sentence evaluation.</p>"}],"config":{"recordTime":5024.000000000001},"children":[]}]}}]""";
await Test("Video dubbing reads per-clip text and fractional limits and requires complete clips", () =>
{
    var questions = SpeechProtocol.ReadQuestions(Json.Parse(dubFixture), 123);
    var question = questions.Single();
    Equal("video-dub", question.Type);
    Equal("", question.ReplyType);
    Equal(2, question.Parts.Count);
    Equal(3000, question.Parts[0].MaximumMilliseconds);
    Assert(question.Parts[1].MaximumMilliseconds is >= 5024 and <= 5025, "fractional milliseconds must not become 120 seconds");
    Assert(question.Parts.All(p => p.Kind == SpeechKind.Sentence), "video dubbing always uses sentence evaluation");
    Equal("Hello world.", question.Parts[0].ReferenceText);
    foreach (var invalid in new[] { dubFixture.Replace("3000", "null"), dubFixture.Replace("3000", "0"), """[{"id":"empty","content":{"type":"video-dub","children":[]}}]""" })
        Throws<InvalidDataException>(() => SpeechProtocol.ReadQuestions(Json.Parse(invalid), 123));
    var missingText = Json.Parse(dubFixture);
    missingText[0]!["content"]!["children"]![0]!["rule"]!["text"] = " ";
    Throws<InvalidDataException>(() => SpeechProtocol.ReadQuestions(missingText, 123));
    return Task.CompletedTask;
});
await Test("Video dubbing adjusts speed and submits each actual recording with video judges", async () =>
{
    var handler = new DemoHandler { ExtraType = "video-dub", ContentDataOverride = dubFixture };
    var speech = new FakeSpeech { DurationMilliseconds = 4000 };
    using var job = await CreateJob(handler, new LearningOptions(OralScript: "This custom script must not replace the video."), speech, speech);
    job.Start();
    await job.Execution!.WaitAsync(TimeSpan.FromSeconds(10));
    Equal(4, handler.SubmitCalls);
    Equal(2, handler.UploadedRecordings);
    Equal(3, speech.Texts.Count);
    Equal("Hello world.", speech.Texts[0]);
    Equal("Hello world.", speech.Texts[1]);
    Assert(speech.Speeds[1] > 1 && speech.Speeds[1] <= 2, "long clip must be synthesized faster");
    Assert(speech.Kinds.All(k => k == SpeechKind.Sentence), "long references still use sentence evaluation");
    var body = handler.SubmittedBodies.Last();
    Equal("dub-instance", body["quesDatas"]![0]!["instanceId"].Text());
    Equal(2, body.Need("isCompleted").Array().Count);
    var judges = body.Need("thirdPartyJudges").Unwrap().Array();
    Equal(2, judges.Count);
    foreach (var judge in judges.Items())
    {
        Equal("video-dub", judge["question_type"].Text());
        Assert(!judge.AsObject().ContainsKey("reply_type"), "absent replyType must be omitted like the web client");
        Equal(36L, judge["payloads"]![0]!["score"].Integer());
        Equal("EN_SENT_SCORE", judge["payloads"]![0]!["type"].Text());
        Equal(138359L, judge["versions"]!["course"].Integer());
    }
});
await Test("Video dubbing exceeding the speed limit or failing assessment never submits", async () =>
{
    foreach (var speech in new[] { new FakeSpeech { DurationMilliseconds = 10000 }, new FakeSpeech { Fail = true } })
    {
        var handler = new DemoHandler { ExtraType = "video-dub", ContentDataOverride = dubFixture };
        using var job = await CreateJob(handler, tts: speech, evaluator: speech);
        job.Start();
        await job.Execution!.WaitAsync(TimeSpan.FromSeconds(10));
        Equal(3, handler.SubmitCalls);
        Equal(0, handler.UploadedRecordings);
        Equal("出错", job.Snapshot().State);
        Assert(speech.Speeds.All(speed => speed <= 2), "speed must remain capped");
    }
});
await Test("Video dubbing can share a multi-question group with a text question", async () =>
{
    var content = Json.Parse(dubFixture).Array();
    content.Add(Json.Parse("""{"id":"text-instance","content":{"type":"basic","replyType":"single-choice","children":[{}]}}"""));
    var handler = new DemoHandler
    {
        ChoiceType = "video-dub,single-choice", ChoiceQuestionCount = 2,
        ContentDataOverride = content.ToJsonString(),
        AnswerDataOverride = """[{"id":"text-instance","answer":{"children":[{"answers":["B"]}]}}]"""
    };
    var speech = new FakeSpeech();
    using var job = await CreateJob(handler, tts: speech, evaluator: speech);
    job.Start();
    await job.Execution!.WaitAsync(TimeSpan.FromSeconds(10));
    Equal(2, handler.SubmitCalls);
    var body = handler.SubmittedBodies.Single(b => b["groupId"].Text() == "task-choice");
    Equal(2, body.Need("quesDatas").Array().Count);
    Equal(3, body.Need("thirdPartyJudges").Unwrap().Array().Count);
    Equal("text-instance", body["quesDatas"]![1]!["instanceId"].Text());
});

Console.WriteLine($"PASS: {passed} checks");
return 0;

async Task Test(string name, Func<Task> action)
{
    await action();
    passed++;
    Console.WriteLine("PASS " + name);
}
static void Assert(bool value, string message) { if (!value) throw new Exception(message); }
static void Equal<T>(T expected, T actual) { if (!EqualityComparer<T>.Default.Equals(expected, actual)) throw new Exception($"Expected {expected}; actual {actual}"); }
static void Throws<T>(Action action) where T : Exception
{
    try { action(); } catch (T) { return; }
    throw new Exception("Expected " + typeof(T).Name);
}
static async Task ThrowsAsync<T>(Func<Task> action) where T : Exception
{
    try { await action(); } catch (T) { return; }
    throw new Exception("Expected " + typeof(T).Name);
}
static async Task Until(Func<bool> predicate)
{
    using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
    while (!predicate()) await Task.Delay(10, timeout.Token);
}
static async Task<LearningJob> CreateJob(DemoHandler handler, LearningOptions? options = null, ITextToSpeech? tts = null, ISpeechEvaluation? evaluator = null)
{
    var api = new UnipusClient(handler: handler);
    var user = await App.AuthenticateAsync(api, new("demo", "demo"), default);
    var courses = await api.GetCoursesAsync(default);
    return new LearningJob(api, user, courses[0], demo: true, options: options, tts: tts, evaluator: evaluator);
}

sealed class RejectingSynchronizationContext : SynchronizationContext
{
    public override void Post(SendOrPostCallback callback, object? state) => throw new InvalidOperationException("UI context must not be captured by a job");
}

sealed class CaptchaLoginHandler : HttpMessageHandler
{
    public List<JsonObject> Bodies { get; } = [];
    public int ErrorCode { get; init; } = 1506;
    public bool AlwaysFail { get; init; }
    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        Bodies.Add(Json.Parse(await request.Content!.ReadAsStringAsync(cancellationToken)).AsObject());
        var text = Bodies.Count == 1 || AlwaysFail ? new JsonObject { ["code"] = ErrorCode, ["msg"] = "需要验证码" }.ToJsonString() :
            """{"code":"0","rs":{"openid":"openid","jwt":"jwt"}}""";
        return new HttpResponseMessage(System.Net.HttpStatusCode.OK) { Content = new StringContent(text, Encoding.UTF8, "application/json") };
    }
}

sealed class FakeSpeech : ITextToSpeech, ISpeechEvaluation
{
    public List<string> Texts { get; } = [];
    public List<float> Speeds { get; } = [];
    public List<SpeechKind> Kinds { get; } = [];
    public int DurationMilliseconds { get; init; } = 1000;
    public bool IgnoreSpeed { get; init; }
    public bool Fail { get; init; }
    public Task<PcmAudio> SynthesizeAsync(string text, CancellationToken ct, Action<string>? progress = null, float speed = 1.0f)
    {
        Texts.Add(text);
        Speeds.Add(speed);
        return Task.FromResult(new PcmAudio(new byte[(int)(DurationMilliseconds * 32 / (IgnoreSpeed ? 1 : speed)) / 2 * 2], 16000));
    }
    public Task<SpeechEvaluationResult> EvaluateAsync(SpeechEvaluationRequest request, CancellationToken ct)
    {
        Kinds.Add(request.Kind);
        if (Fail) throw new IOException("模拟评测失败");
        return Task.FromResult(new SpeechEvaluationResult("https://audio.example/test.mp3", request.Kind == SpeechKind.PersonalStatement ? null : 36, 19, 25, 90, 0, [new WordScore(88, "the")], "", "details", "{}"));
    }
    public void Dispose() { }
}
