using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using Newtonsoft.Json;
using DbSyncTool.Models;

namespace DbSyncTool.Helpers
{
    /// <summary>
    /// 配置持久层 - JSON文件存储，含AES加密
    /// </summary>
    public static class ConfigStore
    {
        private static readonly string ConfigDir = Path.Combine(
            AppDomain.CurrentDomain.BaseDirectory, "Config");

        private static readonly string DbConfigFile = Path.Combine(ConfigDir, "DbConfig.json");
        private static readonly string DictMatchFile = Path.Combine(ConfigDir, "DictMapping.json");
        private static readonly string ApiConfigFile = Path.Combine(ConfigDir, "ApiConfig.json");
        private static readonly string DataCallFile = Path.Combine(ConfigDir, "DataCallConfig.json");
        private static readonly string TaskFile = Path.Combine(ConfigDir, "TaskConfig.json");

        // AES密钥 - 实际项目应使用DPAPI派生
        private static readonly byte[] AesKey = Encoding.UTF8.GetBytes("DbSyncTool2024AESKey_32BytesPad!!");
        private static readonly byte[] AesIV = Encoding.UTF8.GetBytes("DbSyncToolIV16B!");

        static ConfigStore()
        {
            if (!Directory.Exists(ConfigDir))
                Directory.CreateDirectory(ConfigDir);
        }

        // ========================
        // AES 加解密
        // ========================
        public static string Encrypt(string plainText)
        {
            if (string.IsNullOrEmpty(plainText)) return "";
            using var aes = Aes.Create();
            aes.Key = AesKey[..32];
            aes.IV = AesIV[..16];
            var encryptor = aes.CreateEncryptor();
            var bytes = Encoding.UTF8.GetBytes(plainText);
            var encrypted = encryptor.TransformFinalBlock(bytes, 0, bytes.Length);
            return Convert.ToBase64String(encrypted);
        }

        public static string Decrypt(string cipherText)
        {
            if (string.IsNullOrEmpty(cipherText)) return "";
            try
            {
                using var aes = Aes.Create();
                aes.Key = AesKey[..32];
                aes.IV = AesIV[..16];
                var decryptor = aes.CreateDecryptor();
                var bytes = Convert.FromBase64String(cipherText);
                var decrypted = decryptor.TransformFinalBlock(bytes, 0, bytes.Length);
                return Encoding.UTF8.GetString(decrypted);
            }
            catch { return ""; }
        }

        // ========================
        // DbConfig
        // ========================
        public static List<DbConfig> LoadDbConfigs()
        {
            if (!File.Exists(DbConfigFile)) return new();
            var json = File.ReadAllText(DbConfigFile);
            return JsonConvert.DeserializeObject<List<DbConfig>>(json) ?? new();
        }

        public static void SaveDbConfigs(List<DbConfig> configs)
        {
            var json = JsonConvert.SerializeObject(configs, Formatting.Indented);
            File.WriteAllText(DbConfigFile, json);
        }

        public static int NextDbConfigId()
        {
            var list = LoadDbConfigs();
            return list.Count == 0 ? 1 : list.Max(x => x.Id) + 1;
        }

        // ========================
        // DictMatchConfig
        // ========================
        public static List<DictMatchConfig> LoadDictMatchConfigs()
        {
            if (!File.Exists(DictMatchFile)) return new();
            var json = File.ReadAllText(DictMatchFile);
            var list = JsonConvert.DeserializeObject<List<DictMatchConfig>>(json) ?? new();
            // 填充导航属性，供 SyncDirectionDisplay 使用
            var dbConfigs = LoadDbConfigs();
            foreach (var c in list)
            {
                c.SourceDbConfig = dbConfigs.FirstOrDefault(d => d.Id == c.SourceDbConfigId);
                c.TargetDbConfig = dbConfigs.FirstOrDefault(d => d.Id == c.TargetDbConfigId);
            }
            return list;
        }

