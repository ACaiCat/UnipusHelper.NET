using System.Collections.ObjectModel;
using System.Diagnostics.CodeAnalysis;
using Terminal.Gui.App;
using Terminal.Gui.Configuration;
using Terminal.Gui.Drawing;
using Terminal.Gui.Input;
using Terminal.Gui.Text;
using Terminal.Gui.ViewBase;
using Terminal.Gui.Views;
using Color = Terminal.Gui.Drawing.Color;
using Attribute = Terminal.Gui.Drawing.Attribute;

namespace UnipusHelp;

public static class TuiApplication
{
    private const string BaseScheme = "Unipus";
    private const string AccentScheme = "UnipusAccent";
    private const string ErrorScheme = "UnipusError";

    // Terminal.Gui's Init is annotated conservatively. Its 2.5.0 AOT smoke app uses
    // the same initialization; its configuration converters are source-generated.
    [UnconditionalSuppressMessage("Trimming", "IL2026", Justification = "Terminal.Gui 2.5.0 uses its generated configuration converters; validated by the native UI smoke test.")]
    [UnconditionalSuppressMessage("AOT", "IL3050", Justification = "Terminal.Gui 2.5.0 statically registers its drivers; validated by the native UI smoke test.")]
    private static IApplication CreateApplication() => Application.Create().Init();

