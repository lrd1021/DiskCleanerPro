using System;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Runtime.InteropServices;

namespace DiskCleaner.Helpers
{
    /// <summary>
    /// AI API 配置
    /// </summary>
    public class AISettings
    {
        public string Endpoint { get; set; } = "https://api.openai.com/v1/chat/completions";
        public string ApiKey { get; set; } = "";
        public string Model { get; set; } = "gpt-4o-mini";
    }

    /// <summary>
    /// 安全配置管理器 — DPAPI 加密存储
    /// </summary>
    public static class SettingsManager
    {
        private static readonly byte[] Entropy = { 0x44, 0x43, 0x50, 0x72, 0x6F, 0x34, 0x32, 0x30 }; // "DCPro420"
        private static readonly string SettingsDir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "DiskCleanerPro");
        private static readonly string SettingsFile = Path.Combine(SettingsDir, "settings.dat");

        public static AISettings Load()
        {
            try
            {
                if (!File.Exists(SettingsFile)) return new AISettings();
                var ciphertext = File.ReadAllBytes(SettingsFile);
                var plaintext = ProtectedData.Unprotect(ciphertext, Entropy, DataProtectionScope.CurrentUser);
                return JsonSerializer.Deserialize<AISettings>(Encoding.UTF8.GetString(plaintext)) ?? new AISettings();
            }
            catch { return new AISettings(); }
        }

        public static void Save(AISettings settings)
        {
            try
            {
                Directory.CreateDirectory(SettingsDir);
                var plaintext = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(settings));
                var ciphertext = ProtectedData.Protect(plaintext, Entropy, DataProtectionScope.CurrentUser);
                File.WriteAllBytes(SettingsFile, ciphertext);
            }
            catch { /* 忽略保存失败 */ }
        }
    }
}
