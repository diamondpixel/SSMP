namespace MMS.Services.Utility;

/// <summary>
/// Generates random tokens and lobby codes.
/// Uses a <see langword="[ThreadStatic]"/> <see cref="Random"/> instance per thread
/// to avoid lock contention on hot paths.
/// </summary>
internal static class TokenGenerator
{
    private const string TokenChars = "abcdefghijklmnopqrstuvwxyz0123456789";
    private const string LobbyCodeChars = "ABCDEFGHIJKLMNOPQRSTUVWXYZ0123456789";

    /// <summary>Fixed length of all generated lobby codes.</summary>
    private const int LobbyCodeLength = 6;

    [ThreadStatic]
    private static Random? _random;

    /// <summary>Per-thread RNG; initialized lazily on first access per thread.</summary>
    private static Random Rng => _random ??= new Random();

    /// <summary>
    /// Generates a random URL-safe token of the requested length.
    /// Characters are drawn from lowercase alphanumerics (<c>a-z0-9</c>).
    /// </summary>
    /// <param name="length">Number of characters in the returned token.</param>
    /// <returns>A random lowercase alphanumeric string of <paramref name="length"/> characters.</returns>
    public static string GenerateToken(int length) =>
        string.Create(length, 0, (span, _) =>
        {
            for (var i = 0; i < span.Length; i++)
                span[i] = TokenChars[Rng.Next(TokenChars.Length)];
        });

    /// <summary>
    /// Generates a unique <see cref="LobbyCodeLength"/>-character lobby code that does not
    /// already exist in <paramref name="existingCodes"/>.
    /// Characters are drawn from uppercase alphanumerics (<c>A-Z0-9</c>).
    /// </summary>
    /// <param name="existingCodes">The current set of live lobby codes used for collision detection.</param>
    /// <returns>A unique uppercase alphanumeric lobby code.</returns>
    public static string GenerateUniqueLobbyCode(ICollection<string> existingCodes)
    {
        string code;
        do
        {
            code = string.Create(LobbyCodeLength, 0, (span, _) =>
            {
                for (var i = 0; i < span.Length; i++)
                    span[i] = LobbyCodeChars[Rng.Next(LobbyCodeChars.Length)];
            });
        } while (existingCodes.Contains(code));

        return code;
    }
}
