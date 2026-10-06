using System.Collections.Generic;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Runtime.Serialization;

namespace Phosphor
{
    /// <summary>Root of apps.json.</summary>
    [DataContract]
    public sealed class CatalogFile
    {
        [DataMember(Name = "apps")] public List<AppDef> Apps { get; set; } = new List<AppDef>();
    }

    /// <summary>One entry of apps.json.</summary>
    [DataContract]
    public sealed class AppDef
    {
        /// <summary>Display name.</summary>
        [DataMember(Name = "name")] public string Name { get; set; }

        /// <summary>winget package id (exact match is used).</summary>
        [DataMember(Name = "id")] public string Id { get; set; }

        [DataMember(Name = "category")] public string Category { get; set; }

        [DataMember(Name = "description")] public string Description { get; set; }

        /// <summary>winget source: "winget" (default) or "msstore".</summary>
        [DataMember(Name = "source")] public string Source { get; set; }

        /// <summary>Extra arguments appended to `winget install`, e.g. "--scope machine".</summary>
        [DataMember(Name = "args")] public string Args { get; set; }

        /// <summary>Pre-selected on start.</summary>
        [DataMember(Name = "selected")] public bool Selected { get; set; }
    }

    public enum InstallState { Idle, Queued, Running, Done, Skipped, Failed }

    public abstract class Observable : INotifyPropertyChanged
    {
        public event PropertyChangedEventHandler PropertyChanged;

        protected bool Set<T>(ref T field, T value, [CallerMemberName] string name = null)
        {
            if (EqualityComparer<T>.Default.Equals(field, value)) return false;
            field = value;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
            return true;
        }

        protected void Raise(string name) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
    }

    /// <summary>A row in the app list.</summary>
    public sealed class AppItem : Observable
    {
        public AppItem(AppDef def) { Def = def; _isChecked = def.Selected; }

        public AppDef Def { get; }
        public string Name => Def.Name;
        public string Id => Def.Id;
        public string Category => string.IsNullOrWhiteSpace(Def.Category) ? "Прочее" : Def.Category.Trim();
        public string Description => Def.Description ?? "";

        private bool _isChecked;
        public bool IsChecked { get => _isChecked; set => Set(ref _isChecked, value); }

        private InstallState _state;
        public InstallState State { get => _state; set { if (Set(ref _state, value)) Raise(nameof(StatusText)); } }

        private string _detail;
        /// <summary>Live detail such as download progress, shown while Running.</summary>
        public string Detail { get => _detail; set { if (Set(ref _detail, value)) Raise(nameof(StatusText)); } }

        public string StatusText
        {
            get
            {
                switch (State)
                {
                    case InstallState.Queued: return "В очереди";
                    case InstallState.Running: return string.IsNullOrEmpty(Detail) ? "Установка…" : Detail;
                    case InstallState.Done: return string.IsNullOrEmpty(Detail) ? "Готово" : Detail;
                    case InstallState.Skipped: return string.IsNullOrEmpty(Detail) ? "Пропущено" : Detail;
                    case InstallState.Failed: return string.IsNullOrEmpty(Detail) ? "Ошибка" : Detail;
                    default: return "";
                }
            }
        }

        public bool Matches(string query)
        {
            if (string.IsNullOrWhiteSpace(query)) return true;
            var q = query.Trim();
            return Contains(Name, q) || Contains(Id, q) || Contains(Description, q) || Contains(Category, q);
        }

        private static bool Contains(string s, string q) =>
            s != null && s.IndexOf(q, System.StringComparison.OrdinalIgnoreCase) >= 0;
    }

    /// <summary>A row in the category list.</summary>
    public sealed class CategoryItem : Observable
    {
        public const string All = "*";
        public const string Selected = "+";

        public CategoryItem(string key, string title) { Key = key; Title = title; }

        public string Key { get; }
        public string Title { get; }

        private int _count;
        /// <summary>Apps in this category.</summary>
        public int Count { get => _count; set { if (Set(ref _count, value)) Raise(nameof(CountText)); } }

        private int _checked;
        /// <summary>Checked apps in this category.</summary>
        public int Checked { get => _checked; set { if (Set(ref _checked, value)) Raise(nameof(CountText)); } }

        public string CountText =>
            Key == Selected ? Checked.ToString("00")
            : Checked > 0 ? $"{Checked}/{Count:00}"
            : Count.ToString("00");
    }
}
