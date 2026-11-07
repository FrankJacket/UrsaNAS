using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Nas.Agent.Services;
using Nas.Contracts;

namespace Nas.Agent.Tests;

public class DiskFilesTests : IDisposable
{
    readonly string _dir = Path.Combine(Path.GetTempPath(), "ursanas-diskfiles-" + Guid.NewGuid().ToString("N"));
    readonly string _usb;
    readonly DiskFilesService _svc;

    string Usb => _usb.Replace('\\', '/');

    public DiskFilesTests()
    {
        var roots = Path.Combine(_dir, "mnt");
        _usb = Path.Combine(roots, "usb");
        Directory.CreateDirectory(Path.Combine(_usb, "docs", "2024"));
        File.WriteAllText(Path.Combine(_usb, "docs", "a.txt"), "alpha");
        File.WriteAllText(Path.Combine(_usb, "docs", "2024", "b.txt"), "beta");
        File.WriteAllText(Path.Combine(_usb, "root.txt"), "r");

        var cfg = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Backup:Roots:0"] = roots.Replace('\\', '/'),
            ["Backup:RequireMountPoint"] = "false",
        }).Build();
        var run = new FakeRunner();
        var users = new UserService(run);
        var backup = new BackupService(run, new AclService(run, users, new GroupService(run, users), cfg), cfg, NullLogger<BackupService>.Instance, new StubLifetime());
        _svc = new DiskFilesService(backup);
    }

    public void Dispose() { try { Directory.Delete(_dir, true); } catch (IOException) { } }

    [Theory]
    [InlineData("/srv/nas/docs", true)]
    [InlineData("docs", false)]
    public void Un_emplacement_qui_commence_par_une_barre_est_un_disque(string location, bool disk) => Assert.Equal(disk, DiskFilesService.IsDisk(location));

    [Fact]
    public void Liste_les_dossiers_et_les_fichiers_du_disque()
    {
        var root = _svc.List(Usb, "", true);
        Assert.Equal(["docs"], root.Folders.Select(f => f.Name));
        Assert.Equal(["root.txt"], root.Files!.Select(f => f.Name));
        Assert.Equal(["2024"], _svc.List(Usb, "docs", false).Folders.Select(f => f.Name));
        Assert.Null(_svc.List(Usb, "docs", false).Files);
    }

    [Theory]
    [InlineData("/etc")]
    [InlineData("/mnt")]
    [InlineData("")]
    public void Refuse_un_dossier_qui_n_est_pas_un_disque_monte(string dir) => Assert.Throws<OperationException>(() => _svc.List(dir, "", false));

    [Theory]
    [InlineData("../x")]
    [InlineData("docs/../../x")]
    [InlineData("absent")]
    public void Refuse_les_chemins_qui_sortent_du_disque_ou_qui_n_existent_pas(string path) => Assert.Throws<OperationException>(() => _svc.List(Usb, path, false));

    [Fact]
    public void Cree_un_dossier_et_refuse_un_doublon_meme_a_la_casse_pres()
    {
        _svc.CreateFolder(new CreateFolderRequest(Usb, "docs", "new"));
        Assert.True(Directory.Exists(Path.Combine(_usb, "docs", "new")));
        Assert.Throws<OperationException>(() => _svc.CreateFolder(new CreateFolderRequest(Usb, "docs", "NEW")));
        Assert.Throws<OperationException>(() => _svc.CreateFolder(new CreateFolderRequest(Usb, "docs", "a/b")));
    }

    [Fact]
    public void Renomme_deplace_et_copie_dossiers_et_fichiers()
    {
        _svc.Move(new MoveFolderRequest(Usb, "docs/a.txt", "docs", "z.txt", true));
        Assert.True(File.Exists(Path.Combine(_usb, "docs", "z.txt")));

        _svc.Move(new MoveFolderRequest(Usb, "docs/2024", "", "archive", false));
        Assert.Equal("beta", File.ReadAllText(Path.Combine(_usb, "archive", "b.txt")));

        _svc.Copy(new CopyFolderRequest(Usb, "archive", "docs", "copy", false));
        Assert.Equal("beta", File.ReadAllText(Path.Combine(_usb, "docs", "copy", "b.txt")));
        Assert.True(File.Exists(Path.Combine(_usb, "archive", "b.txt"))); // l'original reste

        _svc.Copy(new CopyFolderRequest(Usb, "root.txt", "docs", "root - copy.txt", true));
        Assert.Equal("r", File.ReadAllText(Path.Combine(_usb, "docs", "root - copy.txt")));
    }

    [Fact]
    public void Refuse_de_deplacer_un_dossier_dans_lui_meme_ou_sur_un_nom_pris()
    {
        Assert.Throws<OperationException>(() => _svc.Move(new MoveFolderRequest(Usb, "docs", "docs/2024", "docs", false)));
        Assert.Throws<OperationException>(() => _svc.Move(new MoveFolderRequest(Usb, "root.txt", "docs", "A.TXT", true)));
        Assert.Throws<OperationException>(() => _svc.Move(new MoveFolderRequest(Usb, "", "docs", "x", false))); // la racine du disque
    }

    [Fact]
    public void Supprime_un_fichier_ou_un_dossier_mais_jamais_la_racine_du_disque()
    {
        _svc.Delete(Usb, "docs/a.txt", true);
        Assert.False(File.Exists(Path.Combine(_usb, "docs", "a.txt")));
        _svc.Delete(Usb, "docs", false);
        Assert.False(Directory.Exists(Path.Combine(_usb, "docs")));
        Assert.Throws<OperationException>(() => _svc.Delete(Usb, "", false));
        Assert.True(Directory.Exists(_usb));
    }
}
