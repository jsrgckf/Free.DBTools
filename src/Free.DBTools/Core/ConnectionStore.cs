using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Security.Cryptography;
using Newtonsoft.Json;

namespace Free.DBTools.Core
{
    public sealed class ConnectionProfile
    {
        public string Id { get; set; }
        public string Name { get; set; }
        public string KindKey { get; set; }
        public string Host { get; set; }
        public int Port { get; set; }
        public string Database { get; set; }
        public string User { get; set; }
        public string FilePath { get; set; }
        public bool UseCustomString { get; set; }
        public string EncryptedPassword { get; set; }
        public string EncryptedCustomString { get; set; }
        public DateTime LastUsedAt { get; set; }

        [JsonIgnore]
        public string DisplayName
        {
            get { return string.IsNullOrWhiteSpace(Name) ? BuildAutoName() : Name; }
        }

        [JsonIgnore]
        public string DisplaySummary
        {
            get
            {
                var kind = DatabaseCatalog.Find(KindKey);
                var kindName = kind == null ? KindKey : kind.DisplayName;
                var target = (kind != null && kind.IsFileBased)
                    ? (string.IsNullOrWhiteSpace(FilePath) ? "未指定文件" : Path.GetFileName(FilePath))
                    : (string.IsNullOrWhiteSpace(Host) ? "未指定主机" : Host + (Port > 0 ? ":" + Port : string.Empty));
                if (!string.IsNullOrWhiteSpace(Database))
                {
                    target += " / " + Database;
                }
                return kindName + " · " + target;
            }
        }

        [JsonIgnore]
        public string Password
        {
            get { return SecretProtector.Unprotect(EncryptedPassword); }
            set { EncryptedPassword = SecretProtector.Protect(value); }
        }

        [JsonIgnore]
        public string CustomConnectionString
        {
            get { return SecretProtector.Unprotect(EncryptedCustomString); }
            set { EncryptedCustomString = SecretProtector.Protect(value); }
        }

        public string BuildAutoName()
        {
            var kind = DatabaseCatalog.Find(KindKey);
            var kindName = kind == null ? KindKey : kind.DisplayName;
            if (kind != null && kind.IsFileBased)
            {
                return kindName + " · " + (string.IsNullOrWhiteSpace(FilePath) ? "未命名" : Path.GetFileName(FilePath));
            }

            var target = string.IsNullOrWhiteSpace(Host) ? "未命名" : Host;
            if (!string.IsNullOrWhiteSpace(Database))
            {
                target += "/" + Database;
            }
            return kindName + " · " + target;
        }
    }

    public sealed class ConnectionStore
    {
        private readonly string _filePath;

        public ConnectionStore()
        {
            _filePath = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                "Free.DBTools",
                "connections.json");
        }

        public string FilePath { get { return _filePath; } }

        public List<ConnectionProfile> Load()
        {
            try
            {
                if (!File.Exists(_filePath))
                {
                    return new List<ConnectionProfile>();
                }

                var json = File.ReadAllText(_filePath, Encoding.UTF8);
                var profiles = JsonConvert.DeserializeObject<List<ConnectionProfile>>(json);
                if (profiles == null)
                {
                    return new List<ConnectionProfile>();
                }

                foreach (var profile in profiles)
                {
                    if (string.IsNullOrEmpty(profile.Id))
                    {
                        profile.Id = Guid.NewGuid().ToString("N");
                    }
                }
                return profiles;
            }
            catch
            {
                TryKeepCorruptedCopy();
                return new List<ConnectionProfile>();
            }
        }

        public void Save(IEnumerable<ConnectionProfile> profiles)
        {
            var directory = Path.GetDirectoryName(_filePath);
            if (!string.IsNullOrEmpty(directory))
            {
                Directory.CreateDirectory(directory);
            }

            var json = JsonConvert.SerializeObject(profiles, Formatting.Indented);
            var temp = _filePath + ".tmp";
            File.WriteAllText(temp, json, new UTF8Encoding(false));

            if (File.Exists(_filePath))
            {
                var backup = _filePath + ".bak";
                try
                {
                    File.Replace(temp, _filePath, backup, true);
                }
                catch
                {
                    File.Copy(temp, _filePath, true);
                    File.Delete(temp);
                }
            }
            else
            {
                File.Move(temp, _filePath);
            }
        }

        private void TryKeepCorruptedCopy()
        {
            try
            {
                if (File.Exists(_filePath))
                {
                    File.Copy(_filePath, _filePath + ".corrupt", true);
                }
            }
            catch
            {
            }
        }
    }

    public static class SecretProtector
    {
        private static readonly byte[] Entropy = Encoding.UTF8.GetBytes("Free.DBTools.Connection.Secret.v1");

        public static string Protect(string plainText)
        {
            if (string.IsNullOrEmpty(plainText))
            {
                return null;
            }

            try
            {
                var data = ProtectedData.Protect(
                    Encoding.UTF8.GetBytes(plainText), Entropy, DataProtectionScope.CurrentUser);
                return Convert.ToBase64String(data);
            }
            catch
            {
                return null;
            }
        }

        public static string Unprotect(string protectedText)
        {
            if (string.IsNullOrEmpty(protectedText))
            {
                return string.Empty;
            }

            try
            {
                var data = ProtectedData.Unprotect(
                    Convert.FromBase64String(protectedText), Entropy, DataProtectionScope.CurrentUser);
                return Encoding.UTF8.GetString(data);
            }
            catch
            {
                return string.Empty;
            }
        }
    }
}
