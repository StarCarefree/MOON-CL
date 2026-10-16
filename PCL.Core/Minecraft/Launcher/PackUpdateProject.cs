using System;

namespace PCL.Core.Minecraft.Launcher;

public enum PackProjectMatch { Unknown, NameMatch, SameProject, DifferentProject }

/// <summary>Project identity, not a release number or Minecraft version, determines update compatibility.</summary>
public static class PackUpdateProject
{
    public static PackProjectMatch Compare(PackIdentity installed, PackIdentity downloaded)
    {
        var same = false;
        foreach (var (left, right) in new[]
        {
            (installed.CurseForgeId, downloaded.CurseForgeId),
            (installed.ModrinthId, downloaded.ModrinthId),
            (installed.McmodId, downloaded.McmodId)
        })
        {
            if (string.IsNullOrWhiteSpace(left) || string.IsNullOrWhiteSpace(right)) continue;
            // A conflicting ID must never be hidden by a similar name or another matching ID.
            if (!string.Equals(left, right, StringComparison.Ordinal)) return PackProjectMatch.DifferentProject;
            same = true;
        }
        if (same) return PackProjectMatch.SameProject;
        return PackIdentityResolver.NameScore(installed.Name, downloaded.Name) >= PackIdentityResolver.AcceptScore
            ? PackProjectMatch.NameMatch : PackProjectMatch.Unknown;
    }

    public static string DownloadPage(PackIdentity identity)
    {
        if (System.Text.RegularExpressions.Regex.IsMatch(identity.ModrinthId, "^[A-Za-z0-9]{8}$"))
            return "https://modrinth.com/modpack/" + identity.ModrinthId + "/versions";
        if (long.TryParse(identity.CurseForgeId, out var id) && id > 0)
            return "https://www.curseforge.com/projects/" + id;
        return Uri.TryCreate(identity.Website, UriKind.Absolute, out var uri) &&
               uri.Scheme is "http" or "https" && string.IsNullOrEmpty(uri.UserInfo) ? uri.AbsoluteUri : "";
    }
}
