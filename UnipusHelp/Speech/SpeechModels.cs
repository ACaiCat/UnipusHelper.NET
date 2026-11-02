namespace UnipusHelp.Speech;

public enum SpeechKind { Word, Sentence, Paragraph, PersonalStatement }
public sealed record SpeechPart(string ReferenceText, int MaximumMilliseconds, SpeechKind Kind);
public sealed record SpeechVersions(long Course, long Group = 1, long Template = 1, long Answer = 3, long Content = 0);
public sealed record SpeechQuestion(string InstanceId, string Type, IReadOnlyList<SpeechPart> Parts, SpeechVersions Versions)
{
    public string ReplyType { get; init; } = "record";
}
public sealed record WordScore(double Score, string Text);
public sealed record SpeechEvaluationRequest(string UserId, string ClassId, string ReferenceText, PcmAudio Audio, SpeechKind Kind);
public sealed record SpeechEvaluationResult(string SourceUrl, double? Score, double Accuracy, double Completeness, double Fluency, double Relevance,
    IReadOnlyList<WordScore> Details, string Comment, string AsrDetail, string RawResponse);
public sealed record SpeechRecording(string Url, string Text, SpeechKind Kind, SpeechEvaluationResult Evaluation);
public sealed record RecordedQuestion(string InstanceId, string Type, IReadOnlyList<SpeechRecording> Parts, SpeechVersions Versions)
{
    public string ReplyType { get; init; } = "record";
}
public sealed record RecordingUploadRequest(string SourceUrl, string CourseId, string OpenId);
public sealed record UploadedRecording(string Url);

public interface ISpeechEvaluation : IDisposable
{
    Task<SpeechEvaluationResult> EvaluateAsync(SpeechEvaluationRequest request, CancellationToken ct);
}
