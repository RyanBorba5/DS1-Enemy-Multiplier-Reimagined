namespace DS1_Enemy_Multiplier;

public static class InputValidator
{
    /// <summary>
    /// Tries to parse a multiplier from user input.
    /// Returns true if input is a valid integer >= 1.
    /// Multiplier of 1 is allowed (means restore vanilla).
    /// Multiplier >= 2 means actual multiplication.
    /// </summary>
    public static bool TryParseMultiplier(string? input, out int multiplier, out string error)
    {
        multiplier = 0;
        error = string.Empty;

        if (!int.TryParse(input?.Trim(), out int parsed))
        {
            error = $"'{input}' is not a valid whole number. Please enter a whole number (e.g. 2, 3, 4).";
            return false;
        }

        if (parsed < 1)
        {
            error = $"{parsed} is too small. Enter 1 to restore vanilla files, or 2 or higher to multiply enemies.";
            return false;
        }

        multiplier = parsed;
        return true;
    }
}
