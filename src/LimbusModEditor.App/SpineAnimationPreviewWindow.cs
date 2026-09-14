using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using LimbusModEditor.Application.Spine;
using LimbusModEditor.SpineRuntime;
using NLog;

namespace LimbusModEditor.App;

/// <summary>
/// Spine 骨骼动画预览窗口：用 vendored 的 spine-csharp 4.0 + SkiaSharp 把骨架
/// <b>真的画出来</b>，并支持在动画之间切换、播放 / 暂停、拖时间轴看任意一帧。
///
/// <para><b>它补的是什么</b>：资源页原先的 Spine 预览只给「结构（骨骼/槽位/动画清单）+
/// 图集区域框」，看不到动画长什么样；用户要看动画只能导出到外部工具。这个窗口把
/// 「渲染」接进本工具（渲染逻辑全在 <c>LimbusModEditor.SpineRuntime</c>，这里只负责
/// 拿素材、驱动时间、把 PNG 贴到 <see cref="Image"/>）。</para>
///
/// <para><b>失败要说人话</b>：骨架 / 图集 / 图集页任一件缺失或解析失败，都在信息行给出
/// 中文原因（<c>SpineDocument.TryCreate</c> / <c>RenderFrame</c> 的错误就是这么设计的），
/// 不留一张白图让人猜。</para>
/// </summary>
public sealed class SpineAnimationPreviewWindow : Window
{
    private static readonly Logger Log = LogManager.GetCurrentClassLogger();

    /// <summary>画布尺寸（像素）。要按「立绘瘦高」给，横图会被 FitToCanvas 等比缩进去。</summary>
    private const int CanvasWidth = 440;
    private const int CanvasHeight = 600;

    /// <summary>帧间隔 ~30fps：Spine 动画常见 30/60fps，30 已经看不出卡顿，且 Skia 离屏绘制有余量。</summary>
    private const double FrameIntervalMs = 1000.0 / 30;

    private readonly SpineAnimationSource _source;
    private readonly Image _image;
    private readonly ComboBox _animationBox;
    private readonly Button _playButton;
    private readonly Slider _timeline;
    private readonly TextBlock _info;
    private readonly DispatcherTimer _timer;

    private SpineDocument? _document;
    private SpineFrameRenderer? _renderer;
    private double _duration;
    private double _time;
    private bool _playing;
    private bool _updatingTimeline;

    /// <summary>
    /// 打开某份素材的动画预览（非模态：用户可以一边看动画、一边在资源页继续点别的资源）。
    /// </summary>
    /// <param name="owner">宿主窗口（用于居中与生命周期；可为 null）。</param>
    /// <param name="source">已解析好的素材（见 <see cref="SpineAnimationSourceService"/>）。</param>
    /// <param name="title">标题里显示的资源名（一般是资源所在目录名）。</param>
    public static void ShowFor(Window? owner, SpineAnimationSource source, string? title = null)
    {
        ArgumentNullException.ThrowIfNull(source);
        var window = new SpineAnimationPreviewWindow(source, title);
        if (owner is not null) window.Owner = owner;
        // 非模态 Show：模态会把主窗口与资源页一起冻住，用户就没法「看一个换一个」了。
        window.Show();
    }

