using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace UnipusHelp;

public static partial class Protocol
{
    public static readonly HashSet<string> Preset = ["rich-text-read", "text-learn", "vocabulary", "input", "video-point-read", "discussion"];
    public static readonly HashSet<string> SpeechTypes = ["oral-sentence", "oral-personal-state", "video-dub"];
    public static readonly HashSet<string> Supported = [.. Preset, .. SpeechTypes, "material-banked-cloze", "single-choice", "multichoice", "short-answer", "translation", "sequence", "basic-scoop-content", "basic-scoop-content-dropdown", "video-popup", "writing"];
    public static string NormalizeType(string type) => CamelBoundary().Replace(type.Trim(), "$1-$2").ToLowerInvariant().Replace('_', '-');

    public static string AuthToken(string openId, DateTimeOffset now)
    {
        var header = new JsonObject { ["typ"] = "JWT", ["alg"] = "HS256" };
        var claims = new JsonObject
        {
            ["open_id"] = openId, ["name"] = "", ["email"] = "", ["administrator"] = false,
            // The upstream protocol uses milliseconds, rather than JWT's usual seconds.
            ["exp"] = now.ToUnixTimeMilliseconds() + 31536000000L,
            ["iss"] = "c4f772063dcfa98e9c50", ["aud"] = "edx.unipus.cn"
        };
        var data = Base64Url(Encoding.UTF8.GetBytes(header.ToJsonString())) + "." + Base64Url(Encoding.UTF8.GetBytes(claims.ToJsonString()));
        var signature = HMACSHA256.HashData(Encoding.UTF8.GetBytes("a824b379f126b8b7aa5e33dee83fb0a05aa7462c"), Encoding.UTF8.GetBytes(data));
        return data + "." + Base64Url(signature);
    }

    public static string Decrypt(string data, string keyPart)
    {
        if (!data.StartsWith("unipus.", StringComparison.Ordinal)) return data;
        using var aes = Aes.Create();
        aes.Key = Encoding.UTF8.GetBytes("1a2b3c4d" + keyPart);
        var plain = aes.DecryptEcb(Convert.FromHexString(data[7..]), PaddingMode.None);
        var length = plain.Length;
        while (length > 0 && plain[length - 1] == 0) length--;
        return Encoding.UTF8.GetString(plain.AsSpan(0, length));
    }

    public static string CleanText(string html)
    {
        var text = ActiveContent().Replace(html, " ");
        text = BlockTags().Replace(text, " ");
        text = Tags().Replace(text, "");
        text = WebUtility.HtmlDecode(text).Replace('\u00a0', ' ');
        text = IllegalCharacters().Replace(text, " ");
        return WhiteSpace().Replace(text, " ").Trim();
    }

    internal static JsonObject BuildSubmit(SubmitRequest request)
    {
        ValidateSubmit(request);
        if (request.Questions is not null) return BuildGroupSubmit(request);
        if (request.Recordings is not null) return Speech.SpeechProtocol.BuildSubmit(request);
        var (courseId, taskId, openId, type, answer) = request;
        type = NormalizeType(type);
        if (!Supported.Contains(type)) throw new NotSupportedException($"不支持的题型：{type}");
        var preset = Preset.Contains(type);
        var quesDatas = new JsonArray();
        var completed = new JsonArray();
        var judges = new JsonArray();
        if (!preset)
        {
            if (answer is null) throw new InvalidDataException("题目缺少答案。");
            var groups = ExtractAnswers(answer, type);
            if (groups.Count == 0) throw new InvalidDataException("答案为空，已阻止提交。");
            var children = new JsonArray();
            foreach (var group in groups)
            {
                var cleaned = group.Select(CleanText).ToArray();
                if (cleaned.Length == 0 || cleaned.Any(string.IsNullOrWhiteSpace))
                    throw new InvalidDataException("答案含空值，已阻止提交。");
                var values = new JsonArray();
                foreach (var text in cleaned) values.Push(JsonValue.Create(text));
                children.Push(new JsonObject { ["value"] = values, ["isDone"] = true });
                completed.Push(JsonValue.Create(true));
                judges.Push(new JsonObject
                {
                    ["value"] = string.Join(",", cleaned), ["question_type"] = "basic",
                    ["payloads"] = new JsonArray(new JsonObject { ["recordDetail"] = new JsonObject { ["score"] = 100.0, ["comment"] = "" } })
                });
            }
            var inner = new JsonObject { ["value"] = new JsonArray(), ["children"] = children, ["progress"] = new JsonObject(), ["record"] = new JsonObject { ["url"] = "" } };
            quesDatas.Push(new JsonObject
            {
                ["instanceId"] = answer.InstanceId, ["answer"] = inner.ToJsonString(),
                ["context"] = "{\"state\":\"submitted\"}", ["contextVersion"] = 0, ["answerVersion"] = 0
            });
        }
        return new JsonObject
        {
            ["quesDatas"] = quesDatas, ["groupId"] = taskId, ["isCompleted"] = completed,
            ["thirdPartyJudges"] = judges.ToJsonString(), ["submitType"] = preset || type == "video-popup" ? 2 : 1,
            ["hideLoading"] = preset, ["associationGroupId"] = "", ["courseId"] = courseId, ["openId"] = openId, ["version"] = "default"
        };
    }

