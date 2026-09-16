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

    /// <summary>设置当前项目（打开项目时调用）。</summary>
    public void SetProject(ModProject project, string projectFile)
    {
        lock (_lock)
        {
            _project = project;
            _projectFile = projectFile;
            _langEdits = new LangEditSession();
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