    private SpineAnimationPreviewWindow(SpineAnimationSource source, string? title)
    {
        _source = source;
        Title = $"Spine 动画预览 — {(string.IsNullOrWhiteSpace(title) ? source.Label : title)}";
        Width = 560;
        Height = 760;
        MinWidth = 400;
        MinHeight = 460;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        Background = WbBrush("WbWindowBackgroundBrush") ?? WbBrush("WbPanelBackgroundBrush") ?? Brushes.Transparent;

        _image = new Image { Stretch = Stretch.Uniform };
        _info = new TextBlock
        {
            TextWrapping = TextWrapping.Wrap,
            Opacity = 0.8,
            Foreground = WbBrush("WbTextMutedBrush"),
            Margin = new Thickness(0, 0, 0, 6),
        };

        _animationBox = new ComboBox { MinWidth = 200, IsEnabled = false, Margin = new Thickness(0, 0, 8, 0) };
        _animationBox.SelectionChanged += (_, _) => OnAnimationChanged();
        _playButton = new Button { Content = "▶ 播放", IsEnabled = false, MinWidth = 90 };
        _playButton.Click += (_, _) => TogglePlay();
        _timeline = new Slider
        {
            Minimum = 0,
            Maximum = 1,
            Value = 0,
            IsEnabled = false,
            IsSnapToTickEnabled = false,
            Margin = new Thickness(0, 8, 0, 0),
            ToolTip = "拖动查看任意一帧（拖动时自动暂停播放）",
        };
        _timeline.ValueChanged += (_, e) => OnTimelineChanged(e.NewValue);

        var controls = new DockPanel { Margin = new Thickness(0, 0, 0, 6) };
        DockPanel.SetDock(_playButton, Dock.Right);
        controls.Children.Add(_playButton);
        controls.Children.Add(_animationBox);

        var canvasFrame = new Border
        {
            Background = TryFindResource("Checkerboard") as Brush ?? WbBrush("WbPanelBackgroundBrush"),
            BorderBrush = WbBrush("WbBorderBrush") ?? WbBrush("WbBorderLineBrush"),
            BorderThickness = new Thickness(1),
            Padding = new Thickness(2),
            Child = _image,
        };

        var root = new DockPanel { Margin = new Thickness(10) };
        DockPanel.SetDock(_info, Dock.Top);
        DockPanel.SetDock(controls, Dock.Top);
        DockPanel.SetDock(_timeline, Dock.Bottom);
        root.Children.Add(_info);
        root.Children.Add(controls);
        root.Children.Add(_timeline);
        root.Children.Add(canvasFrame);
        Content = root;

        _timer = new DispatcherTimer(DispatcherPriority.Render) { Interval = TimeSpan.FromMilliseconds(FrameIntervalMs) };
        _timer.Tick += (_, _) => Advance();
        Closed += (_, _) => DisposeDocument();

        LoadDocument();
    }

    /// <summary>把素材交给 Spine 运行时加载（失败只在信息行说明，不弹窗、不崩）。</summary>
    private void LoadDocument()
    {
        SpineLoadResult load;
        try
        {
            load = SpineDocument.TryCreate(_source.SkeletonJson, _source.AtlasText, new SourceTextureSource(_source));
        }
        catch (Exception ex)
        {
            // TryCreate 承诺不抛，但这是 vendored 三方库的边界：兜住，别让它带走主窗口。
            Log.Error(ex, "Spine 动画预览加载异常");
            _info.Text = $"加载 Spine 骨架时发生意外错误：{ex.Message}";
            return;
        }

        if (!load.Success || load.Document is null)
        {
            _info.Text = load.Error ?? "Spine 骨架加载失败。";
            Log.Warn("Spine 动画预览加载失败：{0}（素材 {1}）", load.Error, _source.Label);
            return;
        }

        _document = load.Document;
        _renderer = new SpineFrameRenderer(_document);

        var names = _document.AnimationNames;
        foreach (var name in names) _animationBox.Items.Add(name);
        _animationBox.IsEnabled = names.Count > 0;
        _timeline.IsEnabled = names.Count > 0;
        _playButton.IsEnabled = names.Count > 0;

        var missing = load.MissingPages.Count == 0
            ? string.Empty
            : $" · 缺图集页：{string.Join("、", load.MissingPages)}";
        _info.Text = $"{_source.Label} · 动画 {names.Count} 个 · 皮肤 {_document.Skins.Count} 个 · "
            + $"骨骼 {_document.Bones.Count} 根 · 槽位 {_document.Slots.Count} 个{missing}";
        Log.Info("Spine 动画预览已载入：{0}，动画 {1} 个，缺图集页 {2} 个",
            _source.Label, names.Count, load.MissingPages.Count);

        if (names.Count == 0)
        {
            // 没有动画也把 setup pose 画出来：那本身就是「这个骨架长什么样」。
            RenderAt(0);
            return;
        }
        _animationBox.SelectedIndex = 0;
    }

