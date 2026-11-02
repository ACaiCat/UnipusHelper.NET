namespace UnipusHelp;

// One endpoint per operation; only typed models cross this boundary.
public interface IUnipusApi : IDisposable
{
    Action<string>? OnRequest { get; set; }
    Task<LoginResult> LoginAsync(LoginRequest request, CancellationToken ct);
    Task<UserInfo> GetUserInfoAsync(CancellationToken ct);
    Task<IReadOnlyList<CourseChoice>> GetCoursesAsync(CancellationToken ct);
    Task<ResourceDetail> GetResourceAsync(string resourceId, CancellationToken ct);
    Task<CourseTree> GetCourseTreeAsync(string courseId, CancellationToken ct);
    Task<IReadOnlyList<UnitStrategy>> GetRequiredTasksAsync(GetRequiredTasksRequest request, CancellationToken ct);
    Task<CourseProgress> GetCourseProgressAsync(GetCourseProgressRequest request, CancellationToken ct);
    Task<IReadOnlyList<TaskProgress>> GetUnitProgressAsync(GetUnitProgressRequest request, CancellationToken ct);
    Task<IReadOnlyDictionary<string, TaskTime>> GetTaskTimesAsync(GetTaskTimesRequest request, CancellationToken ct);
    Task<IReadOnlyList<QuestionAnswer>> GetAnswersAsync(GetAnswersRequest request, CancellationToken ct);
    Task<IReadOnlyList<TaskQuestion>> GetTaskQuestionsAsync(GetAnswersRequest request, CancellationToken ct);
    Task<TaskInfo> GetTaskInfoAsync(GetTaskInfoRequest request, CancellationToken ct);
    Task<IReadOnlyList<Speech.SpeechQuestion>> GetSpeechQuestionsAsync(GetAnswersRequest request, CancellationToken ct);
    Task<Speech.UploadedRecording> UploadRecordingAsync(Speech.RecordingUploadRequest request, CancellationToken ct);
    Task<SubmitResult> SubmitAsync(SubmitRequest request, CancellationToken ct);
    Task<DiscussionTopicPage> GetDiscussionTopicsAsync(GetDiscussionTopicsRequest request, CancellationToken ct);
    Task<DiscussionWriteResult> CreateDiscussionTopicAsync(CreateDiscussionTopicRequest request, CancellationToken ct);
    Task<DiscussionReplyPage> GetDiscussionRepliesAsync(GetDiscussionRepliesRequest request, CancellationToken ct);
    Task<DiscussionWriteResult> PostDiscussionReplyAsync(PostDiscussionReplyRequest request, CancellationToken ct);
}