    public static void Run(List<LearningJob> jobs, string? proxy, CancellationToken ct, bool smoke = false, bool demo = false)
    {
        using var app = CreateApplication();
        ConfigureColors();
        using var window = new Window { Title = "UnipusHelp.NET", SchemeName = BaseScheme, BorderStyle = LineStyle.Rounded };
        var header = new Label { Text = "学习任务", X = 1, Y = 0, SchemeName = AccentScheme };
        var count = new Label { X = Pos.AnchorEnd(23), Y = 0, Width = 22, Text = "暂无运行任务" };
        var toolbar = new View { X = 1, Y = 2, Width = Dim.Fill(1), Height = 1, CanFocus = true };
        var functionButtons = new List<Button>();
        Button? previous = null;
        Button Action(string text, Action action)
        {
            var button = new Button { Text = text, X = previous is null ? 0 : Pos.Right(previous) + 1, SchemeName = AccentScheme };
            button.Accepted += (_, _) => action();
            toolbar.Add(button);
            functionButtons.Add(button);
            previous = button;
            return button;
        }

        var listFrame = new FrameView { Title = "任务", X = 1, Y = 4, Width = Dim.Percent(36), Height = Dim.Fill(2), BorderStyle = LineStyle.Rounded };
        var taskList = new ListView { Width = Dim.Fill(), Height = Dim.Fill(), SchemeName = BaseScheme };
        var taskItems = new ObservableCollection<string>();
        taskList.SetSource(taskItems);
        listFrame.Add(taskList);
        var detail = new FrameView { Title = "概览", X = Pos.Right(listFrame) + 1, Y = 4, Width = Dim.Fill(1), Height = 9, BorderStyle = LineStyle.Rounded };
        var title = new Label { Text = "尚未添加任务", X = 1, Y = 0, Width = Dim.Fill(1), Height = 2 };
        var status = new Label { Text = "点击“添加”开始", X = 1, Y = 2, Width = Dim.Fill(1), Height = 1 };
        var current = new Label { X = 1, Y = 3, Width = Dim.Fill(1), Height = 2 };
        var progress = new Label { X = 1, Y = 6, Width = Dim.Fill(1), Text = "" };
        detail.Add(title, status, current, progress);
        var logFrame = new FrameView { X = Pos.Left(detail), Y = Pos.Bottom(detail), Width = Dim.Fill(1), Height = Dim.Fill(2), BorderStyle = LineStyle.Rounded };
        var activity = new ListView { Width = Dim.Fill(), Height = Dim.Fill(), SchemeName = BaseScheme };
        activity.SetSource(new ObservableCollection<string> { "添加任务后，这里会显示运行记录。" });
        logFrame.Add(activity);
        var footer = new Label { X = 1, Y = Pos.AnchorEnd(1), Width = Dim.Fill(1), Text = "↑↓ 切换任务与功能  ←→ 切换按钮  Enter 确认  Esc 退出" };

        LearningJob? Selected() => taskList.SelectedItem is int selected && selected >= 0 && selected / 4 < jobs.Count ? jobs[selected / 4] : null;
        var lastDetails = "";
        var renderedLogErrors = new HashSet<int>();
        var refreshing = false;
        void Refresh()
        {
            if (refreshing) return;
            refreshing = true;
            try
            {
                var selected = taskList.SelectedItem ?? 0;
                while (taskItems.Count > jobs.Count * 4) taskItems.RemoveAt(taskItems.Count - 1);
                for (var i = 0; i < jobs.Count; i++)
                {
                    var snapshot = jobs[i].Snapshot();
                    string[] rows = [$"{i + 1}. {jobs[i].AccountName}", $"   {jobs[i].ResourceName}", $"   {CompactState(snapshot.State)} · 已提交 {snapshot.Submitted}", ""];
                    for (var row = 0; row < rows.Length; row++)
                    {
                        var index = i * 4 + row;
                        if (index >= taskItems.Count) taskItems.Add(rows[row]);
                        else if (taskItems[index] != rows[row]) taskItems[index] = rows[row];
                    }
                }
                if (jobs.Count > 0 && taskList.SelectedItem is null) taskList.SelectedItem = Math.Min(selected / 4, jobs.Count - 1) * 4;
                count.Text = $"{jobs.Count} 个任务 · {jobs.Count(x => x.Execution?.IsCompleted == false)} 个进行中";
                var job = Selected();
                if (job is null)
                {
                    title.Text = "尚未添加任务";
                    status.Text = "点击“添加”开始";
                    current.Text = "";
                    progress.Text = "";
                    if (lastDetails != "empty") activity.SetSource(new ObservableCollection<string> { "添加任务后，这里会显示运行记录。" });
                    lastDetails = "empty";
                    return;
                }
                var s = job.Snapshot();
                title.Text = job.Label;
                status.Text = s.State;
                status.SchemeName = s.State == "出错" ? ErrorScheme : BaseScheme;
                current.SchemeName = s.State == "出错" ? ErrorScheme : BaseScheme;
                current.Text = s.Current;
                progress.Text = $"本次已提交 {s.Submitted} 项     已跳过 {s.Skipped} 项";
                var visibleLogs = s.Logs.Where(x => !x.Message.StartsWith("GET /", StringComparison.Ordinal) && !x.Message.StartsWith("POST /", StringComparison.Ordinal)).ToArray();
                var textLogs = string.Join('\n', visibleLogs);
                var key = job.Label + activity.Viewport.Width + textLogs;
                if (lastDetails != key)
                {
                    lastDetails = key;
                    var lines = new List<string>();
                    renderedLogErrors.Clear();
                    foreach (var entry in visibleLogs)
                        foreach (var line in TextFormatter.WordWrapText(entry.ToString(), Math.Max(10, activity.Viewport.Width)))
                        {
                            if (entry.IsError) renderedLogErrors.Add(lines.Count);
                            lines.Add(line);
                        }
                    activity.SetSource(new ObservableCollection<string>(lines));
                    if (lines.Count > 0) activity.SelectedItem = lines.Count - 1;
                }
            }
            finally { refreshing = false; }
        }

        // Terminal.Gui selects the last button by default, so cancellation goes last.
        bool Confirm(string titleText, string text) => MessageBox.Query(app, titleText, text, "确认", "取消") == 0;
        void StopSelected()
        {
            var job = Selected();
            if (job is null || job.Execution?.IsCompleted == true) return;
            if (Confirm("停止任务", $"确定停止 {job.ShortLabel}？\n已提交的内容会保留。")) job.Stop();
        }

        Action("添加", () => { AddTask(app, jobs, proxy, ct, demo: demo); Refresh(); });
        Action("暂停", () => Selected()?.Pause());
        Action("继续", () => Selected()?.Resume());
        Action("停止", StopSelected);
        Action("移除", () =>
        {
            var job = Selected();
            if (job?.Execution?.IsCompleted != true) return;
            if (!Confirm("移除任务", $"确定从列表移除 {job.ShortLabel}？")) return;
            jobs.Remove(job);
            job.Dispose();
            taskList.SelectedItem = jobs.Count == 0 ? null : 0;
            Refresh();
        });
        Action("退出", () => app.RequestStop(window));
        taskList.ValueChanged += (_, _) => Refresh();
        taskList.KeyDown += (_, key) =>
        {
            if (key != Key.CursorUp && key != Key.CursorDown) return;
            var direction = key == Key.CursorUp ? -1 : 1;
            var next = (taskList.SelectedItem ?? 0) / 4 + direction;
            if (jobs.Count == 0 || next < 0 || next >= jobs.Count)
                functionButtons[direction < 0 ? functionButtons.Count - 1 : 0].SetFocus();
            else taskList.SelectedItem = next * 4;
            key.Handled = true;
        };
        taskList.RowRender += (_, e) =>
        {
            if (e.Row / 4 < jobs.Count && jobs[e.Row / 4].Snapshot().State == "出错")
                e.RowAttribute = new Attribute(new Color(255, 112, 124), new Color(19, 24, 33));
        };
        activity.RowRender += (_, e) =>
        {
            if (renderedLogErrors.Contains(e.Row)) e.RowAttribute = new Attribute(new Color(255, 112, 124), new Color(19, 24, 33));
        };
        window.IsRunningChanging += (_, e) =>
        {
            if (!e.NewValue && !smoke && !ct.IsCancellationRequested)
                e.Cancel = !Confirm("退出程序", $"确定退出 UnipusHelp.NET？\n{jobs.Count(x => x.Execution?.IsCompleted == false)} 个进行中的任务将停止。");
        };
        window.KeyDown += (_, key) =>
        {
            if (key == Key.F2) { AddTask(app, jobs, proxy, ct, demo: demo); key.Handled = true; }
            if (key == Key.F6) { Selected()?.Pause(); key.Handled = true; }
            if (key == Key.F7) { Selected()?.Resume(); key.Handled = true; }
            if (key == Key.F8) { StopSelected(); key.Handled = true; }
        };
        for (var index = 0; index < functionButtons.Count; index++)
        {
            var buttonIndex = index;
            functionButtons[index].KeyDown += (_, key) =>
            {
                if (key != Key.CursorUp && key != Key.CursorDown && key != Key.CursorLeft && key != Key.CursorRight) return;
                var backwards = key == Key.CursorUp || key == Key.CursorLeft;
                var next = buttonIndex + (backwards ? -1 : 1);
                if (next >= 0 && next < functionButtons.Count) functionButtons[next].SetFocus();
                else if (jobs.Count > 0)
                {
                    taskList.SelectedItem = backwards ? (jobs.Count - 1) * 4 : 0;
                    taskList.SetFocus();
                }
                else functionButtons[(next + functionButtons.Count) % functionButtons.Count].SetFocus();
                key.Handled = true;
            };
        }
        window.Add(header, count, toolbar, listFrame, detail, logFrame, footer);
        app.AddTimeout(TimeSpan.FromMilliseconds(200), () =>
        {
            if (ct.IsCancellationRequested) { app.RequestStop(); return false; }
            foreach (var job in jobs) job.Start();
            Refresh();
            return true;
        });
        var navigationVerified = false;
        if (smoke)
        {
            app.AddTimeout(TimeSpan.FromMilliseconds(400), () => { AddTask(app, jobs, proxy, ct, preview: true, demo: demo); return false; });
            app.AddTimeout(TimeSpan.FromMilliseconds(1500), () =>
            {
                taskList.SelectedItem = 0;
                taskList.SetFocus();
                window.NewKeyDownEvent(Key.CursorUp);
                var reachedButtons = functionButtons[^1].HasFocus;
                window.NewKeyDownEvent(Key.CursorDown);
                var returnedToTasks = taskList.HasFocus;
                functionButtons[0].SetFocus();
                window.NewKeyDownEvent(Key.CursorDown);
                navigationVerified = reachedButtons && returnedToTasks && functionButtons[1].HasFocus;
                return false;
            });
            app.AddTimeout(TimeSpan.FromSeconds(4), () => { app.RequestStop(); return false; });
        }
        Refresh();
        app.Run(window);
        if (smoke && !navigationVerified) throw new InvalidOperationException("方向键焦点切换验证失败。");
    }