    private void OnAnimationChanged()
    {
        if (_document is null || _animationBox.SelectedItem is not string name) return;
        var entry = _document.SetAnimation(name, loop: true);
        if (entry is null)
        {
            _info.Text = $"骨架里没有名为「{name}」的动画。";
            Log.Warn("Spine 动画预览：找不到动画 {0}", name);
            return;
        }
        _duration = _document.AnimationDuration(name);
        _time = 0;
        _updatingTimeline = true;
        try
        {
            _timeline.Maximum = Math.Max(0.001, _duration);
            _timeline.Value = 0;
        }
        finally { _updatingTimeline = false; }
        RenderAt(0);
        Log.Debug("Spine 动画预览切换动画：{0}（时长 {1:F3} 秒）", name, _duration);
    }

    private void TogglePlay()
    {
        if (_playing) { StopPlayback(); return; }
        if (_document is null || _animationBox.Items.Count == 0) return;
        // 播到末尾再点播放：从头开始（否则会停在最后一帧不动，看起来像坏了）。
        if (_duration > 0 && _time >= _duration - 1e-3) { _time = 0; RenderAt(0); }
        _playing = true;
        _playButton.Content = "⏸ 暂停";
        _timer.Start();
    }

    private void StopPlayback()
    {
        _playing = false;
        _playButton.Content = "▶ 播放";
        _timer.Stop();
    }

    /// <summary>推进一帧（播放循环）。到末尾按循环回绕。</summary>
    private void Advance()
    {
        if (!_playing || _duration <= 0) return;
        _time += FrameIntervalMs / 1000.0;
        if (_time > _duration) _time -= _duration * Math.Floor(_time / _duration);
        _updatingTimeline = true;
        try { _timeline.Value = Math.Clamp(_time, 0, _duration); }
        finally { _updatingTimeline = false; }
        RenderAt(_time);
    }

    private void OnTimelineChanged(double value)
    {
        if (_updatingTimeline) return;
        // 拖动时间轴时暂停：让用户能停在想看的这一帧上。
        if (_playing) StopPlayback();
        _time = value;
        RenderAt(value);
    }

    /// <summary>渲染某一时刻并贴到画布上。渲染失败要说清楚原因，然后把播放停掉（避免刷屏）。</summary>
    private void RenderAt(double time)
    {
        if (_renderer is null) return;
        SpineRenderResult frame;
        try
        {
            frame = _renderer.RenderFrame(time, CanvasWidth, CanvasHeight);
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Spine 动画预览渲染异常");
            _info.Text = $"渲染动画帧时发生意外错误：{ex.Message}";
            StopPlayback();
            return;
        }
        if (!frame.Success || frame.PngBytes is null)
        {
            _info.Text = frame.Error ?? "渲染失败。";
            Log.Warn("Spine 动画预览渲染失败：{0}（素材 {1}）", frame.Error, _source.Label);
            StopPlayback();
            return;
        }
        _image.Source = ToBitmap(frame.PngBytes);
    }

    private static BitmapImage? ToBitmap(byte[] png)
    {
        try
        {
            var bitmap = new BitmapImage();
            bitmap.BeginInit();
            bitmap.CacheOption = BitmapCacheOption.OnLoad;
            bitmap.StreamSource = new MemoryStream(png);
            bitmap.EndInit();
            bitmap.Freeze();
            return bitmap;
        }
        catch (Exception ex) when (ex is NotSupportedException or IOException or ArgumentException)
        {
            return null;
        }
    }

    private void DisposeDocument()
    {
        _timer.Stop();
        _playing = false;
        // 顺序：先释放渲染器（它持有骨架的中间缓冲），再释放文档（图集 / 纹理）。
        try { _renderer?.Dispose(); } catch (Exception ex) { Log.Debug(ex, "Spine 渲染器释放异常（忽略）"); }
        try { _document?.Dispose(); } catch (Exception ex) { Log.Debug(ex, "Spine 文档释放异常（忽略）"); }
        _renderer = null;
        _document = null;
    }

    /// <summary>把素材里的「页名 → PNG 字节」适配成 Spine 运行时要的纹理来源。</summary>
    private sealed class SourceTextureSource : ISpineTextureSource
    {
        private readonly SpineAnimationSource _source;

        public SourceTextureSource(SpineAnimationSource source) => _source = source;

        public byte[] GetPageBytes(string pageName) => _source.PageBytes(pageName);
    }

    private static Brush? WbBrush(string key)
        => System.Windows.Application.Current?.TryFindResource(key) as Brush;
}
