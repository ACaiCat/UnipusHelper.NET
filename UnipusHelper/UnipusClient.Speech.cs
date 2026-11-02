using System.Text.Json.Nodes;
using UnipusHelper.Speech;

namespace UnipusHelper;

public sealed partial class UnipusClient
{
    public async Task<IReadOnlyList<TaskQuestion>> GetTaskQuestionsAsync(GetAnswersRequest request, CancellationToken ct)
    {
        var data = await ContentAsync($"course/api/v3/content/{Segment(request.CourseId)}/{Segment(request.TaskId)}/default", request.OpenId, ct);
        Json.Check(data, 0, "读取任务题目");
        var content = Json.Parse(Protocol.Decrypt(data.RequiredText("content"), data["k"].Text()));
        return content.Items().Select(item =>
        {
            var root = item.Need("content").Unwrap();
            var type = Protocol.NormalizeType(root["type"].Text());
            var reply = Protocol.NormalizeType(root["replyType"].Text());
            type = type switch
            {
                "basic" => reply == "text-area" ? "short-answer" : reply,
                "basic-oral" => "oral-sentence",
                _ => type
            };
            var speech = Protocol.SpeechTypes.Contains(type)
                ? SpeechProtocol.ReadQuestions(new JsonArray(item.DeepClone()), data["publish_version"].Integer()).Single() : null;
            return new TaskQuestion(item.RequiredText("id"), type, speech);
        }).ToArray();
    }

    public async Task<IReadOnlyList<SpeechQuestion>> GetSpeechQuestionsAsync(GetAnswersRequest request, CancellationToken ct)
    {
        var data = await ContentAsync($"course/api/v3/content/{Segment(request.CourseId)}/{Segment(request.TaskId)}/default", request.OpenId, ct);
        Json.Check(data, 0, "读取口语题目");
        var plain = Protocol.Decrypt(data.RequiredText("content"), data["k"].Text());
        return SpeechProtocol.ReadQuestions(Json.Parse(plain), data["publish_version"].Integer());
    }

    public async Task<UploadedRecording> UploadRecordingAsync(RecordingUploadRequest request, CancellationToken ct)
    {
        var data = await ContentAsync("media/user_resource/fetch", request.OpenId, ct, new JsonObject
        {
            ["url"] = request.SourceUrl, ["fileType"] = "audio", ["userid"] = request.OpenId,
            ["courseid"] = request.CourseId, ["filetype"] = "audio"
        });
        Json.Check(data, 200, "保存口语录音");
        var address = new Uri(new Uri(data.RequiredText("domain")), data.RequiredText("fileKey")).AbsoluteUri;
        return new UploadedRecording(address);
    }
}
