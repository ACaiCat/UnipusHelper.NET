using System.Globalization;
using System.Text.Json.Nodes;

namespace UnipusHelp;

// All protocol JSON uses the DOM, avoiding reflection-based serialization in Native AOT.
public static class Json
{
    public static void Push(this JsonArray array, JsonNode? node) => array.Add(node);
    public static JsonNode Parse(string text) => JsonNode.Parse(text) ?? throw new InvalidDataException("服务端返回空 JSON。");
    public static JsonNode Unwrap(this JsonNode node) => node is JsonValue v && v.TryGetValue<string>(out var s) ? Parse(s) : node;
    public static JsonNode Need(this JsonNode? node, string key) => node?[key] ?? throw new InvalidDataException($"响应缺少字段：{key}");
    public static string Text(this JsonNode? node, string fallback = "") => node?.ToString() ?? fallback;
    public static string RequiredText(this JsonNode node, string key)
    {
        var text = node.Need(key).Text();
        return string.IsNullOrWhiteSpace(text) ? throw new InvalidDataException($"响应字段为空：{key}") : text;
    }
    public static double Number(this JsonNode? node, double fallback = 0) => double.TryParse(node.Text(), NumberStyles.Float, CultureInfo.InvariantCulture, out var n) ? n : fallback;
    public static long Integer(this JsonNode? node, long fallback = 0) => long.TryParse(node.Text(), CultureInfo.InvariantCulture, out var n) ? n : fallback;
    public static bool Flag(this JsonNode? node) => bool.TryParse(node.Text(), out var b) && b;
    public static JsonArray Array(this JsonNode? node) => node as JsonArray ?? throw new InvalidDataException("预期 JSON 数组。");
    public static IEnumerable<JsonNode> Items(this JsonNode? node) => node.Array().Select(x => x ?? throw new InvalidDataException("数组含空元素。"));
    public static void Check(JsonNode node, long expected, string operation = "请求")
    {
        if (node["code"].Integer(long.MinValue) != expected || node["success"]?.Text() == "false")
        {
            var message = new[] { "msg", "message", "error_description", "errorMsg", "error", "detail" }
                .Select(key => node[key] is JsonValue value ? value.Text() : "")
                .FirstOrDefault(text => !string.IsNullOrWhiteSpace(text)) ?? "服务端未提供错误说明";
            throw new UnipusApiException(operation, node["code"].Text("无状态码"), message);
        }
    }
}
