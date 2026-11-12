using System.IO;
using CommunityToolkit.Mvvm.ComponentModel;
using TuneSync.Core.Models;

namespace TuneSync.App.ViewModels;

/// <summary>One registered library source folder, shown in the bottom folder list.</summary>
public sealed partial class LibraryFolderViewModel : ObservableObject
{
    public LibraryFolderEntry Entry { get; }

    public LibraryFolderViewModel(LibraryFolderEntry entry)
    {
        Entry = entry;
    }

    public string Path => Entry.Path;
    public bool IsFile => Entry.IsFile;

    /// <summary>Network folders (WebDAV/FTP/SFTP) are checked by the scan itself, not here.</summary>
    public bool Exists => Entry.IsRemote || (IsFile ? File.Exists(Path) : Directory.Exists(Path));
}
