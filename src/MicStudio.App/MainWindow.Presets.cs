using MicStudio.Core;
using System.Windows.Automation;

namespace MicStudio.App;

public partial class MainWindow
{
    private bool favoritesOnly;
    private IEnumerable<SoundPreset> AllPresets() => BuiltInPresets.All.Concat(State.Presets);
    private void LoadPreset(SoundPreset preset)
    {
        preset.Validate(); vm.Replace(preset.Settings); State.SelectedPresetId = preset.Id; State.CreativeThemeId = "custom"; SaveSoon(); ShowPage(currentPage);
    }
    private void SavePreset()
    {
        if (State.Presets.Count >= 200) { Notice("个人预设已达 200 个，请先导出或删除不再使用的预设。"); return; }
        Draft.Validate();
        var name = Prompt("另存为个人预设", Loc.T("我的声音", "My sound")); if (name is null) return;
        var p = new SoundPreset { Name = name, Description = Loc.T("个人自定义声音", "Personal custom sound"), Settings = Draft.Clone() }; p.Validate();
        State.Presets.Add(p); State.SelectedPresetId = p.Id; SaveSoon(); ShowPage(currentPage); Notice("个人预设已保存。");
    }
    private void BuildPresets()
    {
        Header("", "预设", "");
        Add(Buttons(B(Loc.T("保存当前声音", "Save current sound"), SavePreset, true), B(Loc.T("导入预设", "Import preset"), () =>
        {
            if (State.Presets.Count >= 200) { Notice("个人预设已达 200 个。"); return; }
            var d = new Microsoft.Win32.OpenFileDialog { Filter = Loc.T("Mic Studio 预设|*.json", "Mic Studio preset|*.json"), Title = Loc.T("导入预设 · 最大 256 KB", "Import preset · up to 256 KB") };
            if (d.ShowDialog(this) != true) return;
            var p = PresetStore.ImportFile(d.FileName); State.Presets.Add(p); SaveSoon(); ShowPage(3);
            Notice("预设已导入，选择后使用。");
        }), B(favoritesOnly ? Loc.T("★  只看收藏", "★  Favorites only") : Loc.T("全部预设", "All presets"), () => { favoritesOnly = !favoritesOnly; ShowPage(3); })));
        var presets = AllPresets().Where(p => !favoritesOnly || p.IsFavorite).OrderByDescending(p => p.IsFavorite).ToList();
        if (presets.Count == 0) Add(T("还没有收藏。可先将内置声音复制为个人预设，再收藏。", 12, Muted));
        foreach (var builtIn in new[] { true, false })
        {
            var group = presets.Where(p => p.IsBuiltIn == builtIn).ToList();
            if (group.Count == 0) continue;
            Add(T(builtIn ? Loc.T("内置声音", "Built-in sounds") : Loc.T("我的声音", "My sounds"), 14, Ink, FontWeights.SemiBold, new(0, 12, 0, 10)));
            var choices = new List<UIElement>();
            foreach (var preset in group)
            {
                var p = preset;
                var glyph = p.IsFavorite ? "\uE734" : p.Id switch { "flat" => "\uE9D9", "natural" => "\uE720", "clear" => "\uE8D4", "warm" => "\uE706", _ => "\uE8A5" };
                var caption = p.IsBuiltIn ? p.Id switch
                {
                    "flat" => Loc.T("平直音色 · EQ 关闭", "Neutral tone · EQ off"),
                    "natural" => Loc.T("自然人声", "Natural speech"),
                    "clear" => Loc.T("清楚明亮", "Clear and bright"),
                    "warm" => Loc.T("温暖厚实", "Warm and full"),
                    _ => Loc.PresetDescription(p)
                } : Loc.PresetDescription(p);
                var choice = ThemeChoice(Loc.PresetName(p), glyph, caption, () => LoadPreset(p),
                    () => State.SelectedPresetId == p.Id && SettingsJson(p.Settings) == vm.DraftJson);
                AutomationProperties.SetAutomationId(choice, "sound-preset-" + p.Id);
                choice.ToolTip = Loc.PresetName(p) + "\n" + Loc.PresetDescription(p);
                choices.Add(choice);
            }
            Add(ChoiceGrid(choices, 180));
        }
        var management = new StackPanel();
        var selected = AllPresets().FirstOrDefault(p => p.Id == State.SelectedPresetId);
        if (selected is { } current)
        {
            management.Children.Add(T(Loc.PresetName(current), 14, Ink, FontWeights.SemiBold, new(0, 10, 0, 8)));
            management.Children.Add(T(Loc.PresetDescription(current), 12, Muted, margin: new(0, 0, 0, 10)));
            var actions = Buttons(B(Loc.T("复制为个人预设", "Make a personal copy"), () =>
            {
                if (State.Presets.Count >= 200) { Notice("个人预设已达 200 个。"); return; }
                var copy = current.Clone(); copy.Id = Guid.NewGuid().ToString("N"); copy.IsBuiltIn = false; copy.Name += Loc.T(" · 副本", " · copy"); State.Presets.Add(copy); SaveSoon(); ShowPage(3);
            }), B(Loc.T("导出预设", "Export preset"), () => ExportPreset(current)));
            if (!current.IsBuiltIn)
            {
                actions.Children.Add(B("重命名", () => { var name = Prompt("重命名预设", current.Name); if (name is null) return; current.Name = name; SaveSoon(); ShowPage(3); }));
                actions.Children.Add(B(current.IsFavorite ? "取消收藏" : "收藏", () => { current.IsFavorite = !current.IsFavorite; SaveSoon(); ShowPage(3); }));
                actions.Children.Add(B("删除", () =>
                {
                    if (MessageBox.Show(this, Loc.F("删除个人预设“{0}”？当前草稿会保留。", "Delete the personal preset “{0}”? The current draft is kept.", current.Name), "Mic Studio", MessageBoxButton.OKCancel, MessageBoxImage.Question) != MessageBoxResult.OK) return;
                    State.Presets.Remove(current); SaveSoon(); ShowPage(3);
                }));
            }
            management.Children.Add(actions);
        }
        management.Children.Add(B("导出当前草稿", () => ExportPreset(new SoundPreset { Name = Loc.T("我的声音", "My sound"), Description = Loc.T("Mic Studio 声音设置", "Mic Studio sound settings"), Settings = Draft.Clone() })));
        Add(new Expander { Header = Loc.T("管理与导出", "Manage and export"), Foreground = Ink, Content = management, Margin = new(0, 16, 0, 12) });
    }
    private void ExportPreset(SoundPreset preset)
    {
        var d = new Microsoft.Win32.SaveFileDialog { Filter = Loc.T("Mic Studio 预设|*.json", "Mic Studio preset|*.json"), FileName = "MicStudio-preset.json", Title = Loc.T("导出便携预设", "Export portable preset") };
        if (d.ShowDialog(this) == true) { PresetStore.ExportFile(d.FileName, preset); Notice("预设已导出，不包含设备唯一标识、路径或录音。"); }
    }
}
