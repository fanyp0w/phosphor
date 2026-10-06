using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.Serialization.Json;
using System.Text;

namespace Phosphor
{
    public sealed class CatalogLoadResult
    {
        public List<AppDef> Apps { get; set; } = new List<AppDef>();
        /// <summary>Where the catalog came from: a file path or "встроенный".</summary>
        public string Origin { get; set; }
        /// <summary>Non-fatal problem worth showing in the log.</summary>
        public string Warning { get; set; }
    }

    /// <summary>
    /// Reads apps.json next to the exe. On first run the built-in catalog is written there,
    /// so the user can edit it without rebuilding.
    /// </summary>
    public static class Catalog
    {
        public const string FileName = "apps.json";

        public static string AppDir => AppDomain.CurrentDomain.BaseDirectory;
        public static string FilePath => Path.Combine(AppDir, FileName);

        public static CatalogLoadResult Load()
        {
            var result = new CatalogLoadResult();
            byte[] builtIn = ReadBuiltIn();

            if (!File.Exists(FilePath))
            {
                try
                {
                    File.WriteAllBytes(FilePath, builtIn);
                    result.Warning = $"создан {FileName} со встроенным каталогом — редактируйте его под себя";
                }
                catch (Exception ex)
                {
                    result.Warning = $"не удалось создать {FileName} ({ex.Message}); используется встроенный каталог";
                    result.Origin = "встроенный";
                    result.Apps = Clean(Parse(builtIn));
                    return result;
                }
            }

            try
            {
                result.Apps = Clean(Parse(File.ReadAllBytes(FilePath)));
                result.Origin = FilePath;
                if (result.Apps.Count == 0)
                {
                    result.Warning = $"в {FileName} нет ни одного приложения с полем \"id\" — загружен встроенный каталог";
                    result.Origin = "встроенный";
                    result.Apps = Clean(Parse(builtIn));
                }
            }
            catch (Exception ex)
            {
                result.Warning = $"ошибка разбора {FileName}: {Flatten(ex)} — загружен встроенный каталог";
                result.Origin = "встроенный";
                result.Apps = Clean(Parse(builtIn));
            }

            return result;
        }

        private static byte[] ReadBuiltIn()
        {
            using (var s = Assembly.GetExecutingAssembly().GetManifestResourceStream("Phosphor.apps.default.json"))
            using (var ms = new MemoryStream())
            {
                s.CopyTo(ms);
                return ms.ToArray();
            }
        }

        private static List<AppDef> Parse(byte[] json)
        {
            // Strip a UTF-8 BOM if Notepad added one.
            int offset = json.Length >= 3 && json[0] == 0xEF && json[1] == 0xBB && json[2] == 0xBF ? 3 : 0;
            using (var ms = new MemoryStream(json, offset, json.Length - offset))
            {
                var ser = new DataContractJsonSerializer(typeof(CatalogFile));
                var file = (CatalogFile)ser.ReadObject(ms);
                return file?.Apps ?? new List<AppDef>();
            }
        }

        /// <summary>Drops entries without an id and duplicate ids (first one wins).</summary>
        private static List<AppDef> Clean(List<AppDef> apps)
        {
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var list = new List<AppDef>();
            foreach (var a in apps.Where(a => a != null && !string.IsNullOrWhiteSpace(a.Id)))
            {
                a.Id = a.Id.Trim();
                if (string.IsNullOrWhiteSpace(a.Name)) a.Name = a.Id;
                if (seen.Add(a.Id)) list.Add(a);
            }
            return list;
        }

        private static string Flatten(Exception ex)
        {
            var sb = new StringBuilder(ex.Message);
            for (var inner = ex.InnerException; inner != null; inner = inner.InnerException)
                sb.Append(" → ").Append(inner.Message);
            return sb.ToString();
        }
    }
}
