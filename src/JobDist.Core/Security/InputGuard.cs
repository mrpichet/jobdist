using System.Text;

namespace JobDist.Core.Security;

/// <summary>Small input sanitising helpers used by services (defence in depth on top of contract validation).</summary>
public static class InputGuard
{
    /// <summary>
    /// Removes control characters (log forging / header injection) and truncates.
    /// </summary>
    /// <param name="value">Untrusted text.</param>
    /// <param name="maxLength">Max length to keep.</param>
    /// <returns>Clean text or null.</returns>
    public static string? CleanText(string? value, int maxLength)
    {
        if (string.IsNullOrEmpty(value))
        {
            return value;
        }

        // Step 1: keep printable characters, tabs and new lines only.
        var sb = new StringBuilder(Math.Min(value.Length, maxLength));
        foreach (var ch in value)
        {
            if (sb.Length >= maxLength)
            {
                break;
            }
            if (!char.IsControl(ch) || ch is '\n' or '\t')
            {
                sb.Append(ch);
            }
        }
        return sb.ToString();
    }

    /// <summary>Validates a job type / name token (letters, digits, '.', '_', '-', max 100).</summary>
    public static bool IsValidJobType(string? jobType)
    {
        if (string.IsNullOrEmpty(jobType) || jobType.Length > 100 || !char.IsAsciiLetterOrDigit(jobType[0]))
        {
            return false;
        }
        foreach (var ch in jobType)
        {
            if (!char.IsAsciiLetterOrDigit(ch) && ch is not ('.' or '_' or '-'))
            {
                return false;
            }
        }
        return true;
    }
}
