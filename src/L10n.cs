using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
using System.Web.Script.Serialization;

namespace StandUp
{
    /// <summary>The app's text in each language, from i18n/strings.json (shared with the Mac app).</summary>
    static class L10n
    {
        public static readonly string[] Languages = { "en", "es", "vi" };

        static Dictionary<string, Dictionary<string, string>> _all;
        static string _language = "en";

        public static string Language
        {
            get { return _language; }
            set { _language = Languages.Contains(value) ? value : "en"; }
        }

        public static CultureInfo Culture
        {
            get
            {
                switch (_language)
                {
                    case "es": return CultureInfo.GetCultureInfo("es-ES");
                    case "vi": return CultureInfo.GetCultureInfo("vi-VN");
                    default: return CultureInfo.GetCultureInfo("en-US");
                }
            }
        }

        /// <summary>The Windows display language if StandUp has it, otherwise English.</summary>
        public static string DefaultLanguage()
        {
            string windows = CultureInfo.CurrentUICulture.TwoLetterISOLanguageName;
            return Languages.Contains(windows) ? windows : "en";
        }

        public static string T(string key)
        {
            return Lookup(_language, key) ?? Lookup("en", key) ?? key;
        }

        public static string T(string key, params object[] args)
        {
            return string.Format(Culture, T(key), args);
        }

        /// <summary>A language's own name for itself, such as "Español".</summary>
        public static string NameOf(string language)
        {
            return Lookup(language, "lang.name") ?? language;
        }

        static string Lookup(string language, string key)
        {
            Dictionary<string, string> table;
            string text;
            if (!All.TryGetValue(language, out table)) return null;
            return table.TryGetValue(key, out text) ? text : null;
        }

        static Dictionary<string, Dictionary<string, string>> All
        {
            get
            {
                if (_all != null) return _all;
                using (Stream stream = Assembly.GetExecutingAssembly().GetManifestResourceStream("strings.json"))
                using (var reader = new StreamReader(stream, Encoding.UTF8))
                    _all = new JavaScriptSerializer().Deserialize<Dictionary<string, Dictionary<string, string>>>(reader.ReadToEnd());
                return _all;
            }
        }
    }

    /// <summary>Soft chimes when a break starts and ends.</summary>
    static class Chime
    {
        static System.Media.SoundPlayer _player;   // kept so the sound isn't cut off by garbage collection

        public static void BreakStart() { Play("sound.break-start.wav"); }
        public static void BreakEnd() { Play("sound.break-end.wav"); }

        static void Play(string resource)
        {
            try
            {
                Stream stream = Assembly.GetExecutingAssembly().GetManifestResourceStream(resource);
                if (stream == null) return;
                if (_player != null) _player.Dispose();
                _player = new System.Media.SoundPlayer(stream);
                _player.Play();
            }
            catch (InvalidOperationException) { }
            catch (TimeoutException) { }
        }
    }
}
