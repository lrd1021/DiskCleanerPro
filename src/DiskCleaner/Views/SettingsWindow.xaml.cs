using System;
using System.Collections.Generic;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using DiskCleaner.Helpers;

namespace DiskCleaner.Views
{
    public partial class SettingsWindow : Window
    {
        private readonly Dictionary<string, (string endpoint, string[] models)> _presets = new()
        {
            ["openai"] = ("https://api.openai.com/v1/chat/completions",
                new[] { "gpt-4o-mini", "gpt-4o", "gpt-4-turbo", "gpt-3.5-turbo" }),
            ["deepseek"] = ("https://api.deepseek.com/chat/completions",
                new[] { "deepseek-chat", "deepseek-reasoner" }),
            ["zhipu"] = ("https://open.bigmodel.cn/api/paas/v4/chat/completions",
                new[] { "glm-4-flash", "glm-4-plus", "glm-4" }),
            ["qwen"] = ("https://dashscope.aliyuncs.com/compatible-mode/v1/chat/completions",
                new[] { "qwen-turbo", "qwen-plus", "qwen-max" }),
            ["moonshot"] = ("https://api.moonshot.cn/v1/chat/completions",
                new[] { "moonshot-v1-8k", "moonshot-v1-32k", "moonshot-v1-128k" }),
            ["ernie"] = ("https://aip.baidubce.com/rpc/2.0/ai_custom/v1/wenxinworkshop/chat/completions",
                new[] { "ernie-4.0-turbo", "ernie-3.5-8k" }),
            ["yi"] = ("https://api.lingyiwanwu.com/v1/chat/completions",
                new[] { "yi-large", "yi-medium", "yi-lightning" }),
        };

        private readonly AISettings _settings;

        public SettingsWindow()
        {
            InitializeComponent();
            _settings = SettingsManager.Load();

            // 匹配预设
            string matched = "custom";
            foreach (var kv in _presets)
            {
                if (kv.Value.endpoint == _settings.Endpoint)
                {
                    matched = kv.Key;
                    break;
                }
            }

            foreach (ComboBoxItem item in CmbProvider.Items)
            {
                if ((string)item.Tag == matched)
                {
                    CmbProvider.SelectedItem = item;
                    break;
                }
            }

            ApplyPreset(matched);
            TxtApiKey.Password = _settings.ApiKey;
            CmbModel.Text = _settings.Model;
        }

        private void ApplyPreset(string tag)
        {
            if (_presets.TryGetValue(tag, out var p))
            {
                TxtEndpoint.Text = p.endpoint;
                LblEndpoint.Visibility = Visibility.Collapsed;
                TxtEndpoint.Visibility = Visibility.Collapsed;

                CmbModel.Items.Clear();
                foreach (var m in p.models)
                    CmbModel.Items.Add(m);
                CmbModel.SelectedIndex = 0;
            }
            else
            {
                TxtEndpoint.Text = string.IsNullOrEmpty(_settings.Endpoint)
                    ? "" : _settings.Endpoint;
                LblEndpoint.Visibility = Visibility.Visible;
                TxtEndpoint.Visibility = Visibility.Visible;

                if (CmbModel.Items.Count == 0)
                    CmbModel.Items.Add(_settings.Model);
            }
        }

        private void CmbProvider_Changed(object sender, SelectionChangedEventArgs e)
        {
            if (CmbProvider.SelectedItem is ComboBoxItem item)
                ApplyPreset((string)item.Tag);
        }

        private async void BtnTest_Click(object sender, RoutedEventArgs e)
        {
            BtnTest.IsEnabled = false;
            BtnTest.Content = "…";
            BtnTest.Background = System.Windows.Media.Brushes.Gray;
            TxtStatus.Text = "连接中…";

            System.Windows.Media.Brush resultBg = null;
            string resultText = "";

            try
            {
                var endpoint = TxtEndpoint.Text.Trim();
                var apiKey = TxtApiKey.Password;
                var model = CmbModel.Text.Trim();

                if (string.IsNullOrEmpty(apiKey))
                {
                    TxtStatus.Text = "请先输入 API Key";
                    return;
                }

                if (string.IsNullOrEmpty(endpoint))
                {
                    TxtStatus.Text = "请选择 AI 提供商";
                    return;
                }

                // 必须先校验 https，避免把 API Key 以明文发往 http 自定义地址
                if (!endpoint.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
                {
                    resultBg = TryFindResource("DangerBrush") as System.Windows.Media.Brush
                        ?? System.Windows.Media.Brushes.Red;
                    resultText = "✗";
                    TxtStatus.Text = "地址必须以 https:// 开头";
                    return;
                }

                var body = JsonSerializer.Serialize(new
                {
                    model,
                    messages = new[] { new { role = "user", content = "ok" } },
                    max_tokens = 3
                });

                using var http = new HttpClient { Timeout = System.TimeSpan.FromSeconds(15) };
                var req = new HttpRequestMessage(HttpMethod.Post, endpoint)
                {
                    Content = new StringContent(body, Encoding.UTF8, "application/json")
                };
                req.Headers.Add("Authorization", $"Bearer {apiKey}");

                var resp = await http.SendAsync(req);
                if (resp.IsSuccessStatusCode)
                {
                    resultBg = TryFindResource("SuccessBrush") as System.Windows.Media.Brush
                        ?? System.Windows.Media.Brushes.Green;
                    resultText = "✓";
                    TxtStatus.Text = "连接成功";
                }
                else
                {
                    var errBody = await resp.Content.ReadAsStringAsync();
                    resultBg = TryFindResource("DangerBrush") as System.Windows.Media.Brush
                        ?? System.Windows.Media.Brushes.Red;
                    resultText = "✗";
                    TxtStatus.Text = $"错误 {(int)resp.StatusCode}";
                }
            }
            catch (System.Exception ex)
            {
                resultBg = TryFindResource("DangerBrush") as System.Windows.Media.Brush
                    ?? System.Windows.Media.Brushes.Red;
                resultText = "✗";
                TxtStatus.Text = $"失败: {ex.Message}";
            }
            finally
            {
                BtnTest.IsEnabled = true;
                BtnTest.Content = resultText;
                BtnTest.Background = resultBg;
            }
        }

        private void BtnSave_Click(object sender, RoutedEventArgs e)
        {
            _settings.Endpoint = TxtEndpoint.Text.Trim();
            if (!_settings.Endpoint.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
            {
                TxtStatus.Text = "API 地址必须以 https:// 开头";
                return;
            }
            _settings.ApiKey = TxtApiKey.Password;
            _settings.Model = CmbModel.Text.Trim();
            SettingsManager.Save(_settings);
            DialogResult = true;
            Close();
        }

        private void BtnCancel_Click(object sender, RoutedEventArgs e)
        {
            DialogResult = false;
            Close();
        }
    }
}
