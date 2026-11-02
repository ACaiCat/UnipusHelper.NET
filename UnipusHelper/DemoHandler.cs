using System.Net;
using System.Text;
using System.Text.Json.Nodes;

namespace UnipusHelper;

// Deliberately local: the demo never opens a network connection.
public sealed class DemoHandler : HttpMessageHandler
{
    public bool ZeroScore { get; init; }
    public bool ZeroAverageButPass { get; init; }
    public bool EmptyAnswer { get; init; }
    public string? AnswerDataOverride { get; init; }
    public string? ContentDataOverride { get; init; }
    public int ChoiceQuestionCount { get; init; } = 1;
    public int RateLimitResponses { get; set; }
    public int SubmitCalls { get; private set; }
    public bool FailSubmit { get; init; }
    public string? FailurePath { get; init; }
    public string FailureBody { get; init; } = "{\"code\":2}";
    public string CatalogInstanceId { get; init; } = "course-v2:demo+english+2026";
    public string DetailInstanceId { get; init; } = "course-v2:demo+english+2026";
    public bool EnforceCatalogPath { get; init; }
    public string ChoiceType { get; init; } = "single-choice";
    public string ExtraType { get; init; } = "multi-file-upload";
    public bool NoDiscussionTopic { get; init; }
    public bool AlreadyReplied { get; init; }
    public int OwnReplyPage { get; init; } = 1;
    public bool FailDiscussionReply { get; init; }
    public int DiscussionPostCalls { get; private set; }
    public int CreatedTopics { get; private set; }
    public List<string> DiscussionReplies { get; } = [];
    public List<string> RequestOrder { get; } = [];
    public List<JsonNode> DiscussionRequests { get; } = [];
    private readonly HashSet<string> finished = [];
    public List<JsonNode> SubmittedBodies { get; } = [];
    public int UploadedRecordings { get; private set; }
    public int SpeechPartCount { get; init; } = 2;

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        await Task.Delay(15, cancellationToken);
        var path = request.RequestUri!.AbsolutePath;
        RequestOrder.Add(path);
        if (EnforceCatalogPath && path.Contains("/api/course/", StringComparison.Ordinal) && !path.Contains(CatalogInstanceId, StringComparison.Ordinal))
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("{\"code\":2}", Encoding.UTF8, "application/json") };
        if (FailurePath is not null && path.Contains(FailurePath, StringComparison.Ordinal))
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(FailureBody, Encoding.UTF8, "application/json") };
        JsonNode result;
        if (path.EndsWith("/login", StringComparison.Ordinal))
            result = Json.Parse("""{"code":"0","rs":{"openid":"demo-openid","jwt":"demo-session"}}""");
        else if (path.EndsWith("/user/info", StringComparison.Ordinal))
            result = Envelope(Json.Parse("""{"userInfo":{"name":"演示同学","schName":"离线演示大学","appUserId":"demo-app","ssoId":"demo-sso"}}"""));
        else if (path.EndsWith("/getCourseListByStudent", StringComparison.Ordinal))
            result = Envelope(Json.Parse($$"""{"courseList":[{"name":"大学英语（演示）","startTime":0,"endTime":0,"archived":false,"courseResourceList":[{"id":20000000001,"name":"读写教程","strategyId":123,"instanceId":"course-v2:demo+english+2026","finishPointNum":{{finished.Count}},"totalPointNum":4}]}]}"""));
        else if (path.Contains("getCourseResourceInfoById", StringComparison.Ordinal))
            result = Envelope(Json.Parse("""{"courseResource":{"courseResourceId":20000000001,"courseInstanceId":"course-v2:demo+english+2026"}}"""));
        else if (path.Contains("/api/course/", StringComparison.Ordinal))
            result = new JsonObject { ["code"] = 0, ["course"] = Json.Parse("""{"name":"读写教程","units":[{"id":"unit-1","children":[{"id":"task-read","name":"课文阅读","base":"rich-text-read","question_num":0},{"id":"task-choice","name":"单项选择","base":"single-choice","question_num":1},{"id":"task-writing","name":"写作","base":"writing","question_num":1},{"id":"task-discussion","name":"讨论（跳过示例）","base":"discussion","question_num":1}]}]}""").ToJsonString() };
        else if (path.EndsWith("/courseStudyStrategy/detail", StringComparison.Ordinal))
            result = Envelope(Json.Parse("""{"courseUnitStrategyList":[{"unitId":"unit-1","requiredTask":["task-read","task-choice","task-writing","task-discussion"],"studyStartTime":0,"studyEndTime":0}]}"""));
        else if (path.EndsWith("/totalAndUnitSituation", StringComparison.Ordinal))
            result = Envelope(Json.Parse($$"""{"totalDetail":{"finishProgress":{{finished.Count * 25}}},"unitList":[{"nodeId":"unit-1","caption":"Unit 1","name":"Learning English","finishProgress":{{finished.Count * 25}}}]}"""));
        else if (path.Contains("/course_progress/", StringComparison.Ordinal))
        {
            var leafs = new JsonObject();
            foreach (var id in new[] { "task-read", "task-choice", "task-writing", "task-discussion" })
                leafs[id] = Json.Parse("""{"strategies":{"start_time":0,"end_time":0,"required":true}}""");
            result = new JsonObject { ["code"] = 0, ["rt"] = new JsonObject { ["leafs"] = leafs } };
        }
        else if (path.EndsWith("/unitTaskSituation", StringComparison.Ordinal))
        {
            var list = new JsonArray();
            foreach (var id in new[] { "task-read", "task-choice", "task-writing", "task-discussion" })
                list.Push(new JsonObject { ["nodeId"] = id, ["finishProgress"] = finished.Contains(id) ? 100 : 0 });
            result = Envelope(new JsonObject { ["list"] = list });
        }
        else if (path.Contains("/v3/content/", StringComparison.Ordinal))
        {
            var children = new JsonArray();
            for (var i = 0; i < SpeechPartCount; i++) children.Push(new JsonObject
            {
                ["rule"] = new JsonObject { ["text"] = "This is my recording." },
                ["children"] = new JsonArray(), ["config"] = new JsonObject { ["recordTime"] = 120000 }
            });
            var content = new JsonObject { ["type"] = ExtraType == "oral-sentence" ? "basic-oral" : ExtraType, ["replyType"] = ExtraType == "oral-sentence" ? "record" : ExtraType, ["children"] = children };
            result = new JsonObject { ["code"] = 0, ["publish_version"] = 138359,
                ["content"] = new JsonArray(new JsonObject { ["id"] = "oral-instance", ["content"] = content.ToJsonString() }).ToJsonString() };
        }
        else if (path.EndsWith("/media/user_resource/fetch", StringComparison.Ordinal))
        {
            UploadedRecordings++;
            result = new JsonObject { ["code"] = "200", ["domain"] = "https://course.media.unipus.cn/", ["fileKey"] = $"test/recording-{UploadedRecordings}.mp3" };
        }
        else if (path.Contains("/v3/answer/", StringComparison.Ordinal))
            result = new JsonObject
            {
                ["code"] = 0, ["k"] = "20250627",
                ["data"] = EmptyAnswer ? "[]" : """[{"id":9876543210,"answer":"{\"children\":[{\"answers\":[\"A\"]}]}","analysis":"{\"analysis\":\"<p>Learning English helps us understand the world.</p>\"}"}]"""
            };
        else if (path.Contains("/user_module/", StringComparison.Ordinal))
            result = Json.Parse("""{"code":0,"data":{"state":{"__EXTEND_DATA__":{"__SUMMARY__":{"answerList":{"9876543210":{"questionType":1}}}}}}}""");
        else if (path.StartsWith("/api/bbs/", StringComparison.Ordinal))
        {
            var payload = Json.Parse(await request.Content!.ReadAsStringAsync(cancellationToken));
            DiscussionRequests.Add(payload);
            if (path.EndsWith("/utopic/page", StringComparison.Ordinal))
            {
                var topics = new JsonArray();
                if (!NoDiscussionTopic || CreatedTopics > 0) topics.Push(new JsonObject { ["topicId"] = 42, ["ownerStatus"] = false });
                result = Envelope(new JsonObject { ["utopics"] = topics, ["count"] = topics.Count });
            }
            else if (path.EndsWith("/utopic/add", StringComparison.Ordinal))
            {
                CreatedTopics++;
                result = Envelope(new JsonObject());
            }
            else if (path.EndsWith("/ureply/top/page", StringComparison.Ordinal))
            {
                var pageNumber = (int)payload["pageNum"].Integer(1);
                var owned = AlreadyReplied || DiscussionReplies.Count > 0;
                var list = new JsonArray();
                if (owned && pageNumber < OwnReplyPage)
                    for (var i = 0; i < 20; i++) list.Push(new JsonObject { ["ownerStatus"] = false });
                else if (owned) list.Push(new JsonObject { ["ownerStatus"] = true });
                result = Envelope(new JsonObject { ["replyContents"] = list, ["count"] = owned ? (OwnReplyPage - 1) * 20 + 1 : 0 });
            }
            else if (path.EndsWith("/ureply/add", StringComparison.Ordinal))
            {
                DiscussionPostCalls++;
                if (FailDiscussionReply) throw new HttpRequestException("模拟讨论回复发布失败");
                DiscussionReplies.Add(payload.RequiredText("content"));
                result = Envelope(new JsonObject());
            }
            else throw new InvalidOperationException("未实现的讨论接口：" + path);
        }
        else if (path.EndsWith("/newExploration/submit", StringComparison.Ordinal))
        {
            SubmitCalls++;
            if (FailSubmit) throw new HttpRequestException("模拟网络断开");
            var body = Json.Parse(await request.Content!.ReadAsStringAsync(cancellationToken));
            SubmittedBodies.Add(body);
            if (RateLimitResponses-- > 0) result = Json.Parse("""{"code":600001,"msg":"模拟限流"}""");
            else
            {
                finished.Add(body.RequiredText("groupId"));
                var failed = ZeroScore && !ZeroAverageButPass;
                result = new JsonObject { ["code"] = 0, ["data"] = new JsonObject { ["version"] = "1", ["state"] = new JsonObject {
                    ["score_avg"] = ZeroScore ? 0 : 100,
                    ["real_score_pct"] = failed ? 0 : 1,
                    ["state"] = failed ? 0 : 1 } } };
            }
        }
        else throw new InvalidOperationException("未实现的演示接口：" + path);
        if (path.EndsWith("/getCourseListByStudent", StringComparison.Ordinal))
        {
            result["value"]!["courseList"]![0]!["courseResourceList"]![0]!["instanceId"] = CatalogInstanceId;
            result["value"]!["courseList"]![0]!["classId"] = "demo-class";
            result["value"]!["courseList"]![0]!["id"] = 123;
        }
        if (path.Contains("/api/course/", StringComparison.Ordinal))
        {
            var tree = result.Need("course").Unwrap();
            tree["units"]![0]!["children"]![1]!["base"] = ChoiceType;
            tree["units"]![0]!["children"]![1]!["question_num"] = ChoiceQuestionCount;
            tree["units"]![0]!["children"]![1]!["name"] = ChoiceType == "multichoice" ? "多项选择" : "单项选择";
            tree["units"]![0]!["children"]![3]!["base"] = ExtraType;
            if (Protocol.SpeechTypes.Contains(ExtraType)) tree["units"]![0]!["children"]![3]!["question_num"] = 2;
            tree["units"]![0]!["children"]![3]!["name"] = ExtraType == "discussion" ? "讨论" : "文件上传";
            result["course"] = tree.ToJsonString();
        }
        if (path.Contains("/v3/answer/", StringComparison.Ordinal) && path.Contains("task-choice", StringComparison.Ordinal) && ChoiceType == "multichoice" && !EmptyAnswer)
            result["data"] = """[{"id":9876543210,"answer":"{\"children\":[{\"answers\":[\"A\",\"C\"]}]}","analysis":"{}"}]""";
        if (path.Contains("getCourseResourceInfoById", StringComparison.Ordinal))
            result["value"]!["courseResource"]!["courseInstanceId"] = DetailInstanceId;
        if (path.Contains("/v3/answer/", StringComparison.Ordinal) && AnswerDataOverride is not null)
            result["data"] = AnswerDataOverride;
        if (path.Contains("/v3/content/", StringComparison.Ordinal) && ContentDataOverride is not null)
            result["content"] = ContentDataOverride;
        return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(result.ToJsonString(), Encoding.UTF8, "application/json") };
    }
    private static JsonObject Envelope(JsonNode value) => new() { ["code"] = 1, ["success"] = true, ["value"] = value };
}

