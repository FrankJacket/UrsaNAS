namespace Nas.Agent.Services;

/// <summary>Modifie /etc/fstab de façon atomique (copie de sauvegarde au premier passage). Un seul verrou pour tous les services qui y écrivent.</summary>
public static class FstabFile
{
    static readonly object Lock = new();

    public static void Edit(string fstab, Func<List<string>, IReadOnlyList<string>> edit)
    {
        lock (Lock)
        {
            var lines = File.Exists(fstab) ? File.ReadAllLines(fstab).ToList() : [];
            var backup = fstab + ".ursanas-bak";
            if (File.Exists(fstab) && !File.Exists(backup)) File.Copy(fstab, backup);
            var tmp = fstab + ".ursanas-tmp";
            File.WriteAllLines(tmp, edit(lines));
            File.Move(tmp, fstab, overwrite: true);
        }
    }
}
