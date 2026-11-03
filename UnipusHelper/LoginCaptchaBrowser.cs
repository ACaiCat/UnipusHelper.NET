using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;

namespace UnipusHelper;

public static class LoginCaptchaBrowser
{
    public static async Task<JsonObject> SolveAsync(
        Func<string, JsonObject?, CancellationToken, Task<JsonNode>> send,
        Action<string> showUrl, CancellationToken ct, Action<string>? launch = null)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(TimeSpan.FromMinutes(5));
        // Reserve an ephemeral loopback port; HttpListener only accepts this exact host.
        var reservation = new TcpListener(IPAddress.Loopback, 0);
        reservation.Start();
        var port = ((IPEndPoint)reservation.LocalEndpoint).Port;
        reservation.Stop();
        using var listener = new HttpListener();
        var origin = $"http://127.0.0.1:{port}";
        listener.Prefixes.Add(origin + "/");
        listener.Start();
        var path = "/" + Convert.ToHexString(RandomNumberGenerator.GetBytes(24)) + "/";
        var url = origin + path;
        showUrl(url);
        try
        {
            if (launch is not null) launch(url);
            else Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException)
        { /* The terminal also shows the URL for manual opening. */ }
        try
        {
            while (true)
            {
                var context = await listener.GetContextAsync().WaitAsync(timeout.Token);
                var request = context.Request;
                var response = context.Response;
                response.Headers["Cache-Control"] = "no-store";
                response.Headers["X-Frame-Options"] = "DENY";
                response.Headers["Referrer-Policy"] = "same-origin";
                JsonObject? result = null;
                try
                {
                    var route = request.Url!.AbsolutePath;
                    string content;
                    if (route == path && request.HttpMethod == "GET")
                    {
                        response.ContentType = "text/html; charset=utf-8";
                        content = Page;
                    }
                    else if (request.UrlReferrer?.AbsoluteUri == url &&
                             (request.Headers["Origin"] is null || request.Headers["Origin"] == origin))
                    {
                        response.ContentType = "application/json; charset=utf-8";
                        JsonObject? body = null;
                        if (request.HttpMethod == "POST")
                        {
                            if (request.ContentLength64 is < 0 or > 16384) throw new InvalidDataException("验证码请求过大。");
                            using var reader = new StreamReader(request.InputStream, Encoding.UTF8);
                            body = Json.Parse(await reader.ReadToEndAsync(timeout.Token)).AsObject();
                        }
                        if (route == path + "complete" && body is not null)
                        {
                            var image = !string.IsNullOrWhiteSpace(body["captchaCode"].Text()) && !string.IsNullOrWhiteSpace(body["encodeCaptha"].Text());
                            var slider = new[] { "challenge", "validate", "seccode" }.All(k => !string.IsNullOrWhiteSpace(body[k].Text()));
                            if (!image && !slider) throw new InvalidDataException("验证码结果不完整。");
                            result = body;
                            content = "{}";
                        }
                        else
                        {
                            var upstream = route == path + "register" && request.HttpMethod == "GET" ? "sso/5.0/sso/register" :
                                request.HttpMethod == "POST" && route is "/sso/4.0/sso/cip/image_captcha2" or "/sso/4.0/sso/captcha_verify" ? route[1..] : null;
                            if (upstream is null) { response.StatusCode = 404; content = "{}"; }
                            else content = (await send(upstream, body, timeout.Token)).ToJsonString();
                        }
                    }
                    else { response.StatusCode = 403; content = "Forbidden"; }
                    var bytes = Encoding.UTF8.GetBytes(content);
                    response.ContentLength64 = bytes.Length;
                    await response.OutputStream.WriteAsync(bytes, timeout.Token);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    response.StatusCode = 502;
                    var bytes = Encoding.UTF8.GetBytes(new JsonObject { ["msg"] = "验证码请求失败，请刷新重试。" }.ToJsonString());
                    await response.OutputStream.WriteAsync(bytes, timeout.Token);
                }
                finally { response.Close(); }
                if (result is not null) return result;
            }
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        { throw new TimeoutException("验证码等待超时，请重新登录。完成验证后请保留浏览器页面直到程序继续登录。"); }
    }

    private const string Page = """
        <!doctype html><html lang="zh-CN"><meta charset="utf-8"><meta name="viewport" content="width=device-width">
        <title>U 校园登录验证</title>
        <style>body{font:18px system-ui;max-width:640px;margin:12vh auto;padding:24px}button{padding:12px;margin:8px;cursor:pointer}</style>
        <h1>完成登录验证</h1><p>请手动完成验证码，验证后程序会自动继续登录。此页面不需要输入账号或密码。</p>
        <button onclick="slider()">滑块验证</button><button onclick="image()">图片验证码</button><p id="status">正在加载验证码…</p>
        <script src="https://static.geetest.com/static/js/gt.0.4.9.js"></script>
        <script src="https://birdflock.unipus.cn/rel/fe/usso-captcha/1.2.1/img-captcha.umd.js"></script>
        <script>
        const status=document.getElementById('status');
        async function complete(data){try{const r=await fetch('complete',{method:'POST',headers:{'Content-Type':'application/json'},body:JSON.stringify(data)});if(!r.ok)throw Error();status.textContent='验证已提交，请返回程序查看登录结果。';}catch{status.textContent='提交失败或已超时，请返回程序重新登录。';}}
        function image(){if(!window.ssoImgCaptcha){status.textContent='图片组件加载失败，请刷新页面或尝试滑块验证。';return;}window.ssoImgCaptcha.open({mode:2,extra:{expend:0,source:'sso'},onOk:(code,encoded)=>complete({captchaCode:code,encodeCaptha:encoded}),onError:e=>{status.textContent=e.msg||'验证失败，请重试';},onCancel:()=>{status.textContent='验证已取消，可点击按钮重试。';}});}
        async function slider(){try{const r=await fetch('register');if(!r.ok)throw Error();const d=await r.json();if(String(d.code)!=='0'||!window.initGeetest)throw Error();initGeetest({gt:d.rs.gt,challenge:d.rs.challenge,offline:false,new_captcha:d.rs.new_captcha,product:'bind'},c=>{c.onReady(()=>{status.textContent='请完成滑块验证';c.verify();}).onSuccess(()=>{const v=c.getValidate();if(v)complete({challenge:v.geetest_challenge,seccode:v.geetest_seccode,validate:v.geetest_validate});}).onError(()=>{status.textContent='滑块加载失败，请尝试图片验证码。';});});}catch{status.textContent='滑块加载失败，正在切换图片验证码。';image();}}
        slider();
        </script></html>
        """;
}
