using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;

namespace Payroll.Ros;

/// <summary>
/// Loads a ROS digital certificate from its .p12 file. Per Revenue's REST integration guide (Appendix A),
/// the password protecting the P12 is not the password you type into ROS - it's the MD5 hash of that
/// password (as Latin-1 bytes), Base64-encoded.
/// </summary>
public static class RosCertificateLoader
{
    public static X509Certificate2 Load(string p12Path, string plainPassword)
    {
        var derivedPassword = DerivePkcs12Password(plainPassword);
        return X509CertificateLoader.LoadPkcs12FromFile(p12Path, derivedPassword, X509KeyStorageFlags.Exportable);
    }

    public static string DerivePkcs12Password(string plainPassword)
    {
        var bytes = Encoding.Latin1.GetBytes(plainPassword);
        var hash = MD5.HashData(bytes);
        return Convert.ToBase64String(hash);
    }
}
