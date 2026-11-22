using System.Text;

namespace Nas.Agent.Services;

/// <summary>
/// Empreinte NT d'un mot de passe (MD4 de son texte UTF-16), celle que Samba garde dans sa base. Sert à vérifier un mot de passe contre
/// le compte Samba sans le stocker une seconde fois : <c>pdbedit -w</c> donne l'empreinte, on compare. MD4 n'est pas dans .NET.
/// </summary>
public static class NtHash
{
    /// <summary>Empreinte NT en hexadécimal majuscule (32 caractères), comme l'écrit <c>pdbedit -w</c>.</summary>
    public static string Of(string password) => Convert.ToHexString(Md4(Encoding.Unicode.GetBytes(password)));

    /// <summary>Extrait l'empreinte NT de <c>pdbedit -L -w</c> (<c>nom:uid:LM:NT:[drapeaux]:LCT-…</c>) ; <c>null</c> si absente ou compte sans mot de passe.</summary>
    public static string? FromPdbedit(string output, string name)
    {
        foreach (var line in output.Split('\n', StringSplitOptions.RemoveEmptyEntries))
        {
            var f = line.Trim().Split(':');
            if (f.Length >= 4 && f[0] == name && f[3].Length == 32 && f[3].Any(c => c != 'X')) return f[3].ToUpperInvariant();
        }
        return null;
    }

    static byte[] Md4(byte[] input)
    {
        // Remplissage : un bit à 1, des zéros, puis la longueur en bits sur 64 bits (petit-boutiste).
        var length = (long)input.Length * 8;
        var padded = new byte[(input.Length + 8) / 64 * 64 + 64];
        Array.Copy(input, padded, input.Length);
        padded[input.Length] = 0x80;
        BitConverter.GetBytes(length).CopyTo(padded, padded.Length - 8);

        uint a = 0x67452301, b = 0xefcdab89, c = 0x98badcfe, d = 0x10325476;
        var x = new uint[16];
        for (var block = 0; block < padded.Length; block += 64)
        {
            for (var i = 0; i < 16; i++) x[i] = BitConverter.ToUInt32(padded, block + i * 4);
            uint aa = a, bb = b, cc = c, dd = d;

            static uint F(uint x, uint y, uint z) => (x & y) | (~x & z);
            static uint G(uint x, uint y, uint z) => (x & y) | (x & z) | (y & z);
            static uint H(uint x, uint y, uint z) => x ^ y ^ z;
            static uint Rol(uint v, int s) => (v << s) | (v >> (32 - s));

            // Tour 1
            foreach (var i in new[] { 0, 4, 8, 12 })
            {
                a = Rol(a + F(b, c, d) + x[i], 3);
                d = Rol(d + F(a, b, c) + x[i + 1], 7);
                c = Rol(c + F(d, a, b) + x[i + 2], 11);
                b = Rol(b + F(c, d, a) + x[i + 3], 19);
            }
            // Tour 2
            foreach (var i in new[] { 0, 1, 2, 3 })
            {
                a = Rol(a + G(b, c, d) + x[i] + 0x5a827999, 3);
                d = Rol(d + G(a, b, c) + x[i + 4] + 0x5a827999, 5);
                c = Rol(c + G(d, a, b) + x[i + 8] + 0x5a827999, 9);
                b = Rol(b + G(c, d, a) + x[i + 12] + 0x5a827999, 13);
            }
            // Tour 3
            foreach (var i in new[] { 0, 2, 1, 3 })
            {
                a = Rol(a + H(b, c, d) + x[i] + 0x6ed9eba1, 3);
                d = Rol(d + H(a, b, c) + x[i + 8] + 0x6ed9eba1, 9);
                c = Rol(c + H(d, a, b) + x[i + 4] + 0x6ed9eba1, 11);
                b = Rol(b + H(c, d, a) + x[i + 12] + 0x6ed9eba1, 15);
            }
            a += aa; b += bb; c += cc; d += dd;
        }

        var result = new byte[16];
        BitConverter.GetBytes(a).CopyTo(result, 0);
        BitConverter.GetBytes(b).CopyTo(result, 4);
        BitConverter.GetBytes(c).CopyTo(result, 8);
        BitConverter.GetBytes(d).CopyTo(result, 12);
        return result;
    }
}
