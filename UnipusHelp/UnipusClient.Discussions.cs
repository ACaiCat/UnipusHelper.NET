using System.Text.Json.Nodes;

namespace UnipusHelp;

public sealed partial class UnipusClient
{
    public async Task<DiscussionTopicPage> GetDiscussionTopicsAsync(GetDiscussionTopicsRequest request, CancellationToken ct)
    {
        var payload = DiscussionScopeBody(request.Scope);
        payload["order"] = "desc";
        payload["pageNum"] = request.Page;
        payload["pageSize"] = request.PageSize;
        var data = await DiscussionAsync("api/bbs/utopic/page", payload, "读取讨论主题", ct);
        var value = data.Need("value");
        var topics = value["utopics"] is JsonArray list
            ? list.Items().Select(x => new DiscussionTopic(x.Need("topicId").Integer(), x["ownerStatus"].Flag())).ToArray() : [];
        return new DiscussionTopicPage(topics, value["count"].Integer(topics.Length));
    }

    public async Task<DiscussionWriteResult> CreateDiscussionTopicAsync(CreateDiscussionTopicRequest request, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(request.Title) || string.IsNullOrWhiteSpace(request.Content)) throw new ArgumentException("讨论主题和内容不能为空。");
        var payload = DiscussionScopeBody(request.Scope);
        payload["title"] = request.Title;
        payload["content"] = request.Content;
        var data = await DiscussionAsync("api/bbs/utopic/add", payload, "创建讨论主题", ct, mutation: true);
        return new DiscussionWriteResult(data["code"].Integer() is 10001 or 10002);
    }

    public async Task<DiscussionReplyPage> GetDiscussionRepliesAsync(GetDiscussionRepliesRequest request, CancellationToken ct)
    {
        var data = await DiscussionAsync("api/bbs/ureply/top/page", new JsonObject
        {
            ["topicId"] = request.TopicId, ["parentId"] = "", ["order"] = "desc", ["pageNum"] = request.Page, ["pageSize"] = request.PageSize
        }, "读取讨论回复", ct);
        var value = data.Need("value");
        var replies = value["replyContents"] is JsonArray list
            ? list.Items().Select(x => new DiscussionReply(x["ownerStatus"].Flag())).ToArray() : [];
        return new DiscussionReplyPage(replies, value["count"].Integer(value["replyTotalCount"].Integer(replies.Length)));
    }

    public async Task<DiscussionWriteResult> PostDiscussionReplyAsync(PostDiscussionReplyRequest request, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(request.Content)) throw new ArgumentException("讨论回复不能为空。");
        var data = await DiscussionAsync("api/bbs/ureply/add", new JsonObject
        {
            ["topicId"] = request.TopicId, ["content"] = request.Content, ["contentType"] = "text", ["type"] = 2, ["parentId"] = "", ["topReplyId"] = ""
        }, "发表讨论回复", ct, mutation: true);
        return new DiscussionWriteResult(data["code"].Integer() is 10001 or 10002);
    }

    private static JsonObject DiscussionScopeBody(DiscussionScope scope)
    {
        if (string.IsNullOrWhiteSpace(scope.ClassId) || string.IsNullOrWhiteSpace(scope.CurriculaId))
            throw new InvalidDataException("课程信息缺少班级或课程 ID，无法发表讨论回复。");
        return new JsonObject
        {
            ["courseId"] = scope.CourseId, ["groupId"] = scope.TaskId, ["classId"] = scope.ClassId, ["curriculaId"] = scope.CurriculaId, ["type"] = 2
        };
    }

    private async Task<JsonNode> DiscussionAsync(string path, JsonObject body, string operation, CancellationToken ct, bool mutation = false)
    {
        var data = await SendAsync("ucloud.unipus.cn", path, body, null, ct);
        // Duplicate acknowledgements are verified by reading back the reply before completing a task.
        if (!(mutation && data["code"].Integer() is 10001 or 10002)) Json.Check(data, 1, operation);
        return data;
    }
}