    public static void ValidateSubmit(SubmitRequest request)
    {
        if (request.Questions is { } questions)
        {
            if (questions.Count == 0 || request.Answer is not null || request.Recordings is not null)
                throw new InvalidDataException("多题目提交结构不完整或混用了单题答案。");
            var ids = new HashSet<string>(StringComparer.Ordinal);
            foreach (var question in questions)
            {
                if (Preset.Contains(NormalizeType(question.Type)))
                    throw new InvalidDataException("混合任务中的学习或讨论题暂未支持。");
                if (question.Answer is not null && question.Recording is not null)
                    throw new InvalidDataException("同一道题不能同时提交答案和录音。");
                var id = question.Answer?.InstanceId ?? question.Recording?.InstanceId;
                if (string.IsNullOrWhiteSpace(id) || !ids.Add(id))
                    throw new InvalidDataException("题目 ID 为空或重复，已阻止整组提交。");
                if (question.Recording is { } recording &&
                    NormalizeType(recording.Type) != NormalizeType(question.Type) &&
                    !(NormalizeType(question.Type) == "oral-sentence" && recording.Type == "basic-oral"))
                    throw new InvalidDataException("录音题型与题目不符。");
                ValidateSubmit(ForQuestion(request, question));
            }
            return;
        }
        var type = NormalizeType(request.QuestionType);
        if (!Supported.Contains(type)) throw new NotSupportedException($"不支持的题型：{type}");
        if (Preset.Contains(type)) return;
        if (SpeechTypes.Contains(type))
        {
            Speech.SpeechProtocol.Validate(request);
            return;
        }
        if (request.Recordings is not null) throw new InvalidDataException("非口语题不能提交录音。");
        if (request.Answer is null) throw new InvalidDataException("题目缺少答案。");
        var groups = ExtractAnswers(request.Answer, type);
        if (groups.Count == 0 || groups.Any(x => x.Count == 0 || x.Any(a => string.IsNullOrWhiteSpace(CleanText(a)))))
            throw new InvalidDataException("答案为空，已阻止提交。");
    }

    private static SubmitRequest ForQuestion(SubmitRequest request, QuestionSubmission question) =>
        new(request.CourseId, request.TaskId, request.OpenId, question.Type, question.Answer)
        { Recordings = question.Recording is { } recording ? [recording] : null };

    private static JsonObject BuildGroupSubmit(SubmitRequest request)
    {
        var parts = request.Questions!.Select(q => BuildSubmit(ForQuestion(request, q))).ToArray();
        var result = (JsonObject)parts[0].DeepClone();
        foreach (var key in new[] { "quesDatas", "isCompleted" })
        {
            var combined = new JsonArray();
            foreach (var part in parts)
                foreach (var value in part.Need(key).Items()) combined.Add(value.DeepClone());
            result[key] = combined;
        }
        var judges = new JsonArray();
        foreach (var part in parts)
            foreach (var judge in part.Need("thirdPartyJudges").Unwrap().Items()) judges.Add(judge.DeepClone());
        result["thirdPartyJudges"] = judges.ToJsonString();
        result["submitType"] = parts.All(p => p["submitType"].Integer() == 2) ? 2 : 1;
        result["hideLoading"] = false;
        return result;
    }

    public static IReadOnlyList<IReadOnlyList<string>> ExtractAnswers(QuestionAnswer answer, string type)
    {
        type = NormalizeType(type);
        if (type == "writing") return [[answer.Analysis]];
        if (type == "short-answer") return answer.ChildAnalysis.Select(x => (IReadOnlyList<string>)new[] { x }).ToArray();
        return answer.Answers;
    }

    private static string Base64Url(byte[] bytes) => Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');
    [GeneratedRegex("([a-z])([A-Z])")] private static partial Regex CamelBoundary();
    [GeneratedRegex(@"<(script|style)\b[^>]*>[\s\S]*?</\1\s*>", RegexOptions.IgnoreCase)] private static partial Regex ActiveContent();
    [GeneratedRegex(@"</?(?:p|br|div|li|tr|h[1-6])\b[^>]*>", RegexOptions.IgnoreCase)] private static partial Regex BlockTags();
    [GeneratedRegex("<[^>]*>")] private static partial Regex Tags();
    [GeneratedRegex(@"[^a-zA-ZёЁÀ-ÿ0-9一-龥Ѐ–ӿ\uFF00-\uFFEF\uFFF0-\uFFFF　 ⼀-\u2FDF㐀-䶵豈-\uFAFF\uE863가-\uD7AFᄀ-ᇿ\u3130-\u318Fꥠ-\uA97Fힰ-\uD7FF\u3040-ゟ゠-ヿㇰ-ㇿĀ-ſ\u0080-ÿḀ-ỿ„€「」『』々ヽヾ\[\],.{}()<>?/|`~""'@#$%&*《》（）+=_￥¥。，、;:：；【】…？！!”“’‘•·\s\\-]")]
    private static partial Regex IllegalCharacters();
    [GeneratedRegex(@"\s+")] private static partial Regex WhiteSpace();
}
