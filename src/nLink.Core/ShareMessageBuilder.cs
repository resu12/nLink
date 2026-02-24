using System;
using System.Text;

namespace NLink.Core;

public static class ShareMessageBuilder
{
    public static string BuildInstallMessage(string? code, string? downloadUrl)
    {
        var hasCode = !string.IsNullOrWhiteSpace(code);
        var firstLine = hasCode
            ? $"Install nLink and enter code {code!.Trim()}"
            : "Install nLink";

        var url = string.IsNullOrWhiteSpace(downloadUrl) ? null : downloadUrl.Trim();
        if (string.IsNullOrWhiteSpace(url))
        {
            return firstLine;
        }

        var sb = new StringBuilder(firstLine.Length + url.Length + 2);
        sb.Append(firstLine)
          .Append(Environment.NewLine)
          .Append(url);
        return sb.ToString();
    }
}
