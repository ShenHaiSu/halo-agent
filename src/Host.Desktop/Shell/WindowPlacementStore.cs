using System.Text.Json;
using System.Windows.Forms;

namespace HaloAgent.Desktop.Shell;

/// <summary>
/// 窗口位置读写。存 LocalAppData（纯 UI 偏好，不进数据目录，避免换机恢复跑飞）。
/// 恢复时只要求“与任一屏幕有足够重叠”，允许用户故意跨屏摆放。
/// </summary>
public static class WindowPlacementStore
{
    private static string Path_ =>
        Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "HaloAgent", "window.json");

    public static WindowPlacement Load()
    {
        try
        {
            if (!File.Exists(Path_)) return new WindowPlacement();
            var json = File.ReadAllText(Path_);
            return JsonSerializer.Deserialize<WindowPlacement>(json) ?? new WindowPlacement();
        }
        catch
        {
            return new WindowPlacement();
        }
    }

    public static void Save(WindowPlacement p)
    {
        try
        {
            var path = Path_;
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, JsonSerializer.Serialize(p,
                new JsonSerializerOptions { WriteIndented = true }));
        }
        catch { /* 保存失败不影响退出 */ }
    }

    public static Rectangle ClampToVisibleScreen(Rectangle saved)
    {
        // 尺寸兜底（防手改荒谬值）。
        int w = Math.Max(900, Math.Min(saved.Width, 8000));
        int h = Math.Max(560, Math.Min(saved.Height, 8000));
        var candidate = new Rectangle(saved.Left, saved.Top, w, h);

        foreach (var screen in Screen.AllScreens)
        {
            var inter = Rectangle.Intersect(candidate, screen.WorkingArea);
            if (inter.Width >= 200 && inter.Height >= 100)
                return candidate;
        }

        var wa = Screen.PrimaryScreen!.WorkingArea;
        int cw = Math.Min(w, wa.Width - 80);
        int ch = Math.Min(h, wa.Height - 80);
        return new Rectangle(
            wa.Left + (wa.Width - cw) / 2,
            wa.Top + (wa.Height - ch) / 2,
            cw, ch);
    }
}
