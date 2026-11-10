using System.IO;
using TuneSync.Core.Models;

namespace TuneSync.App.ViewModels;

/// <summary>Read-only display wrapper around one planned <see cref="MergeAction"/> row.</summary>
public sealed class MergeActionViewModel(MergeAction action)
{
    public MergeAction Action { get; } = action;

    public string SourceFileName => Path.GetFileName(Action.SourcePath);
    public string DestFileName => Action.DestFileName;
    public string Reason => Action.Reason;

    public string ActionLabel => Action.ActionType switch
    {
        MergeActionType.Move => "이동",
        MergeActionType.Replace => "교체 (기존 파일은 격리)",
        MergeActionType.Quarantine => "격리 (소스 보류)",
        MergeActionType.RenameAndMove => "이름 변경 후 이동",
        _ => Action.ActionType.ToString(),
    };
}
