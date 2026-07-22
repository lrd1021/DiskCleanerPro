using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace DiskCleaner.Helpers
{
    /// <summary>
    /// AI 文件分析结果
    /// </summary>
    public class AIAnalysisResult
    {
        public string FilePath { get; set; }
        public string FileName { get; set; }
        public FileSafetyLevel SafetyLevel { get; set; }
        public string Description { get; set; }
        public string BelongsTo { get; set; }
        public string Suggestion { get; set; }
        public bool Success { get; set; }
        public string Error { get; set; }
    }

    public static class AIFileAnalyzer
    {
        private static readonly HttpClient _http = new HttpClient { Timeout = TimeSpan.FromSeconds(30) };

        /// <summary>批量分析未知文件（每批最多15个），支持取消与重试</summary>
        public static async Task<List<AIAnalysisResult>> AnalyzeBatchAsync(
            List<string> filePaths, AISettings settings, CancellationToken ct = default)
        {
            if (string.IsNullOrEmpty(settings.ApiKey))
                return filePaths.Select(p => new AIAnalysisResult
                {
                    FilePath = p, FileName = Path.GetFileName(p),
                    Success = false, Error = "未配置 API Key，请先在设置中配置"
                }).ToList();

            var results = new List<AIAnalysisResult>();
            const int batchSize = 15;

            for (int i = 0; i < filePaths.Count; i += batchSize)
            {
                ct.ThrowIfCancellationRequested();
                var batch = filePaths.Skip(i).Take(batchSize).ToList();
                var batchResults = await AnalyzeOneBatch(batch, settings, ct);
                results.AddRange(batchResults);
            }

            return results;
        }

        private static async Task<List<AIAnalysisResult>> AnalyzeOneBatch(
            List<string> filePaths, AISettings settings, CancellationToken ct)
        {
            var results = new List<AIAnalysisResult>();

            // 读取每个文件的基本信息（严格限制：文件名、扩展名、大小，绝不包含绝对路径或文件内容）
            var fileInfos = new List<string>();
            foreach (var path in filePaths)
            {
                try
                {
                    var fi = new FileInfo(path);
                    var size = FileSizeFormatter.Format(fi.Length);
                    var ext = Path.GetExtension(path).ToLowerInvariant();

                    fileInfos.Add($"- 文件名: {fi.Name}\n  扩展名: {ext}\n  大小: {size}");
                }
                catch
                {
                    fileInfos.Add($"- 文件名: {Path.GetFileName(path)} (无法读取)");
                }
            }

            // 只发送文件名、扩展名与大小，不发送绝对路径、目录或文件内容；强制 https 由调用方保证
            var prompt = $@"你是 Windows 系统文件分析专家。请分析以下文件，判断每个文件是否可安全删除。

严格按下面的文件顺序返回 JSON 数组，第 i 项对应第 i 个文件（不要打乱顺序、不要省略）：
[
  {{
    ""level"": ""safe"" / ""caution"" / ""danger"" / ""unknown"",
    ""description"": ""这个文件是什么（20字内）"",
    ""belongsTo"": ""属于哪个软件/系统组件"",
    ""suggestion"": ""删除建议（30字内）""
  }}
]

文件列表（按顺序）：
{string.Join("\n\n", fileInfos)}

只返回 JSON 数组，不要其他内容。";

            try
            {
                var requestBody = JsonSerializer.Serialize(new
                {
                    model = settings.Model,
                    messages = new[]
                    {
                        new { role = "system", content = "你是 Windows 文件分析专家，只返回 JSON 数组，且数组顺序与输入文件列表一致。" },
                        new { role = "user", content = prompt }
                    },
                    temperature = 0.1,
                    max_tokens = 2000
                });

                if (!settings.Endpoint.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
                {
                    foreach (var fp in filePaths)
                        results.Add(new AIAnalysisResult
                        {
                            FilePath = fp, FileName = Path.GetFileName(fp),
                            Success = false, Error = "Endpoint 必须是 https，已拒绝发送以防密钥泄露"
                        });
                    return results;
                }

                var responseText = await PostJsonAsync(settings.Endpoint, settings.ApiKey, requestBody, ct);

                var json = JsonDocument.Parse(responseText);
                var content = ExtractContent(json.RootElement) ?? "";

                // 清理 markdown 包裹
                content = content.Trim();
                if (content.StartsWith("```json")) content = content[7..];
                else if (content.StartsWith("```")) content = content[3..];
                if (content.EndsWith("```")) content = content[..^3];
                content = content.Trim();

                if (string.IsNullOrEmpty(content))
                {
                    foreach (var fp in filePaths)
                        results.Add(new AIAnalysisResult
                        {
                            FilePath = fp, FileName = Path.GetFileName(fp),
                            Success = false, Error = "API 未返回可解析的内容"
                        });
                    return results;
                }

                var parsed = JsonDocument.Parse(content).RootElement;
                var items = parsed.EnumerateArray().ToArray();

                // 按索引稳定回填，杜绝名称匹配错位（之前用文件名 Contains 会张冠李戴）
                for (int i = 0; i < filePaths.Count; i++)
                {
                    var fp = filePaths[i];
                    if (i >= items.Length)
                    {
                        results.Add(new AIAnalysisResult
                        {
                            FilePath = fp, FileName = Path.GetFileName(fp),
                            Success = false, Error = "AI 未返回该文件的判定"
                        });
                        continue;
                    }

                    var item = items[i];
                    var levelStr = item.TryGetProperty("level", out var lv) ? lv.GetString() ?? "unknown" : "unknown";
                    results.Add(new AIAnalysisResult
                    {
                        FilePath = fp,
                        FileName = Path.GetFileName(fp),
                        SafetyLevel = levelStr switch
                        {
                            "safe" => FileSafetyLevel.Safe,
                            "caution" => FileSafetyLevel.Caution,
                            "danger" => FileSafetyLevel.Danger,
                            _ => FileSafetyLevel.Unknown
                        },
                        Description = item.TryGetProperty("description", out var d) ? d.GetString() ?? "" : "",
                        BelongsTo = item.TryGetProperty("belongsTo", out var b) ? b.GetString() ?? "" : "",
                        Suggestion = item.TryGetProperty("suggestion", out var s) ? s.GetString() ?? "" : "",
                        Success = true
                    });
                }
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex)
            {
                foreach (var fp in filePaths)
                    results.Add(new AIAnalysisResult
                    {
                        FilePath = fp, FileName = Path.GetFileName(fp),
                        Success = false, Error = $"分析失败: {ex.Message}"
                    });
            }

            return results;
        }

        /// <summary>
        /// 兼容多厂商响应结构：OpenAI / DeepSeek / 智谱 / 通义 / 月之暗面 / yi（choices[].message.content），
        /// 以及文心 ernie（result 字段）等。返回 null 表示无法提取。
        /// </summary>
        private static string ExtractContent(JsonElement root)
        {
            try
            {
                if (root.TryGetProperty("choices", out var choices) &&
                    choices.ValueKind == JsonValueKind.Array && choices.GetArrayLength() > 0)
                {
                    var first = choices[0];
                    if (first.TryGetProperty("message", out var msg) &&
                        msg.TryGetProperty("content", out var c)) return c.GetString();
                    if (first.TryGetProperty("text", out var t)) return t.GetString();
                }
            }
            catch { }
            if (root.TryGetProperty("result", out var r)) return r.GetString();   // 文心
            if (root.TryGetProperty("output", out var o)) return o.GetString();
            if (root.TryGetProperty("response", out var resp)) return resp.GetString();
            return null;
        }

        /// <summary>
        /// 带指数退避重试的 POST（仅对 5xx 与网络异常重试，4xx 直接抛出）
        /// </summary>
        private static async Task<string> PostJsonAsync(string endpoint, string apiKey, string body, CancellationToken ct)
        {
            const int maxAttempts = 3;
            for (int attempt = 0; attempt < maxAttempts; attempt++)
            {
                ct.ThrowIfCancellationRequested();

                var req = new HttpRequestMessage(HttpMethod.Post, endpoint)
                {
                    Content = new StringContent(body, Encoding.UTF8, "application/json")
                };
                req.Headers.Add("Authorization", $"Bearer {apiKey}");

                HttpResponseMessage resp = null;
                try
                {
                    resp = await _http.SendAsync(req, ct);
                    var text = await resp.Content.ReadAsStringAsync(ct);
                    if (resp.IsSuccessStatusCode) return text;

                    bool isServerError = (int)resp.StatusCode >= 500;
                    if (!isServerError || attempt == maxAttempts - 1)
                        throw new HttpRequestException(
                            $"API 错误: {(int)resp.StatusCode} {resp.ReasonPhrase} - {text}");

                    // 5xx 且仍有重试机会
                    await Task.Delay(400 * (attempt + 1), ct);
                }
                catch (OperationCanceledException) { throw; }
                catch (HttpRequestException) { throw; }
                catch (Exception)
                {
                    if (attempt == maxAttempts - 1) throw;
                    await Task.Delay(400 * (attempt + 1), ct);
                }
                finally
                {
                    resp?.Dispose();
                }
            }
            throw new HttpRequestException("AI 请求重试后仍失败");
        }

    }
}
