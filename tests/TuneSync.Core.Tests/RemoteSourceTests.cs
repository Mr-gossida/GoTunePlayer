using TuneSync.Core.Models;
using TuneSync.Core.Services;

namespace TuneSync.Core.Tests;

/// <summary>Network locations in the settings file: the server key/certificate trusted for a location
/// is kept with it, and locations saved before that existed still load.</summary>
public class RemoteSourceTests : IDisposable
{
    private readonly string _path = Path.Combine(Path.GetTempPath(), $"tunesync-settings-{Guid.NewGuid():N}.json");

    public void Dispose()
    {
        if (File.Exists(_path)) File.Delete(_path);
    }

    [Fact]
    public void LocationSavedByAnOlderVersion_LoadsWithNothingTrustedYet()
    {
        File.WriteAllText(_path, """
            { "RemoteSources": [ { "Id": "a1", "Name": "거실 NAS", "Kind": 3, "Address": "nas.local", "Port": 0,
                                   "UserName": "me", "AcceptAnyCertificate": true } ] }
            """);

        var source = Assert.Single(AppSettingsStore.Load(_path).RemoteSources);

        Assert.Equal("a1", source.Id);
        Assert.Equal(RemoteKind.Sftp, source.Kind);
        Assert.True(source.AcceptAnyCertificate);
        Assert.Null(source.HostKeyFingerprint);
        Assert.Null(source.HostKeyAlgorithm);
        Assert.Null(source.CertificateThumbprint);
    }

    [Fact]
    public void TrustedKeyAndCertificate_AreKeptWithTheLocation()
    {
        AppSettingsStore.Save(new AppSettings
        {
            RemoteSources =
            [
                new RemoteSource
                {
                    Id = "ssh", Kind = RemoteKind.Sftp, Address = "nas.local",
                    HostKeyFingerprint = "ohD8VZEXGWo6Ez8GSEJQ9WpafgLFsOfLOtGGQCQo6Og", HostKeyAlgorithm = "ssh-ed25519",
                },
                new RemoteSource
                {
                    Id = "dav", Kind = RemoteKind.WebDav, Address = "https://192.168.0.10:5006", AcceptAnyCertificate = true,
                    CertificateThumbprint = "3AF1C2D4E5F60718293A4B5C6D7E8F90A1B2C3D4E5F60718293A4B5C6D7E8F90",
                },
            ],
        }, _path);

        var reloaded = AppSettingsStore.Load(_path).RemoteSources;

        var ssh = reloaded.Single(s => s.Id == "ssh");
        Assert.Equal("ohD8VZEXGWo6Ez8GSEJQ9WpafgLFsOfLOtGGQCQo6Og", ssh.HostKeyFingerprint);
        Assert.Equal("ssh-ed25519", ssh.HostKeyAlgorithm);
        Assert.Null(ssh.CertificateThumbprint);
        var dav = reloaded.Single(s => s.Id == "dav");
        Assert.Equal("3AF1C2D4E5F60718293A4B5C6D7E8F90A1B2C3D4E5F60718293A4B5C6D7E8F90", dav.CertificateThumbprint);
        Assert.Null(dav.HostKeyFingerprint);
    }
}
