using MicStudio.Core;

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
        var name = Prompt("另存为个人预设", "我的声音"); if (name is null) return;
        var p = new SoundPreset { Name = name, Description = "个人自定义声音", Settings = Draft.Clone() }; p.Validate();
        State.Presets.Add(p); State.SelectedPresetId = p.Id; SaveSoon(); ShowPage(currentPage); Notice("个人预设已保存。");
    }
    private void BuildPresets()
    {
        Header("YOUR SOUND LIBRARY / 04", "把喜欢的声音留下来。", "内置预设可复制成个人版本。导入导出只包含音频参数，不包含设备 ID、插件路径或录音。");
        Add(Buttons(B("新建 · 从当前声音另存", SavePreset, true), B("导入 JSON 预设", () =>
        {
            if (State.Presets.Count >= 200) { Notice("个人预设已达 200 个。"); return; }
            var d = new Microsoft.Win32.OpenFileDialog { Filter = "Mic Studio 预设|*.json", Title = "导入预设 · 最大 256 KB" };
            if (d.ShowDialog(this) != true) return;
            var p = PresetStore.ImportFile(d.FileName); State.Presets.Add(p); SaveSoon(); ShowPage(3);
            Notice("预设已导入，选择后使用。");
        }), B("导出当前草稿", () => ExportPreset(new SoundPreset { Name = "我的声音", Description = "Mic Studio 声音设置", Settings = Draft.Clone() }))));
        Add(Buttons(B(favoritesOnly ? "★  已收藏" : "全部预设", () => { favoritesOnly = !favoritesOnly; ShowPage(3); })));
        var presets = AllPresets().Where(p => !favoritesOnly || p.IsFavorite).OrderByDescending(p => p.IsFavorite).ToList();
        if (presets.Count == 0) Add(T("还没有收藏。可先将内置声音复制为个人预设，再收藏。", 12, Muted));
        foreach (var item in presets)
        {
            var p = item;
            var row = Buttons(B("使用", () => LoadPreset(p), true), B("复制", () =>
            {
                if (State.Presets.Count >= 200) { Notice("个人预设已达 200 个。"); return; }
                var copy = p.Clone(); copy.Id = Guid.NewGuid().ToString("N"); copy.IsBuiltIn = false; copy.Name += " · 副本"; State.Presets.Add(copy); SaveSoon(); ShowPage(3);
            }), B("导出", () => ExportPreset(p)));
            if (!p.IsBuiltIn)
            {
                row.Children.Add(B("重命名", () => { var name = Prompt("重命名预设", p.Name); if (name is null) return; p.Name = name; SaveSoon(); ShowPage(3); }));
                row.Children.Add(B(p.IsFavorite ? "取消收藏" : "收藏", () => { p.IsFavorite = !p.IsFavorite; SaveSoon(); ShowPage(3); }));
                row.Children.Add(B("删除", () =>
                {
                    if (MessageBox.Show(this, "删除个人预设“" + p.Name + "”？当前草稿会保留。", "Mic Studio", MessageBoxButton.OKCancel, MessageBoxImage.Question) != MessageBoxResult.OK) return;
                    State.Presets.Remove(p); SaveSoon(); ShowPage(3);
                }));
            }
            Add(Card((p.IsFavorite ? "★  " : "") + p.Name + (p.IsBuiltIn ? "  /  内置" : "  /  个人"), p.Description, row));
        }
    }
    private void ExportPreset(SoundPreset preset)
    {
        var d = new Microsoft.Win32.SaveFileDialog { Filter = "Mic Studio 预设|*.json", FileName = "MicStudio-preset.json", Title = "导出便携预设" };
        if (d.ShowDialog(this) == true) { PresetStore.ExportFile(d.FileName, preset); Notice("预设已导出，不包含设备唯一标识、路径或录音。"); }
    }
}
