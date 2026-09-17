using LimbusModEditor.Application.Texts;
using LimbusModEditor.Domain.Projects;

namespace LimbusModEditor.Application.Ipc;

/// <summary>当前项目状态（线程安全）。由 project.open 设置，供后续编辑/导出使用。</summary>
public sealed class ProjectState
{
    private readonly object _lock = new();
    private ModProject? _project;
    private string? _projectFile;
    private LangEditSession? _langEdits;
    private StaticEditSession? _staticEdits;

    /// <summary>当前项目（null = 无打开项目）。</summary>
    public ModProject? Project { get { lock (_lock) return _project; } }

    /// <summary>当前项目文件路径。</summary>
    public string? ProjectFile { get { lock (_lock) return _projectFile; } }

    /// <summary>当前语言编辑会话。</summary>
    public LangEditSession? LangEdits { get { lock (_lock) return _langEdits; } }

    /// <summary>当前静态数据编辑会话。</summary>
    public StaticEditSession? StaticEdits { get { lock (_lock) return _staticEdits; } }

    /// <summary>
    /// 设置当前项目（打开 / 新建项目时调用）。
    ///
    /// <para><paramref name="langService"/> 必须是网关那份 <see cref="LangTextWorkbenchService"/>：
    /// lang.applyPatch / lang.editEntry 改的是它，<see cref="LangEdits"/> 的快照与导出读的也是它。
    /// 传 null（或不传）会另起一份空服务 —— 那样「工作台改的」与「导出读的」是两份互不可见的编辑集。</para>
    ///
    /// <para>换项目时清掉上一份编辑集：它是内存态，按条目相对路径存的，
    /// 带着上一个项目的改动到新项目里会导出出错的东西。</para>
    /// </summary>
    public void SetProject(ModProject project, string projectFile, LangTextWorkbenchService? langService = null)
    {
        lock (_lock)
        {
            var switching = !string.Equals(_projectFile, projectFile, StringComparison.OrdinalIgnoreCase);
            if (switching && langService is not null && langService.EditedFiles.Count > 0)
                langService.ClearEdits();

            _project = project;
            _projectFile = projectFile;
            _langEdits = new LangEditSession(langService);
            _staticEdits = new StaticEditSession();
        }
    }

    /// <summary>清除当前项目（关闭项目时调用）。</summary>
    public void Clear()
    {
        lock (_lock)
        {
            _project = null;
            _projectFile = null;
            _langEdits = null;
            _staticEdits = null;
        }
    }
}
