using System.Net.WebSockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;

namespace UnipusHelper.Speech;

public sealed class SpeechEvaluationClient : ISpeechEvaluation
{
    // Public application parameters used by the U-campus web speech SDK.
    private const string AppKey = "ucampus";
    private const string AppSecret = "SNzkJAAqMYjRZykz";
    private readonly HttpClient http;
    private (string Key, string Secret)? engine;
    private readonly SemaphoreSlim gate = new(1, 1);

    public SpeechEvaluationClient(HttpMessageHandler? handler = null) => http = handler is null ? new HttpClient() : new HttpClient(handler);

    public static string CreateAuth(DateTimeOffset now)
    {
        var time = now.ToUnixTimeMilliseconds().ToString(System.Globalization.CultureInfo.InvariantCulture);
        var hash = Convert.ToHexString(MD5.HashData(Encoding.UTF8.GetBytes($"{AppKey}:{AppSecret}:{time[^10..]}"))).ToLowerInvariant();
        return Convert.ToBase64String(Encoding.UTF8.GetBytes($"{AppKey}:{time[..7]}{hash}{time[^6..]}"));
    }

    public async Task<SpeechEvaluationResult> EvaluateAsync(SpeechEvaluationRequest request, CancellationToken ct)
    {
        if (request.Audio.SampleRate != 16000 || request.Audio.Samples.Length == 0 || request.Audio.Samples.Length % 2 != 0)
            throw new ArgumentException("评测音频必须是 16 kHz 单声道 PCM16。");
        await gate.WaitAsync(ct).ConfigureAwait(false);
        string? token = null;
        try
        {
            engine ??= await InitializeAsync(ct).ConfigureAwait(false);
            var acquired = await CallAsync("acquire/v2", new JsonObject
            {
                ["quesType"] = QuestionType(request.Kind), ["userId"] = request.UserId, ["sceneId"] = 0,
                ["lang"] = 1, ["engine"] = "3", ["classId"] = request.ClassId
            }, ct).ConfigureAwait(false);
            var session = acquired.Need("data");
            token = session.RequiredText("token");
            if (session["engine"].Text("3") != "3") throw new NotSupportedException("评测服务选择了尚未适配的语音引擎。");

            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(TimeSpan.FromMinutes(3));
            using var socket = new ClientWebSocket();
            socket.Options.KeepAliveInterval = TimeSpan.FromSeconds(20);
            socket.Options.SetRequestHeader("Origin", "https://ucontent.unipus.cn");
            await socket.ConnectAsync(new Uri("wss://speech.unipus.cn/speech/proxy/wss"), timeout.Token).ConfigureAwait(false);
            var timestamp = DateTimeOffset.UtcNow.ToUnixTimeSeconds().ToString(System.Globalization.CultureInfo.InvariantCulture);
            var signature = Convert.ToHexString(SHA1.HashData(Encoding.UTF8.GetBytes(engine.Value.Key + engine.Value.Secret + timestamp))).ToLowerInvariant();
            await SendJsonAsync(socket, new JsonObject
            {
                ["sdk"] = new JsonObject { ["version"] = 16777216, ["source"] = 4, ["protocol"] = "websocket" },
                ["app"] = new JsonObject { ["applicationId"] = engine.Value.Key, ["sig"] = signature, ["timestamp"] = timestamp, ["userId"] = request.UserId, ["alg"] = "sha1" }
            }, timeout.Token).ConfigureAwait(false);
            await SendJsonAsync(socket, new JsonObject
            {
                ["tokenId"] = Guid.NewGuid().ToString(),
                ["audio"] = new JsonObject { ["audioType"] = "wav", ["channel"] = 1, ["sampleRate"] = 16000, ["sampleBytes"] = 2 },
                ["request"] = new JsonObject
                {
                    ["apiName"] = ApiName(request.Kind), ["transcript"] = request.ReferenceText, ["userId"] = request.UserId,
                    ["sig"] = signature, ["parameters"] = new JsonObject { ["details"] = new JsonObject { ["adjust"] = 0 } }
                }
            }, timeout.Token).ConfigureAwait(false);
            for (var offset = 0; offset < request.Audio.Samples.Length; offset += 16384)
            {
                var count = Math.Min(16384, request.Audio.Samples.Length - offset);
                await socket.SendAsync(request.Audio.Samples.AsMemory(offset, count), WebSocketMessageType.Binary, true, timeout.Token).ConfigureAwait(false);
            }
            await SendJsonAsync(socket, new JsonObject { ["stop"] = true }, timeout.Token).ConfigureAwait(false);
            var response = await ReceiveResultAsync(socket, timeout.Token).ConfigureAwait(false);
            var evaluation = ParseResult(response, request.Kind);
            var report = new JsonObject
            {
                ["id"] = Guid.NewGuid().ToString(), ["text"] = request.ReferenceText, ["type"] = RecordType(request.Kind),
                ["duration"] = (long)request.Audio.Duration.TotalMilliseconds, ["evaluationWay"] = "1", ["audio"] = "",
                ["isNativeStream"] = false, ["userId"] = request.UserId, ["libraryText"] = "", ["isNotEvaluation"] = false,
                ["languageType"] = 1, ["schoolCode"] = "", ["source"] = 3, ["engineType"] = "3", ["refText"] = request.ReferenceText,
                ["soeQuestionType"] = QuestionType(request.Kind), ["engine"] = "3", ["oriUrl"] = evaluation.SourceUrl,
                ["soeUrl"] = evaluation.SourceUrl, ["ssoId"] = "-", ["status"] = 0, ["result"] = evaluation.RawResponse
            };
            await CallAsync("log/v2", report, timeout.Token).ConfigureAwait(false);
            return evaluation;
        }
        finally
        {
            if (token is not null)
            {
                using var cleanup = new CancellationTokenSource(TimeSpan.FromSeconds(10));
                try { await CallAsync("release/v2", new JsonObject { ["token"] = token, ["userId"] = request.UserId }, cleanup.Token).ConfigureAwait(false); }
                catch (Exception ex) when (ex is HttpRequestException or OperationCanceledException or InvalidOperationException) { }
            }
            gate.Release();
        }
    }

