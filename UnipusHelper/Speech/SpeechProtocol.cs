using System.Text.Json.Nodes;

namespace UnipusHelper.Speech;

public static class SpeechProtocol
{
    public static IReadOnlyList<SpeechQuestion> ReadQuestions(JsonNode content, long courseVersion)
    {
        return content.Items().Select(item =>
        {
            var root = item.Need("content").Unwrap();
            var type = Protocol.NormalizeType(root["type"].Text(root["replyType"].Text()));
            if (!Protocol.SpeechTypes.Contains(type) && type != "basic-oral") throw new NotSupportedException("口语任务包含尚未支持的题型：" + type);
            if (type == "video-dub" && root["children"] is not JsonArray { Count: > 0 })
                throw new InvalidDataException("视频配音题缺少片段，未提交任务。");
            var parts = new List<SpeechPart>();
            Visit(root);
            if (parts.Count == 0) throw new InvalidDataException("口语题没有可录音的子题。");
            var replyType = root["replyType"].Text(root["children"]?[0]?["replyType"].Text() ?? "");
            if (string.IsNullOrWhiteSpace(replyType) && type != "video-dub") replyType = "record";
            return new SpeechQuestion(item.RequiredText("id"), type, parts, new SpeechVersions(courseVersion))
            {
                ReplyType = replyType
            };

            void Visit(JsonNode node)
            {
                if (node["children"] is JsonArray children && children.Count > 0)
                {
                    foreach (var child in children.Items()) Visit(child);
                    return;
                }
                var text = Protocol.CleanText(node["rule"]?["text"].Text() ?? "");
                if (string.IsNullOrWhiteSpace(text) && node["contents"] is JsonArray contents)
                    text = Protocol.CleanText(string.Join(" ", contents.Items().Select(x => x["text"].Text())));
                var words = text.Split(' ', StringSplitOptions.RemoveEmptyEntries).Length;
                var kind = type == "oral-personal-state" ? SpeechKind.PersonalStatement : type == "video-dub" ? SpeechKind.Sentence : words <= 1 ? SpeechKind.Word : words > 15 ? SpeechKind.Paragraph : SpeechKind.Sentence;
                var maximum = node["config"]?["recordTime"].Number(type == "video-dub" ? double.NaN : 120000) ?? (type == "video-dub" ? double.NaN : 120000);
                if (!double.IsFinite(maximum) || maximum < 0 || maximum > int.MaxValue ||
                    (type == "video-dub" && (maximum <= 0 || string.IsNullOrWhiteSpace(text))))
                    throw new InvalidDataException("录音片段缺少有效文本或时限，未提交任务。");
                parts.Add(new SpeechPart(text, (int)Math.Ceiling(maximum), kind));
            }
        }).ToArray();
    }

    public static void Validate(SubmitRequest request)
    {
        if (request.Recordings is not { Count: > 0 } questions) throw new InvalidDataException("缺少口语录音，已阻止提交。");
        foreach (var question in questions)
        {
            if ((!Protocol.SpeechTypes.Contains(question.Type) && question.Type != "basic-oral") || string.IsNullOrEmpty(question.InstanceId) || question.Parts.Count == 0)
                throw new InvalidDataException("口语题录音结构不完整。");
            foreach (var part in question.Parts)
            {
                if (!Uri.TryCreate(part.Url, UriKind.Absolute, out var uri) || uri.Scheme is not ("http" or "https") || string.IsNullOrWhiteSpace(part.Text))
                    throw new InvalidDataException("缺少录音地址或口语稿。");
                if (part.Kind != SpeechKind.PersonalStatement && part.Evaluation.Score is null)
                    throw new InvalidDataException("缺少真实口语评分。");
            }
        }
    }

    internal static JsonObject BuildSubmit(SubmitRequest request)
    {
        Validate(request);
        var questions = new JsonArray();
        var completed = new JsonArray();
        var judges = new JsonArray();
        foreach (var question in request.Recordings!)
        {
            var children = new JsonArray();
            foreach (var part in question.Parts)
            {
                var record = BuildRecord(part);
                children.Push(new JsonObject { ["value"] = new JsonArray(), ["record"] = record, ["isDone"] = true });
                completed.Push(JsonValue.Create(true));
                var v = question.Versions;
                var judge = new JsonObject
                {
                    ["value"] = "", ["payloads"] = new JsonArray(record.DeepClone()),
                    ["specific_scores"] = record["specific_scores"]!.DeepClone(),
                    ["versions"] = new JsonObject { ["course"] = v.Course, ["group"] = v.Group, ["template"] = v.Template, ["answer"] = v.Answer, ["content"] = v.Content },
                    ["question_type"] = question.Type == "oral-sentence" ? "basic-oral" : question.Type
                };
                if (!string.IsNullOrWhiteSpace(question.ReplyType)) judge["reply_type"] = question.ReplyType;
                judges.Push(judge);
            }
            questions.Push(new JsonObject
            {
                ["instanceId"] = question.InstanceId,
                ["answer"] = new JsonObject { ["value"] = new JsonArray(), ["children"] = children, ["progress"] = new JsonObject(), ["record"] = new JsonObject { ["url"] = "" } }.ToJsonString(),
                ["context"] = "{\"state\":\"submitted\"}", ["contextVersion"] = 0, ["answerVersion"] = 0
            });
        }
        return new JsonObject
        {
            ["quesDatas"] = questions, ["groupId"] = request.TaskId, ["isCompleted"] = completed,
            ["thirdPartyJudges"] = judges.ToJsonString(), ["submitType"] = 1, ["hideLoading"] = false,
            ["associationGroupId"] = "", ["courseId"] = request.CourseId, ["openId"] = request.OpenId, ["version"] = "default"
        };
    }

    private static JsonObject BuildRecord(SpeechRecording part)
    {
        var e = part.Evaluation;
        var scores = new JsonObject { ["accuracy"] = e.Accuracy / 100, ["integrity"] = e.Completeness / 100, ["fluency"] = e.Fluency / 100, ["relevance"] = e.Relevance / 100 };
        if (e.Score is { } total) scores["total"] = total / 100;
        var details = new JsonArray();
        foreach (var word in e.Details) details.Push(new JsonObject { ["score"] = word.Score, ["char"] = word.Text });
        var detail = new JsonObject
        {
            ["smooth"] = e.Fluency, ["completed"] = e.Completeness, ["correctness"] = e.Accuracy, ["relevance"] = e.Relevance,
            ["audioUrl"] = part.Url, ["details"] = details, ["detailsWords"] = new JsonArray(), ["comment"] = e.Comment, ["asrDetail"] = e.AsrDetail
        };
        var record = new JsonObject
        {
            ["url"] = part.Url, ["specific_scores"] = scores, ["recordDetail"] = detail,
            ["list"] = new JsonArray(), ["type"] = SpeechEvaluationClient.RecordType(part.Kind), ["text"] = part.Text
        };
        if (e.Score is { } score) { record["score"] = score; detail["score"] = score; }
        return record;
    }
}
