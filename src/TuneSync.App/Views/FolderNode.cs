using System.Collections.ObjectModel;
using System.IO;

namespace TuneSync.App.Views;

/// <summary>Lazily-expanding folder tree node, Explorer-style: a node's subfolders are only
/// enumerated the first time it is expanded, so opening the tree never walks the whole disk.</summary>
public sealed class FolderNode
{
    private static readonly FolderNode Placeholder = new("", "");
    private bool _loaded;

    public string Path { get; }
    public string Name { get; }
    public ObservableCollection<FolderNode> Children { get; } = [];

    public FolderNode(string path, string name)
    {
        Path = path;
        Name = name;
        Children.Add(Placeholder);
    }

    public void EnsureLoaded()
    {
        if (_loaded) return;
        _loaded = true;
        Children.Clear();
        try
        {
            foreach (var dir in Directory.EnumerateDirectories(Path).OrderBy(d => d, StringComparer.CurrentCultureIgnoreCase))
            {
                var info = new DirectoryInfo(dir);
                if ((info.Attributes & (FileAttributes.Hidden | FileAttributes.System)) != 0) continue;
                Children.Add(new FolderNode(dir, info.Name));
            }
        }
        catch
        {
            // access denied / unreadable junction - leave the node childless rather than crash the tree
        }
    }

    public static ObservableCollection<FolderNode> CreateRoots()
    {
        var roots = new ObservableCollection<FolderNode>();
        foreach (var drive in DriveInfo.GetDrives())
        {
            if (!drive.IsReady) continue;
            var label = string.IsNullOrEmpty(drive.VolumeLabel) ? drive.Name : $"{drive.VolumeLabel} ({drive.Name})";
            roots.Add(new FolderNode(drive.RootDirectory.FullName, label));
        }
        return roots;
    }
}
