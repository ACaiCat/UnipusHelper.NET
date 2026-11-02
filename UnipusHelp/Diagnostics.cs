using System.Text;

namespace UnipusHelp;

internal static class Diagnostics
{
    public static async Task<int> RunAsync()
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(3));
        var ct = timeout.Token;
        Console.Write("账号：");
        var username = Console.ReadLine() ?? "";
        Console.Write("密码：");
        var secret = ReadSecret();
        using IUnipusApi api = new UnipusClient();
        api.OnRequest = text => Console.WriteLine(text);
        try
        {
            var answerFailures = 0;
            var user = await Program.AuthenticateAsync(api, new(username, secret), ct);
            Console.WriteLine("登录及用户信息：成功");
            var courses = await api.GetCoursesAsync(ct);
            Console.WriteLine($"课程数：{courses.Count}");
            foreach (var choice in courses.Where(x => LearningJob.IsCourseOpen(x, DateTimeOffset.UtcNow)))
            {
                Console.WriteLine("教程：" + choice.Resource.Name);
                var detail = await api.GetResourceAsync(choice.Resource.Id, ct);
                Console.WriteLine("目录 ID 与详情 ID 相同：" + (choice.Resource.InstanceId == detail.CourseInstanceId));
                var tree = await api.GetCourseTreeAsync(choice.Resource.InstanceId, ct);
                Console.WriteLine($"目录成功：{tree.Units.Count} 个单元");
                var required = await api.GetRequiredTasksAsync(new(choice.Resource.StrategyId, detail.ResourceId), ct);
                var progress = await api.GetCourseProgressAsync(new(detail.ResourceId, user.AppUserId), ct);
                Console.WriteLine($"必修策略：{required.Count} 个单元；进度：{progress.FinishProgress}%");
                var unit = progress.Units.FirstOrDefault();
                if (unit is not null)
                {
                    var times = await api.GetTaskTimesAsync(new(detail.CourseInstanceId, unit.NodeId, user.OpenId), ct);
                    var tasks = await api.GetUnitProgressAsync(new(unit.NodeId, detail.ResourceId, user.AppUserId, user.SsoId), ct);
                    Console.WriteLine($"任务时间：{times.Count} 项；单元进度：{tasks.Count} 个根节点");
                }
                var nodes = LearningJob.Index(tree.Units, x => x.Id, x => x.Children);
                foreach (var pendingUnit in progress.Units.Where(x => x.FinishProgress < 100))
                {
                    var unitProgress = await api.GetUnitProgressAsync(new(pendingUnit.NodeId, detail.ResourceId, user.AppUserId, user.SsoId), ct);
                    var taskProgress = LearningJob.Index(unitProgress, x => x.NodeId, x => x.Children);
                    var strategy = required.FirstOrDefault(x => x.UnitId == pendingUnit.NodeId);
                    foreach (var taskId in strategy?.RequiredTasks ?? [])
                    {
                        if (!taskProgress.TryGetValue(taskId, out var p) || p.FinishProgress >= 100 || !nodes.TryGetValue(taskId, out var n) || n.Types.Count != 1 || n.QuestionCount > 1 || !Protocol.Supported.Contains(n.Types[0]) || Protocol.Preset.Contains(n.Types[0]) || Protocol.SpeechTypes.Contains(n.Types[0])) continue;
                        try
                        {
                            var found = await api.GetAnswersAsync(new(detail.CourseInstanceId, taskId, user.OpenId), ct);
                            foreach (var a in found) Protocol.ValidateSubmit(new(detail.CourseInstanceId, taskId, user.OpenId, n.Types[0], a));
                            Console.WriteLine($"答案核查成功：{n.Name} ({n.Types[0]}) {taskId}");
                        }
                        catch (InvalidDataException ex)
                        {
                            answerFailures++;
                            Console.WriteLine($"答案核查失败：{n.Name} ({n.Types[0]}) {taskId}：{ex.Message}");
                        }
                    }
                }
                var speakingNodes = nodes.Values.Where(x => x.Types.Any(Protocol.SpeechTypes.Contains)).ToArray();
                Console.WriteLine("口语类型：" + string.Join(", ", speakingNodes.SelectMany(x => x.Types).Distinct()));
                foreach (var oral in speakingNodes.Where(x => x.Types.All(Protocol.SpeechTypes.Contains)).GroupBy(x => x.Types[0]).Select(x => x.First()))
                {
                    Console.WriteLine("口语样本：" + oral.Name);
                    var questions = await api.GetSpeechQuestionsAsync(new(detail.CourseInstanceId, oral.Id, user.OpenId), ct);
                    Console.WriteLine($"口语题目：{questions.Count} 组、{questions.Sum(x => x.Parts.Count)} 条录音；协议类型 {string.Join(", ", questions.Select(x => x.Type + "/" + x.ReplyType))}");
                }
                var discussionId = required.SelectMany(x => x.RequiredTasks).FirstOrDefault(id => nodes.TryGetValue(id, out var n) && n.Types.Contains("discussion"));
                if (discussionId is not null)
                {
                    var topics = await api.GetDiscussionTopicsAsync(new(new(detail.CourseInstanceId, discussionId, choice.ClassId, choice.CurriculaId)), ct);
                    Console.WriteLine($"讨论主题读取：{topics.Topics.Count} 项（未发表回复）");
                }
                var sampleId = required.SelectMany(x => x.RequiredTasks).FirstOrDefault(id => nodes.TryGetValue(id, out var n) &&
                    n.Types.Count == 1 && n.QuestionCount == 1 && Protocol.Supported.Contains(n.Types[0]) && !Protocol.Preset.Contains(n.Types[0]));
                if (sampleId is not null)
                {
                    var answers = await api.GetAnswersAsync(new(detail.CourseInstanceId, sampleId, user.OpenId), ct);
                    Console.WriteLine($"答案读取与解密：成功，{answers.Count} 个题目（不输出答案内容）");
                }
            }
            Console.WriteLine($"只读联调完成，答案核查失败 {answerFailures} 项，未提交答案。");
            return answerFailures == 0 ? 0 : 1;
        }
        catch (Exception ex) { Console.Error.WriteLine(ex.Message); return 1; }
    }

    private static string ReadSecret()
    {
        if (Console.IsInputRedirected) return Console.ReadLine() ?? "";
        var value = new StringBuilder();
        while (true)
        {
            var key = Console.ReadKey(true);
            if (key.Key == ConsoleKey.Enter) { Console.WriteLine(); return value.ToString(); }
            if (key.Key == ConsoleKey.Backspace) { if (value.Length > 0) value.Length--; }
            else if (!char.IsControl(key.KeyChar)) value.Append(key.KeyChar);
        }
    }
}