    private async Task<(string Key, string Secret)> InitializeAsync(CancellationToken ct)
    {
        var response = await CallAsync("initialize/v2", null, ct).ConfigureAwait(false);
        var configuration = response.Need("data").Items().FirstOrDefault(x => x["engine"].Text() == "3")
            ?? throw new NotSupportedException("语音评测服务未提供所需引擎配置。");
        using var aes = Aes.Create();
        aes.Key = Encoding.UTF8.GetBytes(AppSecret);
        byte[] iv = [1, 2, 3, 4, 5, 6, 7, 8, 9, 10, 11, 12, 13, 14, 15, 16];
        var decoded = Encoding.UTF8.GetString(aes.DecryptCbc(Convert.FromHexString(configuration.RequiredText("config")), iv, PaddingMode.PKCS7)).Split(':');
        if (decoded.Length < 2 || string.IsNullOrWhiteSpace(decoded[0]) || string.IsNullOrWhiteSpace(decoded[1])) throw new InvalidDataException("评测引擎配置无效。");
        return (decoded[0], decoded[1]);
    }

    private async Task<JsonNode> CallAsync(string method, JsonObject? body, CancellationToken ct)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, "https://zt.unipus.cn/soe/api/" + method);
        request.Headers.Add("auth", CreateAuth(DateTimeOffset.UtcNow));
        request.Headers.Add("Origin", "https://ucontent.unipus.cn");
        if (body is not null) request.Content = new StringContent(body.ToJsonString(), Encoding.UTF8, "application/json");
        using var response = await http.SendAsync(request, ct).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
        var data = Json.Parse(await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false));
        if (data["code"].Integer(long.MinValue) != 0) throw new UnipusApiException("语音评测", data["code"].Text(), data["info"].Text(data["msg"].Text("服务端未提供说明")));
        return data;
    }

    public static SpeechEvaluationResult ParseResult(JsonNode response, SpeechKind kind)
    {
        Json.Check(response, 0, "语音评测");
        var final = response.Need("finalResult");
        var result = final.Need("result");
        var scoreNode = result["total"] ?? result["overall"];
        var score = scoreNode is null ? (double?)null : scoreNode.Number(double.NaN);
        if (score is not null && (!double.IsFinite(score.Value) || score < 0 || score > 100)) throw new InvalidDataException("评测分数无效。");
        if (kind != SpeechKind.PersonalStatement && score is null) throw new InvalidDataException("评测服务未返回口语分数。");
        var details = result["details"] is JsonArray list ? list.Items().Select(x => new WordScore(x["score"].Number(), x["word"].Text(x["text"].Text()))).ToArray() : [];
        var multi = kind == SpeechKind.PersonalStatement && result["details"] is JsonObject ? result["details"]?["multi_dim"] : null;
        return new SpeechEvaluationResult(final.RequiredText("url"), score, (result["accuracy"] ?? multi?["pron"]).Number(), (result["completeness"] ?? multi?["cnt"]).Number(),
            (result["fluency"]?["overall"] ?? multi?["flu"]).Number(), result["relevance"].Number(), details,
            result["extra_details"]?["multi_dim_feedback"]?["conclusion"].Text() ?? "", result["detail"].Text(result["asr_detail"].Text()), response.ToJsonString());
    }

    private static async Task SendJsonAsync(ClientWebSocket socket, JsonNode body, CancellationToken ct) =>
        await socket.SendAsync(Encoding.UTF8.GetBytes(body.ToJsonString()).AsMemory(), WebSocketMessageType.Text, true, ct).ConfigureAwait(false);

    private static async Task<JsonNode> ReceiveResultAsync(ClientWebSocket socket, CancellationToken ct)
    {
        var buffer = new byte[16384];
        while (true)
        {
            using var message = new MemoryStream();
            ValueWebSocketReceiveResult part;
            do
            {
                part = await socket.ReceiveAsync(buffer.AsMemory(), ct).ConfigureAwait(false);
                if (part.MessageType == WebSocketMessageType.Close) throw new IOException("语音评测连接提前关闭。");
                message.Write(buffer, 0, part.Count);
                if (message.Length > 4 * 1024 * 1024) throw new InvalidDataException("评测响应过大。");
            } while (!part.EndOfMessage);
            if (part.MessageType != WebSocketMessageType.Text) continue;
            var result = Json.Parse(Encoding.UTF8.GetString(message.GetBuffer(), 0, (int)message.Length));
            if (result["code"] is not null && result["code"].Integer() != 0) Json.Check(result, 0, "语音评测");
            if (result["finalResult"] is not null) return result;
        }
    }

    public static int QuestionType(SpeechKind kind) => kind switch { SpeechKind.Word => 1, SpeechKind.Sentence => 4, SpeechKind.Paragraph => 5, _ => 9 };
    public static string ApiName(SpeechKind kind) => kind switch { SpeechKind.Word => "en.word.score", SpeechKind.Sentence => "en.sent.score", SpeechKind.Paragraph => "en.pred.score", _ => "asr.en" };
    public static string RecordType(SpeechKind kind) => kind switch { SpeechKind.Word => "EN_WORD_SCORE", SpeechKind.Sentence => "EN_SENT_SCORE", SpeechKind.Paragraph => "EN_PRED_SCORE", _ => "EN_ASR_REC" };
    public void Dispose() { http.Dispose(); gate.Dispose(); }
}
