namespace UnipusHelp;

public sealed record JobLog(DateTimeOffset Time, string Message, bool IsError)
{
    public override string ToString() => $"{Time:HH:mm:ss} {Message}";
}
public sealed class UnipusApiException(string operation, string code, string serverMessage) : Exception($"{operation}失败（{code}）：{serverMessage}")
{
    public string Operation { get; } = operation;
    public string Code { get; } = code;
    public string ServerMessage { get; } = serverMessage;
}

public sealed record LoginRequest(string Username, string Password);
public sealed record LoginResult(string OpenId, string Jwt);
public sealed record UserInfo(string Name, string School, string AppUserId, string SsoId);
public sealed record User(string Name, string School, string OpenId, string AppUserId, string SsoId);
public sealed record CourseResource(string Id, string Name, long StrategyId, string InstanceId, int FinishedPoints, int TotalPoints);
public sealed record CourseChoice(string CourseName, long Start, long End, bool Archived, CourseResource Resource)
{
    public string ClassId { get; init; } = "";
    public string CurriculaId { get; init; } = "";
    public string Label => $"{CourseName} / {Resource.Name} ({Resource.FinishedPoints}/{Resource.TotalPoints})";
}
public sealed record LearningOptions(string DiscussionReply = "", string OralScript = "");
public sealed record DiscussionScope(string CourseId, string TaskId, string ClassId, string CurriculaId);
public sealed record GetDiscussionTopicsRequest(DiscussionScope Scope, int Page = 1, int PageSize = 20);
public sealed record DiscussionTopic(long Id, bool IsOwner);
public sealed record DiscussionTopicPage(IReadOnlyList<DiscussionTopic> Topics, long Total);
public sealed record CreateDiscussionTopicRequest(DiscussionScope Scope, string Title, string Content);
public sealed record GetDiscussionRepliesRequest(long TopicId, int Page = 1, int PageSize = 20);
public sealed record DiscussionReply(bool IsOwner);
public sealed record DiscussionReplyPage(IReadOnlyList<DiscussionReply> Replies, long Total);
public sealed record PostDiscussionReplyRequest(long TopicId, string Content);
public sealed record DiscussionWriteResult(bool AlreadyExists);
public sealed record ResourceDetail(string ResourceId, string CourseInstanceId);
public sealed record CourseNode(string Id, string Name, IReadOnlyList<string> Types, int QuestionCount, IReadOnlyList<CourseNode> Children);
public sealed record CourseTree(string Name, IReadOnlyList<CourseNode> Units);
public sealed record GetRequiredTasksRequest(long StrategyId, string ResourceId);
public sealed record UnitStrategy(string UnitId, IReadOnlyList<string> RequiredTasks, long StudyStartTime, long StudyEndTime);
public sealed record GetCourseProgressRequest(string ResourceId, string AppUserId);
public sealed record UnitProgress(string NodeId, string Caption, string Name, double FinishProgress);
public sealed record CourseProgress(double FinishProgress, IReadOnlyList<UnitProgress> Units);
public sealed record GetUnitProgressRequest(string UnitId, string ResourceId, string AppUserId, string SsoId);
public sealed record TaskProgress(string NodeId, double FinishProgress, IReadOnlyList<TaskProgress> Children);
public sealed record GetTaskTimesRequest(string CourseId, string UnitId, string OpenId);
public sealed record TaskTime(long Start, long End);
public sealed record GetAnswersRequest(string CourseId, string TaskId, string OpenId);
public sealed record TaskQuestion(string InstanceId, string Type, Speech.SpeechQuestion? Speech = null);
public sealed record QuestionSubmission(string Type, QuestionAnswer? Answer = null, Speech.RecordedQuestion? Recording = null);
public sealed record GetTaskInfoRequest(string CourseId, string TaskId, string Version, string OpenId);
public sealed record TaskQuestionInfo(string Id, int QuestionType);
public sealed record TaskInfo(IReadOnlyList<TaskQuestionInfo> Questions);
public sealed record QuestionAnswer(string InstanceId, IReadOnlyList<IReadOnlyList<string>> Answers, IReadOnlyList<string> ChildAnalysis, string Analysis);
public sealed record SubmitRequest(string CourseId, string TaskId, string OpenId, string QuestionType, QuestionAnswer? Answer)
{
    public IReadOnlyList<Speech.RecordedQuestion>? Recordings { get; init; }
    public IReadOnlyList<QuestionSubmission>? Questions { get; init; }
}
// Score is the server's average score. Subjective tasks (for example short-answer) often report
// 0 there while RealScore is still a full mark; Passed is the authoritative server verdict.
public sealed record SubmitResult(long Code, string Message, string Version, double? Score, bool? Passed = null, double? RealScore = null)
{
    public bool IsRateLimited => Code is 600001 or 600002;
}

