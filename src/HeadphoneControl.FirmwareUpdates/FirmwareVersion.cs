using System.Globalization;

namespace HeadphoneControl.FirmwareUpdates;

internal static class FirmwareVersion
{
    private const int MaxParts = 8;

    // Leading zeros parse as numbers, so without this a manifest could put text of any length into the UI and the log.
    private const int MaxLength = 32;

    public static bool TryParse(string text, out int[] parts)
    {
        var fields = text.Trim().Split('.');
        parts = new int[fields.Length];
        if (fields.Length > MaxParts || text.Length > MaxLength)
        {
            return false;
        }

        for (var i = 0; i < fields.Length; i++)
        {
            // NumberStyles.None: digits only, so "1.-1", "1. 2" and "1.2b" are rejected.
            if (!int.TryParse(fields[i], NumberStyles.None, CultureInfo.InvariantCulture, out parts[i]))
            {
                return false;
            }
        }

        return true;
    }

    public static int Compare(int[] left, int[] right)
    {
        for (var i = 0; i < Math.Max(left.Length, right.Length); i++)
        {
            var difference = left.ElementAtOrDefault(i).CompareTo(right.ElementAtOrDefault(i));
            if (difference != 0)
            {
                return difference;
            }
        }

        return 0;
    }
}