        public static void SaveDictMatchConfigs(List<DictMatchConfig> configs)
        {
            var json = JsonConvert.SerializeObject(configs, Formatting.Indented);
            File.WriteAllText(DictMatchFile, json);
        }

        public static int NextDictMatchId()
        {
            var list = LoadDictMatchConfigs();
            return list.Count == 0 ? 1 : list.Max(x => x.Id) + 1;
        }

        // ========================
        // ApiConfig
        // ========================
        public static List<ApiConfig> LoadApiConfigs()
        {
            if (!File.Exists(ApiConfigFile)) return new();
            var json = File.ReadAllText(ApiConfigFile);
            return JsonConvert.DeserializeObject<List<ApiConfig>>(json) ?? new();
        }

        public static void SaveApiConfigs(List<ApiConfig> configs)
        {
            var json = JsonConvert.SerializeObject(configs, Formatting.Indented);
            File.WriteAllText(ApiConfigFile, json);
        }

        public static int NextApiConfigId()
        {
            var list = LoadApiConfigs();
            return list.Count == 0 ? 1 : list.Max(x => x.Id) + 1;
        }

        // ========================
        // DataCallConfig
        // ========================
        public static List<DataCallConfig> LoadDataCallConfigs()
        {
            if (!File.Exists(DataCallFile)) return new();
            var json = File.ReadAllText(DataCallFile);
            return JsonConvert.DeserializeObject<List<DataCallConfig>>(json) ?? new();
        }

        public static void SaveDataCallConfigs(List<DataCallConfig> configs)
        {
            var json = JsonConvert.SerializeObject(configs, Formatting.Indented);
            File.WriteAllText(DataCallFile, json);
        }

        // ========================
        // ProcConfig
        // ========================
        private static readonly string ProcFile = Path.Combine(ConfigDir, "proc_configs.json");

        public static List<ProcConfig> LoadProcConfigs()
        {
            if (!File.Exists(ProcFile)) return new();
            var json = File.ReadAllText(ProcFile);
            var list = JsonConvert.DeserializeObject<List<ProcConfig>>(json) ?? new();
            var dbConfigs = LoadDbConfigs();
            var apiConfigs = LoadApiConfigs();
            foreach (var c in list)
            {
                c.SourceDbConfig = dbConfigs.FirstOrDefault(d => d.Id == c.SourceDbConfigId);
                c.TargetDbConfig = dbConfigs.FirstOrDefault(d => d.Id == c.TargetDbConfigId);
                c.ApiConfig = apiConfigs.FirstOrDefault(a => a.Id == c.ApiConfigId);
            }
            return list;
        }

        public static void SaveProcConfigs(List<ProcConfig> configs)
        {
            var json = JsonConvert.SerializeObject(configs, Formatting.Indented);
            File.WriteAllText(ProcFile, json);
        }

        public static int NextProcId()
        {
            var list = LoadProcConfigs();
            return list.Count == 0 ? 1 : list.Max(x => x.Id) + 1;
        }

        public static int NextDataCallId()
        {
            var list = LoadDataCallConfigs();
            return list.Count == 0 ? 1 : list.Max(x => x.Id) + 1;
        }

        // ========================
        // TaskConfig
        // ========================
        public static List<TaskConfig> LoadTaskConfigs()
        {
            if (!File.Exists(TaskFile)) return new();
            var json = File.ReadAllText(TaskFile);
            return JsonConvert.DeserializeObject<List<TaskConfig>>(json) ?? new();
        }

        public static void SaveTaskConfigs(List<TaskConfig> configs)
        {
            var json = JsonConvert.SerializeObject(configs, Formatting.Indented);
            File.WriteAllText(TaskFile, json);
        }

        public static int NextTaskId()
        {
            var list = LoadTaskConfigs();
            return list.Count == 0 ? 1 : list.Max(x => x.Id) + 1;
        }
    }
}
