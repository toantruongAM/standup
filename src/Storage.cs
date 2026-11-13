using System;
using System.Collections.Generic;
using System.Drawing;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Security.Cryptography;
using Microsoft.Win32;

namespace StandUp
{
    static class Paths
    {
        static string _dataDir;

        public static string DataDir
        {
            get
            {
                if (_dataDir == null)
                    _dataDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "StandUp");
                Directory.CreateDirectory(_dataDir);
                return _dataDir;
            }
            set { _dataDir = value; }
        }

        public static string Settings { get { return Path.Combine(DataDir, "settings.ini"); } }
        public static string History { get { return Path.Combine(DataDir, "history.csv"); } }
        public static string Photos { get { return Path.Combine(DataDir, "photos"); } }
        public static string Log { get { return Path.Combine(DataDir, "app.log"); } }
    }

    sealed class AppSettings
    {
        const int Iterations = 100000;

        public decimal WorkMinutes = 60;
        public decimal BreakMinutes = 1;
        public int MaxSnoozes = 2;
        public decimal LongEveryMinutes = 180;
        public decimal LongBreakMinutes = 10;
        public decimal SnoozeMinutes = 2;
        public decimal IdleMinutes = 5;
        public string Language = "";
        public bool StartWithWindows = true;
        public string PasscodeSalt = "";
        public string PasscodeHash = "";

        public bool HasPasscode { get { return PasscodeHash.Length > 0; } }

        public EngineConfig ToEngineConfig()
        {
            return new EngineConfig
            {
                WorkSeconds = (int)(WorkMinutes * 60),
                BreakSeconds = (int)(BreakMinutes * 60),
                LongEverySeconds = (int)(LongEveryMinutes * 60),
                LongBreakSeconds = (int)(LongBreakMinutes * 60),
                SnoozeSeconds = (int)(SnoozeMinutes * 60),
                MaxSnoozes = MaxSnoozes
            };
        }

        public void SetPasscode(string passcode)
        {
            var salt = new byte[16];
            using (var rng = RandomNumberGenerator.Create()) rng.GetBytes(salt);
            PasscodeSalt = Convert.ToBase64String(salt);
            PasscodeHash = Convert.ToBase64String(Derive(passcode, salt));
        }

        public bool CheckPasscode(string passcode)
        {
            if (!HasPasscode) return false;
            byte[] expected = Convert.FromBase64String(PasscodeHash);
            byte[] actual = Derive(passcode, Convert.FromBase64String(PasscodeSalt));
            int difference = expected.Length ^ actual.Length;
            for (int i = 0; i < Math.Min(expected.Length, actual.Length); i++) difference |= expected[i] ^ actual[i];
            return difference == 0;
        }

        static byte[] Derive(string passcode, byte[] salt)
        {
            using (var kdf = new Rfc2898DeriveBytes(passcode, salt, Iterations, HashAlgorithmName.SHA256))
                return kdf.GetBytes(32);
        }

        public static AppSettings Load()
        {
            var settings = new AppSettings();
            if (!File.Exists(Paths.Settings))
            {
                settings.Language = L10n.DefaultLanguage();
                return settings;
            }

            foreach (string line in File.ReadAllLines(Paths.Settings))
            {
                int split = line.IndexOf('=');
                if (split < 0) continue;
                string key = line.Substring(0, split).Trim();
                string value = line.Substring(split + 1).Trim();
                switch (key)
                {
                    case "work": settings.WorkMinutes = ParseMinutes(value, settings.WorkMinutes); break;
                    case "break": settings.BreakMinutes = ParseMinutes(value, settings.BreakMinutes); break;
                    case "snooze": settings.SnoozeMinutes = ParseMinutes(value, settings.SnoozeMinutes); break;
                    case "longEvery": settings.LongEveryMinutes = ParseMinutes(value, settings.LongEveryMinutes); break;
                    case "longBreak": settings.LongBreakMinutes = ParseMinutes(value, settings.LongBreakMinutes); break;
                    case "idle": settings.IdleMinutes = ParseMinutes(value, settings.IdleMinutes); break;
                    case "lang": settings.Language = value; break;
                    case "snoozes":
                        int snoozes;
                        if (int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out snoozes) && snoozes >= 0 && snoozes <= 10)
                            settings.MaxSnoozes = snoozes;
                        break;
                    case "autostart": settings.StartWithWindows = value == "1"; break;
                    case "salt": settings.PasscodeSalt = value; break;
                    case "hash": settings.PasscodeHash = value; break;
                }
            }
            if (!L10n.Languages.Contains(settings.Language)) settings.Language = L10n.DefaultLanguage();
            return settings;
        }

        static decimal ParseMinutes(string text, decimal fallback)
        {
            decimal value;
            bool ok = decimal.TryParse(text, NumberStyles.Number, CultureInfo.InvariantCulture, out value) && value > 0 && value <= 1440;
            return ok ? value : fallback;
        }

        public void Save()
        {
            File.WriteAllLines(Paths.Settings, new[]
            {
                "work=" + WorkMinutes.ToString(CultureInfo.InvariantCulture),
                "break=" + BreakMinutes.ToString(CultureInfo.InvariantCulture),
                "snooze=" + SnoozeMinutes.ToString(CultureInfo.InvariantCulture),
                "longEvery=" + LongEveryMinutes.ToString(CultureInfo.InvariantCulture),
                "longBreak=" + LongBreakMinutes.ToString(CultureInfo.InvariantCulture),
                "idle=" + IdleMinutes.ToString(CultureInfo.InvariantCulture),
                "lang=" + Language,
                "snoozes=" + MaxSnoozes.ToString(CultureInfo.InvariantCulture),
                "autostart=" + (StartWithWindows ? "1" : "0"),
                "salt=" + PasscodeSalt,
                "hash=" + PasscodeHash
            });
        }
    }

    static class AutoStart
    {
        const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
        const string ValueName = "StandUp";

        public static void Apply(bool enabled, string exePath)
        {
            using (RegistryKey key = Registry.CurrentUser.CreateSubKey(RunKey))
            {
                if (enabled) key.SetValue(ValueName, "\"" + exePath + "\"");
                else key.DeleteValue(ValueName, false);
            }
        }
    }

    sealed class DayStats
    {
        public DateTime Date;
        public int Breaks;
        public int Snoozes;
        public int BreakSeconds;
    }

    /// <summary>An append-only CSV: local time, "break" or "snooze", seconds.</summary>
    sealed class History
    {
        const string Stamp = "yyyy-MM-dd'T'HH:mm:ss";
        readonly SortedDictionary<DateTime, DayStats> _days = new SortedDictionary<DateTime, DayStats>();

        public History()
        {
            if (!File.Exists(Paths.History)) return;
            foreach (string line in File.ReadAllLines(Paths.History))
            {
                string[] parts = line.Split(',');
                DateTime at;
                int seconds;
                if (parts.Length != 3) continue;
                if (!DateTime.TryParseExact(parts[0], Stamp, CultureInfo.InvariantCulture, DateTimeStyles.None, out at)) continue;
                if (!int.TryParse(parts[2], NumberStyles.Integer, CultureInfo.InvariantCulture, out seconds)) continue;
                Count(at, parts[1], seconds);
            }
        }

        public int BreaksToday { get { return For(DateTime.Today).Breaks; } }

        public IEnumerable<DayStats> NewestFirst { get { return _days.Values.Reverse(); } }

        public void RecordBreak(DateTime utc, int seconds) { Append(utc.ToLocalTime(), "break", seconds); }

        public void RecordSnooze(DateTime utc, int seconds) { Append(utc.ToLocalTime(), "snooze", seconds); }

        void Append(DateTime local, string kind, int seconds)
        {
            Count(local, kind, seconds);
            string line = local.ToString(Stamp, CultureInfo.InvariantCulture) + "," + kind + "," + seconds.ToString(CultureInfo.InvariantCulture);
            File.AppendAllLines(Paths.History, new[] { line });
        }

        void Count(DateTime local, string kind, int seconds)
        {
            DayStats day = For(local.Date);
            if (kind == "break")
            {
                day.Breaks++;
                day.BreakSeconds += seconds;
                _days[local.Date] = day;
            }
            else if (kind == "snooze")
            {
                day.Snoozes++;
                _days[local.Date] = day;
            }
        }

        public DayStats For(DateTime date)
        {
            DayStats day;
            return _days.TryGetValue(date, out day) ? day : new DayStats { Date = date };
        }
    }

    /// <summary>Built-in pictures plus anything the user drops in the photos folder.</summary>
    sealed class PhotoPool
    {
        const string ResourcePrefix = "photo.", LongPrefix = "long.";
        static readonly string[] Extensions = { ".png", ".jpg", ".jpeg", ".jfif", ".bmp", ".gif", ".tif", ".tiff", ".webp", ".heic" };

        readonly Random _random = new Random();
        string _last, _lastLong;

        public static string FileDialogPattern { get { return string.Join(";", Extensions.Select(e => "*" + e)); } }

        public static int BuiltInCount { get { return BuiltInNames().Count(); } }

        public static int UserCount
        {
            get { return Directory.Exists(Paths.Photos) ? Directory.GetFiles(Paths.Photos).Count(IsImage) : 0; }
        }

        /// <summary>Copies readable pictures into the pool and returns the names of any it couldn't read.</summary>
        public static string[] AddFiles(string[] files)
        {
            Directory.CreateDirectory(Paths.Photos);
            var rejected = new List<string>();
            foreach (string file in files)
            {
                using (Bitmap image = ReadFile(file))
                {
                    if (image == null || !IsImage(file))
                    {
                        rejected.Add(Path.GetFileName(file));
                        continue;
                    }
                }
                File.Copy(file, FreeName(Path.GetFileName(file)));
            }
            return rejected.ToArray();
        }

        static string FreeName(string fileName)
        {
            string target = Path.Combine(Paths.Photos, fileName);
            for (int i = 2; File.Exists(target); i++)
                target = Path.Combine(Paths.Photos, Path.GetFileNameWithoutExtension(fileName) + " (" + i + ")" + Path.GetExtension(fileName));
            return target;
        }

        static IEnumerable<string> BuiltInNames()
        {
            // Built-in pictures have their captions in each language: photo.<language>.<name>.png
            string prefix = ResourcePrefix + L10n.Language + ".";
            return Assembly.GetExecutingAssembly().GetManifestResourceNames()
                .Where(n => n.StartsWith(prefix, StringComparison.Ordinal));
        }

        /// <summary>
        /// For long breaks: a dog exercising, from their own built-in set (long.&lt;language&gt;.&lt;name&gt;.png),
        /// never the same one twice in a row. Separate from the short-break pictures. Null if none can be read.
        /// </summary>
        public Bitmap NextLong()
        {
            string prefix = LongPrefix + L10n.Language + ".";
            List<string> pool = Assembly.GetExecutingAssembly().GetManifestResourceNames()
                .Where(n => n.StartsWith(prefix, StringComparison.Ordinal)).ToList();
            while (pool.Count > 0)
            {
                List<string> choices = pool.Count > 1 ? pool.Where(p => p != _lastLong).ToList() : pool;
                string pick = choices[_random.Next(choices.Count)];
                Bitmap image = Load(pick);
                if (image != null)
                {
                    _lastLong = pick;
                    return image;
                }
                pool.Remove(pick);
            }
            return null;
        }

        public static int LongCount
        {
            get
            {
                string prefix = LongPrefix + L10n.Language + ".";
                return Assembly.GetExecutingAssembly().GetManifestResourceNames().Count(n => n.StartsWith(prefix, StringComparison.Ordinal));
            }
        }

        public Bitmap Next()
        {
            Directory.CreateDirectory(Paths.Photos);
            List<string> pool = BuiltInNames().Concat(Directory.GetFiles(Paths.Photos).Where(IsImage)).ToList();

            while (pool.Count > 0)
            {
                List<string> choices = pool.Count > 1 ? pool.Where(p => p != _last).ToList() : pool;
                string pick = choices[_random.Next(choices.Count)];
                Bitmap image = Load(pick);
                if (image != null)
                {
                    _last = pick;
                    return image;
                }
                pool.Remove(pick);   // unreadable file: try another
            }
            return null;
        }

        static bool IsImage(string path)
        {
            return Extensions.Contains(Path.GetExtension(path).ToLowerInvariant());
        }

        static Bitmap ReadFile(string path)
        {
            try
            {
                using (var source = new Bitmap(path))
                    return new Bitmap(source);
            }
            catch (ArgumentException) { }   // not a format GDI+ reads; Windows' newer codecs may (WebP, HEIC)

            try
            {
                var frame = System.Windows.Media.Imaging.BitmapFrame.Create(new Uri(path), System.Windows.Media.Imaging.BitmapCreateOptions.None, System.Windows.Media.Imaging.BitmapCacheOption.OnLoad);
                var encoder = new System.Windows.Media.Imaging.PngBitmapEncoder();
                encoder.Frames.Add(frame);
                using (var memory = new MemoryStream())
                {
                    encoder.Save(memory);
                    memory.Position = 0;
                    using (var source = new Bitmap(memory))
                        return new Bitmap(source);
                }
            }
            catch (Exception)
            {
                return null;
            }
        }

        static Bitmap Load(string name)
        {
            try
            {
                if (name.StartsWith(ResourcePrefix, StringComparison.Ordinal) || name.StartsWith(LongPrefix, StringComparison.Ordinal))
                {
                    using (Stream stream = Assembly.GetExecutingAssembly().GetManifestResourceStream(name))
                    using (var source = new Bitmap(stream))
                        return new Bitmap(source);
                }
                return ReadFile(name);
            }
            catch (Exception)
            {
                return null;
            }
        }
    }

    static class Log
    {
        public static bool Enabled;

        public static void Write(string message)
        {
            if (!Enabled) return;
            File.AppendAllText(Paths.Log, DateTime.Now.ToString("HH:mm:ss.fff", CultureInfo.InvariantCulture) + " " + message + Environment.NewLine);
        }
    }
}
