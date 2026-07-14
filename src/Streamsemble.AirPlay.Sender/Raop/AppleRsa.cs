using System.Security.Cryptography;

namespace Streamsemble.AirPlay.Sender.Raop;

/// <summary>
/// The well-known AirPort Express RSA public key. Classic RAOP receivers
/// (TXT <c>et</c> contains 1) expect the per-session AES-128 key to arrive
/// RSA-OAEP-encrypted to this key in the ANNOUNCE SDP (<c>a=rsaaeskey</c>).
/// </summary>
public static class AppleRsa
{
    private const string ModulusBase64 =
        "59dE8qLieItsH1WgjrcFRKj6eUWqi+bGLOX1HL3U3GhC/j0Qg90u3sG/1CUtwC5vOYvfDmFI6oSF" +
        "Xi5ELabWJmT2dKHzBJKa3k9ok+8t9ucRqMd6DZHJ2YCCLlDRKSKv6kDqnw4UwPdpOMXziC/AMj3Z" +
        "/lUVX1G7WSHCAWKf1zNS1eLvqr+boEjXuBOitnZ/bDzPHrTOZz0Dew0uowxf/+sG+NCK3eQJVxqc" +
        "aJ/vEHKIVd2M+5qL71yJQ+87X6oV3eaYvt3zWZYD6z5vYTcrtij2VZ9Zmni/UAaHqn9JdsBWLUEp" +
        "VviYnhimNVvYFZeCXg/IdTQ+x4IRdiXNv5hEew==";

    public static byte[] EncryptAesKey(byte[] aesKey)
    {
        using var rsa = RSA.Create(new RSAParameters
        {
            Modulus = Convert.FromBase64String(ModulusBase64),
            Exponent = [1, 0, 1],
        });
        return rsa.Encrypt(aesKey, RSAEncryptionPadding.OaepSHA1);
    }
}