    private static string CompactState(string state) => state switch
    {
        "执行结束，仍有未完成任务" => "未完成",
        "准备就绪" => "就绪",
        _ => state
    };

    private static void AddTask(IApplication app, List<LearningJob> jobs, string? proxy, CancellationToken ct, bool preview = false, bool demo = false)
    {
        using var dialog = new Window
        {
            Title = "添加学习任务",
            X = Pos.Center(),
            Y = Pos.Center(),
            Width = Dim.Percent(85),
            Height = Dim.Percent(85),
            SchemeName = BaseScheme,
            BorderStyle = LineStyle.Rounded
        };
        using var cancel = CancellationTokenSource.CreateLinkedTokenSource(ct);
        var api = new UnipusClient(proxy, demo ? new DemoHandler { ChoiceType = "multichoice", ExtraType = "discussion" } : null);
        User? user = null;
        IReadOnlyList<CourseChoice> courses = [];
        var transferred = false;
        var closed = false;
        var busy = false;
        var account = new TextField { X = 12, Y = 1, Width = Dim.Fill(2), SchemeName = BaseScheme };
        var password = new TextField { X = 12, Y = 3, Width = Dim.Fill(2), Secret = true, SchemeName = BaseScheme };
        var discussionReply = new TextField { X = 12, Y = 5, Width = Dim.Fill(2), SchemeName = BaseScheme };
        var oralScript = new TextField { X = 12, Y = 7, Width = Dim.Fill(2), SchemeName = BaseScheme };
        var note = new Label { X = 2, Y = 6, Width = Dim.Fill(2), Text = "" };
        var courseFrame = new FrameView { Title = "教程", X = 2, Y = 9, Width = Dim.Fill(2), Height = Dim.Fill(5), BorderStyle = LineStyle.Rounded };
        var courseList = new ListView { Width = Dim.Fill(), Height = Dim.Fill(), SchemeName = BaseScheme };
        courseFrame.Add(courseList);
        var message = new Label { X = 2, Y = Pos.AnchorEnd(4), Width = Dim.Fill(2), Height = 2, SchemeName = ErrorScheme };
        var submit = new Button { Text = "登录", X = 2, Y = Pos.AnchorEnd(1), SchemeName = AccentScheme };
        var close = new Button { Text = "取消", X = Pos.Right(submit) + 2, Y = Pos.Top(submit), SchemeName = AccentScheme };
        close.Accepted += (_, _) => app.RequestStop(dialog);
        account.Accepted += (_, _) => password.SetFocus();
        password.Accepted += (_, _) => submit.InvokeCommand(Command.Accept);
        courseList.Accepted += (_, _) => { if (user is not null) submit.InvokeCommand(Command.Accept); };
        submit.Accepted += (_, _) =>
        {
            if (busy) return;
            if (user is not null)
            {
                if (courseList.SelectedItem is not int selected || selected < 0 || selected >= courses.Count) { message.Text = "请选择教程。"; return; }
                var choice = courses[selected];
                if (!LearningJob.IsCourseOpen(choice, DateTimeOffset.UtcNow)) { message.Text = "该课程尚未开始、已结束或已归档。"; return; }
                if (jobs.Any(x => x.IsSameTarget(user, choice) && x.Execution?.IsCompleted == false)) { message.Text = "该教程的任务正在运行。"; return; }
                jobs.Add(new LearningJob(api, user, choice, demo, new LearningOptions(discussionReply.Text, oralScript.Text)));
                transferred = true;
                app.RequestStop(dialog);
                return;
            }
            var username = account.Text.Trim();
            var secret = password.Text;
            if (string.IsNullOrWhiteSpace(username) || string.IsNullOrEmpty(secret)) { message.Text = "请填写账号和密码。"; return; }
            busy = true;
            submit.Enabled = false;
            message.SchemeName = BaseScheme;
            message.Text = "正在登录并读取课程…";
            _ = Task.Run(async () =>
            {
                try
                {
                    var loggedIn = await Program.AuthenticateAsync(api, new(username, secret), cancel.Token);
                    var available = await api.GetCoursesAsync(cancel.Token);
                    app.Invoke(() =>
                    {
                        if (closed) return;
                        user = loggedIn;
                        courses = available;
                        password.Text = "";
                        account.Enabled = password.Enabled = false;
                        note.Text = $"{user.School} · {user.Name}";
                        courseList.SetSource(new ObservableCollection<string>(courses.Select(x => x.Label)));
                        if (courses.Count > 0) courseList.SelectedItem = 0;
                        message.Text = courses.Count == 0 ? "未找到教程。" : "选择教程后点击“开始任务”。";
                        submit.Text = "开始任务";
                        submit.Enabled = courses.Count > 0;
                        busy = false;
                        courseList.SetFocus();
                    });
                }
                catch (Exception ex)
                {
                    if (cancel.IsCancellationRequested) return;
                    app.Invoke(() => { if (closed) return; message.SchemeName = ErrorScheme; message.Text = ex.Message; submit.Enabled = true; busy = false; });
                }
            });
        };
        dialog.Add(new Label { Text = "账号", X = 2, Y = 1 }, account,
            new Label { Text = "密码", X = 2, Y = 3 }, password,
            new Label { Text = "讨论回复", X = 2, Y = 5 }, discussionReply, note,
            new Label { Text = "口语稿", X = 2, Y = 7 }, oralScript,
            courseFrame, message, submit, close);
        if (preview)
        {
            app.AddTimeout(TimeSpan.FromMilliseconds(100), () =>
            {
                oralScript.Text = "My own oral script.";
                var options = new LearningOptions(discussionReply.Text, oralScript.Text);
                if (options.OralScript != "My own oral script." || options.DiscussionReply != "")
                    throw new InvalidOperationException("口语稿输入框配置验证失败。");
                return false;
            });
            app.AddTimeout(TimeSpan.FromMilliseconds(500), () => { app.RequestStop(dialog); return false; });
        }
        try { app.Run(dialog); }
        finally
        {
            closed = true;
            cancel.Cancel();
            password.Text = "";
            if (!transferred) api.Dispose();
        }
    }

    private static void ConfigureColors()
    {
        var background = new Color(19, 24, 33);
        var foreground = new Color(216, 222, 233);
        var accent = new Color(136, 192, 208);
        SchemeManager.AddScheme(BaseScheme, new Scheme
        {
            Normal = new Attribute(foreground, background),
            Focus = new Attribute(background, accent),
            HotNormal = new Attribute(accent, background),
            HotFocus = new Attribute(background, accent),
            Disabled = new Attribute(new Color(95, 108, 128), background),
            Editable = new Attribute(foreground, new Color(33, 41, 55))
        });
        SchemeManager.AddScheme(AccentScheme, new Scheme
        {
            Normal = new Attribute(accent, background),
            Focus = new Attribute(background, accent),
            HotNormal = new Attribute(accent, background),
            HotFocus = new Attribute(background, accent)
        });
        SchemeManager.AddScheme(ErrorScheme, new Scheme
        {
            Normal = new Attribute(new Color(255, 112, 124), background),
            Focus = new Attribute(new Color(255, 112, 124), background)
        });
    }
}
