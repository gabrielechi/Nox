namespace Client.Windows.Services;

internal static class PasswordStrengthEstimator
{
    // Bound the work for very large pasted passwords. This is an advisory,
    // conservative estimate of the prefix, never a registration requirement.
    public const int MaximumEvaluatedLength = 128;

    public static int Estimate(string password, string username)
    {
        string candidate = password[..Math.Min(password.Length, MaximumEvaluatedLength)];
        return Zxcvbn.Core.EvaluatePassword(candidate, new[] { username, "nox" }).Score;
    }
}
