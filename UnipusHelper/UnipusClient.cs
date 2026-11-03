using System.Net;
using System.Text;
using System.Text.Json.Nodes;

namespace UnipusHelper;

public sealed partial class UnipusClient : IUnipusApi
{
    private readonly HttpClient http;
    private string? jwt;
    public Action<string>? OnRequest { get; set; }
    public Func<CancellationToken, Task<JsonObject>>? SolveLoginCaptchaAsync { get; set; }

    public Task<JsonObject> OpenLoginCaptchaAsync(Action<string> showUrl, CancellationToken ct) =>
        LoginCaptchaBrowser.SolveAsync((path, body, token) => SendAsync("sso.unipus.cn", path, body, null, token), showUrl, ct);
    public UnipusClient(string? proxy = null, HttpMessageHandler? handler = null)
    {
        if (handler is null)
        {
            var transport = new HttpClientHandler { CookieContainer = new CookieContainer(), AutomaticDecompression = DecompressionMethods.All };
            if (!string.IsNullOrWhiteSpace(proxy)) transport.Proxy = new WebProxy(proxy);
            handler = transport;
        }
        http = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(40) };
        http.DefaultRequestHeaders.UserAgent.ParseAdd("UnipusHelper.NET/1.0");
    }

    public async Task<LoginResult> LoginAsync(LoginRequest request, CancellationToken ct)
    {
        var body = new JsonObject
        {
            ["username"] = request.Username, ["password"] = request.Password, ["remember"] = true, ["agreement"] = true, ["service"] = "https://uai.unipus.cn/home"
        };
        var data = await SendAsync("sso.unipus.cn", "sso/0.1/sso/login", body, null, ct);
        for (var attempt = 0; data["code"].Text() == "1506" && SolveLoginCaptchaAsync is not null && attempt < 3; attempt++)
        {
            var solution = await SolveLoginCaptchaAsync(ct);
            foreach (var key in new[] { "challenge", "seccode", "validate", "captchaCode", "encodeCaptha" }) body.Remove(key);
            foreach (var (key, value) in solution)
                if (key is "challenge" or "seccode" or "validate" or "captchaCode" or "encodeCaptha") body[key] = value?.DeepClone();
            data = await SendAsync("sso.unipus.cn", "sso/0.1/sso/login", body, null, ct);
        }
        Json.Check(data, 0, "登录");
        var account = data.Need("rs");
        var result = new LoginResult(account.RequiredText("openid"), account["jwt"].Text());
        jwt = result.Jwt;
        return result;
    }

    public async Task<UserInfo> GetUserInfoAsync(CancellationToken ct)
    {
        var data = await MainAsync("api/account/user/info", ct);
        var user = data.Need("value").Need("userInfo");
        return new UserInfo(user.RequiredText("name"), user["schName"].Text(), user.RequiredText("appUserId"), user.RequiredText("ssoId"));
    }

    public async Task<IReadOnlyList<CourseChoice>> GetCoursesAsync(CancellationToken ct)
    {
        var data = await MainAsync("api/cmgt/course/getCourseListByStudent", ct);
        var result = new List<CourseChoice>();
        foreach (var course in data.Need("value").Need("courseList").Items())
        foreach (var resource in course.Need("courseResourceList").Items())
            result.Add(new CourseChoice(course["name"].Text(), course["startTime"].Integer(), course["endTime"].Integer(), course["archived"].Flag(),
                new CourseResource(resource.RequiredText("id"), resource["name"].Text(), resource.Need("strategyId").Integer(), resource.RequiredText("instanceId"),
                    (int)resource["finishPointNum"].Integer(), (int)resource["totalPointNum"].Integer()))
                { ClassId = course["classId"].Text(), CurriculaId = course["id"].Text() });
        return result;
    }

    public async Task<ResourceDetail> GetResourceAsync(string resourceId, CancellationToken ct)
    {
        var data = await MainAsync("api/cmgt/course/getCourseResourceInfoById/" + Segment(resourceId), ct);
        var resource = data.Need("value").Need("courseResource");
        return new ResourceDetail(resource.RequiredText("courseResourceId"), resource.RequiredText("courseInstanceId"));
    }

    public async Task<CourseTree> GetCourseTreeAsync(string courseId, CancellationToken ct)
    {
        var data = await ContentAsync($"course/api/course/{Segment(courseId)}/default", null, ct);
        Json.Check(data, 0, "读取课程目录");
        var tree = data.Need("course").Unwrap();
        return new CourseTree(tree["name"].Text(), tree.Need("units").Items().Select(ReadCourseNode).ToArray());
    }

    public async Task<IReadOnlyList<UnitStrategy>> GetRequiredTasksAsync(GetRequiredTasksRequest request, CancellationToken ct)
    {
        var data = await MainAsync("api/tla/courseStudyStrategy/detail", ct, new JsonObject { ["id"] = request.StrategyId, ["courseResourceId"] = request.ResourceId });
        return data.Need("value").Need("courseUnitStrategyList").Items().Select(x => new UnitStrategy(
            x.RequiredText("unitId"), x.Need("requiredTask").Items().Select(id => id.Text()).ToArray(), x["studyStartTime"].Integer(), x["studyEndTime"].Integer())).ToArray();
    }

    public async Task<CourseProgress> GetCourseProgressAsync(GetCourseProgressRequest request, CancellationToken ct)
    {
        var data = await MainAsync("api/tla/learningDetail/studyRecord/totalAndUnitSituation" + Query(("id", request.ResourceId), ("appUserId", request.AppUserId)), ct);
        var value = data.Need("value");
        // Courses without study records return totalDetail: {} and omit unit progress.
        return new CourseProgress(value.Need("totalDetail")["finishProgress"].Number(), value.Need("unitList").Items().Select(x =>
            new UnitProgress(x.RequiredText("nodeId"), x["caption"].Text(), x["name"].Text(), x["finishProgress"].Number())).ToArray());
    }

    public async Task<IReadOnlyList<TaskProgress>> GetUnitProgressAsync(GetUnitProgressRequest request, CancellationToken ct)
    {
        var data = await MainAsync("api/tla/learningDetail/studyRecord/unitTaskSituation" + Query(
            ("nodeId", request.UnitId), ("id", request.ResourceId), ("appUserId", request.AppUserId), ("ssoId", request.SsoId)), ct);
        return data.Need("value").Need("list").Items().Select(ReadTaskProgress).ToArray();
    }

    public async Task<IReadOnlyDictionary<string, TaskTime>> GetTaskTimesAsync(GetTaskTimesRequest request, CancellationToken ct)
    {
        var data = await ContentAsync($"course/api/v2/course_progress/{Segment(request.CourseId)}/{Segment(request.UnitId)}/{Segment(request.OpenId)}/default", request.OpenId, ct);
        Json.Check(data, 0, "读取任务开放时间");
        var leafs = data.Need("rt").Need("leafs").AsObject();
        var result = new Dictionary<string, TaskTime>(StringComparer.Ordinal);
        foreach (var (id, leaf) in leafs)
        {
            if (leaf?["strategies"] is { } strategies)
                result[id] = new TaskTime(strategies["start_time"].Integer(), strategies["end_time"].Integer());
        }
        return result;
    }

    public async Task<IReadOnlyList<QuestionAnswer>> GetAnswersAsync(GetAnswersRequest request, CancellationToken ct)
    {
        var data = await ContentAsync($"course/api/v3/answer/{Segment(request.CourseId)}/{Segment(request.TaskId)}/default", request.OpenId, ct);
        Json.Check(data, 0, "读取题目答案");
        var plain = Protocol.Decrypt(data.RequiredText("data"), data["k"].Text());
        return Json.Parse(plain).Items().Select(ReadAnswer).ToArray();
    }

    public async Task<SubmitResult> SubmitAsync(SubmitRequest request, CancellationToken ct)
    {
        var data = await ContentAsync("course/api/v3/newExploration/submit", request.OpenId, ct, Protocol.BuildSubmit(request));
        var code = data["code"].Integer(long.MinValue);
        if (code is not (0 or 600001 or 600002)) Json.Check(data, 0, "提交答案");
        var state = data["data"]?["state"];
        var score = state?["score_avg"]?.Number(double.NaN);
        var passed = state?["state"]?.Integer(long.MinValue) switch { 1 => true, 0 => false, _ => (bool?)null };
        var realScore = state?["real_score_pct"]?.Number(double.NaN) is { } pct && !double.IsNaN(pct) ? pct * 100 : (double?)null;
        return new SubmitResult(code, data["msg"].Text(data["message"].Text()), data["data"]?["version"].Text() ?? "", score, passed, realScore);
    }

    public async Task<TaskInfo> GetTaskInfoAsync(GetTaskInfoRequest request, CancellationToken ct)
    {
        var data = await ContentAsync($"api/mobile/user_module/{Segment(request.CourseId)}/{Segment(request.TaskId + "-" + request.Version)}", request.OpenId, ct);
        Json.Check(data, 0, "读取题目信息");
        var answers = data.Need("data").Need("state").Need("__EXTEND_DATA__").Need("__SUMMARY__").Need("answerList").AsObject();
        return new TaskInfo(answers.Select(x => new TaskQuestionInfo(x.Key, (int)x.Value.Need("questionType").Integer())).ToArray());
    }

    private static CourseNode ReadCourseNode(JsonNode x) => new(x["id"].Text(x["url"].Text()), x["name"].Text(),
        x["base"].Text().Split(',', StringSplitOptions.TrimEntries).Select(Protocol.NormalizeType).ToArray(), (int)x["question_num"].Integer(1),
        x["children"] is JsonArray children ? children.Items().Select(ReadCourseNode).ToArray() : []);
    private static TaskProgress ReadTaskProgress(JsonNode x) => new(x["nodeId"].Text(), x["finishProgress"].Number(),
        x["children"] is JsonArray children ? children.Items().Select(ReadTaskProgress).ToArray() : []);
    private static QuestionAnswer ReadAnswer(JsonNode x)
    {
        var answer = x["answer"]?.Unwrap();
        var analysis = x["analysis"]?.Unwrap();
        IReadOnlyList<IReadOnlyList<string>> answers = answer?["children"] is JsonArray a
            ? a.Items().Select(c => (IReadOnlyList<string>)(c["answers"] is JsonArray values ? values.Items().Select(v => v.Text()).ToArray() : [])).ToArray() : [];
        var childAnalysis = analysis?["children"] is JsonArray b ? b.Items().Select(c => c["analysis"].Text()).ToArray() : [];
        return new QuestionAnswer(x.RequiredText("id"), answers, childAnalysis, analysis?["analysis"].Text() ?? "");
    }

    private async Task<JsonNode> MainAsync(string path, CancellationToken ct, JsonObject? body = null)
    {
        var data = await SendAsync("uai.unipus.cn", path, body, null, ct);
        Json.Check(data, 1, OperationName(path));
        return data;
    }
    private Task<JsonNode> ContentAsync(string path, string? openId, CancellationToken ct, JsonObject? body = null) => SendAsync("ucontent.unipus.cn", path, body, openId, ct);
    // Match the web client/OkHttp path spelling: ':' and '+' are legal path characters.
    // Query values still require full escaping, where '+' otherwise means a space.
    private static string Segment(string value) => Uri.EscapeDataString(value).Replace("%3A", ":", StringComparison.Ordinal).Replace("%2B", "+", StringComparison.Ordinal);
    private static string Query(params (string Key, string Value)[] values) => "?" + string.Join("&", values.Select(x => Uri.EscapeDataString(x.Key) + "=" + Uri.EscapeDataString(x.Value)));

    private static string OperationName(string path) => path switch
    {
        _ when path.Contains("user/info", StringComparison.Ordinal) => "读取用户信息",
        _ when path.Contains("getCourseListByStudent", StringComparison.Ordinal) => "读取课程列表",
        _ when path.Contains("getCourseResourceInfoById", StringComparison.Ordinal) => "读取教程信息",
        _ when path.Contains("courseStudyStrategy/detail", StringComparison.Ordinal) => "读取必修策略",
        _ when path.Contains("totalAndUnitSituation", StringComparison.Ordinal) => "读取课程进度",
        _ when path.Contains("unitTaskSituation", StringComparison.Ordinal) => "读取单元进度",
        _ => "请求"
    };

    private async Task<JsonNode> SendAsync(string host, string path, JsonObject? body, string? openId, CancellationToken ct)
    {
        using var request = new HttpRequestMessage(body is null ? HttpMethod.Get : HttpMethod.Post, $"https://{host}/{path}");
        if (!string.IsNullOrEmpty(jwt)) request.Headers.TryAddWithoutValidation("Authorization", jwt);
        if (host == "ucloud.unipus.cn")
        {
            request.Headers.Add("u-app-id", "39");
            request.Headers.Add("u-platform", "2");
            request.Headers.Add("Origin", "https://ucontent.unipus.cn");
            request.Headers.Referrer = new Uri("https://ucontent.unipus.cn/_explorationpc_default/pc.html");
        }
        if (openId is not null) request.Headers.Add("x-annotator-auth-token", Protocol.AuthToken(openId, DateTimeOffset.UtcNow));
        if (body is not null) request.Content = new StringContent(body.ToJsonString(), Encoding.UTF8, "application/json");
        using var response = await http.SendAsync(request, ct);
        OnRequest?.Invoke($"{request.Method} /{path.Split('?')[0]} → {(int)response.StatusCode}");
        response.EnsureSuccessStatusCode();
        return Json.Parse(await response.Content.ReadAsStringAsync(ct));
    }
    public void Dispose() => http.Dispose();

}
